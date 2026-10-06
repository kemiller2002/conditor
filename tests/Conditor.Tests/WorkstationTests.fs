/// Workstation bootstrap: profiles, plan-before-authorization, receipts,
/// idempotency, ownership, uninstall, rollback, reconciliation and
/// registration-after-receipt. Uses sacrificial homes and locally built
/// release bundles; nothing touches the real user home.
module WorkstationTests

open System
open System.Formats.Tar
open System.IO
open System.IO.Compression
open System.Security.Cryptography
open Conditor.Core
open Conditor.Core.Workstation

let private temp label =
    let dir = Path.Combine(Path.GetTempPath(), $"conditor-ws-{label}-{Guid.NewGuid():N}")
    Directory.CreateDirectory dir |> ignore
    dir

let private probe exe args = ProcessRunner.runProcess (Path.GetTempPath()) exe args

/// A release bundle `<id>-<rid>.tar.gz` whose executable reports `version`.
let private bundle (mirror: string) (id: string) (version: string) (rid: string) =
    let staging = temp "bundle"
    let root = Path.Combine(staging, $"{id}-{rid}")
    Directory.CreateDirectory root |> ignore
    let exe = Path.Combine(root, id)
    File.WriteAllText(exe, $"#!/bin/sh\necho \"{id} {version}\"\n")
    File.SetUnixFileMode(exe, UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute)
    let name = $"{id}-{rid}.tar.gz"
    let archive = Path.Combine(mirror, name)

    do
        use file = File.Create archive
        use gzip = new GZipStream(file, CompressionLevel.Fastest)
        TarFile.CreateFromDirectory(staging, gzip, false)

    use stream = File.OpenRead archive
    name, Convert.ToHexString(SHA256.HashData stream).ToLowerInvariant()

let private profileFile (dir: string) (components: (string * string * string * string) list) =
    let rid = Platform.runtimeIdentifier ()

    let entries =
        components
        |> List.map (fun (id, version, name, sha) ->
            $$"""{ "id": "{{id}}", "version": "{{version}}", "executable": "{{id}}", "versionProbe": ["--version"],
                  "source": { "kind": "github-release", "repository": "example/{{id}}", "tag": "v{{version}}" },
                  "assets": { "{{rid}}": { "name": "{{name}}", "sha256": "{{sha}}" } } }""")
        |> String.concat ","

    let path = Path.Combine(dir, "test.profile.json")
    File.WriteAllText(path, $$"""{ "schema": "conditor.workstation-profile/v1", "id": "test", "version": 1, "extends": "minimal", "components": [ {{entries}} ] }""")
    path

let private fileSha256 path =
    use stream = File.OpenRead path
    Convert.ToHexString(SHA256.HashData stream).ToLowerInvariant()

let private resolvedSetFile (dir: string) (id: string) (version: string) (assetName: string) (assetSha: string) =
    let rid = Platform.runtimeIdentifier ()
    let path = Path.Combine(dir, $"{id}.resolved.json")
    let profileSha = String.replicate 64 "a"
    let catalogSha = String.replicate 64 "b"
    let releaseSha = String.replicate 64 "c"

    let template =
        """{
          "schema": "echelon.resolved-release-set/v1",
          "profile": {
            "id": "registry-test",
            "version": "0.1.0",
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
              "role": "host-tool",
              "required": true,
              "version": "__VERSION__",
              "repository": "example/__ID__",
              "tag": "v__VERSION__",
              "commit": "1111111111111111111111111111111111111111",
              "releaseStage": "stable",
              "lifecycleState": "active",
              "distributionClass": "self-contained-native-cli",
              "executable": "__ID__",
              "releaseManifest": {
                "schema": "echelon.release/v2",
                "sha256": "__RELEASE_SHA__"
              },
              "distribution": {
                "mechanism": "github-release",
                "url": "https://github.com/example/__ID__/releases/tag/v__VERSION__"
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
        }"""

    let json =
        template
            .Replace("__PROFILE_SHA__", profileSha)
            .Replace("__CATALOG_SHA__", catalogSha)
            .Replace("__RELEASE_SHA__", releaseSha)
            .Replace("__RID__", rid)
            .Replace("__ID__", id)
            .Replace("__VERSION__", version)
            .Replace("__ASSET_NAME__", assetName)
            .Replace("__ASSET_SHA__", assetSha)

    File.WriteAllText(path, json)
    path, fileSha256 path

