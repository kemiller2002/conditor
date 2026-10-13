namespace Conditor.Core

// Stepwise current upgrade (CON-310..315, DF-CON-2026-A003 decisions 4..6).
// `upgrade --current --check --stepwise` turns a move to the supplied target
// resolved set into a sequence of steps. In each step, every declared
// component advances by at most one registered minor line. Every step targets
// a Registry-produced resolved set: the supplied target, the channel's current
// set, or a historical channel set whose SHA-256 equals the digest the channel
// index recorded at the same commit. Conditor never composes or edits a set.
// Only the first step can be checked against the repository as it is; it gets
// the ordinary current-upgrade preview and digest.

open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open Conditor.Core.Workstation

/// A Registry-produced resolved set that a step may target.
type StepCandidate =
    { Label: string
      /// Where the set's bytes are (the materialized cache path for a
      /// historical set).
      Path: string
      Sha256: string
      ProfileVersion: SemanticVersion option
      Selections: Map<string, string>
      IsFinal: bool
      /// Bytes not yet on disk (a historical set read from Git).
      PendingBytes: byte array option }

type StepMove =
    { Id: string
      FromVersion: string
      ToVersion: string
      Crosses: CrossedRelease list }

type StepGap =
    { Id: string
      /// Registered minor lines the step jumps over, as "major.minor".
      SkippedLines: string list }

type UpgradeStep =
    { Index: int
      Candidate: StepCandidate
      Moves: StepMove list
      Gaps: StepGap list }

type StepwisePlan =
    { Steps: UpgradeStep list
      /// The ordinary current-upgrade preview of step 1 against the repository.
      FirstStepPlan: CurrentUpgradePlan option
      Registry: DriftRegistry }

