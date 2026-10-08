module CurrentUpgradeTests

open System
open System.Formats.Tar
open System.IO
open System.IO.Compression
open System.Security.Cryptography
open Conditor.Core
open Conditor.Core.Workstation

let private temp label =
    let path = Path.Combine(Path.GetTempPath(), $"conditor-current-{label}-{Guid.NewGuid():N}")
    Directory.CreateDirectory path |> ignore
    path

let private sha256File path =
    use stream = File.OpenRead path
    Convert.ToHexString(SHA256.HashData stream).ToLowerInvariant()

let private lifecycleBundle mirror id version repository commit rid =
    let staging = temp "bundle"
    let root = Path.Combine(staging, $"{id}-{rid}")
    Directory.CreateDirectory root |> ignore
    let executable = Path.Combine(root, id)

    let script =
        String.concat
            "\n"
            [ "#!/bin/sh"
              "set -eu"
              "op=\"$1\""
              "case \"$op\" in"
              $"  version) echo '{{\"SystemId\":\"{id}\",\"Repository\":\"{repository}\",\"Executable\":\"{id}\",\"ReleaseVersion\":\"{version}\",\"SourceCommit\":\"{commit}\"}}' ;;"
              "  upgrade)"
              "    root=\"$3\""
              "    mkdir -p \"$root/.gamma\""
              $"    echo '{version}' > \"$root/.gamma/version\""
              "    ;;"
              "  verify|status|doctor)"
              "    root=\"$3\""
              $"    test \"$(cat \"$root/.gamma/version\" 2>/dev/null || true)\" = \"{version}\""
              "    ;;"
              "  init)"
              "    root=\"$3\""
              "    mkdir -p \"$root/.gamma\""
              $"    echo '{version}' > \"$root/.gamma/version\""
              "    ;;"
              "  *) exit 2 ;;"
              "esac"
              "" ]

    File.WriteAllText(executable, script)
    File.SetUnixFileMode(
        executable,
        UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute
    )

    let name = $"{id}-{rid}.tar.gz"
    let archive = Path.Combine(mirror, name)

    do
        use file = File.Create archive
        use gzip = new GZipStream(file, CompressionLevel.Fastest)
        TarFile.CreateFromDirectory(staging, gzip, false)

    name, sha256File archive

let private resolvedSet path id version repository commit rid assetName assetSha =
    let profileSha = String.replicate 64 "a"
    let catalogSha = String.replicate 64 "b"
    let releaseSha = String.replicate 64 "c"

    let template =
        """{
  "schema": "echelon.resolved-release-set/v1",
  "profile": {
    "id": "echelon-current-test",
    "version": "1.0.0",
    "sha256": "__PROFILE_SHA__"
  },
  "platform": "__RID__",
  "resolver": {
    "name": "test",
    "version": "1.0.0"
  },
  "catalogSnapshot": {
    "sha256": "__CATALOG_SHA__"
  },
  "components": [
    {
      "systemId": "__ID__",
      "role": "repository-lifecycle",
      "required": true,
      "version": "__VERSION__",
      "repository": "__REPOSITORY__",
      "tag": "v__VERSION__",
      "commit": "__COMMIT__",
      "releaseStage": "stable",
      "lifecycleState": "active",
      "distributionClass": "self-contained-native-cli",
      "executable": "__ID__",
      "repositoryLifecycle": {
        "contract": "echelon.repository-lifecycle",
        "contractVersion": 1
      },
      "releaseManifest": {
        "schema": "echelon.release/v2",
        "sha256": "__RELEASE_SHA__"
      },
      "distribution": {
        "mechanism": "github-release",
        "url": "https://github.com/__REPOSITORY__/releases/tag/v__VERSION__"
      },
      "artifacts": [
        {
          "name": "__ASSET_NAME__",
          "purpose": "executable",
          "platform": "__RID__",
          "sha256": "__ASSET_SHA__"
        }
      ]
    }
  ]
}
"""

    let json =
        template
            .Replace("__PROFILE_SHA__", profileSha)
            .Replace("__CATALOG_SHA__", catalogSha)
            .Replace("__RID__", rid)
            .Replace("__ID__", id)
            .Replace("__VERSION__", version)
            .Replace("__REPOSITORY__", repository)
            .Replace("__COMMIT__", commit)
            .Replace("__RELEASE_SHA__", releaseSha)
            .Replace("__ASSET_NAME__", assetName)
            .Replace("__ASSET_SHA__", assetSha)

    File.WriteAllText(path, json)
    sha256File path