let private setup () =
    let home = temp "home"
    let mirror = temp "mirror"
    let alpha = bundle mirror "alpha" "1.0.0" (Platform.runtimeIdentifier ())
    let beta = bundle mirror "beta" "2.0.0" (Platform.runtimeIdentifier ())
    let profile = profileFile mirror [ "alpha", "1.0.0", fst alpha, snd alpha; "beta", "2.0.0", fst beta, snd beta ]
    home, mirror, profile

let private context home mirror praxis : WorkstationContext =
    { Home = home; ArtifactMirror = Some mirror; Offline = false; Praxis = praxis; TargetId = Some "ws-test" }

let private planFor ctx profilePath =
    match Profiles.resolve profilePath with
    | Error e -> failwith e
    | Ok profile -> Engine.plan ctx profile (Platform.runtimeIdentifier ()) (Engine.discover probe profile.Prerequisites) []

let private applyOk ctx plan =
    match Engine.apply ctx probe plan plan.Digest true with
    | Ok r -> r
    | Error e -> failwith e

let private fakePraxis (dir: string) =
    let log = Path.Combine(dir, "praxis-calls.log")
    let script = Path.Combine(dir, "praxis")
    File.WriteAllText(script, $"#!/bin/sh\necho \"$@\" >> \"{log}\"\nprintf '%%s' '{{\"outcome\":\"recorded\"}}'\n")
    File.SetUnixFileMode(script, UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute)
    script, log

