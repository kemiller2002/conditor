module PinConsistencyTests

// Pin consistency across every pin kind (CON-350..354, WI-0039). This is the
// defect Summa's step 3 found with Conditor 0.10.0. An authorized Ordo
// 1.5.0 -> 1.6.0 upgrade moved conditor.json and Ordo's own state, but left
// .echelon/foundations.json at 1.5.0. `upgrade --current --check` then said
// nothing was behind, while `drift` reported the disagreement. The fixture
// runs the real embedded-qualified Ordo path, with a stand-in `ordo` binary
// installed through the workstation engine and verified through the
// integrity-only gate.

open System
open System.Formats.Tar
open System.IO
open System.IO.Compression
open System.Security.Cryptography
open Conditor.Core
open Conditor.Core.Workstation

let private joined (values: string list) = String.concat "; " values

let private temp label =
    let path = Path.Combine(Path.GetTempPath(), $"conditor-pins-{label}-{Guid.NewGuid():N}")
    Directory.CreateDirectory path |> ignore
    path

let private sha256File (path: string) =
    Convert.ToHexString(SHA256.HashData(File.ReadAllBytes path)).ToLowerInvariant()

/// A stand-in Ordo release archive: `ordo upgrade` moves .sde/VERSION to
/// `version`, and `ordo verify --integrity-only --json` reports an
/// ordo.verify-json/v1 pass for exactly that installed version.
let private ordoBundle (mirror: string) (version: string) (rid: string) =
    let staging = temp "bundle"
    let root = Path.Combine(staging, $"ordo-{rid}")
    Directory.CreateDirectory root |> ignore
    let executable = Path.Combine(root, "ordo")

    let report =
        $$"""{"schemaVersion":1,"command":"verify","tool":"sde","package":"@echelon-foundry/sde","state":"installed","installedVersion":"{{version}}","managedFileCount":1,"mode":"integrity-only","strict":false,"passed":true,"problems":[],"strictFailures":[],"failures":[],"reviewSignals":[],"structural":{"ran":true,"error":null,"enabled":true,"configurationSource":"defaults","inspectedFiles":1,"findings":[]},"exitCode":0}"""

    let script =
        String.concat
            "\n"
            [ "#!/bin/sh"
              "set -eu"
              "case \"$1\" in"
              $"  --version) echo '{version}' ;;"
              $"  upgrade) mkdir -p .sde; echo '{version}' > .sde/VERSION ;;"
              $"  verify) test \"$(cat .sde/VERSION)\" = '{version}'; echo '{report}' ;;"
              "  doctor) exit 0 ;;"
              "  *) exit 2 ;;"
              "esac"
              "" ]

    File.WriteAllText(executable, script)
    File.SetUnixFileMode(executable, UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute)
    let name = $"ordo-{rid}.tar.gz"
    let archive = Path.Combine(mirror, name)

    do
        use file = File.Create archive
        use gzip = new GZipStream(file, CompressionLevel.Fastest)
        TarFile.CreateFromDirectory(staging, gzip, false)

    name, sha256File archive

/// A Registry-shaped set selecting Ordo `version` as a native host tool
/// without the generic lifecycle contract: the embedded-qualified path.
let private ordoSet (path: string) (version: string) (rid: string) (assetName: string) (assetSha: string) =
    File.WriteAllText(
        path,
        $$"""{
  "schema": "echelon.resolved-release-set/v1",
  "profile": { "id": "echelon-current", "version": "1.21.0", "sha256": "{{String.replicate 64 "a"}}" },
  "platform": "{{rid}}",
  "resolver": { "name": "test", "version": "1.0.0" },
  "catalogSnapshot": { "sha256": "{{String.replicate 64 "b"}}" },
  "components": [
    {
      "systemId": "ordo",
      "role": "host-tool",
      "required": true,
      "version": "{{version}}",
      "repository": "kemiller2002/ordo",
      "tag": "v{{version}}",
      "commit": "{{String.replicate 40 "8"}}",
      "releaseStage": "stable",
      "lifecycleState": "active",
      "distributionClass": "self-contained-native-cli",
      "executable": "ordo",
      "releaseManifest": { "schema": "echelon.release/v2", "sha256": "{{String.replicate 64 "c"}}" },
      "distribution": { "mechanism": "github-release", "package": null, "url": "https://github.com/kemiller2002/ordo/releases/tag/v{{version}}" },
      "artifacts": [
        { "name": "{{assetName}}", "purpose": "executable", "platform": "{{rid}}", "sha256": "{{assetSha}}" }
      ]
    }
  ]
}
"""
    )

    sha256File path

