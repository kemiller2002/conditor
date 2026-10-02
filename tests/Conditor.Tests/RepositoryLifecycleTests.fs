/// Generic repository lifecycle (echelon.repository-lifecycle v1) consumed
/// from Registry resolved release sets. Every component here is synthetic so
/// the tests prove the abstraction, not one product: identities, executable
/// names and versions are deliberately unlike any real Echelon system.
module RepositoryLifecycleTests

open System
open System.Formats.Tar
open System.IO
open System.IO.Compression
open System.Security.Cryptography
open System.Text.Json.Nodes
open Conditor.Core
open Conditor.Core.Workstation

let private temp label =
    let dir = Path.Combine(Path.GetTempPath(), $"conditor-lc-{label}-{Guid.NewGuid():N}")
    Directory.CreateDirectory dir |> ignore
    dir

let private probe exe args = ProcessRunner.runProcess (Path.GetTempPath()) exe args

let private fileSha256 path =
    use stream = File.OpenRead path
    Convert.ToHexString(SHA256.HashData stream).ToLowerInvariant()

let private rid = Platform.runtimeIdentifier ()

type private Identity =
    { SystemId: string
      Repository: string
      Executable: string
      Version: string
      Commit: string }

let private fixture =
    { SystemId = "lifecycle-fixture"
      Repository = "example/lifecycle-fixture"
      Executable = "lc-tool"
      Version = "7.3.1"
      Commit = String.replicate 40 "c" }

let private versionJson (identity: Identity) =
    let o = JsonObject()
    o["SystemId"] <- JsonValue.Create identity.SystemId
    o["Repository"] <- JsonValue.Create identity.Repository
    o["Executable"] <- JsonValue.Create identity.Executable
    o["ReleaseVersion"] <- JsonValue.Create identity.Version
    o["SourceCommit"] <- JsonValue.Create identity.Commit
    o.ToJsonString()

/// A contract-conforming executable. It owns `.lc/state` in the repository
/// and logs every invocation so tests can see exactly what Conditor ran.
let private conformingScript (identity: Identity) (log: string) =
    String.concat
        "\n"
        [ "#!/bin/sh"
          $"echo \"$*\" >> \"{log}\""
          "op=\"$1\"; root=\"$3\""
          "case \"$op\" in"
          "  version) echo '" + versionJson identity + "' ;;"
          "  init) [ \"$2\" = \"--root\" ] || { echo '{\"error\":\"invalid\"}'; exit 2; }"
          "        if [ -f \"$root/.lc/state\" ]; then echo '{\"changed\":false}'; else mkdir -p \"$root/.lc\" && echo owned > \"$root/.lc/state\" && echo '{\"changed\":true}'; fi ;;"
          "  upgrade) [ -f \"$root/.lc/state\" ] || { echo '{\"changed\":false,\"reason\":\"not-installed\"}'; exit 3; }; echo '{\"changed\":false}' ;;"
          "  status|verify|doctor) if [ -f \"$root/.lc/state\" ]; then echo '{\"healthy\":true}'; else echo '{\"healthy\":false}'; exit 3; fi ;;"
          "  *) echo '{\"error\":\"invalid\"}'; exit 2 ;;"
          "esac"
          "" ]

/// `<name>.tar.gz` holding the executable at the archive root.
let private bundle (mirror: string) (identity: Identity) (log: string) =
    let staging = temp "bundle"
    let exe = Path.Combine(staging, identity.Executable)
    File.WriteAllText(exe, conformingScript identity log)
    File.SetUnixFileMode(exe, UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute)
    let name = $"{identity.Executable}-{rid}.tar.gz"
    let archive = Path.Combine(mirror, name)

    do
        use file = File.Create archive
        use gzip = new GZipStream(file, CompressionLevel.Fastest)
        TarFile.CreateFromDirectory(staging, gzip, false)

    name, fileSha256 archive

let private artifact (name: string) (purpose: string) (platform: string option) (sha: string) =
    let a = JsonObject()
    a["name"] <- JsonValue.Create name
    a["purpose"] <- JsonValue.Create purpose
    match platform with
    | Some p -> a["platform"] <- JsonValue.Create p
    | None -> a["platform"] <- (null: JsonNode | null)

    a["sha256"] <- JsonValue.Create sha
    a

let private lifecycleDeclaration (contract: string) (version: int) =
    let l = JsonObject()
    l["contract"] <- JsonValue.Create contract
    l["contractVersion"] <- JsonValue.Create version
    l