let private context home mirror : WorkstationContext =
    { Home = home
      ArtifactMirror = Some mirror
      Offline = true
      Praxis = None
      TargetId = Some "current-upgrade-test" }

let private establishLock target manifestPath id version =
    let plan =
        { ProjectName = "current-upgrade-test"
          Operation = Init
          Components =
            [ { Id = id
                Version = version
                Distribution = HostTool
                Package = id
                SourceReference = Some $"fixture:{id}@{version}" } ]
          Actions = [] }

    LockFile.write target manifestPath plan |> ignore

let run (check: string -> bool -> unit) =
    if OperatingSystem.IsWindows() then
        check "current upgrade fixture is skipped on Windows" true
    else
        let target = temp "target"
        let home = temp "home"
        let mirror = temp "mirror"
        let rid = Platform.runtimeIdentifier ()
        let id = "gamma"
        let repository = "example/gamma"
        let commit = String.replicate 40 "d"
        let assetName, assetSha = lifecycleBundle mirror id "2.0.0" repository commit rid
        let resolvedPath = Path.Combine(mirror, "current.resolved.json")
        let resolvedSha = resolvedSet resolvedPath id "2.0.0" repository commit rid assetName assetSha
        let manifestPath = Path.Combine(target, "conditor.json")

        File.WriteAllText(
            manifestPath,
            """{
  "schemaVersion": 1,
  "name": "current-upgrade-test",
  "components": [
    { "id": "gamma", "version": "1.0.0", "required": true }
  ],
  "requirements": [],
  "execution": { "enabled": false }
}
"""
        )

        Directory.CreateDirectory(Path.Combine(target, ".gamma")) |> ignore
        File.WriteAllText(Path.Combine(target, ".gamma", "version"), "1.0.0\n")
        establishLock target manifestPath id "1.0.0"

        let ctx = context home mirror
        let priorPath = Environment.GetEnvironmentVariable "PATH"

        // The workstation's bin directory is NOT on this process's PATH (a
        // real upgrade only adds it to future shells), and a decoy `gamma`
        // that fails every command IS: verification must run the copy the
        // upgrade installed, not whatever PATH finds.
        let decoys = temp "decoys"
        let decoy = Path.Combine(decoys, id)
        File.WriteAllText(decoy, "#!/bin/sh\necho 'decoy gamma on PATH' >&2\nexit 7\n")
        File.SetUnixFileMode(decoy, UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute)
        Environment.SetEnvironmentVariable("PATH", decoys + string Path.PathSeparator + priorPath)

        try
            match Manifest.load manifestPath with
            | Error errors ->
                let details = String.concat "; " errors
                check $"current upgrade source manifest parses: {details}" false
            | Ok manifest ->
                let probe executable arguments = ProcessRunner.runProcess target executable arguments

                match CurrentUpgrade.preview target manifestPath manifest resolvedPath resolvedSha ctx probe with
                | Error errors ->
                    let details = String.concat "; " errors
                    check $"current upgrade preview succeeds: {details}" false
                | Ok plan ->
                    let observedTransitions =
                        plan.Transitions
                        |> List.map (fun item -> item.Id, item.FromVersion, item.ToVersion)

                    check
                        "current upgrade selects exact Registry transition"
                        (observedTransitions = [ "gamma", "1.0.0", "2.0.0" ])

                    check "current upgrade uses generic repository lifecycle" (plan.Transitions.Head.Mode = "repository-lifecycle")
                    check
                        "current upgrade plans exact native workstation artifact"
                        (plan.WorkstationPlan
                         |> Option.exists (fun workstation ->
                             workstation.Steps
                             |> List.exists (fun step ->
                                 step.Artifact
                                 |> Option.exists (fun (_, digest) -> digest = "sha256:" + assetSha))))

                    check
                        "current upgrade planning is read-only"
                        (File.ReadAllText(Path.Combine(target, ".gamma", "version")).Trim() = "1.0.0")

                    let repeated =
                        CurrentUpgrade.preview target manifestPath manifest resolvedPath resolvedSha ctx probe
                        |> Result.map (fun second -> second.Digest)

                    check "current upgrade plan is deterministic" (repeated = Ok plan.Digest)

                    let stale =
                        CurrentUpgrade.apply
                            target
                            manifestPath
                            manifest
                            resolvedPath
                            resolvedSha
                            ctx
                            probe
                            "sha256:stale"

                    check "current upgrade refuses stale authorization" (Result.isError stale)
                    check
                        "stale current upgrade does not mutate repository lifecycle"
                        (File.ReadAllText(Path.Combine(target, ".gamma", "version")).Trim() = "1.0.0")

                    match
                        CurrentUpgrade.apply
                            target
                            manifestPath
                            manifest
                            resolvedPath
                            resolvedSha
                            ctx
                            probe
                            plan.Digest
                    with
                    | Error errors ->
                        let details = String.concat "; " errors
                        check $"authorized current upgrade succeeds: {details}" false
                    | Ok result ->
                        check
                            "current upgrade runs component-owned upgrade"
                            (File.ReadAllText(Path.Combine(target, ".gamma", "version")).Trim() = "2.0.0")

                        check
                            "current upgrade materializes exact Registry authority"
                            (File.Exists result.AuthorityPath && sha256File result.AuthorityPath = resolvedSha)

                        check "current upgrade proves zero remaining version drift" result.NoRemainingVersionChanges
                        check "current upgrade writes a fresh lock" (File.Exists result.LockPath)

                        match Manifest.load manifestPath with
                        | Error errors ->
                            let details = String.concat "; " errors
                            check $"upgraded manifest parses: {details}" false
                        | Ok upgraded ->
                            check
                                "current upgrade updates declared component version"
                                (upgraded.Components
                                 |> List.exists (fun entry ->
                                     entry.Id = "gamma"
                                     && entry.Version = Some "2.0.0"))

                            check
                                "current upgrade records exact authority digest"
                                (upgraded.RegistryAuthority
                                 |> Option.exists (fun authority -> authority.Sha256 = resolvedSha))

                            match CurrentUpgrade.preview target manifestPath upgraded resolvedPath resolvedSha ctx probe with
                            | Error errors ->
                                let details = String.concat "; " errors
                                check $"second current upgrade preview succeeds: {details}" false
                            | Ok second ->
                                check "second current upgrade has zero version transitions" second.Transitions.IsEmpty
            // The resolution rule itself: a preferred directory wins over
            // PATH; a name it does not hold still resolves through PATH.
            let preferred = temp "preferred"
            let installedCopy = Path.Combine(preferred, id)
            File.WriteAllText(installedCopy, "#!/bin/sh\nexit 0\n")
            File.SetUnixFileMode(installedCopy, UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute)

            check
                "a preferred directory resolves before PATH"
                (ProcessRunner.resolveExecutableIn [ preferred ] id = Some installedCopy)

            check
                "without a preferred directory, PATH decides"
                (ProcessRunner.resolveExecutable id = Some decoy)

            check
                "a name the preferred directory lacks falls back to PATH"
                (ProcessRunner.resolveExecutableIn [ temp "empty" ] id = Some decoy)
        finally
            Environment.SetEnvironmentVariable("PATH", priorPath)

        let bindingTarget = temp "binding-target"
        let bindingManifestPath = Path.Combine(bindingTarget, "conditor.json")
        let bindingResolved = Path.Combine(mirror, "binding.resolved.json")
        let profileSha = String.replicate 64 "a"
        let catalogSha = String.replicate 64 "b"
        let releaseSha = String.replicate 64 "c"
        let commit2 = String.replicate 40 "e"
        let packageSha = String.replicate 64 "f"

        let bindingTemplate =
            """{
  "schema": "echelon.resolved-release-set/v1",
  "profile": { "id": "binding-current", "version": "1.0.0", "sha256": "__PROFILE_SHA__" },
  "platform": "__RID__",
  "resolver": { "name": "test", "version": "1.0.0" },
  "catalogSnapshot": { "sha256": "__CATALOG_SHA__" },
  "components": [
    {
      "systemId": "gamma",
      "role": "host-tool",
      "required": false,
      "version": "2.0.0",
      "repository": "example/gamma",
      "tag": "v2.0.0",
      "commit": "__GAMMA_COMMIT__",
      "releaseStage": "stable",
      "lifecycleState": "active",
      "distributionClass": "self-contained-native-cli",
      "executable": "gamma",
      "releaseManifest": { "schema": "echelon.release/v2", "sha256": "__RELEASE_SHA__" },
      "distribution": { "mechanism": "github-release", "url": "https://github.com/example/gamma/releases/tag/v2.0.0" },
      "artifacts": [
        { "name": "__GAMMA_ASSET__", "purpose": "executable", "platform": "__RID__", "sha256": "__GAMMA_SHA__" }
      ]
    },
    {
      "systemId": "forma",
      "role": "project-binding",
      "required": true,
      "version": "9.0.0",
      "repository": "example/forma",
      "tag": "v9.0.0",
      "commit": "__COMMIT__",
      "releaseStage": "stable",
      "lifecycleState": "active",
      "distributionClass": "web-package",
      "executable": null,
      "releaseManifest": { "schema": "echelon.release/v2", "sha256": "__RELEASE_SHA__" },
      "distribution": { "mechanism": "github-release", "url": "https://github.com/example/forma/releases/tag/v9.0.0" },
      "artifacts": [
        { "name": "forma.tgz", "purpose": "package", "platform": null, "sha256": "__PACKAGE_SHA__" }
      ]
    }
  ]
}
"""

        let bindingJson =
            bindingTemplate
                .Replace("__PROFILE_SHA__", profileSha)
                .Replace("__RID__", rid)
                .Replace("__CATALOG_SHA__", catalogSha)
                .Replace("__COMMIT__", commit2)
                .Replace("__GAMMA_COMMIT__", commit)
                .Replace("__GAMMA_ASSET__", assetName)
                .Replace("__GAMMA_SHA__", assetSha)
                .Replace("__RELEASE_SHA__", releaseSha)
                .Replace("__PACKAGE_SHA__", packageSha)

        File.WriteAllText(bindingResolved, bindingJson)

        let bindingSha = sha256File bindingResolved
        File.WriteAllText(
            bindingManifestPath,
            """{"schemaVersion":1,"name":"binding","components":[{"id":"forma","version":"0.3.0","required":true}],"requirements":[],"execution":{"enabled":false}}"""
        )
        establishLock bindingTarget bindingManifestPath "forma" "0.3.0"

        match Manifest.load bindingManifestPath with
        | Error _ -> check "project binding refusal fixture parses" false
        | Ok bindingManifest ->
            let probe executable arguments = ProcessRunner.runProcess bindingTarget executable arguments

            match CurrentUpgrade.preview bindingTarget bindingManifestPath bindingManifest bindingResolved bindingSha ctx probe with
            | Error errors ->
                let details = String.concat "; " errors
                check $"project binding current plan is produced: {details}" false
            | Ok bindingPlan ->
                check
                    "current upgrade refuses an unproven project-binding version mutation (no package identity, unqualified target)"
                    (bindingPlan.Refusals
                     |> List.exists (fun error -> error.Contains("'forma'@9.0.0 names no distribution package")))

        // A package-distributed repository lifecycle tool (Visual Engineering is
        // an npm CLI) selected by the authority at the version conditor.json
        // already pins is governed, not refused. A version change is planned
        // only to a version this Conditor build qualifies, distributed under
        // the package its descriptor names; anything else is refused.
        let packageLifecycleCaseWith (label: string) (selectedVersion: string) (selectedPackage: string) =
            let packageTarget = temp $"package-lifecycle-{label}"
            let packageManifestPath = Path.Combine(packageTarget, "conditor.json")
            let packageResolved = Path.Combine(mirror, $"package-lifecycle-{label}.resolved.json")

            let veComponent =
                """,
    {
      "systemId": "visual-engineering",
      "role": "repository-lifecycle",
      "required": true,
      "version": "__VE_VERSION__",
      "repository": "kemiller2002/visual-engineering",
      "tag": "visual-engineering-v__VE_VERSION__",
      "commit": "__COMMIT__",
      "releaseStage": "stable",
      "lifecycleState": "active",
      "distributionClass": "repository-lifecycle",
      "executable": null,
      "releaseManifest": { "schema": "echelon.release/v2", "sha256": "__RELEASE_SHA__" },
      "distribution": { "mechanism": "npm", "package": "__VE_PACKAGE__", "url": "https://registry.npmjs.org/@echelon-foundry/visual-engineering/-/visual-engineering-__VE_VERSION__.tgz" },
      "artifacts": [
        { "name": "echelon-foundry-visual-engineering-__VE_VERSION__.tgz", "purpose": "package", "platform": null, "sha256": "__PACKAGE_SHA__" }
      ]
    }
  ]
}
"""

            let json =
                bindingJson.Substring(0, bindingJson.LastIndexOf("\n  ]\n}"))
                + veComponent
                    .Replace("__VE_VERSION__", selectedVersion)
                    .Replace("__VE_PACKAGE__", selectedPackage)
                    .Replace("__COMMIT__", commit2)
                    .Replace("__RELEASE_SHA__", releaseSha)
                    .Replace("__PACKAGE_SHA__", packageSha)

            File.WriteAllText(packageResolved, json)
            let packageSha256 = sha256File packageResolved

            File.WriteAllText(
                packageManifestPath,
                """{"schemaVersion":1,"name":"package-lifecycle","components":[{"id":"visual-engineering","version":"1.0.0","required":true}],"requirements":[],"execution":{"enabled":false}}"""
            )

            establishLock packageTarget packageManifestPath "visual-engineering" "1.0.0"

            packageTarget, packageManifestPath, packageResolved, packageSha256

        let previewPackageCase (packageTarget, packageManifestPath, packageResolved, packageSha256) =
            match Manifest.load packageManifestPath with
            | Error _ -> Error [ "package lifecycle fixture does not parse" ]
            | Ok packageManifest ->
                let probe executable arguments = ProcessRunner.runProcess packageTarget executable arguments
                CurrentUpgrade.preview packageTarget packageManifestPath packageManifest packageResolved packageSha256 ctx probe

        let packageLifecycleCase label selectedVersion =
            packageLifecycleCaseWith label selectedVersion "@echelon-foundry/visual-engineering"
            |> previewPackageCase

        match packageLifecycleCase "current" "1.0.0" with
        | Error errors ->
            let details = String.concat "; " errors
            check $"package lifecycle current plan is produced: {details}" false
        | Ok packagePlan ->
            let details = String.concat "; " packagePlan.Refusals
            check
                $"current upgrade governs a package-distributed lifecycle tool at its authority version without refusal: {details}"
                packagePlan.Refusals.IsEmpty

            check "current upgrade plans no transition for an already-current lifecycle package" packagePlan.Transitions.IsEmpty

        match packageLifecycleCase "newer" "1.1.0" with
        | Error errors ->
            let details = String.concat "; " errors
            check $"package lifecycle version-change plan is produced: {details}" false
        | Ok packagePlan ->
            check
                "current upgrade refuses an uncontracted lifecycle package version change"
                (packagePlan.Refusals
                 |> List.exists (fun error -> error.Contains("'visual-engineering' 1.0.0 -> 1.1.0")))

        // Visual Engineering 1.0.0 -> 1.0.1 is the qualified echelon-current
        // transition: VE's own `upgrade` rewrites its managed .gitignore region
        // (to `!.visual-engineering/`) that a 1.0.1 `verify --strict` reports
        // stale on a 1.0.0 install.
        match packageLifecycleCase "qualified" "1.0.1" with
        | Error errors ->
            let details = String.concat "; " errors
            check $"qualified lifecycle package transition plan is produced: {details}" false
        | Ok packagePlan ->
            let details = String.concat "; " packagePlan.Refusals
            check $"current upgrade plans the qualified VE 1.0.0 -> 1.0.1 transition without refusal: {details}" packagePlan.Refusals.IsEmpty

            check
                "current upgrade selects the exact VE package lifecycle transition"
                (packagePlan.Transitions
                 |> List.map (fun item -> item.Id, item.FromVersion, item.ToVersion, item.Mode) =
                    [ ("visual-engineering", "1.0.0", "1.0.1", "embedded-qualified-package-lifecycle") ])

            check
                "the VE transition runs the qualified package's own upgrade, then its verify contract"
                (packagePlan.PackageLifecycleTransitions
                 |> List.map (fun (transition: PackageLifecycleTransition) ->
                     transition.Package, transition.UpgradeArguments, transition.VerifyArguments) =
                    [ ("@echelon-foundry/visual-engineering", [ "upgrade" ], [ "verify"; "--strict" ]) ])

            check
                "the VE transition binds the Registry package artifact digest into the plan"
                (packagePlan.PackageLifecycleTransitions
                 |> List.forall (fun (transition: PackageLifecycleTransition) -> transition.ArtifactSha256 = packageSha))

        match packageLifecycleCaseWith "renamed" "1.0.1" "@example/not-visual-engineering" |> previewPackageCase with
        | Error errors ->
            let details = String.concat "; " errors
            check $"renamed lifecycle package plan is produced: {details}" false
        | Ok packagePlan ->
            check
                "current upgrade refuses a lifecycle package whose Registry package differs from the qualified descriptor"
                (packagePlan.Transitions.IsEmpty
                 && packagePlan.Refusals
                    |> List.exists (fun error ->
                        error.Contains("'visual-engineering' 1.0.0 -> 1.0.1")
                        && error.Contains("@example/not-visual-engineering")))

        // Execution: a fake npx stands in for the npm registry so the exact
        // commands Conditor runs, their order and the governance commit are
        // observable without network access.
        let fakeBin = temp "fake-npx"
        let npxLog = Path.Combine(fakeBin, "npx.log")

        let npxScript =
            String.concat
                "\n"
                [ "#!/bin/sh"
                  "set -eu"
                  $"echo \"$*\" >> '{npxLog}'"
                  "[ \"$1\" = --yes ] || exit 64"
                  "version=\"${2##*@}\""
                  "shift 3"
                  "case \"$1\" in"
                  "  --version) echo \"$version\" ;;"
                  "  upgrade)"
                  "    if [ -e .ve/refuse-upgrade ]; then echo 'upgrade refused' >&2; exit 1; fi"
                  "    mkdir -p .ve && echo \"$version\" > .ve/version ;;"
                  "  verify) test \"$(cat .ve/version 2>/dev/null || true)\" = \"$version\" ;;"
                  "  *) exit 2 ;;"
                  "esac"
                  "" ]

        File.WriteAllText(Path.Combine(fakeBin, "npx"), npxScript)
        File.SetUnixFileMode(
            Path.Combine(fakeBin, "npx"),
            UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute
        )

        let applyPackageCase label (prepare: string -> unit) =
            let ((packageTarget, packageManifestPath, packageResolved, packageSha256) as fixture) =
                packageLifecycleCaseWith label "1.0.1" "@echelon-foundry/visual-engineering"

            Directory.CreateDirectory(Path.Combine(packageTarget, ".ve")) |> ignore
            File.WriteAllText(Path.Combine(packageTarget, ".ve", "version"), "1.0.0\n")
            prepare packageTarget
            let priorPath = Environment.GetEnvironmentVariable "PATH"
            Environment.SetEnvironmentVariable("PATH", fakeBin + string Path.PathSeparator + priorPath)

            try
                let outcome =
                    match previewPackageCase fixture, Manifest.load packageManifestPath with
                    | Ok plan, Ok packageManifest ->
                        let probe executable arguments = ProcessRunner.runProcess packageTarget executable arguments

                        CurrentUpgrade.apply
                            packageTarget
                            packageManifestPath
                            packageManifest
                            packageResolved
                            packageSha256
                            ctx
                            probe
                            plan.Digest
                    | Error errors, _
                    | _, Error errors -> Error errors

                packageTarget, packageManifestPath, outcome
            finally
                Environment.SetEnvironmentVariable("PATH", priorPath)

        let _, packageManifestPath, applied = applyPackageCase "apply" ignore

        match applied with
        | Error errors ->
            let details = String.concat "; " errors
            check $"authorized VE 1.0.0 -> 1.0.1 current upgrade succeeds: {details}" false
        | Ok result ->
            let commands = File.ReadAllLines npxLog |> Array.toList

            check
                "VE current upgrade runs the exact 1.0.1 package upgrade before any verification"
                (commands
                 |> List.tryHead = Some "--yes --package=@echelon-foundry/visual-engineering@1.0.1 visual-engineering upgrade")

            check
                "VE current upgrade verifies the upgraded repository with the qualified strict contract"
                (commands
                 |> List.contains "--yes --package=@echelon-foundry/visual-engineering@1.0.1 visual-engineering verify --strict")

            check
                "VE current upgrade never runs an unqualified or source package version"
                (commands |> List.forall (fun line -> line.Contains "@echelon-foundry/visual-engineering@1.0.1 "))

            check "VE current upgrade proves zero remaining version drift" result.NoRemainingVersionChanges
            check "VE current upgrade reports the changed component" (result.ChangedComponents = [ "visual-engineering" ])

            check
                "VE current upgrade commits the 1.0.1 declaration and the Registry authority"
                (match Manifest.load packageManifestPath with
                 | Ok upgraded ->
                     upgraded.Components
                     |> List.exists (fun entry -> entry.Id = "visual-engineering" && entry.Version = Some "1.0.1")
                     && upgraded.RegistryAuthority.IsSome
                 | Error _ -> false)

            check "VE current upgrade writes a fresh lock" (File.Exists result.LockPath)

        File.Delete npxLog

        let refusedTarget, refusedManifestPath, refused =
            applyPackageCase "refused" (fun target -> File.WriteAllText(Path.Combine(target, ".ve", "refuse-upgrade"), ""))

        check "a failing VE upgrade stops the current upgrade" (Result.isError refused)

        check
            "a failing VE upgrade leaves conditor.json at 1.0.0 without Registry authority"
            (match Manifest.load refusedManifestPath with
             | Ok unchanged ->
                 unchanged.Components
                 |> List.exists (fun entry -> entry.Id = "visual-engineering" && entry.Version = Some "1.0.0")
                 && unchanged.RegistryAuthority.IsNone
             | Error _ -> false)

        check
            "a failing VE upgrade does not run verification or commit authority"
            (File.ReadAllLines npxLog |> Array.forall (fun line -> not (line.Contains " verify"))
             && not (File.Exists(Path.Combine(refusedTarget, ".conditor", "authority", "resolved-release-set.json"))))


    // Every echelon-current selection Conditor carries a descriptor for is
    // qualified at exactly the selected version (registry main f0e45db).
    let echelonCurrent =
        [ "praxis", "3.7.2"
          "ordo", "1.4.2"
          "percepta", "0.1.0"
          "visual-engineering", "1.0.1"
          "communication-engineering", "1.0.0"
          "tutela", "0.1.0"
          "aegis", "1.0.0"
          "limen", "0.7.1"
          "forma", "0.4.1"
          "folio", "0.3.0" ]

    for id, version in echelonCurrent do
        check
            $"Conditor qualifies the echelon-current selection {id} {version}"
            (Registry.qualifiedVersions id |> Option.exists (Set.contains version))

    check
        "Visual Engineering 1.0.1 is the default lifecycle version and 1.0.0 stays qualified as its upgrade source"
        (Registry.tryFind "visual-engineering"
         |> Option.exists (fun definition -> definition.DefaultVersion = "1.0.1")
         && Registry.qualifiedVersions "visual-engineering" = Some(Set.ofList [ "1.0.0"; "1.0.1" ]))