let private foundations (ordo: string option) =
    let ordoEntry =
        match ordo with
        | Some version -> $",\n    \"ordo\": {{\n      \"required\": true,\n      \"version\": \"{version}\"\n    }}"
        | None -> ""

    $"{{\n  \"schemaVersion\": 1,\n  \"application\": \"Summa\",\n  \"capabilities\": {{\n    \"aegis\": {{\n      \"required\": true,\n      \"version\": \"1.0.0\"\n    }}{ordoEntry}\n  }}\n}}\n"

type private Repo =
    { Target: string
      ManifestPath: string
      Mirror: string
      Context: WorkstationContext }

/// A Summa-shaped repository: Conditor declares Ordo `declared` (and the lock
/// proves it), Ordo's own state is at `installed`, and foundations.json names
/// Ordo `foundationsOrdo` (or has no Ordo capability).
let private summaShaped label (declared: string) (installed: string) (foundationsOrdo: string option) =
    let target = temp $"{label}-target"
    let mirror = temp $"{label}-mirror"
    let home = temp $"{label}-home"
    let manifestPath = Path.Combine(target, "conditor.json")

    File.WriteAllText(
        manifestPath,
        $$"""{
  "schemaVersion": 1,
  "name": "summa-shaped",
  "components": [
    { "id": "ordo", "version": "{{declared}}", "required": true }
  ],
  "requirements": [],
  "execution": { "enabled": false }
}
"""
    )

    LockFile.write
        target
        manifestPath
        { ProjectName = "summa-shaped"
          Operation = Init
          Components =
            [ { Id = "ordo"
                Version = declared
                Distribution = HostTool
                Package = "ordo"
                SourceReference = Some $"host:ordo@{declared}" } ]
          Actions = [] }
    |> ignore

    Directory.CreateDirectory(Path.Combine(target, ".sde")) |> ignore
    File.WriteAllText(Path.Combine(target, ".sde", "VERSION"), $"{installed}\n")
    Directory.CreateDirectory(Path.Combine(target, ".echelon")) |> ignore
    File.WriteAllText(Path.Combine(target, ".echelon", "foundations.json"), foundations foundationsOrdo)

    { Target = target
      ManifestPath = manifestPath
      Mirror = mirror
      Context =
        { Home = home
          ArtifactMirror = Some mirror
          Offline = true
          Praxis = None
          TargetId = Some "pin-consistency-test" } }

let private setFor (repo: Repo) (version: string) =
    let rid = Platform.runtimeIdentifier ()
    let assetName, assetSha = ordoBundle repo.Mirror version rid
    let path = Path.Combine(repo.Mirror, $"ordo-{version}.json")
    path, ordoSet path version rid assetName assetSha

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

let private foundationsText (repo: Repo) =
    File.ReadAllText(Path.Combine(repo.Target, ".echelon", "foundations.json"))

let private declaredOrdo (repo: Repo) =
    match Manifest.load repo.ManifestPath with
    | Ok manifest -> manifest.Components |> List.tryFind (fun item -> item.Id = "ordo") |> Option.bind _.Version
    | Error _ -> None