/// A resolved component in the exact shape the Registry resolver emits.
let private nativeComponent (identity: Identity) (role: string) (lifecycle: JsonObject option) (artifacts: JsonObject list) =
    let c = JsonObject()
    c["systemId"] <- JsonValue.Create identity.SystemId
    c["role"] <- JsonValue.Create role
    c["required"] <- JsonValue.Create true
    c["version"] <- JsonValue.Create identity.Version
    c["repository"] <- JsonValue.Create identity.Repository
    c["tag"] <- JsonValue.Create $"{identity.SystemId}-v{identity.Version}"
    c["commit"] <- JsonValue.Create identity.Commit
    c["releaseStage"] <- JsonValue.Create "stable"
    c["lifecycleState"] <- JsonValue.Create "active"
    c["distributionClass"] <- JsonValue.Create "self-contained-native-cli"
    c["executable"] <- JsonValue.Create identity.Executable
    lifecycle |> Option.iter (fun l -> c["repositoryLifecycle"] <- l.DeepClone())
    let manifest = JsonObject()
    manifest["schema"] <- JsonValue.Create "echelon.release/v2"
    manifest["sha256"] <- JsonValue.Create(String.replicate 64 "d")
    c["releaseManifest"] <- manifest
    let distribution = JsonObject()
    distribution["mechanism"] <- JsonValue.Create "github-release"
    distribution["package"] <- (null: JsonNode | null)
    distribution["url"] <- JsonValue.Create $"https://github.com/{identity.Repository}/releases/tag/{identity.SystemId}-v{identity.Version}"
    c["distribution"] <- distribution
    let list = JsonArray()
    artifacts |> List.iter (fun a -> list.Add(a.DeepClone()))
    c["artifacts"] <- list
    c

let private projectBinding () =
    let c =
        nativeComponent
            { SystemId = "binding-fixture"; Repository = "example/binding-fixture"; Executable = "unused"; Version = "1.0.0"; Commit = String.replicate 40 "e" }
            "project-binding"
            None
            [ artifact "Binding.Fixture.1.0.0.nupkg" "package" None (String.replicate 64 "f") ]

    c["distributionClass"] <- JsonValue.Create "nuget-library"
    c["executable"] <- (null: JsonNode | null)
    let distribution = JsonObject()
    distribution["mechanism"] <- JsonValue.Create "nuget"
    distribution["package"] <- JsonValue.Create "Binding.Fixture"
    c["distribution"] <- distribution
    c

/// Write a resolved release set and return its path and actual SHA-256.
let private resolvedSet (dir: string) (label: string) (platform: string) (components: JsonObject list) =
    let root = JsonObject()
    root["schema"] <- JsonValue.Create "echelon.resolved-release-set/v1"
    let profile = JsonObject()
    profile["id"] <- JsonValue.Create "lifecycle-proof"
    profile["version"] <- JsonValue.Create "0.1.0"
    profile["sha256"] <- JsonValue.Create(String.replicate 64 "a")
    root["profile"] <- profile
    root["platform"] <- JsonValue.Create platform
    let resolver = JsonObject()
    resolver["name"] <- JsonValue.Create "echelon-registry-resolver"
    resolver["version"] <- JsonValue.Create "0.1.0"
    root["resolver"] <- resolver
    let catalog = JsonObject()
    catalog["sha256"] <- JsonValue.Create(String.replicate 64 "b")
    root["catalogSnapshot"] <- catalog
    let list = JsonArray()
    components |> List.iter list.Add
    root["components"] <- list
    let path = Path.Combine(dir, $"{label}.resolved.json")
    File.WriteAllText(path, root.ToJsonString())
    path, fileSha256 path

let private load (path, sha) = ResolvedReleaseSets.loadFile rid path sha

let private loadOk set =
    match load set with
    | Ok profile -> profile
    | Error e -> failwith e

let private context home mirror : WorkstationContext =
    { Home = home; ArtifactMirror = Some mirror; Offline = true; Praxis = None; TargetId = None }

let private install ctx profile =
    let plan = Engine.plan ctx profile rid [] []

    match Engine.apply ctx probe plan plan.Digest true with
    | Ok result -> plan, result
    | Error e -> failwith e

