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
        Environment.SetEnvironmentVariable("PATH", Path.Combine(home, ".local", "bin") + string Path.PathSeparator + priorPath)

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
                    "current upgrade refuses uncontracted project-binding version mutation"
                    (bindingPlan.Refusals
                     |> List.exists (fun error -> error.Contains("no safe repository upgrade contract")))

        // A package-distributed repository lifecycle tool (Visual Engineering is
        // an npm CLI) selected by the authority at the version conditor.json
        // already pins is governed, not refused; a version change is still
        // refused because Conditor has no upgrade contract for it.
        let packageLifecycleCase (label: string) (selectedVersion: string) =
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
      "distribution": { "mechanism": "npm", "package": "@echelon-foundry/visual-engineering", "url": "https://registry.npmjs.org/@echelon-foundry/visual-engineering/-/visual-engineering-__VE_VERSION__.tgz" },
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

            match Manifest.load packageManifestPath with
            | Error _ -> Error [ "package lifecycle fixture does not parse" ]
            | Ok packageManifest ->
                let probe executable arguments = ProcessRunner.runProcess packageTarget executable arguments
                CurrentUpgrade.preview packageTarget packageManifestPath packageManifest packageResolved packageSha256 ctx probe

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
