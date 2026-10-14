module LifecycleRollbackTests

// Conditor 0.10.4 on Chrona (WI-0044): `upgrade --current` to echelon-current
// 1.23.0 moved Praxis 3.11.0 -> 3.12.0 through Praxis's own lifecycle. That
// lifecycle moves the `praxis` capability in .echelon/foundations.json
// itself, so Conditor's planned foundations edit then found 3.12.0 and
// refused the run (CON-357). The refusal restored only the files Conditor
// edits, leaving everything Praxis's lifecycle wrote: .echelon/ros.json,
// .echelon/toolchain.json, managed docs and a new schema (CON-356).
//
// The fixture is a Chrona-shaped repository on the real embedded-qualified
// Praxis path: a stand-in `praxis` release is installed through the
// workstation engine, and its `upgrade` behaves like Praxis's lifecycle. It
// moves the foundations pin, rewrites state, managed docs and toolchain, and
// adds a schema. A variant then fails its strict verify, so the run must
// roll every one of those files back.

open System
open System.Formats.Tar
open System.IO
open System.IO.Compression
open System.Security.Cryptography
open Conditor.Core
open Conditor.Core.Workstation

let private joined (values: string list) = String.concat "; " values

let private temp label =
    let path = Path.Combine(Path.GetTempPath(), $"conditor-rollback-{label}-{Guid.NewGuid():N}")
    Directory.CreateDirectory path |> ignore
    path

let private sha256File (path: string) =
    Convert.ToHexString(SHA256.HashData(File.ReadAllBytes path)).ToLowerInvariant()

let private git (root: string) (arguments: string list) =
    let result = ProcessRunner.runProcess root "git" arguments
    let shown = String.concat " " arguments
    if result.ExitCode <> 0 then failwith $"git {shown}: {result.StandardError}"

/// A stand-in Praxis release whose `upgrade` does what Praxis's lifecycle
/// does to a governed repository. `verifyPasses = false` makes its strict
/// verify fail after the upgrade wrote everything.
let private praxisBundle (mirror: string) (version: string) (rid: string) (verifyPasses: bool) =
    let staging = temp "bundle"
    let root = Path.Combine(staging, $"praxis-{rid}")
    Directory.CreateDirectory root |> ignore
    let executable = Path.Combine(root, "praxis")
    let verify = if verifyPasses then $"grep -q '\"installedVersion\": \"{version}\"' .echelon/ros.json" else "echo 'verification failed: stand-in refusal' >&2; exit 1"

    let script =
        String.concat
            "\n"
            [ "#!/bin/sh"
              "set -eu"
              "case \"$1\" in"
              $"  --version) echo 'praxis {version}' ;;"
              "  upgrade)"
              "    from=$(sed -n 's/.*\"installedVersion\": \"\\([^\"]*\\)\".*/\\1/p' .echelon/ros.json)"
              // Praxis's lifecycle moves its own foundations capability.
              $"    sed -i \"s/\\\"version\\\": \\\"$from\\\"/\\\"version\\\": \\\"{version}\\\"/\" .echelon/foundations.json"
              $"    printf '{{\\n  \"installedVersion\": \"{version}\"\\n}}\\n' > .echelon/ros.json"
              $"    sed -i 's/\"praxis\": \"[^\"]*\"/\"praxis\": \"{version}\"/' .echelon/toolchain.json"
              $"    echo 'Managed by Praxis {version}.' >> docs/work-protocol.md"
              "    mkdir -p schemas/approval"
              "    echo '{\"title\":\"ecir approval\"}' > schemas/approval/ecir-approval-v1.schema.json"
              "    ;;"
              $"  verify) {verify} ;;"
              "  doctor) exit 0 ;;"
              "  *) exit 2 ;;"
              "esac"
              "" ]

    File.WriteAllText(executable, script)
    File.SetUnixFileMode(executable, UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute)
    let name = $"praxis-{rid}.tar.gz"
    let archive = Path.Combine(mirror, name)

    do
        use file = File.Create archive
        use gzip = new GZipStream(file, CompressionLevel.Fastest)
        TarFile.CreateFromDirectory(staging, gzip, false)

    name, sha256File archive

