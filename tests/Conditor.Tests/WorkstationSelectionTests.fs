/// Workstation host-tool selection (CON-360..363, DF-CON-2026-A005, WI-0040).
/// Conditor 0.10.1's `workstation plan --profile echelon-engineering` planned
/// the embedded profile's frozen pins (Praxis 3.6.0, Ordo 1.4.0), below the
/// shared workstation's Praxis 3.11.0 and Ordo 1.5.0. Applying it would have
/// downgraded the host tools. Every test uses a sacrificial home and locally
/// built release bundles; nothing touches the real user home.
module WorkstationSelectionTests

open System
open System.Formats.Tar
open System.IO
open System.IO.Compression
open System.Security.Cryptography
open Conditor.Core
open Conditor.Core.Workstation

let private joined (values: string list) = String.concat "; " values

let private temp label =
    let dir = Path.Combine(Path.GetTempPath(), $"conditor-wsel-{label}-{Guid.NewGuid():N}")
    Directory.CreateDirectory dir |> ignore
    dir

let private probe exe args = ProcessRunner.runProcess (Path.GetTempPath()) exe args

let private sha256File (path: string) =
    Convert.ToHexString(SHA256.HashData(File.ReadAllBytes path)).ToLowerInvariant()

let private executableScript (path: string) (id: string) (version: string) =
    File.WriteAllText(path, $"#!/bin/sh\necho \"{id} {version}\"\n")
    File.SetUnixFileMode(path, UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute)

/// A release bundle `<id>-<rid>.tar.gz` whose executable reports `version`.
let private bundle (mirror: string) (id: string) (version: string) (rid: string) =
    let staging = temp "bundle"
    let root = Path.Combine(staging, $"{id}-{rid}")
    Directory.CreateDirectory root |> ignore
    executableScript (Path.Combine(root, id)) id version
    let name = $"{id}-{version}-{rid}.tar.gz"
    let archive = Path.Combine(mirror, name)

    do
        use file = File.Create archive
        use gzip = new GZipStream(file, CompressionLevel.Fastest)
        TarFile.CreateFromDirectory(staging, gzip, false)

    name, sha256File archive

/// A Registry-shaped resolved set selecting each (id, version) as a native
/// host tool, bundled into `mirror`.
let private hostSet (mirror: string) (tools: (string * string) list) =
    let rid = Platform.runtimeIdentifier ()

    let entries =
        tools
        |> List.map (fun (id, version) ->
            let name, sha = bundle mirror id version rid

            $$"""
    {
      "systemId": "{{id}}",
      "role": "host-tool",
      "required": true,
      "version": "{{version}}",
      "repository": "kemiller2002/{{id}}",
      "tag": "v{{version}}",
      "commit": "{{String.replicate 40 "1"}}",
      "releaseStage": "stable",
      "lifecycleState": "active",
      "distributionClass": "self-contained-native-cli",
      "executable": "{{id}}",
      "releaseManifest": { "schema": "echelon.release/v2", "sha256": "{{String.replicate 64 "c"}}" },
      "distribution": { "mechanism": "github-release", "url": "https://github.com/kemiller2002/{{id}}/releases/tag/v{{version}}" },
      "artifacts": [ { "name": "{{name}}", "purpose": "executable", "platform": "{{rid}}", "sha256": "{{sha}}" } ]
    }""")
        |> String.concat ","

    let path = Path.Combine(mirror, $"set-{Guid.NewGuid():N}.json")

    File.WriteAllText(
        path,
        $$"""{
  "schema": "echelon.resolved-release-set/v1",
  "profile": { "id": "echelon-current", "version": "1.22.0", "sha256": "{{String.replicate 64 "a"}}" },
  "platform": "{{rid}}",
  "resolver": { "name": "test", "version": "1.0.0" },
  "catalogSnapshot": { "sha256": "{{String.replicate 64 "b"}}" },
  "components": [{{entries}}
  ]
}
"""
    )

    match ResolvedReleaseSets.loadFile rid path (sha256File path) with
    | Ok set -> set
    | Error error -> failwith $"fixture set does not load: {error}"

let private context home mirror : WorkstationContext =
    { Home = home; ArtifactMirror = Some mirror; Offline = true; Praxis = None; TargetId = Some "ws-selection-test" }

let private engineering () =
    match Profiles.resolve "echelon-engineering" with
    | Ok profile -> profile
    | Error error -> failwith error

let private versionsOf (profile: WorkstationProfile) =
    profile.Components |> List.map (fun tool -> tool.Id, tool.Version)