/// Opting a project in to an optional native repository-lifecycle tool the
/// target set selects: declared in conditor.json at exactly the selected
/// version, installed into the workstation, initialised in the repository and
/// verified with the installed copy, in one --current upgrade. This is the
/// path a project takes to adopt strata from echelon-current 1.2.0; before
/// it, --current refused any manifest change and `conditor init` could not
/// run a tool nothing had installed.
let runOptIn (check: string -> bool -> unit) =
    if OperatingSystem.IsWindows() then
        check "current upgrade opt-in fixture is skipped on Windows" true
    else
        let target = temp "optin-target"
        let home = temp "optin-home"
        let mirror = temp "optin-mirror"
        let rid = Platform.runtimeIdentifier ()
        let id = "gamma"
        let repository = "example/gamma"
        let commit = String.replicate 40 "d"
        let assetName, assetSha = lifecycleBundle mirror id "2.0.0" repository commit rid
        let resolvedPath = Path.Combine(mirror, "optin.resolved.json")
        let resolvedSha = resolvedSet resolvedPath id "2.0.0" repository commit rid assetName assetSha
        let manifestPath = Path.Combine(target, "conditor.json")

        let manifestWith (components: string) (name: string) =
            File.WriteAllText(
                manifestPath,
                $$"""{
  "schemaVersion": 1,
  "name": "{{name}}",
  "components": [{{components}}],
  "requirements": [],
  "execution": { "enabled": false }
}
"""
            )

        // The environment was established with no components at all.
        manifestWith "" "optin-test"

        LockFile.write
            target
            manifestPath
            { ProjectName = "optin-test"
              Operation = Init
              Components = []
              Actions = [] }
        |> ignore

        let ctx = context home mirror
        let priorPath = Environment.GetEnvironmentVariable "PATH"

        // As on a fresh machine: nothing installed on PATH, only a decoy that
        // fails, so init and verify must use the copy the upgrade installs.
        let decoys = temp "optin-decoys"
        let decoy = Path.Combine(decoys, id)
        File.WriteAllText(decoy, "#!/bin/sh\necho 'decoy gamma on PATH' >&2\nexit 7\n")
        File.SetUnixFileMode(decoy, UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute)
        Environment.SetEnvironmentVariable("PATH", decoys + string Path.PathSeparator + priorPath)

        let probe executable arguments = ProcessRunner.runProcess target executable arguments

        let preview () =
            Manifest.load manifestPath
            |> Result.bind (fun manifest ->
                CurrentUpgrade.preview target manifestPath manifest resolvedPath resolvedSha ctx probe
                |> Result.map (fun plan -> manifest, plan))

        let refusedWith (fragment: string) =
            match preview () with
            | Ok(_, plan) -> plan.Refusals |> List.exists (fun refusal -> refusal.Contains fragment)
            | Error errors -> errors |> List.exists (fun error -> error.Contains fragment)

        try
            // Not opt-ins: a different version, a tool the set does not
            // select, or any other change to conditor.json.
            manifestWith """{ "id": "gamma", "version": "1.5.0", "required": true }""" "optin-test"
            check "opt-in refuses a version other than the selected one" (refusedWith "exactly the version the target set selects")

            manifestWith """{ "id": "gamma", "version": "2.0.0", "required": true }""" "renamed"
            check "a manifest change that is not only an addition still refuses" (refusedWith "manifest has changed")

            manifestWith """{ "id": "gamma", "version": "2.0.0", "required": true }""" "optin-test"

            match preview () with
            | Error errors ->
                let details = String.concat "; " errors
                check $"opt-in preview succeeds: {details}" false
            | Ok(manifest, plan) ->
                check "opt-in plan has no refusals" plan.Refusals.IsEmpty
                check "opt-in is named in the plan" (plan.OptIns |> List.map _.Id = [ id ])
                check "opt-in plans the tool's lifecycle init" plan.GenericInitPlan.IsSome
                check
                    "opt-in installs the exact Registry artifact"
                    (plan.WorkstationPlan
                     |> Option.exists (fun workstation ->
                         workstation.Steps
                         |> List.exists (fun step -> step.Artifact |> Option.exists (fun (_, digest) -> digest = "sha256:" + assetSha))))

                check "opt-in planning is read-only" (not (Directory.Exists(Path.Combine(target, ".gamma"))))

                match CurrentUpgrade.apply target manifestPath manifest resolvedPath resolvedSha ctx probe plan.Digest with
                | Error errors ->
                    let details = String.concat "; " errors
                    check $"authorized opt-in succeeds: {details}" false
                | Ok result ->
                    check
                        "opt-in initialises the tool in the repository with the installed copy"
                        (File.ReadAllText(Path.Combine(target, ".gamma", "version")).Trim() = "2.0.0")

                    check "opt-in reports what it opted in to" (result.OptedIn = [ "gamma@2.0.0" ])
                    check "opt-in commits the Registry authority" (File.Exists result.AuthorityPath && sha256File result.AuthorityPath = resolvedSha)

                    match LockFile.readManifestSnapshot target |> Result.bind Manifest.parseText with
                    | Error errors ->
                        let details = String.concat "; " errors
                        check $"opt-in lock snapshot parses: {details}" false
                    | Ok established ->
                        check "the lock now establishes the opted-in tool" (established.Components |> List.exists (fun c -> c.Id = id))

                    match preview () with
                    | Ok(_, second) -> check "a second plan has nothing left to opt in to" (second.OptIns.IsEmpty && second.Refusals.IsEmpty)
                    | Error errors ->
                        let details = String.concat "; " errors
                        check $"second opt-in preview succeeds: {details}" false
        finally
            Environment.SetEnvironmentVariable("PATH", priorPath)