/// A Registry-shaped set selecting Praxis `version` as a native host tool
/// without the generic lifecycle contract: the embedded-qualified path.
let private praxisSet (path: string) (version: string) (rid: string) (assetName: string) (assetSha: string) =
    File.WriteAllText(
        path,
        $$"""{
  "schema": "echelon.resolved-release-set/v1",
  "profile": { "id": "echelon-current", "version": "1.23.0", "sha256": "{{String.replicate 64 "a"}}" },
  "platform": "{{rid}}",
  "resolver": { "name": "test", "version": "1.0.0" },
  "catalogSnapshot": { "sha256": "{{String.replicate 64 "b"}}" },
  "components": [
    {
      "systemId": "praxis",
      "role": "host-tool",
      "required": true,
      "version": "{{version}}",
      "repository": "kemiller2002/praxis",
      "tag": "v{{version}}",
      "commit": "{{String.replicate 40 "6"}}",
      "releaseStage": "stable",
      "lifecycleState": "active",
      "distributionClass": "self-contained-native-cli",
      "executable": "praxis",
      "releaseManifest": { "schema": "echelon.release/v2", "sha256": "{{String.replicate 64 "c"}}" },
      "distribution": { "mechanism": "github-release", "package": null, "url": "https://github.com/kemiller2002/praxis/releases/tag/v{{version}}" },
      "artifacts": [
        { "name": "{{assetName}}", "purpose": "executable", "platform": "{{rid}}", "sha256": "{{assetSha}}" }
      ]
    }
  ]
}
"""
    )

    sha256File path

let private foundations (praxis: string) =
    $"{{\n  \"schemaVersion\": 1,\n  \"application\": \"Chrona\",\n  \"capabilities\": {{\n    \"ordo\": {{\n      \"required\": true,\n      \"version\": \"1.7.0\"\n    }},\n    \"praxis\": {{\n      \"required\": true,\n      \"version\": \"{praxis}\"\n    }}\n  }}\n}}\n"

type private Repo =
    { Target: string
      ManifestPath: string
      Mirror: string
      Context: WorkstationContext }

/// A Chrona-shaped repository on Praxis 3.11.0: conditor.json, the lock,
/// Praxis's own state, toolchain and foundations all at 3.11.0, plus a
/// managed doc. With `inGit`, it is a committed Git work tree.
let private chronaShaped label (inGit: bool) =
    let target = temp $"{label}-target"
    let mirror = temp $"{label}-mirror"
    let home = temp $"{label}-home"
    let manifestPath = Path.Combine(target, "conditor.json")

    File.WriteAllText(
        manifestPath,
        """{
  "schemaVersion": 1,
  "name": "chrona-shaped",
  "components": [
    { "id": "praxis", "version": "3.11.0", "required": true }
  ],
  "requirements": [],
  "execution": { "enabled": false }
}
"""
    )

    LockFile.write
        target
        manifestPath
        { ProjectName = "chrona-shaped"
          Operation = Init
          Components =
            [ { Id = "praxis"
                Version = "3.11.0"
                Distribution = HostTool
                Package = "praxis"
                SourceReference = Some "host:praxis@3.11.0" } ]
          Actions = [] }
    |> ignore

    Directory.CreateDirectory(Path.Combine(target, ".echelon")) |> ignore
    Directory.CreateDirectory(Path.Combine(target, "docs")) |> ignore
    File.WriteAllText(Path.Combine(target, ".echelon", "foundations.json"), foundations "3.11.0")
    File.WriteAllText(Path.Combine(target, ".echelon", "ros.json"), "{\n  \"installedVersion\": \"3.11.0\"\n}\n")
    File.WriteAllText(Path.Combine(target, ".echelon", "toolchain.json"), "{\n  \"schemaVersion\": 1,\n  \"ordo\": \"1.7.0\",\n  \"praxis\": \"3.11.0\"\n}\n")
    File.WriteAllText(Path.Combine(target, "docs", "work-protocol.md"), "# Work protocol\n")

    if inGit then
        File.WriteAllText(Path.Combine(target, ".gitignore"), "build/\n")
        git target [ "init"; "-q" ]
        git target [ "add"; "-A" ]
        git target [ "-c"; "user.name=t"; "-c"; "user.email=t@example.invalid"; "commit"; "-q"; "-m"; "chrona-shaped" ]

    { Target = target
      ManifestPath = manifestPath
      Mirror = mirror
      Context =
        { Home = home
          ArtifactMirror = Some mirror
          Offline = true
          Praxis = None
          TargetId = Some "rollback-test" } }