/// A home where `id` `version` is already installed the way Conditor installs
/// it (install directory plus `~/.local/bin` shim).
let private installAt (ctx: WorkstationContext) (id: string) (version: string) =
    let directory = Path.Combine(WorkstationPaths.installRoot ctx, id, version)
    Directory.CreateDirectory directory |> ignore
    executableScript (Path.Combine(directory, id)) id version
    Directory.CreateDirectory(WorkstationPaths.binDir ctx) |> ignore
    executableScript (Path.Combine(WorkstationPaths.binDir ctx, id)) id version

let private selection check =
    let mirror = temp "mirror"
    let set = hostSet mirror [ "praxis", "3.11.0"; "ordo", "1.7.0"; "percepta-repo", "0.1.0" ]
    let profile = engineering ()

    // The regression: the embedded profile still carries the freeze pins,
    // and nothing may select them.
    check
        "the embedded echelon-engineering profile still carries the stale freeze pins this change stops using"
        (versionsOf profile = [ "praxis", "3.6.0"; "ordo", "1.4.0" ])

    match Selection.select (Some profile) None with
    | Ok selected -> check $"a profile with host tools and no Registry set is refused, not planned at {versionsOf selected}" false
    | Error error ->
        check
            "a profile with host tools and no Registry set is refused, naming --resolved-set (CON-361)"
            (error.Contains "--resolved-set" && error.Contains "DF-CON-2026-A005")

    check
        "a profile without host tools still plans without a set"
        (Profiles.resolve "minimal" |> Result.bind (fun minimal -> Selection.select (Some minimal) None) |> Result.isOk)

    match Selection.select (Some profile) (Some set) with
    | Error error -> check $"a profile with a Registry set is selected: {error}" false
    | Ok selected ->
        check
            "the profile's host tools take the Registry set's versions (CON-360)"
            (versionsOf selected = [ "praxis", "3.11.0"; "ordo", "1.7.0" ])

        check
            "the profile keeps its own prerequisites and tool list, not the set's other tools"
            (selected.Prerequisites = profile.Prerequisites && selected.Id = profile.Id)

        check
            "the plan is bound to the Registry set's identity"
            (selected.SourceIdentity = set.SourceIdentity && selected.SourceIdentity.IsSome)

        // Isolated-home effects are unchanged: the same steps as installing
        // those exact set entries directly, all under the workstation home,
        // and planning writes nothing.
        let home = temp "home-effects"
        let ctx = context home mirror
        let rid = Platform.runtimeIdentifier ()
        let fromProfile = Engine.plan ctx selected rid [] []

        let fromSet =
            Engine.plan ctx { set with Components = set.Components |> List.filter (fun tool -> tool.Id <> "percepta-repo") } rid [] []

        check
            "profile-selected steps equal the set's own steps for those tools"
            (fromProfile.Steps |> List.map (fun step -> step.Id, step.Resource, step.Artifact) = (fromSet.Steps |> List.map (fun step -> step.Id, step.Resource, step.Artifact)))

        check
            "every planned effect stays under the workstation home"
            (fromProfile.Steps |> List.forall (fun step -> step.Resource.StartsWith("~/", StringComparison.Ordinal)))

        check "planning writes nothing to the workstation home" (Directory.GetFileSystemEntries home |> Array.isEmpty)

    let partial = hostSet mirror [ "praxis", "3.11.0" ]

    match Selection.select (Some profile) (Some partial) with
    | Ok _ -> check "a profile tool the set does not select is refused" false
    | Error error -> check "a profile tool the set does not select is refused by name" (error.Contains "ordo")

    match Selection.select None (Some set) with
    | Ok whole -> check "a set alone keeps its whole native environment (CON-363)" (whole.Components.Length = 3)
    | Error error -> check $"a set alone is selected: {error}" false