let private repositoryState (root: string) =
    Directory.GetFiles(root, "*", SearchOption.AllDirectories)
    |> Array.map (fun path -> Path.GetRelativePath(root, path), fileSha256 path)
    |> Array.sort
    |> Array.toList

let private conditorSourceRoot () =
    let rec up (dir: DirectoryInfo option) =
        match dir with
        | None -> None
        | Some d when File.Exists(Path.Combine(d.FullName, "Conditor.slnx")) -> Some d.FullName
        | Some d -> up (d.Parent |> Option.ofObj)

    up (Some(DirectoryInfo AppContext.BaseDirectory))

let run (check: string -> bool -> unit) =
    let mirror = temp "mirror"
    let log = Path.Combine(mirror, "invocations.log")
    let assetName, assetSha = bundle mirror fixture log
    let executableArtifact = artifact assetName "executable" (Some rid) assetSha
    let checksums = artifact "checksums.txt" "checksums" None (String.replicate 64 "9")
    let declared = Some(lifecycleDeclaration RepositoryLifecycleContract.Capability 1)

    let set =
        resolvedSet mirror "environment" rid [ nativeComponent fixture "repository-lifecycle" declared [ executableArtifact; checksums ]; projectBinding () ]

    // Registry facts --------------------------------------------------------
    let profile = loadOk set
    let selected = profile.Components |> List.tryExactlyOne

    check "generic lifecycle component is accepted from a valid resolved release set" (selected |> Option.exists (fun c -> c.Lifecycle = Some { ContractVersion = 1; SourceCommit = fixture.Commit }))
    check "project-bound libraries are never turned into native lifecycle components" (profile.Components |> List.forall (fun c -> c.Id <> "binding-fixture"))
    check "exact version comes from Registry resolution" (selected |> Option.exists (fun c -> c.Version = fixture.Version))
    check "executable name comes from Registry, not the system id" (selected |> Option.exists (fun c -> c.Executable = "lc-tool" && c.Id = "lifecycle-fixture"))
    check "platform artifact comes from Registry" (selected |> Option.exists (fun c -> c.Assets |> Map.toList = [ rid, { Name = assetName; Sha256 = assetSha } ]))
    check "lifecycle contract defines the identity probe" (selected |> Option.exists (fun c -> c.VersionProbe = [ "version" ]))
    check "resolved-set identity is bound into the profile source identity" (profile.SourceIdentity |> Option.exists (fun s -> s.Contains $"resolved-set=sha256:{snd set}"))

    // Clean-host installation ------------------------------------------------
    let home = temp "home"
    let ctx = context home mirror
    let firstInstallPlan, firstInstall = install ctx profile

    check "artifact digest is preserved into the authorized install plan" (firstInstallPlan.Steps |> List.exists (fun s -> s.Artifact |> Option.exists (fun (url, digest) -> digest = "sha256:" + assetSha && url.EndsWith assetName)))
    check "installed executable is verified against the full Registry identity" (firstInstallPlan.Steps |> List.exists (fun s -> match s.Expected with Expected.IdentityReports(_, [ "version" ], identity) -> identity |> List.contains ("sourceCommit", fixture.Commit) | _ -> false))
    check "lifecycle component installs through the workstation engine" (firstInstall.Failed.IsNone && not firstInstall.Completed.IsEmpty)

    // Generic lifecycle plan -------------------------------------------------
    let repo = temp "repo"
    File.WriteAllText(Path.Combine(repo, "README.md"), "user-owned content\n")
    let initPlan = Lifecycle.plan ctx profile rid repo LifecycleOperation.Init

    check "lifecycle plan for a conforming component has no refusals" initPlan.Refusals.IsEmpty
    check
        "planner generates generic init then verify contract invocations"
        (initPlan.Steps |> List.map (fun s -> s.Id, s.Arguments) = [ "lifecycle-fixture-init", [ "init"; "--root"; repo ]; "lifecycle-fixture-verify", [ "verify"; "--root"; repo ] ])
    check "planned executable is the installed Registry release" (initPlan.Steps |> List.forall (fun s -> s.Executable.EndsWith $"/lifecycle-fixture/{fixture.Version}/lc-tool"))
    check "lifecycle preconditions revalidate the artifact digest" (initPlan.Preconditions |> List.exists (fun p -> p.Artifact = Expected.FileSha256($"~/.conditor/cache/lifecycle-fixture/{fixture.Version}/{assetName}", assetSha)))

    check "lifecycle apply refuses a digest other than the disclosed plan" (Lifecycle.apply ctx probe initPlan "sha256:0" |> Result.isError)
    File.WriteAllText(log, "")

    let first =
        match Lifecycle.apply ctx probe initPlan initPlan.Digest with
        | Ok r -> r
        | Error e -> failwith e

    check "first lifecycle application succeeds" first.Failed.IsNone
    check "first lifecycle application created component-owned state" (File.Exists(Path.Combine(repo, ".lc", "state")))
    check "first init reports a change" (first.Results |> List.exists (fun r -> r.Step.Id = "lifecycle-fixture-init" && r.StandardOutput.Contains "\"changed\":true"))
    check "only disclosed invocations ran (plus the read-only identity probe)" (File.ReadAllLines log |> Array.filter (fun l -> l <> "version") = [| $"init --root {repo}"; $"verify --root {repo}" |])

    let afterFirst = repositoryState repo

    // Second application of the same desired state --------------------------
    let secondInstallPlan, secondInstall = install ctx profile
    check "second install plan is identical" (secondInstallPlan.Digest = firstInstallPlan.Digest)
    check "second install reuses every receipt and changes nothing" (secondInstall.Failed.IsNone && secondInstall.Completed.IsEmpty && secondInstall.Reused.Length = firstInstallPlan.Steps.Length)

    let secondPlan = Lifecycle.plan ctx profile rid repo LifecycleOperation.Init
    check "second lifecycle plan is deterministic" (secondPlan.Digest = initPlan.Digest)

    let second =
        match Lifecycle.apply ctx probe secondPlan secondPlan.Digest with
        | Ok r -> r
        | Error e -> failwith e

    check "second application is idempotent when the component reports desired state" (second.Failed.IsNone && second.Results |> List.exists (fun r -> r.Step.Id = "lifecycle-fixture-init" && r.StandardOutput.Contains "\"changed\":false"))
    check "second application leaves the repository byte-identical" (repositoryState repo = afterFirst)

    for operation in [ LifecycleOperation.Status; LifecycleOperation.Verify; LifecycleOperation.Doctor; LifecycleOperation.Upgrade ] do
        let p = Lifecycle.plan ctx profile rid repo operation
        let wire = LifecycleOperation.toWire operation
        check $"generic {wire} plan invokes `{wire} --root`" (p.Steps |> List.exists (fun s -> s.Arguments = [ wire; "--root"; repo ]))
        check $"generic {wire} succeeds on the initialized repository" (Lifecycle.apply ctx probe p p.Digest |> Result.map _.Failed.IsNone = Ok true)

    check "inspection and upgrade left the repository byte-identical" (repositoryState repo = afterFirst)

    let uninitialized = temp "uninitialized"
    let verifyUninitialized = Lifecycle.plan ctx profile rid uninitialized LifecycleOperation.Verify
    check "a non-zero contract exit is a failed lifecycle step" (Lifecycle.apply ctx probe verifyUninitialized verifyUninitialized.Digest |> Result.map (fun r -> r.Failed |> Option.map fst) = Ok(Some "lifecycle-fixture-verify"))
    check "a relative repository root is refused" (not (Lifecycle.plan ctx profile rid "relative/repo" LifecycleOperation.Init).Refusals.IsEmpty)

    // Revalidation before repository effects --------------------------------
    let cached = Path.Combine(home, ".conditor", "cache", "lifecycle-fixture", fixture.Version, assetName)
    File.AppendAllText(cached, "tampered")
    check "lifecycle refuses to run when the cached artifact no longer matches its Registry digest" (Lifecycle.apply ctx probe initPlan initPlan.Digest |> Result.isError)

    let notInstalled = Lifecycle.plan (context (temp "empty-home") mirror) profile rid repo LifecycleOperation.Init
    check "lifecycle refuses to run before the Registry release is installed" (Lifecycle.apply (context (temp "empty-home") mirror) probe notInstalled notInstalled.Digest |> Result.isError)

    // Refusals ---------------------------------------------------------------
    let refused label (reason: string) components =
        match load (resolvedSet mirror label rid components) with
        | Error e -> e.Contains reason
        | Ok _ -> false

    check "wrong platform is rejected" (load (resolvedSet mirror "wrong-platform" "unsupported-x64" [ nativeComponent fixture "repository-lifecycle" declared [ executableArtifact ] ]) |> Result.isError)
    check "resolved-set digest mismatch is rejected" (load (fst set, String.replicate 64 "0") |> Result.isError)

    let inactive = nativeComponent fixture "repository-lifecycle" declared [ executableArtifact ]
    inactive["lifecycleState"] <- JsonValue.Create "deprecated"
    check "inactive release is rejected" (refused "inactive" "is deprecated" [ inactive ])

    let missingExecutable = nativeComponent fixture "repository-lifecycle" declared [ executableArtifact ]
    missingExecutable["executable"] <- (null: JsonNode | null)
    check "missing executable is rejected" (refused "missing-executable" "has no executable" [ missingExecutable ])

    check "missing platform artifact is rejected" (refused "missing-artifact" "no digest-verified executable artifact" [ nativeComponent fixture "repository-lifecycle" declared [ checksums ] ])
    check "ambiguous executable artifacts are rejected" (refused "ambiguous" "selection is ambiguous" [ nativeComponent fixture "repository-lifecycle" declared [ executableArtifact; artifact "other.tar.gz" "executable" (Some rid) assetSha ] ])
    check "unsupported lifecycle contract version is rejected" (refused "contract-v2" "unsupported repository lifecycle contract version 2" [ nativeComponent fixture "repository-lifecycle" (Some(lifecycleDeclaration RepositoryLifecycleContract.Capability 2)) [ executableArtifact ] ])
    check "unknown lifecycle contract is rejected" (refused "unknown-contract" "unknown repository lifecycle contract" [ nativeComponent fixture "repository-lifecycle" (Some(lifecycleDeclaration "example.other-lifecycle" 1)) [ executableArtifact ] ])

    let undeclared = loadOk (resolvedSet mirror "undeclared" rid [ nativeComponent fixture "repository-lifecycle" None [ executableArtifact ] ])
    check "a lifecycle-role component without a declared contract still installs natively" (undeclared.Components |> List.exists (fun c -> c.Lifecycle.IsNone && c.VersionProbe = [ "--version" ]))
    check "unknown lifecycle semantics are refused by the generic planner" (Lifecycle.plan ctx undeclared rid repo LifecycleOperation.Init |> fun p -> p.Steps.IsEmpty && not p.Refusals.IsEmpty)

    let hostTool = loadOk (resolvedSet mirror "host-tool" rid [ nativeComponent fixture "host-tool" declared [ executableArtifact ] ])
    check "a host-tool role never receives repository lifecycle actions" (Lifecycle.plan ctx hostTool rid repo LifecycleOperation.Init |> fun p -> p.Steps.IsEmpty && not p.Refusals.IsEmpty)

    let wrongDigest = loadOk (resolvedSet mirror "wrong-artifact-digest" rid [ nativeComponent fixture "repository-lifecycle" declared [ artifact assetName "executable" (Some rid) (String.replicate 64 "7") ] ])
    let _, wrongDigestInstall = install (context (temp "wrong-digest-home") mirror) wrongDigest
    check "artifact digest mismatch stops installation" (wrongDigestInstall.Failed |> Option.exists (fun (step, _) -> step = "lifecycle-fixture-download"))

    let impostorMirror = temp "impostor-mirror"
    let impostorName, impostorSha = bundle impostorMirror { fixture with Commit = String.replicate 40 "0" } (Path.Combine(impostorMirror, "log"))
    let impostor = loadOk (resolvedSet impostorMirror "impostor" rid [ nativeComponent fixture "repository-lifecycle" declared [ artifact impostorName "executable" (Some rid) impostorSha ] ])
    let _, impostorInstall = install (context (temp "impostor-home") impostorMirror) impostor
    check "an executable reporting a different source commit fails its identity receipt" (impostorInstall.Failed |> Option.exists (fun (step, detail) -> step = "lifecycle-fixture-shim" && detail.Contains "sourceCommit"))

    // No system-specific lifecycle logic --------------------------------------
    match conditorSourceRoot () with
    | None -> check "Conditor source tree is locatable for the no-special-case scan" false
    | Some root ->
        let mentions =
            Directory.GetFiles(Path.Combine(root, "src"), "*.fs", SearchOption.AllDirectories)
            |> Array.filter (fun path -> not (path.Contains $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            |> Array.filter (fun path -> File.ReadAllText(path).Contains("dokimos", StringComparison.OrdinalIgnoreCase))

        check "no Conditor source branches on, or names, the dokimos system" (mentions.Length = 0)