let private setFor (repo: Repo) (version: string) (verifyPasses: bool) =
    let rid = Platform.runtimeIdentifier ()
    let assetName, assetSha = praxisBundle repo.Mirror version rid verifyPasses
    let path = Path.Combine(repo.Mirror, $"praxis-{version}.json")
    path, praxisSet path version rid assetName assetSha

let private preview (repo: Repo) (path: string, sha: string) =
    match Manifest.load repo.ManifestPath with
    | Error errors -> Error errors
    | Ok manifest ->
        let probe executable arguments = ProcessRunner.runProcess repo.Target executable arguments
        CurrentUpgrade.preview repo.Target repo.ManifestPath manifest path sha repo.Context probe

let private apply (repo: Repo) (path: string, sha: string) digest =
    match Manifest.load repo.ManifestPath with
    | Error errors -> Error errors
    | Ok manifest ->
        let probe executable arguments = ProcessRunner.runProcess repo.Target executable arguments
        CurrentUpgrade.apply repo.Target repo.ManifestPath manifest path sha repo.Context probe digest

/// Every file under the work tree (except .git) with its bytes.
let private tree (root: string) =
    Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
    |> Seq.filter (fun path -> not (path.Contains $"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}"))
    |> Seq.map (fun path -> Path.GetRelativePath(root, path).Replace('\\', '/'), sha256File path)
    |> Map.ofSeq

let private read (repo: Repo) (relative: string) = File.ReadAllText(Path.Combine(repo.Target, relative))

/// CON-357: the lifecycle already moved its own pin; Conditor verifies it.
let private lifecycleOwnedPin check =
    let repo = chronaShaped "owned-pin" true
    let set = setFor repo "3.12.0" true

    match preview repo set with
    | Error errors -> check $"Chrona-shaped Praxis 3.11.0 -> 3.12.0 plan is produced: {joined errors}" false
    | Ok plan ->
        check
            $"the plan moves praxis through its embedded lifecycle and plans the foundations pin (the 0.10.4 shape): {joined plan.Refusals}"
            (plan.Refusals.IsEmpty
             && plan.Transitions |> List.map (fun item -> item.Id, item.FromVersion, item.ToVersion, item.Mode) = [ "praxis", "3.11.0", "3.12.0", "embedded-qualified-lifecycle" ]
             && plan.FoundationsEdits |> List.map (fun edit -> edit.Capability, edit.FromVersion, edit.ToVersion) = [ "praxis", "3.11.0", "3.12.0" ])

        match apply repo set plan.Digest with
        | Error errors -> check $"a lifecycle that already moved its foundations pin does not refuse the run (CON-357): {joined errors}" false
        | Ok result ->
            check "foundations.json declares praxis exactly once, at 3.12.0" (read repo ".echelon/foundations.json" = foundations "3.12.0")
            check "conditor.json declares praxis 3.12.0" ((read repo "conditor.json").Contains "\"version\": \"3.12.0\"")
            check "the lifecycle's own writes are kept on success" (File.Exists(Path.Combine(repo.Target, "schemas", "approval", "ecir-approval-v1.schema.json")))
            check "no transitions and no pin repairs remain" result.NoRemainingVersionChanges

    // The edit itself: already at the target is verified; anything else still refuses.
    let path = Path.Combine(temp "edit", "foundations.json")
    let edit = { Path = path; Sha256 = ""; Capability = "praxis"; FromVersion = "3.11.0"; ToVersion = "3.12.0" } : FoundationsEdit
    File.WriteAllText(path, foundations "3.12.0")
    check "a foundations pin already at exactly the target is verified, not refused" (WebPackageBinding.applyFoundations edit = Ok() && File.ReadAllText path = foundations "3.12.0")
    File.WriteAllText(path, foundations "3.10.0")
    check "a foundations pin at any other version is still refused" (Result.isError (WebPackageBinding.applyFoundations edit))