let run (check: string -> bool -> unit) =
    // Profiles --------------------------------------------------------------
    match Profiles.resolve "echelon-engineering" with
    | Error e -> check $"embedded echelon-engineering profile resolves: {e}" false
    | Ok p ->
        check "echelon-engineering composes minimal's git prerequisite" (p.Prerequisites |> List.exists (fun x -> x.Id = "git" && x.Required))
        check "echelon-engineering pins praxis 3.6.0 and ordo 1.4.0 native releases" (p.Components |> List.map (fun c -> c.Id, c.Version) = [ "praxis", "3.6.0"; "ordo", "1.4.0" ])
        check "every pinned asset carries a sha256" (p.Components |> List.forall (fun c -> c.Assets.Count = 6 && c.Assets |> Map.forall (fun _ a -> a.Sha256.Length = 64)))
        check "optional capabilities are not installed unless selected" (p.Optional |> List.exists (fun (id, _) -> id = "forma"))

    check "minimal profile installs nothing" (Profiles.resolve "minimal" |> Result.map (fun p -> p.Components.IsEmpty) = Ok true)

    // Registry resolved release sets ----------------------------------------
    let registryHome = temp "registry-home"
    let registryMirror = temp "registry-mirror"
    let registryAsset = bundle registryMirror "gamma" "3.0.0" (Platform.runtimeIdentifier ())
    let registrySet, registrySetSha = resolvedSetFile registryMirror "gamma" "3.0.0" (fst registryAsset) (snd registryAsset)

    check
        "Registry resolved set refuses the wrong digest"
        (ResolvedReleaseSets.loadFile (Platform.runtimeIdentifier ()) registrySet (String.replicate 64 "0") |> Result.isError)

    let registryProfile =
        match ResolvedReleaseSets.loadFile (Platform.runtimeIdentifier ()) registrySet registrySetSha with
        | Error e -> failwith e
        | Ok profile -> profile

    check "Registry semantic profile version is preserved" (registryProfile.Version = "0.1.0")
    check "Registry source identity is preserved for plan authorization" (registryProfile.SourceIdentity |> Option.exists (fun s -> s.Contains($"resolved-set=sha256:{registrySetSha}")))
    check "Registry native component becomes an exact workstation component" (registryProfile.Components |> List.map (fun x -> x.Id, x.Version) = [ "gamma", "3.0.0" ])

    let registryCtx = context registryHome registryMirror None
    let registryPlan = Engine.plan registryCtx registryProfile (Platform.runtimeIdentifier ()) [] []
    check "Registry plan has no refusals for the supported native release" registryPlan.Refusals.IsEmpty
    check "Registry plan carries the exact selected artifact digest" (registryPlan.Steps |> List.exists (fun s -> s.Artifact |> Option.exists (fun (_, digest) -> digest = "sha256:" + snd registryAsset)))
    let registryApplied = applyOk registryCtx registryPlan
    check "Registry-resolved native release installs through the normal workstation engine" registryApplied.Failed.IsNone
    check "Registry-resolved shim reports its selected version" ((probe (Path.Combine(registryHome, ".local", "bin", "gamma")) [ "--version" ]).StandardOutput.Contains "3.0.0")

    let rawHome = temp "registry-raw-home"
    let rawName = $"rawtool-{Platform.runtimeIdentifier ()}"
    let rawPath = Path.Combine(registryMirror, rawName)
    File.WriteAllText(rawPath, "#!/bin/sh\necho \"rawtool 4.0.0\"\n")
    File.SetUnixFileMode(rawPath, UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute)
    let rawSet, rawSetSha = resolvedSetFile registryMirror "rawtool" "4.0.0" rawName (fileSha256 rawPath)
    let rawProfile =
        match ResolvedReleaseSets.loadFile (Platform.runtimeIdentifier ()) rawSet rawSetSha with
        | Error e -> failwith e
        | Ok profile -> profile
    let rawCtx = context rawHome registryMirror None
    let rawPlan = Engine.plan rawCtx rawProfile (Platform.runtimeIdentifier ()) [] []
    let rawApplied = applyOk rawCtx rawPlan
    check "raw self-contained native artifact installs through workstation engine" rawApplied.Failed.IsNone
    check
        "raw native artifact shim reports its selected version"
        ((probe (Path.Combine(rawHome, ".local", "bin", "rawtool")) [ "--version" ]).StandardOutput.Contains "4.0.0")

    let offlineMissingHome = temp "registry-offline-missing-home"
    let offlineEmptyMirror = temp "registry-offline-empty-mirror"
    let offlineMissingCtx =
        { context offlineMissingHome offlineEmptyMirror None with Offline = true }
    let offlineMissingPlan =
        Engine.plan offlineMissingCtx registryProfile (Platform.runtimeIdentifier ()) [] []

    match Engine.apply offlineMissingCtx probe offlineMissingPlan offlineMissingPlan.Digest true with
    | Error error ->
        check "offline workstation refuses network fallback when mirror artifact is absent" (error.Contains "Offline workstation mode")
    | Ok result ->
        check
            "offline workstation refuses network fallback when mirror artifact is absent"
            (result.Failed |> Option.exists (fun (_, detail) -> detail.Contains "Offline workstation mode"))

    let wrongPlatformText =
        File.ReadAllText(registrySet)
            .Replace($"\"platform\": \"{Platform.runtimeIdentifier ()}\"", "\"platform\": \"unsupported-x64\"")

    let wrongPlatformPath = Path.Combine(registryMirror, "wrong-platform.resolved.json")
    File.WriteAllText(wrongPlatformPath, wrongPlatformText)

    check
        "Registry resolved set refuses a different target platform"
        (ResolvedReleaseSets.loadFile (Platform.runtimeIdentifier ()) wrongPlatformPath (fileSha256 wrongPlatformPath) |> Result.isError)

    let lifecycleText =
        File.ReadAllText(registrySet)
            .Replace("\"role\": \"host-tool\"", "\"role\": \"repository-lifecycle\"")

    let lifecyclePath = Path.Combine(registryMirror, "repository-lifecycle.resolved.json")
    File.WriteAllText(lifecyclePath, lifecycleText)

    let lifecycleResult =
        ResolvedReleaseSets.loadFile (Platform.runtimeIdentifier ()) lifecyclePath (fileSha256 lifecyclePath)
        |> Result.map (fun profile -> profile.Components |> List.map (fun item -> item.Id))

    check
        "Registry workstation adapter accepts native repository-lifecycle tools"
        (lifecycleResult = Ok [ "gamma" ])

    let projectBindingText =
        File.ReadAllText(registrySet)
            .Replace("\"role\": \"host-tool\"", "\"role\": \"project-binding\"")
            .Replace("\"distributionClass\": \"self-contained-native-cli\"", "\"distributionClass\": \"nuget-library\"")

    let projectBindingPath = Path.Combine(registryMirror, "project-binding.resolved.json")
    File.WriteAllText(projectBindingPath, projectBindingText)

    check
        "Registry workstation adapter refuses project-bound-only sets instead of guessing an install target"
        (ResolvedReleaseSets.loadFile (Platform.runtimeIdentifier ()) projectBindingPath (fileSha256 projectBindingPath) |> Result.isError)

    let mixedText =
        File.ReadAllText(registrySet)
            .Replace(
                "          ]\n        }",
                """          ,
            {
              "systemId": "delta",
              "role": "project-binding",
              "required": true,
              "version": "1.0.0",
              "repository": "example/delta",
              "tag": "nuget:Delta@1.0.0",
              "commit": "2222222222222222222222222222222222222222",
              "releaseStage": "stable",
              "lifecycleState": "active",
              "distributionClass": "nuget-library",
              "executable": null,
              "releaseManifest": {
                "schema": "echelon.release/v2",
                "sha256": "dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd"
              },
              "distribution": {
                "mechanism": "nuget",
                "package": "Delta",
                "url": "https://example.invalid/delta.1.0.0.nupkg"
              },
              "artifacts": [
                {
                  "name": "Delta.1.0.0.nupkg",
                  "purpose": "package",
                  "platform": null,
                  "sha256": "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee"
                }
              ]
            }
          ]
        }"""
            )

    let mixedPath = Path.Combine(registryMirror, "mixed-environment.resolved.json")
    File.WriteAllText(mixedPath, mixedText)

    let mixedResult =
        ResolvedReleaseSets.loadFile (Platform.runtimeIdentifier ()) mixedPath (fileSha256 mixedPath)
        |> Result.map (fun profile -> profile.Components |> List.map (fun item -> item.Id))

    check
        "Registry workstation adapter consumes native subset from full environment set"
        (mixedResult = Ok [ "gamma" ])

    // A package-distributed repository lifecycle tool (an npm CLI such as
    // Visual Engineering, or a packed archive on a GitHub release) is part of
    // the governed environment set but never a native workstation install.
    let packageLifecycleComponent (mechanism: string) (distributionClass: string) (lifecycleState: string) (extra: string) (artifacts: string) =
        $$"""          ,
            {
              "systemId": "epsilon",
              "role": "repository-lifecycle",
              "required": true,
              "version": "1.0.0",
              "repository": "example/epsilon",
              "tag": "v1.0.0",
              "commit": "3333333333333333333333333333333333333333",
              "releaseStage": "stable",
              "lifecycleState": "{{lifecycleState}}",
              "distributionClass": "{{distributionClass}}",
              "executable": null,{{extra}}
              "releaseManifest": {
                "schema": "echelon.release/v2",
                "sha256": "dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd"
              },
              "distribution": {
                "mechanism": "{{mechanism}}",
                "package": "@example/epsilon",
                "url": "https://example.invalid/epsilon-1.0.0.tgz"
              },
              "artifacts": [{{artifacts}}]
            }
          ]
        }"""

    let packageArtifact =
        """
                {
                  "name": "example-epsilon-1.0.0.tgz",
                  "purpose": "package",
                  "platform": null,
                  "sha256": "ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff"
                }"""

    let loadWithPackageLifecycle (label: string) (fragment: string) =
        let path = Path.Combine(registryMirror, $"{label}.resolved.json")
        File.WriteAllText(path, File.ReadAllText(registrySet).Replace("          ]\n        }", fragment))
        ResolvedReleaseSets.loadFile (Platform.runtimeIdentifier ()) path (fileSha256 path)
        |> Result.map (fun profile -> profile.Components |> List.map (fun item -> item.Id))

    for mechanism, distributionClass in [ "npm", "repository-lifecycle"; "github-release", "repository-lifecycle"; "npm", "web-package" ] do
        check
            $"Registry workstation adapter keeps a {distributionClass}/{mechanism} lifecycle package out of native installation"
            (loadWithPackageLifecycle $"package-lifecycle-{mechanism}-{distributionClass}" (packageLifecycleComponent mechanism distributionClass "active" "" packageArtifact) = Ok [ "gamma" ])

    check
        "Registry workstation adapter refuses a lifecycle package that declares the native lifecycle contract"
        (loadWithPackageLifecycle
            "package-lifecycle-contract"
            (packageLifecycleComponent "npm" "repository-lifecycle" "active" "\n              \"repositoryLifecycle\": { \"contract\": \"echelon.repository-lifecycle\", \"contractVersion\": 1 }," packageArtifact)
         |> Result.isError)

    check
        "Registry workstation adapter refuses a lifecycle package without a package artifact"
        (loadWithPackageLifecycle "package-lifecycle-no-artifact" (packageLifecycleComponent "npm" "repository-lifecycle" "active" "" "") |> Result.isError)

    check
        "Registry workstation adapter refuses a lifecycle package with an ambiguous package artifact"
        (loadWithPackageLifecycle "package-lifecycle-two-artifacts" (packageLifecycleComponent "npm" "repository-lifecycle" "active" "" (packageArtifact + "," + packageArtifact.Replace("example-epsilon-1.0.0.tgz", "other.tgz"))) |> Result.isError)

    check
        "Registry workstation adapter refuses a security-revoked lifecycle package"
        (loadWithPackageLifecycle "package-lifecycle-revoked" (packageLifecycleComponent "npm" "repository-lifecycle" "security-revoked" "" packageArtifact) |> Result.isError)

    check
        "Registry workstation adapter refuses a lifecycle package on an unsupported mechanism"
        (loadWithPackageLifecycle "package-lifecycle-nuget" (packageLifecycleComponent "nuget" "repository-lifecycle" "active" "" packageArtifact) |> Result.isError)

    check
        "Registry workstation adapter still refuses a lifecycle tool of any other non-native class"
        (loadWithPackageLifecycle "package-lifecycle-nuget-library" (packageLifecycleComponent "nuget" "nuget-library" "active" "" packageArtifact) |> Result.isError)

    let revokedText =
        File.ReadAllText(registrySet)
            .Replace("\"lifecycleState\": \"active\"", "\"lifecycleState\": \"security-revoked\"")

    let revokedPath = Path.Combine(registryMirror, "revoked.resolved.json")
    File.WriteAllText(revokedPath, revokedText)

    check
        "Registry workstation adapter refuses a security-revoked release"
        (ResolvedReleaseSets.loadFile (Platform.runtimeIdentifier ()) revokedPath (fileSha256 revokedPath) |> Result.isError)

    // Offline source mirror ------------------------------------------------
    let sourceMirrorRoot = temp "source-mirror"
    let sourceCacheRoot = temp "source-cache"
    let source =
        { Repository = "example/contracts"
          Commit = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
          Entrypoint = FileArtifact "docs/contract.md" }

    let sourceCheckout = SourceCache.mirrorCheckoutPath sourceMirrorRoot source
    let sourceDocs = Path.Combine(sourceCheckout, "docs")
    Directory.CreateDirectory sourceDocs |> ignore
    let sourcePath = Path.Combine(sourceDocs, "contract.md")
    File.WriteAllText(sourcePath, "frozen contract\n")
    let sourceSha = fileSha256 sourcePath

    File.WriteAllText(
        Path.Combine(sourceCheckout, ".conditor-source.json"),
        $"""{{ 
          "schema": "conditor.source-mirror/v1",
          "repository": "example/contracts",
          "commit": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
          "files": {{
            "docs/contract.md": "{sourceSha}"
          }}
        }}"""
    )

    let priorMirror = Environment.GetEnvironmentVariable "CONDITOR_SOURCE_MIRROR"
    let priorOffline = Environment.GetEnvironmentVariable "CONDITOR_OFFLINE"
    let priorCache = Environment.GetEnvironmentVariable "CONDITOR_CACHE_DIR"

    try
        Environment.SetEnvironmentVariable("CONDITOR_SOURCE_MIRROR", sourceMirrorRoot)
        Environment.SetEnvironmentVariable("CONDITOR_OFFLINE", "1")
        Environment.SetEnvironmentVariable("CONDITOR_CACHE_DIR", sourceCacheRoot)

        let mirroredSource =
            SourceCache.ensure "requirements:test" source
            |> Result.bind (fun checkout -> SourceCache.resolveEntrypoint checkout source)
            |> Result.map File.ReadAllText

        check
            "offline source mirror resolves exact pinned file without GitHub"
            (mirroredSource = Ok "frozen contract\n")

        File.AppendAllText(sourcePath, "tamper")

        check
            "offline source mirror refuses tampered governing input"
            (SourceCache.ensure "requirements:test" source |> Result.isError)
    finally
        Environment.SetEnvironmentVariable("CONDITOR_SOURCE_MIRROR", priorMirror)
        Environment.SetEnvironmentVariable("CONDITOR_OFFLINE", priorOffline)
        Environment.SetEnvironmentVariable("CONDITOR_CACHE_DIR", priorCache)

    // Offline bundle verification ------------------------------------------
    let bundleRoot = temp "offline-bundle"
    let bundleResolved = Path.Combine(bundleRoot, "resolved-set.json")
    let bundleArtifactDirectory = Path.Combine(bundleRoot, "artifacts", "gamma")
    Directory.CreateDirectory bundleArtifactDirectory |> ignore
    let bundleArtifact = Path.Combine(bundleArtifactDirectory, "gamma.bin")
    File.WriteAllText(bundleResolved, "{\"schema\":\"fixture\"}\n")
    File.WriteAllText(bundleArtifact, "gamma-offline-artifact\n")
    let bundleResolvedSha = fileSha256 bundleResolved
    let bundleArtifactSha = fileSha256 bundleArtifact
    File.WriteAllText(
        Path.Combine(bundleRoot, "bundle.json"),
        $"""{{ 
          "schema": "conditor.offline-bundle/v1",
          "profileId": "registry-test",
          "profileVersion": "0.1.0",
          "platform": "{Platform.runtimeIdentifier ()}",
          "resolvedSetSha256": "{bundleResolvedSha}",
          "resolvedSetPath": "resolved-set.json",
          "artifacts": [
            {{
              "systemId": "gamma",
              "version": "3.0.0",
              "role": "host-tool",
              "mechanism": "github-release",
              "purpose": "executable",
              "name": "gamma.bin",
              "sourceUrl": "https://example.invalid/gamma.bin",
              "sha256": "{bundleArtifactSha}",
              "path": "artifacts/gamma/gamma.bin"
            }}
          ]
        }}"""
    )

    let verifiedBundle =
        OfflineBundle.verify bundleRoot
        |> Result.map (fun summary -> summary.ArtifactCount = 1 && summary.ResolvedSetSha256 = bundleResolvedSha)

    check
        "offline bundle verifier accepts exact resolved set and artifact bytes"
        (verifiedBundle = Ok true)

    File.AppendAllText(bundleArtifact, "tamper")
    check
        "offline bundle verifier refuses tampered artifact bytes"
        (OfflineBundle.verify bundleRoot |> Result.isError)

    // Dry run ---------------------------------------------------------------
    let home, mirror, profile = setup ()
    let ctx = context home mirror None
    let plan = planFor ctx profile
    check "workstation plan is a dry run: nothing is created" (Directory.GetFileSystemEntries home |> Array.isEmpty)
    check "the plan digest is deterministic" (plan.Digest = (planFor ctx profile).Digest)
    check "the plan discloses downloads with artifact identity and digest" (plan.Steps |> List.filter (fun s -> s.Operation = StepOperation.Download) |> List.forall (fun s -> s.Artifact.IsSome))
    check "the plan never shows an absolute home path" (plan.Steps |> List.forall (fun s -> not (s.Resource.Contains home)))
    check "apply refuses a digest that is not the disclosed plan" (Result.isError (Engine.apply ctx probe plan "sha256:other" true))

    // Apply, idempotency ----------------------------------------------------
    let first = applyOk ctx plan
    check "first bootstrap completes every step with a matching receipt" (first.Failed.IsNone && first.Completed.Length = 7)
    check "installed shim reports its version" ((probe (Path.Combine(home, ".local", "bin", "alpha")) [ "--version" ]).StandardOutput.Contains "1.0.0")
    let second = applyOk ctx (planFor ctx profile)
    check "second bootstrap is idempotent: every step is reused" (second.Completed.IsEmpty && second.Failed.IsNone)
    let profileText = File.ReadAllText(Path.Combine(home, ".profile"))
    check "no duplicate PATH/profile entries" (profileText.Split(Engine.blockBegin "workstation-path").Length - 1 = 1)

    // Ownership + uninstall -------------------------------------------------
    File.AppendAllText(Path.Combine(home, ".profile"), "# mine\n")
    let uninstall = Engine.uninstallPlan ctx
    check "uninstall plan retains the adopted git prerequisite" (uninstall.Actions |> List.exists (fun (r, _, a) -> r = "git" && a = Engine.UninstallAction.RetainAdopted))
    check "uninstall plan restores the shared shell profile" (uninstall.Actions |> List.exists (fun (r, _, a) -> r = "~/.profile" && a = Engine.UninstallAction.Restore))
    check "uninstall plan is a dry run" (File.Exists(Path.Combine(home, ".local", "bin", "alpha")))
    check "uninstall refuses an unauthorized digest" (Result.isError (Engine.uninstall ctx probe None uninstall "sha256:x"))

    match Engine.uninstall ctx probe None uninstall uninstall.Digest with
    | Error e -> check $"uninstall succeeds: {e}" false
    | Ok(results, _) ->
        check "uninstall removes Conditor-created resources with receipts" (results |> List.forall (fun (_, _, o) -> o = "match"))
        check "uninstall removed the shim" (not (File.Exists(Path.Combine(home, ".local", "bin", "alpha"))))
        check "uninstall kept the user's own profile content" ((File.ReadAllText(Path.Combine(home, ".profile"))).Contains "# mine")
        check "uninstall removed the managed block" (not ((File.ReadAllText(Path.Combine(home, ".profile"))).Contains "conditor:workstation-path"))
        check "a second uninstall plan has nothing left to remove" ((Engine.uninstallPlan ctx).Actions |> List.forall (fun (_, _, a) -> a <> Engine.UninstallAction.Remove))

    // User-owned resources are never overwritten ----------------------------
    let home2, mirror2, profile2 = setup ()
    let ctx2 = context home2 mirror2 None
    let userShim = Path.Combine(home2, ".local", "bin", "beta")
    Directory.CreateDirectory(Path.Combine(home2, ".local", "bin")) |> ignore
    File.WriteAllText(userShim, "#!/bin/sh\necho mine\n")
    let refused = applyOk ctx2 (planFor ctx2 profile2)
    check "a pre-existing user-owned resource is not overwritten" (refused.Failed |> Option.exists (fun (s, _) -> s = "beta-shim") && File.ReadAllText userShim = "#!/bin/sh\necho mine\n")
    check "partial failure rolls back this run's Conditor-created effects" (refused.RolledBack |> List.exists (fun (s, o) -> s = "alpha-shim" && o = "match") && not (File.Exists(Path.Combine(home2, ".local", "bin", "alpha"))))
    check "rollback leaves the user-owned resource in place" (File.Exists userShim)

    // Partial failure: a corrupt artifact -----------------------------------
    let home3, mirror3, profile3 = setup ()
    File.WriteAllText(Path.Combine(mirror3, $"beta-{Platform.runtimeIdentifier ()}.tar.gz"), "tampered")
    let praxis, log = fakePraxis mirror3
    let ctx3 = context home3 mirror3 (Some praxis)
    let failed = applyOk ctx3 (planFor ctx3 profile3)
    check "a digest mismatch stops the bootstrap" (failed.Failed |> Option.exists (fun (s, _) -> s = "beta-download"))
    check "rollback removed the earlier component" (not (Directory.Exists(Path.Combine(home3, ".local", "share", "echelon", "alpha", "1.0.0"))) && not (File.Exists(Path.Combine(home3, ".local", "bin", "alpha"))))
    let ledger = File.ReadAllText(Path.Combine(home3, ".conditor", "workstation", "ledger.jsonl"))
    check "rollback produces receipts" (ledger.Contains "\"entry\":\"rolled-back\"")
    let calls = if File.Exists log then File.ReadAllText log else ""
    check "registration only after a matching install receipt" (calls.Contains "--system alpha" && not (calls.Contains "--system beta"))
    check "registration carries the environment target and artifact digest" (calls.Contains "--target-id ws-test" && calls.Contains "--digest sha256:")

    // Unknown effect reconciliation -----------------------------------------
    let home4, mirror4, profile4 = setup ()
    let ctx4 = context home4 mirror4 None
    let plan4 = planFor ctx4 profile4
    Ledger.append ctx4 [ "entry", "started"; "step", "alpha-extract" ]
    check "an unknown non-idempotent effect refuses retry" (Result.isError (Engine.apply ctx4 probe plan4 plan4.Digest true))
    check "reconciliation observes; it never assumes" (Engine.reconcile ctx4 probe plan4 "alpha-extract" = Ok "did-not-occur")
    let resumed = applyOk ctx4 (planFor ctx4 profile4)
    check "after reconciliation the bootstrap resumes" (resumed.Failed.IsNone)
    check "reconciliation refuses a step without an unknown effect" (Result.isError (Engine.reconcile ctx4 probe plan4 "alpha-extract"))