/// CON-350: the Ordo transition moves foundations.json with it.
let private ordoUpgradeMovesFoundations check =
    let repo = summaShaped "ordo-moves" "1.5.0" "1.5.0" (Some "1.5.0")
    let set = setFor repo "1.6.0"

    match preview repo set with
    | Error errors -> check $"Ordo 1.5.0 -> 1.6.0 plan is produced: {joined errors}" false
    | Ok plan ->
        check $"Ordo 1.5.0 -> 1.6.0 is planned: {joined plan.Refusals}" (plan.Refusals.IsEmpty)

        check
            "the Ordo transition is embedded-qualified and moves its foundations capability (CON-350)"
            (plan.Transitions |> List.map (fun item -> item.Id, item.Mode) = [ "ordo", "embedded-qualified-lifecycle" ]
             && plan.FoundationsEdits |> List.map (fun edit -> edit.Capability, edit.FromVersion, edit.ToVersion) = [ "ordo", "1.5.0", "1.6.0" ])

        match apply repo set plan.Digest with
        | Error errors -> check $"the authorized Ordo upgrade succeeds: {joined errors}" false
        | Ok result ->
            check "Ordo's own state moved to 1.6.0" (File.ReadAllText(Path.Combine(repo.Target, ".sde", "VERSION")).Trim() = "1.6.0")
            check "conditor.json declares Ordo 1.6.0" (declaredOrdo repo = Some "1.6.0")

            check
                "foundations.json moved only the Ordo capability version (Summa's step-3 defect)"
                (foundationsText repo = foundations (Some "1.6.0"))

            check "the upgrade leaves no transitions and no pin repairs (CON-354)" result.NoRemainingVersionChanges

    // A missing capability is never created.
    let bare = summaShaped "ordo-no-capability" "1.5.0" "1.5.0" None
    let bareSet = setFor bare "1.6.0"

    match preview bare bareSet with
    | Error errors -> check $"no-capability plan is produced: {joined errors}" false
    | Ok plan ->
        check "with no Ordo capability there is no foundations edit" plan.FoundationsEdits.IsEmpty

        match apply bare bareSet plan.Digest with
        | Error errors -> check $"no-capability upgrade succeeds: {joined errors}" false
        | Ok _ -> check "a missing foundations capability is never created" (foundationsText bare = foundations None)

/// CON-352, CON-354: the half-applied state Summa is in. conditor.json and
/// Ordo say 1.6.0, foundations.json still says 1.5.0.
let private repairFoundationsBehind check =
    let repo = summaShaped "foundations-behind" "1.6.0" "1.6.0" (Some "1.5.0")
    let set = setFor repo "1.6.0"

    match preview repo set with
    | Error errors -> check $"repair plan is produced: {joined errors}" false
    | Ok plan ->
        check
            $"a stale foundations pin is planned as a repair, not reported as current: {joined plan.Refusals}"
            (plan.Refusals.IsEmpty
             && plan.Transitions.IsEmpty
             && plan.PinRepairs |> List.map (fun edit -> edit.Capability, edit.FromVersion, edit.ToVersion) = [ "ordo", "1.5.0", "1.6.0" ])

        match apply repo set plan.Digest with
        | Error errors -> check $"the authorized pin repair succeeds: {joined errors}" false
        | Ok result ->
            check "the repair aligns foundations.json to the applied 1.6.0" (foundationsText repo = foundations (Some "1.6.0"))
            check "the repair changes no version" (declaredOrdo repo = Some "1.6.0")
            check "after the repair nothing remains" result.NoRemainingVersionChanges

            match preview repo set with
            | Ok again -> check "a second check has no repair left" (again.PinRepairs.IsEmpty && again.Transitions.IsEmpty)
            | Error errors -> check $"post-repair check is produced: {joined errors}" false

    // A stale foundations pin on a component that moves is refused, with the
    // repair named, rather than skipped.
    let moving = summaShaped "foundations-stale-moving" "1.6.0" "1.6.0" (Some "1.5.0")
    let next = setFor moving "1.7.0"

    match preview moving next with
    | Error errors -> check $"stale-moving plan is produced: {joined errors}" false
    | Ok plan ->
        check
            $"a moving component with a stale foundations pin is refused with the repair named: {joined plan.Refusals}"
            (plan.Refusals |> List.exists (fun refusal -> refusal.Contains "neither the source 1.6.0 nor the target 1.7.0" && refusal.Contains "CON-352"))