module StepwiseUpgrade =
    let private sha256Bytes (bytes: byte array) =
        Convert.ToHexString(SHA256.HashData bytes).ToLowerInvariant()

    let private tryProperty (name: string) (element: JsonElement) =
        let mutable value = Unchecked.defaultof<JsonElement>

        if element.ValueKind = JsonValueKind.Object && element.TryGetProperty(name, &value) then
            Some value
        else
            None

    let private str name element =
        tryProperty name element
        |> Option.bind (fun value ->
            if value.ValueKind = JsonValueKind.String then value.GetString() |> Option.ofObj else None)

    let private objects name element =
        match tryProperty name element with
        | Some value when value.ValueKind = JsonValueKind.Array ->
            value.EnumerateArray() |> Seq.filter (fun item -> item.ValueKind = JsonValueKind.Object) |> Seq.toList
        | _ -> []

    /// The selections and profile version of resolved-set bytes.
    let private describeSet (bytes: byte array) =
        try
            use document = JsonDocument.Parse bytes
            let root = document.RootElement

            let selections =
                objects "components" root
                |> List.choose (fun entry -> Option.map2 (fun id version -> id, version) (str "systemId" entry) (str "version" entry))
                |> Map.ofList

            let profileVersion = tryProperty "profile" root |> Option.bind (str "version") |> Option.bind SemanticVersion.tryParse
            Some(selections, profileVersion)
        with :? JsonException ->
            None

    /// Every historical platform set of the channel, from the Registry's Git
    /// history, kept only when its bytes hash to the digest the channel index
    /// recorded at the same commit (CON-311).
    let historicalSets
        (run: string -> string -> string list -> ProcessResult)
        (registryRoot: string)
        (channel: string)
        (platform: string)
        : StepCandidate list =
        let indexPath = $"channels/{channel}/channel.json"
        let log = run registryRoot "git" [ "log"; "--format=%H"; "--"; indexPath ]

        if log.ExitCode <> 0 then
            []
        else
            log.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries ||| StringSplitOptions.TrimEntries)
            |> Array.toList
            |> List.choose (fun commit ->
                let show path = run registryRoot "git" [ "show"; $"{commit}:{path}" ]
                let index = show indexPath

                if index.ExitCode <> 0 then
                    None
                else
                    try
                        use document = JsonDocument.Parse index.StandardOutput

                        objects "platforms" document.RootElement
                        |> List.tryFind (fun entry -> str "platform" entry = Some platform)
                        |> Option.bind (fun entry -> Option.map2 (fun path sha -> path, sha) (str "path" entry) (str "sha256" entry))
                        |> Option.bind (fun (path, expected) ->
                            let content = show path

                            if content.ExitCode <> 0 then
                                None
                            else
                                let bytes = Encoding.UTF8.GetBytes content.StandardOutput

                                if sha256Bytes bytes <> expected then
                                    None
                                else
                                    describeSet bytes
                                    |> Option.map (fun (selections, profileVersion) ->
                                        let shown = profileVersion |> Option.map string |> Option.defaultValue "?"

                                        { Label = $"{channel} {shown} (Registry commit {commit.Substring(0, 12)})"
                                          Path = String.Empty
                                          Sha256 = expected
                                          ProfileVersion = profileVersion
                                          Selections = selections
                                          IsFinal = false
                                          PendingBytes = Some bytes }))
                    with :? JsonException ->
                        None)

    let private line (version: SemanticVersion) = version.Major, version.Minor
    let private lineText (major: int, minor: int) = $"{major}.{minor}"

    /// The registered minor line a component may reach next from `current`,
    /// bounded by `final`: the lowest registered line above `current`'s, or
    /// `current`'s own line when only patches remain.
    let private nextLine (registered: RegisteredRelease list) (current: SemanticVersion) (final: SemanticVersion) =
        registered
        |> List.map _.Version
        |> List.filter (fun version -> version > current && version <= final && line version > line current)
        |> List.map line
        |> List.sort
        |> List.tryHead
        |> Option.defaultValue (line current)

    /// The registered lines strictly between `current`'s next line and
    /// `target`'s line: what a step to `target` would skip.
    let private skippedLines (registered: RegisteredRelease list) (current: SemanticVersion) (target: SemanticVersion) =
        registered
        |> List.map _.Version
        |> List.filter (fun version -> version > current && version < target && line version > line current && line version < line target)
        |> List.map line
        |> List.distinct
        |> List.sort

    let private parse (text: string) = SemanticVersion.tryParse text

    /// Plans the steps from `declared` to `final` over `candidates` (pure,
    /// CON-310, CON-312). Each step is the newest candidate that selects every
    /// governed component, downgrades none, passes the final target with none,
    /// moves each by at most one registered minor line, and moves at least one.
    /// When no candidate qualifies, the candidate skipping the fewest lines is
    /// taken and the gap is reported.
    let plan
        (registered: Map<string, RegisteredRelease list>)
        (declared: Map<string, string>)
        (final: StepCandidate)
        (candidates: StepCandidate list)
        : Result<(StepCandidate * StepMove list * StepGap list) list, string> =
        // Components the final target moves: declared, selected by the final
        // set, and behind it.
        let governed =
            declared
            |> Map.toList
            |> List.choose (fun (id, version) ->
                match parse version, final.Selections |> Map.tryFind id |> Option.bind parse with
                | Some current, Some target -> Some(id, (current, target))
                | _ -> None)
            |> Map.ofList

        let releasesOf id = registered |> Map.tryFind id |> Option.defaultValue []

        let ordered =
            (final :: candidates)
            |> List.distinctBy _.Sha256
            |> List.sortByDescending (fun candidate -> candidate.ProfileVersion, candidate.IsFinal)

        let rec loop (state: Map<string, SemanticVersion>) acc =
            let moving =
                governed |> Map.filter (fun id (_, target) -> state[id] < target) |> Map.keys |> Seq.toList

            if moving.IsEmpty then
                Ok(List.rev acc)
            else
                let selectedOf (candidate: StepCandidate) id =
                    candidate.Selections |> Map.tryFind id |> Option.bind parse

                let bounded (candidate: StepCandidate) =
                    governed
                    |> Map.forall (fun id (_, target) ->
                        match selectedOf candidate id with
                        | Some selected -> selected >= state[id] && selected <= target
                        | None -> false)
                    && moving |> List.exists (fun id -> selectedOf candidate id |> Option.exists (fun selected -> selected > state[id]))

                let withinOneLine (candidate: StepCandidate) =
                    moving
                    |> List.forall (fun id ->
                        let _, target = governed[id]

                        match selectedOf candidate id with
                        | Some selected -> line selected <= nextLine (releasesOf id) state[id] target
                        | None -> false)

                let gapsOf (candidate: StepCandidate) =
                    moving
                    |> List.choose (fun id ->
                        match selectedOf candidate id with
                        | Some selected ->
                            match skippedLines (releasesOf id) state[id] selected with
                            | [] -> None
                            | lines -> Some { Id = id; SkippedLines = lines |> List.map lineText }
                        | None -> None)

                let admissible = ordered |> List.filter bounded

                let chosen =
                    match admissible |> List.tryFind withinOneLine with
                    | Some candidate -> Some candidate
                    | None ->
                        admissible
                        |> List.sortBy (fun candidate -> gapsOf candidate |> List.sumBy (fun gap -> gap.SkippedLines.Length))
                        |> List.tryHead

                match chosen with
                | None ->
                    let ids = String.concat ", " moving
                    Error $"No Registry-produced resolved set advances {ids} without a downgrade or passing the target."
                | Some candidate ->
                    let moves =
                        moving
                        |> List.choose (fun id ->
                            selectedOf candidate id
                            |> Option.filter (fun selected -> selected > state[id])
                            |> Option.map (fun selected ->
                                { Id = id
                                  FromVersion = string state[id]
                                  ToVersion = string selected
                                  Crosses =
                                    releasesOf id
                                    |> List.filter (fun release -> release.Version > state[id] && release.Version <= selected)
                                    |> List.map (fun release ->
                                        { Version = string release.Version
                                          MigrationNotes = release.MigrationNotes }) }))

                    let next =
                        governed
                        |> Map.map (fun id _ -> selectedOf candidate id |> Option.defaultValue state[id])

                    loop next ((candidate, moves, gapsOf candidate) :: acc)

        loop (governed |> Map.map (fun _ (current, _) -> current)) []

    /// Where a recovered historical set is materialized: content-addressed,
    /// non-authoritative workstation cache (CON-315, CON-029).
    let cachePath (ctx: WorkstationContext) (sha256: string) =
        Path.Combine(WorkstationPaths.cache ctx, "registry-steps", $"{sha256}.json")

    let private materialize (ctx: WorkstationContext) (candidate: StepCandidate) =
        match candidate.PendingBytes with
        | None -> candidate
        | Some bytes ->
            let path = cachePath ctx candidate.Sha256

            if not (File.Exists path) || sha256Bytes (File.ReadAllBytes path) <> candidate.Sha256 then
                Directory.CreateDirectory(Path.GetDirectoryName path |> Option.ofObj |> Option.defaultValue ".") |> ignore
                File.WriteAllBytes(path, bytes)

            { candidate with Path = path; PendingBytes = None }

    /// The stepwise plan for `upgrade --current --check --stepwise`.
    let preview
        (run: string -> string -> string list -> ProcessResult)
        (target: string)
        (manifestPath: string)
        (manifest: ProjectManifest)
        (finalSetPath: string)
        (finalSetSha256: string)
        (registryRoot: string)
        (channel: string)
        (ctx: WorkstationContext)
        (probe: string -> string list -> ProcessResult)
        : Result<StepwisePlan, string list> =
        let platform = Platform.runtimeIdentifier ()
        let expected = finalSetSha256.Replace("sha256:", "")

        if not (File.Exists finalSetPath) then
            Error [ $"Target resolved set not found: {finalSetPath}" ]
        else
            let finalBytes = File.ReadAllBytes finalSetPath

            if sha256Bytes finalBytes <> expected then
                Error [ $"Target resolved set {finalSetPath} has sha256:{sha256Bytes finalBytes}, not sha256:{expected}." ]
            else
                match describeSet finalBytes with
                | None -> Error [ $"Target resolved set {finalSetPath} is not valid JSON." ]
                | Some(finalSelections, finalProfile) ->
                    Drift.loadRegistry run registryRoot channel platform
                    |> Result.bind (fun registry ->
                        let final =
                            { Label = "supplied target set"
                              Path = Path.GetFullPath finalSetPath
                              Sha256 = expected
                              ProfileVersion = finalProfile
                              Selections = finalSelections
                              IsFinal = true
                              PendingBytes = None }

                        let current =
                            let path = Path.Combine(registry.Root, registry.ResolvedSetPath)

                            describeSet (File.ReadAllBytes path)
                            |> Option.map (fun (selections, profileVersion) ->
                                { Label = $"{channel} {registry.ProfileVersion} (Registry working tree)"
                                  Path = path
                                  Sha256 = registry.ResolvedSetSha256
                                  ProfileVersion = profileVersion
                                  Selections = selections
                                  IsFinal = false
                                  PendingBytes = None })
                            |> Option.toList

                        let candidates = current @ historicalSets run registry.Root channel platform

                        let declared =
                            manifest.Components
                            |> List.choose (fun request -> request.Version |> Option.map (fun version -> request.Id, version))
                            |> Map.ofList

                        plan registry.Releases declared final candidates
                        |> Result.mapError List.singleton
                        |> Result.bind (fun steps ->
                            let steps =
                                steps
                                |> List.mapi (fun index (candidate, moves, gaps) ->
                                    { Index = index + 1
                                      Candidate = materialize ctx candidate
                                      Moves = moves
                                      Gaps = gaps })

                            match steps with
                            | [] -> Ok { Steps = []; FirstStepPlan = None; Registry = registry }
                            | first :: _ ->
                                CurrentUpgrade.preview target manifestPath manifest first.Candidate.Path first.Candidate.Sha256 ctx probe
                                |> Result.map (fun firstPlan ->
                                    { Steps = steps
                                      FirstStepPlan = Some firstPlan
                                      Registry = registry })))