let private downgrades check =
    let mirror = temp "mirror-down"
    let profile = engineering ()
    let stale = hostSet mirror [ "praxis", "3.6.0"; "ordo", "1.4.0" ]
    let current = hostSet mirror [ "praxis", "3.11.0"; "ordo", "1.7.0" ]
    let rid = Platform.runtimeIdentifier ()

    let planFor ctx set =
        match Selection.select (Some profile) (Some set) with
        | Ok selected -> Engine.plan ctx selected rid [] []
        | Error error -> failwith error

    // The shared workstation's state: Praxis 3.11.0 and Ordo 1.5.0.
    let home = temp "home-down"
    let ctx = context home mirror
    installAt ctx "praxis" "3.11.0"
    installAt ctx "ordo" "1.5.0"

    let guarded, found = Downgrade.guard false ctx probe (planFor ctx stale)

    check
        "a plan below the installed host tools is refused, naming each tool and both versions (CON-362)"
        (found |> List.map (fun (id, planned, installed, _) -> id, planned, installed) = [ "praxis", "3.6.0", "3.11.0"; "ordo", "1.4.0", "1.5.0" ]
         && guarded.Refusals |> List.exists (fun refusal -> refusal.Contains "praxis 3.6.0 would downgrade the installed praxis 3.11.0")
         && guarded.Refusals |> List.exists (fun refusal -> refusal.Contains "ordo 1.4.0 would downgrade the installed ordo 1.5.0"))

    check
        "a refused downgrade cannot be applied"
        (Engine.apply ctx probe guarded guarded.Digest true |> Result.isError
         && File.Exists(Path.Combine(WorkstationPaths.installRoot ctx, "praxis", "3.11.0", "praxis"))
         && not (Directory.Exists(Path.Combine(WorkstationPaths.installRoot ctx, "praxis", "3.6.0"))))

    let upgraded, none = Downgrade.guard false ctx probe (planFor ctx current)
    check $"moving up to the channel's versions is not a downgrade: {joined upgraded.Refusals}" (none.IsEmpty && upgraded.Refusals.IsEmpty)

    // --allow-downgrade lifts the refusal explicitly; the downgrade is still
    // disclosed and the plan applies.
    // The installed tools were put there by Conditor itself (ledger-owned),
    // as on the shared workstation.
    let allowedHome = temp "home-allowed"
    let allowedCtx = context allowedHome mirror
    let first = planFor allowedCtx current

    match Engine.apply allowedCtx probe first first.Digest true with
    | Ok installed when installed.Failed.IsNone -> ()
    | _ -> check "the allowed-downgrade fixture installs the current tools through Conditor" false

    let allowed, disclosed = Downgrade.guard true allowedCtx probe (planFor allowedCtx stale)

    check
        "--allow-downgrade lifts the refusal but still discloses the downgrade"
        (allowed.Refusals.IsEmpty && disclosed |> List.exists (fun (id, planned, _, _) -> id = "praxis" && planned = "3.6.0"))

    match Engine.apply allowedCtx probe allowed allowed.Digest true with
    | Error error -> check $"an explicitly allowed downgrade applies: {error}" false
    | Ok result ->
        let failure = result.Failed |> Option.map (fun (step, why) -> $"{step}: {why}") |> Option.defaultValue "none"

        check
            $"an explicitly allowed downgrade applies to exactly the selected release (failure: {failure})"
            (result.Failed.IsNone
             && (probe (Path.Combine(WorkstationPaths.binDir allowedCtx, "praxis")) [ "--version" ]).StandardOutput.Contains "3.6.0")

    // An isolated home is never influenced by the host: a newer praxis on
    // PATH does not count as installed for a sacrificial home.
    let isolated = temp "home-isolated"
    let isolatedCtx = context isolated mirror
    let decoys = temp "path-decoys"
    executableScript (Path.Combine(decoys, "praxis")) "praxis" "9.9.9"
    let priorPath = Environment.GetEnvironmentVariable "PATH"
    Environment.SetEnvironmentVariable("PATH", decoys + string Path.PathSeparator + priorPath)

    try
        check "a sacrificial home is not the real home" (not (Downgrade.isRealHome isolatedCtx))

        check
            "an isolated home ignores host tools on PATH"
            ((Downgrade.find isolatedCtx probe (planFor isolatedCtx stale).Profile).IsEmpty)
    finally
        Environment.SetEnvironmentVariable("PATH", priorPath)

    // The Echelon native installer's copy (~/.echelon/bin) counts as installed.
    let native = temp "home-native"
    let nativeCtx = context native mirror
    Directory.CreateDirectory(Path.Combine(native, ".echelon", "bin")) |> ignore
    executableScript (Path.Combine(native, ".echelon", "bin", "ordo")) "ordo" "1.5.0"

    check
        "a native-installer copy in ~/.echelon/bin counts as installed"
        (Downgrade.find nativeCtx probe (planFor nativeCtx stale).Profile
         |> List.exists (fun (id, _, installed, where) -> id = "ordo" && installed = "1.5.0" && where.Contains ".echelon/bin"))

let run (check: string -> bool -> unit) =
    if OperatingSystem.IsWindows() then
        check "workstation selection fixture is skipped on Windows" true
    else
        selection check
        downgrades check
