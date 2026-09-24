namespace Conditor.Core

open System
open System.Diagnostics
open System.IO
open System.Text.RegularExpressions

module SourceCache =
    let private nonEmpty value = not (String.IsNullOrWhiteSpace value)

    let private cacheRoot () =
        let overridePath = Environment.GetEnvironmentVariable "CONDITOR_CACHE_DIR"

        if nonEmpty overridePath then
            Path.GetFullPath overridePath
        elif OperatingSystem.IsWindows() then
            Path.Combine(
                Environment.GetFolderPath Environment.SpecialFolder.LocalApplicationData,
                "Conditor",
                "Cache"
            )
        elif OperatingSystem.IsMacOS() then
            Path.Combine(
                Environment.GetFolderPath Environment.SpecialFolder.UserProfile,
                "Library",
                "Caches",
                "conditor"
            )
        else
            let xdg = Environment.GetEnvironmentVariable "XDG_CACHE_HOME"

            if nonEmpty xdg then
                Path.Combine(xdg, "conditor")
            else
                Path.Combine(
                    Environment.GetFolderPath Environment.SpecialFolder.UserProfile,
                    ".cache",
                    "conditor"
                )

    let private safeSegment (value: string) =
        Regex.Replace(value, "[^A-Za-z0-9_.-]", "_")

    let private validateSource (source: GitHubSource) =
        let repositoryOk = Regex.IsMatch(source.Repository, "^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$")
        let commitOk = Regex.IsMatch(source.Commit, "^[0-9a-fA-F]{40}$")

        let entrypoint =
            match source.Entrypoint with
            | NodeScript path -> path

        let entrypointParts =
            entrypoint.Replace('\\', '/').Split('/')

        let entrypointOk =
            nonEmpty entrypoint
            && not (Path.IsPathRooted entrypoint)
            && not (entrypointParts |> Array.exists ((=) ".."))

        [ if not repositoryOk then
              yield $"Invalid GitHub repository '{source.Repository}'."
          if not commitOk then
              yield $"GitHub source '{source.Repository}' must use a full 40-character commit SHA."
          if not entrypointOk then
              yield $"GitHub source entrypoint '{entrypoint}' must be a safe relative path." ]

    let private run workingDirectory executable arguments =
        let info = ProcessStartInfo()
        info.FileName <- executable
        info.WorkingDirectory <- workingDirectory
        info.UseShellExecute <- false
        info.RedirectStandardOutput <- true
        info.RedirectStandardError <- true

        for argument in arguments do
            info.ArgumentList.Add argument

        use childProcess = new Process()
        childProcess.StartInfo <- info

        if not (childProcess.Start()) then
            Error [ $"Unable to start '{executable}'." ]
        else
            let outputTask = childProcess.StandardOutput.ReadToEndAsync()
            let errorTask = childProcess.StandardError.ReadToEndAsync()
            childProcess.WaitForExit()

            let output = outputTask.GetAwaiter().GetResult().Trim()
            let error = errorTask.GetAwaiter().GetResult().Trim()

            if childProcess.ExitCode = 0 then
                Ok output
            else
                Error
                    [ $"Command failed with exit code {childProcess.ExitCode}: {executable} {String.Join(" ", arguments)}"
                      output
                      error ]
                |> Result.mapError (List.filter nonEmpty)

    let private currentHead checkout =
        match run checkout "git" [ "rev-parse"; "HEAD" ] with
        | Ok value -> Some(value.Trim())
        | Error _ -> None

    let private entrypointPath checkout source =
        let relative =
            match source.Entrypoint with
            | NodeScript path -> path

        let root = Path.GetFullPath checkout
        let candidate = Path.GetFullPath(Path.Combine(root, relative))
        let rootPrefix = root.TrimEnd(Path.DirectorySeparatorChar) + string Path.DirectorySeparatorChar

        if candidate.StartsWith(rootPrefix, StringComparison.Ordinal)
           && File.Exists candidate then
            Ok candidate
        else
            Error [ $"Source entrypoint is missing or escapes its checkout: {relative}" ]

    let private verifyCheckout checkout source =
        match currentHead checkout with
        | Some head when String.Equals(head, source.Commit, StringComparison.OrdinalIgnoreCase) ->
            entrypointPath checkout source |> Result.map (fun _ -> checkout)
        | Some head ->
            Error [ $"Cached source HEAD '{head}' does not match required commit '{source.Commit}'." ]
        | None ->
            Error [ $"Unable to verify cached source at '{checkout}'." ]

    let private populate checkout source =
        let parent = Directory.GetParent(checkout).FullName
        Directory.CreateDirectory parent |> ignore
        let temporary = $"{checkout}.tmp-{Guid.NewGuid():N}"

        try
            Directory.CreateDirectory temporary |> ignore

            match run temporary "git" [ "init" ] with
            | Error errors -> Error errors
            | Ok _ ->
                let url = $"https://github.com/{source.Repository}.git"

                match run temporary "git" [ "remote"; "add"; "origin"; url ] with
                | Error errors -> Error errors
                | Ok _ ->
                    match run temporary "git" [ "fetch"; "--depth"; "1"; "origin"; source.Commit ] with
                    | Error errors -> Error errors
                    | Ok _ ->
                        match run temporary "git" [ "checkout"; "--detach"; source.Commit ] with
                        | Error errors -> Error errors
                        | Ok _ ->
                            match verifyCheckout temporary source with
                            | Error errors -> Error errors
                            | Ok _ ->
                                try
                                    Directory.Move(temporary, checkout)
                                    Ok checkout
                                with :? IOException ->
                                    if Directory.Exists checkout then
                                        verifyCheckout checkout source
                                    else
                                        Error [ $"Unable to move prepared source into cache '{checkout}'." ]
        finally
            if Directory.Exists temporary then
                Directory.Delete(temporary, true)

    let ensure componentId source =
        let validationErrors = validateSource source

        if not validationErrors.IsEmpty then
            Error validationErrors
        else
            let checkout =
                Path.Combine(
                    cacheRoot (),
                    "sources",
                    safeSegment componentId,
                    source.Commit.ToLowerInvariant()
                )

            if Directory.Exists checkout then
                match verifyCheckout checkout source with
                | Ok path -> Ok path
                | Error _ ->
                    Directory.Delete(checkout, true)
                    populate checkout source
            else
                populate checkout source

    let resolveEntrypoint checkout source =
        entrypointPath checkout source

    let sourceReference source =
        let entrypoint =
            match source.Entrypoint with
            | NodeScript path -> $"node:{path}"

        $"github:{source.Repository}#{source.Commit}|{entrypoint}"
