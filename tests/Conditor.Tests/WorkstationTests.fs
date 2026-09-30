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

    File.WriteAllText(
        path,
        $"""{
          "schema": "echelon.resolved-release-set/v1",
          "profile": {
            "id": "registry-test",
            "version": "0.1.0",
            "sha256": "{{profileSha}}"
          },
          "platform": "{{rid}}",
          "resolver": {
            "name": "test",
            "version": "1.0.0"
          },
          "catalogSnapshot": {
            "sha256": "{{catalogSha}}"
          },
          "components": [
            {
              "systemId": "{{id}}",
              "role": "host-tool",
              "required": true,
              "version": "{{version}}",
              "repository": "example/{{id}}",
              "tag": "v{{version}}",
              "commit": "1111111111111111111111111111111111111111",
              "releaseStage": "stable",
              "lifecycleState": "active",
              "distributionClass": "self-contained-native-cli",
              "executable": "{{id}}",
              "releaseManifest": {
                "schema": "echelon.release/v2",
                "sha256": "{{releaseSha}}"
              },
              "distribution": {
                "mechanism": "github-release",
                "url": "https://github.com/example/{{id}}/releases/tag/v{{version}}"
              },
              "artifacts": [
                {
                  "name": "{{assetName}}",
                  "purpose": "executable",
                  "platform": "{{rid}}",
                  "sha256": "{{assetSha}}"
                }
              ]
            }
          ]
        }""")

    path, fileSha256 path

let private setup () =
    let home = temp "home"
    let mirror = temp "mirror"
    let alpha = bundle mirror "alpha" "1.0.0" (Platform.runtimeIdentifier ())
    let beta = bundle mirror "beta" "2.0.0" (Platform.runtimeIdentifier ())
    let profile = profileFile mirror [ "alpha", "1.0.0", fst alpha, snd alpha; "beta", "2.0.0", fst beta, snd beta ]
    home, mirror, profile

let private context home mirror praxis : WorkstationContext =
    { Home = home; ArtifactMirror = Some mirror; Praxis = praxis; TargetId = Some "ws-test" }

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

    let wrongPlatformText =
        File.ReadAllText(registrySet)
            .Replace($"\"platform\": \"{Platform.runtimeIdentifier ()}\"", "\"platform\": \"unsupported-x64\"")

    let wrongPlatformPath = Path.Combine(registryMirror, "wrong-platform.resolved.json")
    File.WriteAllText(wrongPlatformPath, wrongPlatformText)

    check
        "Registry resolved set refuses a different target platform"
        (ResolvedReleaseSets.loadFile (Platform.runtimeIdentifier ()) wrongPlatformPath (fileSha256 wrongPlatformPath) |> Result.isError)

    let projectBindingText =
        File.ReadAllText(registrySet)
            .Replace("\"role\": \"host-tool\"", "\"role\": \"project-binding\"")
            .Replace("\"distributionClass\": \"self-contained-native-cli\"", "\"distributionClass\": \"nuget-library\"")

    let projectBindingPath = Path.Combine(registryMirror, "project-binding.resolved.json")
    File.WriteAllText(projectBindingPath, projectBindingText)

    check
        "Registry workstation adapter refuses project-bound libraries instead of guessing an install target"
        (ResolvedReleaseSets.loadFile (Platform.runtimeIdentifier ()) projectBindingPath (fileSha256 projectBindingPath) |> Result.isError)

    let revokedText =
        File.ReadAllText(registrySet)
            .Replace("\"lifecycleState\": \"active\"", "\"lifecycleState\": \"security-revoked\"")

    let revokedPath = Path.Combine(registryMirror, "revoked.resolved.json")
    File.WriteAllText(revokedPath, revokedText)

    check
        "Registry workstation adapter refuses a security-revoked release"
        (ResolvedReleaseSets.loadFile (Platform.runtimeIdentifier ()) revokedPath (fileSha256 revokedPath) |> Result.isError)

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
