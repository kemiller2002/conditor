namespace Conditor.Core

open System
open System.IO

/// The repository as it was before a run's first effect, so a refused or
/// failed run can return every file to exactly that state (CON-356).
///
/// A current upgrade runs components' own lifecycles (`praxis upgrade`,
/// `ordo upgrade`, ...) that write files Conditor never edits itself:
/// managed docs, state such as `.echelon/ros.json`, new schemas. Restoring
/// only the files Conditor edits left those behind after a refusal
/// (conditor 0.10.4, Chrona). The snapshot covers every file Git tracks
/// or would track (untracked and not ignored); outside Git, every file except
/// `.git/` and `node_modules/`. Ignored build output and installed packages
/// are not repository state and are not captured.
type RepositorySnapshot =
    { Root: string
      Files: Map<string, byte array>
      Directories: Set<string> }

[<RequireQualifiedAccess>]
module RepositorySnapshot =
    let private excluded = set [ ".git"; "node_modules" ]

    let private normalize (relative: string) = relative.Replace('\\', '/')

    let private walkFiles (root: string) =
        let rec walk (directory: string) =
            seq {
                for file in Directory.EnumerateFiles directory do
                    yield normalize (Path.GetRelativePath(root, file))

                for child in Directory.EnumerateDirectories directory do
                    if not (excluded.Contains(Path.GetFileName child |> Option.ofObj |> Option.defaultValue "")) then
                        yield! walk child
            }

        walk root |> List.ofSeq

    let private walkDirectories (root: string) =
        let rec walk (directory: string) =
            seq {
                for child in Directory.EnumerateDirectories directory do
                    if not (excluded.Contains(Path.GetFileName child |> Option.ofObj |> Option.defaultValue "")) then
                        yield normalize (Path.GetRelativePath(root, child))
                        yield! walk child
            }

        walk root |> Set.ofSeq

    /// Files Git tracks or would track, relative to `root`; None outside Git.
    let private gitFiles (root: string) =
        let result = ProcessRunner.runProcess root "git" [ "ls-files"; "-z"; "--cached"; "--others"; "--exclude-standard" ]

        if result.ExitCode <> 0 then
            None
        else
            result.StandardOutput.Split('\000', StringSplitOptions.RemoveEmptyEntries)
            |> Array.map normalize
            |> Array.distinct
            |> List.ofArray
            |> Some

    let private repositoryFiles (root: string) =
        match gitFiles root with
        | Some files -> files
        | None -> walkFiles root

    let take (root: string) : RepositorySnapshot =
        let root = Path.GetFullPath root

        let files =
            repositoryFiles root
            |> List.choose (fun relative ->
                let path = Path.Combine(root, relative)
                if File.Exists path then Some(relative, File.ReadAllBytes path) else None)
            |> Map.ofList

        { Root = root
          Files = files
          Directories = walkDirectories root }

    /// Returns the repository to the snapshot: rewrites every file whose bytes
    /// changed, recreates every file that was removed, deletes every file the
    /// run created, and removes directories the run created once empty.
    /// Returns the relative paths it changed, sorted.
    let restore (snapshot: RepositorySnapshot) : string list =
        let root = snapshot.Root

        let rewritten =
            snapshot.Files
            |> Map.toList
            |> List.choose (fun (relative, bytes) ->
                let path = Path.Combine(root, relative)

                if File.Exists path && File.ReadAllBytes path = bytes then
                    None
                else
                    Directory.CreateDirectory(Path.GetDirectoryName path |> Option.ofObj |> Option.defaultValue root) |> ignore
                    File.WriteAllBytes(path, bytes)
                    Some relative)

        let created =
            repositoryFiles root
            |> List.filter (fun relative -> not (snapshot.Files.ContainsKey relative) && File.Exists(Path.Combine(root, relative)))

        for relative in created do
            File.Delete(Path.Combine(root, relative))

        // Deepest first, so a new nested directory empties before its parent.
        walkDirectories root
        |> Set.toList
        |> List.filter (fun directory -> not (snapshot.Directories.Contains directory))
        |> List.sortByDescending (fun directory -> directory.Length)
        |> List.iter (fun directory ->
            let path = Path.Combine(root, directory)

            if Directory.Exists path && Seq.isEmpty (Directory.EnumerateFileSystemEntries path) then
                Directory.Delete path)

        (rewritten @ created) |> List.distinct |> List.sort