/// CON-356: a failure after the lifecycle wrote files restores all of them.
let private failedRunRestoresEverything check (inGit: bool) =
    let label = if inGit then "git" else "plain"
    let repo = chronaShaped $"rollback-{label}" inGit
    let set = setFor repo "3.12.0" false
    let before = tree repo.Target

    match preview repo set with
    | Error errors -> check $"[{label}] failing-lifecycle plan is produced: {joined errors}" false
    | Ok plan ->
        match apply repo set plan.Digest with
        | Ok _ -> check $"[{label}] a lifecycle whose verify fails stops the run" false
        | Error errors ->
            check $"[{label}] the run stops at the failed lifecycle verification" (errors |> List.exists (fun error -> error.Contains "repository verification failed"))
            check $"[{label}] every repository file is byte-identical to before the run, and no file the lifecycle created remains" (tree repo.Target = before)
            check $"[{label}] a directory the lifecycle created is removed" (not (Directory.Exists(Path.Combine(repo.Target, "schemas"))))

            check
                $"[{label}] the refusal names every restored file"
                (errors
                 |> List.exists (fun error ->
                     [ ".echelon/foundations.json"; ".echelon/ros.json"; ".echelon/toolchain.json"; "docs/work-protocol.md"; "schemas/approval/ecir-approval-v1.schema.json" ]
                     |> List.forall error.Contains))

/// The snapshot itself: modified, deleted and created files, nested new
/// directories, and ignored output left alone.
let private snapshotSemantics check =
    let root = temp "snapshot"
    File.WriteAllText(Path.Combine(root, ".gitignore"), "build/\n")
    File.WriteAllText(Path.Combine(root, "kept.txt"), "kept\n")
    File.WriteAllText(Path.Combine(root, "edited.txt"), "before\n")
    File.WriteAllText(Path.Combine(root, "removed.txt"), "removed\n")
    Directory.CreateDirectory(Path.Combine(root, "existing-empty")) |> ignore
    git root [ "init"; "-q" ]
    git root [ "add"; "-A" ]
    git root [ "-c"; "user.name=t"; "-c"; "user.email=t@example.invalid"; "commit"; "-q"; "-m"; "base" ]
    File.WriteAllText(Path.Combine(root, "untracked.txt"), "untracked before the run\n")

    let snapshot = RepositorySnapshot.take root
    File.WriteAllText(Path.Combine(root, "edited.txt"), "after\n")
    File.Delete(Path.Combine(root, "removed.txt"))
    Directory.CreateDirectory(Path.Combine(root, "new", "deeper")) |> ignore
    File.WriteAllText(Path.Combine(root, "new", "deeper", "created.json"), "{}\n")
    File.WriteAllText(Path.Combine(root, "untracked.txt"), "changed\n")
    Directory.CreateDirectory(Path.Combine(root, "build")) |> ignore
    File.WriteAllText(Path.Combine(root, "build", "output.bin"), "ignored\n")

    let restored = RepositorySnapshot.restore snapshot
    check "the snapshot restores edited, removed and untracked files and deletes created ones" (restored = [ "edited.txt"; "new/deeper/created.json"; "removed.txt"; "untracked.txt" ])
    check "restored files have their exact prior bytes" (File.ReadAllText(Path.Combine(root, "edited.txt")) = "before\n" && File.ReadAllText(Path.Combine(root, "removed.txt")) = "removed\n" && File.ReadAllText(Path.Combine(root, "untracked.txt")) = "untracked before the run\n")
    check "directories the run created are removed; ones that existed stay" (not (Directory.Exists(Path.Combine(root, "new"))) && Directory.Exists(Path.Combine(root, "existing-empty")))
    check "ignored output is not repository state and is left alone" (File.Exists(Path.Combine(root, "build", "output.bin")))
    check "a second restore changes nothing" (RepositorySnapshot.restore snapshot |> List.isEmpty)

let run (check: string -> bool -> unit) =
    if OperatingSystem.IsWindows() then
        check "lifecycle rollback fixtures are skipped on Windows" true
    else
        lifecycleOwnedPin check
        failedRunRestoresEverything check true
        failedRunRestoresEverything check false
        snapshotSemantics check