/// The other order: conditor.json is behind foundations.json.
let private repairConditorBehind check =
    // The set moves Ordo to the version foundations already names: the
    // transition makes every pin agree and foundations needs no edit.
    let repo = summaShaped "conditor-behind-moving" "1.5.0" "1.5.0" (Some "1.6.0")
    let set = setFor repo "1.6.0"

    match preview repo set with
    | Error errors -> check $"conditor-behind plan is produced: {joined errors}" false
    | Ok plan ->
        check
            $"conditor.json behind a foundations pin at the target is a plain transition: {joined plan.Refusals}"
            (plan.Refusals.IsEmpty && plan.FoundationsEdits.IsEmpty && plan.Transitions.Length = 1)

        match apply repo set plan.Digest with
        | Error errors -> check $"conditor-behind upgrade succeeds: {joined errors}" false
        | Ok result ->
            check
                "after it every pin agrees on 1.6.0"
                (declaredOrdo repo = Some "1.6.0" && foundationsText repo = foundations (Some "1.6.0") && result.NoRemainingVersionChanges)

    // The set keeps Ordo where conditor.json declares it: conditor.json is
    // the governed version, so the foundations pin that runs ahead of it is
    // a repair back to the declared version.
    let steady = summaShaped "conditor-behind-steady" "1.5.0" "1.5.0" (Some "1.6.0")
    let steadySet = setFor steady "1.5.0"

    match preview steady steadySet with
    | Error errors -> check $"conditor-behind steady plan is produced: {joined errors}" false
    | Ok plan ->
        check
            $"a foundations pin ahead of conditor.json is repaired to the declared version: {joined plan.Refusals}"
            (plan.Refusals.IsEmpty
             && plan.PinRepairs |> List.map (fun edit -> edit.FromVersion, edit.ToVersion) = [ "1.6.0", "1.5.0" ])

/// CON-351: a pin kind no step moves fails the invariant, and Conditor's own
/// edits are restored with governance unchanged.
let private invariantCatchesSkippedPin check =
    let repo = summaShaped "invariant" "1.5.0" "1.5.0" (Some "1.5.0")

    // A Conditor NuGet feed lock naming Ordo: a pin kind drift reads that
    // this upgrade has no step to move.
    let lockPath = Path.Combine(repo.Target, "vendor", "nuget", "ordo.lock")
    Directory.CreateDirectory(Path.GetDirectoryName lockPath |> nonNull) |> ignore
    File.WriteAllText(lockPath, "# conditor.nuget-feed-lock/v1. Written by Conditor; do not edit.\nsystem ordo\nversion 1.5.0\n")
    let manifestBefore = File.ReadAllText repo.ManifestPath
    let foundationsBefore = foundationsText repo
    let set = setFor repo "1.6.0"

    match preview repo set with
    | Error errors -> check $"invariant fixture plan is produced: {joined errors}" false
    | Ok plan ->
        match apply repo set plan.Digest with
        | Ok _ -> check "a pin kind no step moves fails the consistency invariant" false
        | Error errors ->
            check
                "a pin kind no step moves fails the consistency invariant (CON-351)"
                (errors |> List.exists (fun error -> error.Contains "Pin consistency invariant failed" && error.Contains "vendor/nuget/ordo.lock"))

            check "the invariant restores the foundations edit Conditor made" (foundationsText repo = foundationsBefore)
            check "the invariant leaves conditor.json unchanged" (File.ReadAllText repo.ManifestPath = manifestBefore)

            check
                "the invariant commits no Registry authority"
                (not (File.Exists(Path.Combine(repo.Target, ".conditor", "authority", "resolved-release-set.json"))))

let run (check: string -> bool -> unit) =
    if OperatingSystem.IsWindows() then
        check "pin consistency fixture is skipped on Windows" true
    else
        ordoUpgradeMovesFoundations check
        repairFoundationsBehind check
        repairConditorBehind check
        invariantCatchesSkippedPin check
