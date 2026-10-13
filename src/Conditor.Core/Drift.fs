namespace Conditor.Core

// The rolling-upgrade advisory (CON-300..320, DF-CON-2026-A003,
// docs/drift-advisory.md). For a target repository and an Echelon Registry
// channel, report how far every pinned Registry component has fallen behind,
// and the next step, one registered minor line at a time. Read-only: nothing
// is downloaded, installed or written, and the Registry is read only through
// its own digest chain.

open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.RegularExpressions

/// How far behind is too far (CON-316). `None` disables a threshold.
type StalenessPolicy =
    { WarnAfterMinorsBehind: int option
      WarnAfterMajorsBehind: int option
      WarnAfterReleasesBehind: int option
      WarnAfterPinAgeDays: int option
      FailBelowSupportedRange: bool }

module StalenessPolicy =
    /// The built-in defaults: warn at more than one minor line or any major
    /// behind; fail only below the Registry's supported range.
    let defaults =
        { WarnAfterMinorsBehind = Some 1
          WarnAfterMajorsBehind = Some 0
          WarnAfterReleasesBehind = None
          WarnAfterPinAgeDays = None
          FailBelowSupportedRange = true }

    let fieldNames =
        [ "warnAfterMinorsBehind"
          "warnAfterMajorsBehind"
          "warnAfterReleasesBehind"
          "warnAfterPinAgeDays"
          "failBelowSupportedRange" ]

    /// One declared field of a `stalenessPolicy` object.
    type Override =
        | Threshold of string * int option
        | FailBelowRange of bool

    /// The overrides a `stalenessPolicy` object declares, or every problem with
    /// it. Unknown fields are refused so a misspelt threshold is not silently
    /// ignored.
    let parse (source: string) (element: JsonElement) : Result<Override list, string list> =
        if element.ValueKind <> JsonValueKind.Object then
            Error [ $"{source} stalenessPolicy must be an object." ]
        else
            let results =
                element.EnumerateObject()
                |> Seq.map (fun property ->
                    match property.Name, property.Value.ValueKind with
                    | "failBelowSupportedRange", (JsonValueKind.True | JsonValueKind.False) ->
                        Ok(FailBelowRange(property.Value.GetBoolean()))
                    | "failBelowSupportedRange", _ -> Error $"{source} stalenessPolicy.failBelowSupportedRange must be true or false."
                    | name, JsonValueKind.Null when List.contains name fieldNames -> Ok(Threshold(name, None))
                    | name, JsonValueKind.Number when List.contains name fieldNames ->
                        match property.Value.TryGetInt32() with
                        | true, value when value >= 0 -> Ok(Threshold(name, Some value))
                        | _ -> Error $"{source} stalenessPolicy.{name} must be a non-negative integer or null."
                    | name, _ when List.contains name fieldNames ->
                        Error $"{source} stalenessPolicy.{name} must be a non-negative integer or null."
                    | name, _ -> Error $"{source} stalenessPolicy has unknown field '{name}'.")
                |> Seq.toList

            match results |> List.choose (function Error e -> Some e | Ok _ -> None) with
            | [] -> Ok(results |> List.choose (function Ok o -> Some o | Error _ -> None))
            | errors -> Error errors

    let private applyOne (policy: StalenessPolicy) =
        function
        | FailBelowRange value -> { policy with FailBelowSupportedRange = value }
        | Threshold("warnAfterMinorsBehind", value) -> { policy with WarnAfterMinorsBehind = value }
        | Threshold("warnAfterMajorsBehind", value) -> { policy with WarnAfterMajorsBehind = value }
        | Threshold("warnAfterReleasesBehind", value) -> { policy with WarnAfterReleasesBehind = value }
        | Threshold("warnAfterPinAgeDays", value) -> { policy with WarnAfterPinAgeDays = value }
        | Threshold _ -> policy

    let private fieldOf =
        function
        | FailBelowRange _ -> "failBelowSupportedRange"
        | Threshold(name, _) -> name

    /// Defaults, then the Registry set's overrides, then the repository's
    /// (CON-317), with the source of every effective field.
    let resolve (registry: Override list) (repository: Override list) =
        let layers = [ "registry", registry; "conditor.json", repository ]

        let policy =
            layers |> List.fold (fun state (_, overrides) -> overrides |> List.fold applyOne state) defaults

        let sources =
            fieldNames
            |> List.map (fun field ->
                let source =
                    layers
                    |> List.rev
                    |> List.tryFind (fun (_, overrides) -> overrides |> List.exists (fun item -> fieldOf item = field))
                    |> Option.map fst
                    |> Option.defaultValue "default"

                field, source)

        policy, sources

/// The advisory level of one component, and of the whole report.
type DriftLevel =
    | DriftOk
    | DriftInfo
    | DriftWarn
    | DriftFail

module DriftLevel =
    let toWire =
        function
        | DriftOk -> "ok"
        | DriftInfo -> "info"
        | DriftWarn -> "warn"
        | DriftFail -> "fail"

/// One release the Registry records for a system, read from its digest-bound
/// release manifest.
type RegisteredRelease =
    { SystemId: string
      Version: SemanticVersion
      Repository: string
      Tag: string
      ReleaseStage: string
      LifecycleState: string
      DistributionClass: string
      /// npm package names this release is distributed as.
      NpmPackages: string list
      /// NuGet package ids this release is distributed as.
      NugetPackages: string list
      /// GitHub release-asset URLs of its package tarballs.
      TarballUrls: string list
      MigrationNotes: string
      ManifestPath: string }

/// The Registry channel as drift reads it (CON-303).
type DriftRegistry =
    { Root: string
      Channel: string
      ChannelSha256: string
      Platform: string
      ProfileId: string
      ProfileVersion: string
      ResolvedSetPath: string
      ResolvedSetSha256: string
      CatalogSnapshotSha256: string
      /// Registered (active, allowed-stage) releases, ascending, per system.
      Releases: Map<string, RegisteredRelease list>
      /// The version the channel's resolved set selects, per system.
      Selected: Map<string, string>
      /// The profile's `version.range`, per system.
      SupportedRanges: Map<string, string>
      PolicyOverrides: StalenessPolicy.Override list
      /// When each release manifest was added to the Registry (Git history).
      RegisteredAt: Map<string, DateTimeOffset> }

/// One observed pin of a Registry component (CON-301).
type DriftPin =
    { SystemId: string
      Source: string
      Path: string
      Line: int option
      Specifier: string
      Version: string option
      PinnedSince: DateTimeOffset option }

/// One step's worth of crossed release.
type CrossedRelease =
    { Version: string
      MigrationNotes: string }

/// The exact new specifier of one pin for the next step.
type PinUpdate =
    { Source: string
      Path: string
      Line: int option
      From: string
      To: string }

type DriftNextStep =
    { Version: string
      Crosses: CrossedRelease list
      PinUpdates: PinUpdate list
      Command: string option
      /// For an undeclared web package: the exact conditor.json entry that
      /// lets `upgrade --current` adopt and move every pin (CON-334).
      Declare: string option }

type ComponentDrift =
    { Id: string
      Pins: DriftPin list
      Declared: bool
      PinnedVersion: string option
      Consistent: bool
      ChannelSelected: string option
      LatestRegistered: string option
      ReleasesBehind: int option
      MinorsBehind: int option
      MajorsBehind: int option
      SupportedRange: string option
      BelowSupportedRange: bool option
      PinAgeDays: int option
      BehindSince: DateTimeOffset option
      Level: DriftLevel
      Reasons: string list
      NextStep: DriftNextStep option }

type DriftReport =
    { Target: string
      ObservedAt: DateTimeOffset
      Registry: DriftRegistry
      Policy: StalenessPolicy
      PolicySources: (string * string) list
      Components: ComponentDrift list
      Level: DriftLevel }

module Drift =
    [<Literal>]
    let Schema = "conditor.drift-report/v1"

    let private ignoredDirectories = set [ "node_modules"; ".git"; "bin"; "obj"; ".conditor" ]

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

    let private strings name element =
        match tryProperty name element with
        | Some value when value.ValueKind = JsonValueKind.Array ->
            value.EnumerateArray()
            |> Seq.choose (fun item -> if item.ValueKind = JsonValueKind.String then item.GetString() |> Option.ofObj else None)
            |> Seq.toList
        | _ -> []

    let private fileName (path: string) =
        Path.GetFileName path |> Option.ofObj |> Option.defaultValue path

    let private relative (root: string) (path: string) =
        Path.GetRelativePath(root, path).Replace('\\', '/')

    // ---- Registry (digest chain, CON-303) -------------------------------------

    /// A Registry-relative path that stays inside the Registry checkout.
    let private registryFile (root: string) (relativePath: string) =
        let full = Path.GetFullPath(Path.Combine(root, relativePath))
        let rootFull = Path.GetFullPath root

        if full.StartsWith(rootFull.TrimEnd(Path.DirectorySeparatorChar) + string Path.DirectorySeparatorChar, StringComparison.Ordinal) then
            Ok full
        else
            Error $"Registry path '{relativePath}' leaves the Registry checkout."

    /// The bytes of a Registry file whose SHA-256 must equal `expected`.
    let private verifiedBytes (root: string) (label: string) (relativePath: string) (expected: string) =
        registryFile root relativePath
        |> Result.bind (fun path ->
            if not (File.Exists path) then
                Error $"{label} {relativePath} is missing from the Registry checkout."
            else
                let bytes = File.ReadAllBytes path
                let actual = sha256Bytes bytes

                if actual <> expected then
                    Error $"{label} {relativePath} has sha256:{actual}, not the recorded sha256:{expected}."
                else
                    Ok bytes)

    let private parseRelease (systemId: string) (manifestPath: string) (bytes: byte array) : Result<RegisteredRelease, string> =
        try
            use document = JsonDocument.Parse bytes
            let root = document.RootElement

            match str "version" root |> Option.bind SemanticVersion.tryParse, str "repository" root, str "tag" root with
            | Some version, Some repository, Some tag ->
                let distributionClass = str "distributionClass" root |> Option.defaultValue ""
                let distributions = objects "distributions" root
                let packageArtifacts = objects "artifacts" root |> List.filter (fun artifact -> str "purpose" artifact = Some "package")
                let artifactNames = packageArtifacts |> List.choose (str "name")
                let distributionPackages = distributions |> List.choose (str "package")
                let isNuget = distributionClass = "nuget-library" || (distributions |> List.exists (fun d -> str "mechanism" d = Some "nuget"))
                let versionText = version.ToString()

                let nugetFromArtifacts =
                    artifactNames
                    |> List.filter (fun name -> name.EndsWith($".{versionText}.nupkg", StringComparison.OrdinalIgnoreCase))
                    |> List.map (fun name -> name.Substring(0, name.Length - $".{versionText}.nupkg".Length))

                Ok
                    { SystemId = systemId
                      Version = version
                      Repository = repository
                      Tag = tag
                      ReleaseStage = str "releaseStage" root |> Option.defaultValue ""
                      LifecycleState = str "lifecycleState" root |> Option.defaultValue ""
                      DistributionClass = distributionClass
                      NpmPackages = if isNuget then [] else distributionPackages |> List.distinct
                      NugetPackages = if isNuget then distributionPackages @ nugetFromArtifacts |> List.distinct else []
                      TarballUrls =
                        artifactNames
                        |> List.filter (fun name -> name.EndsWith(".tgz", StringComparison.Ordinal))
                        |> List.map (fun name -> $"https://github.com/{repository}/releases/download/{tag}/{name}")
                      MigrationNotes =
                        str "migrationNotes" root
                        |> Option.filter (fun url -> url.StartsWith("https://", StringComparison.Ordinal))
                        |> Option.defaultValue $"https://github.com/{repository}/releases/tag/{tag}"
                      ManifestPath = manifestPath }
            | _ -> Error $"Release manifest {manifestPath} has no exact version, repository or tag."
        with :? JsonException as ex ->
            Error $"Release manifest {manifestPath} is not valid JSON: {ex.Message}"

    let private parseJson (label: string) (bytes: byte array) (read: JsonElement -> Result<'T, string list>) =
        try
            use document = JsonDocument.Parse bytes
            read document.RootElement
        with :? JsonException as ex ->
            Error [ $"{label} is not valid JSON: {ex.Message}" ]

    /// When each release manifest under `releases/` was added, from the
    /// Registry's Git history; empty when that history is unavailable.
    let registrationTimes (run: string -> string -> string list -> ProcessResult) (root: string) : Map<string, DateTimeOffset> =
        let result = run root "git" [ "log"; "--diff-filter=A"; "--format=%x00%cI"; "--name-only"; "--"; "releases" ]

        if result.ExitCode <> 0 then
            Map.empty
        else
            result.StandardOutput.Split('\000', StringSplitOptions.RemoveEmptyEntries)
            |> Array.toList
            |> List.collect (fun block ->
                match block.Split('\n', StringSplitOptions.RemoveEmptyEntries ||| StringSplitOptions.TrimEntries) |> Array.toList with
                | date :: files ->
                    match DateTimeOffset.TryParse date with
                    | true, at -> files |> List.map (fun file -> file, at)
                    | _ -> []
                | [] -> [])
            // Newest first: keep the oldest addition of each path.
            |> List.fold (fun (state: Map<string, DateTimeOffset>) (file, at) -> state.Add(file, at)) Map.empty

    /// Reads a Registry channel through its digest chain: the channel index
    /// binds the platform resolved set and the catalog snapshot, which binds
    /// every release and profile manifest. Any break stops with every reason.
    let loadRegistry
        (run: string -> string -> string list -> ProcessResult)
        (registryRoot: string)
        (channel: string)
        (platform: string)
        : Result<DriftRegistry, string list> =
        let root = Path.GetFullPath registryRoot
        let indexRelative = $"channels/{channel}/channel.json"

        match registryFile root indexRelative with
        | Error error -> Error [ error ]
        | Ok indexPath when not (File.Exists indexPath) ->
            Error [ $"Registry channel index {indexRelative} not found under {root}." ]
        | Ok indexPath ->
            let indexBytes = File.ReadAllBytes indexPath

            parseJson indexRelative indexBytes (fun index ->
                let setEntry =
                    objects "platforms" index |> List.tryFind (fun entry -> str "platform" entry = Some platform)

                let snapshot = tryProperty "catalogSnapshot" index
                let profile = tryProperty "profile" index

                match setEntry |> Option.bind (fun e -> Option.map2 (fun p s -> p, s) (str "path" e) (str "sha256" e)),
                      snapshot |> Option.bind (fun s -> Option.map2 (fun p d -> p, d) (str "path" s) (str "sha256" s)),
                      profile |> Option.bind (fun p -> Option.map3 (fun i v d -> i, v, d) (str "id" p) (str "version" p) (str "sha256" p)) with
                | None, _, _ -> Error [ $"Registry channel '{channel}' has no resolved set for platform '{platform}'." ]
                | _, None, _ -> Error [ $"Registry channel index {indexRelative} names no catalog snapshot." ]
                | _, _, None -> Error [ $"Registry channel index {indexRelative} names no profile." ]
                | Some(setPath, setSha), Some(snapshotPath, snapshotSha), Some(profileId, profileVersion, profileSha) ->
                    let setBytes = verifiedBytes root "Resolved set" setPath setSha
                    let snapshotBytes = verifiedBytes root "Catalog snapshot" snapshotPath snapshotSha

                    match setBytes, snapshotBytes with
                    | Error a, Error b -> Error [ a; b ]
                    | Error a, _
                    | _, Error a -> Error [ a ]
                    | Ok setBytes, Ok snapshotBytes ->
                        let selection =
                            parseJson setPath setBytes (fun set ->
                                let selected =
                                    objects "components" set
                                    |> List.choose (fun entry -> Option.map2 (fun id version -> id, version) (str "systemId" entry) (str "version" entry))
                                    |> Map.ofList

                                match tryProperty "stalenessPolicy" set with
                                | None -> Ok(selected, [])
                                | Some policy ->
                                    StalenessPolicy.parse $"Registry resolved set {setPath}" policy
                                    |> Result.map (fun overrides -> selected, overrides))

                        let catalog =
                            parseJson snapshotPath snapshotBytes (fun snapshot ->
                                let profileManifest =
                                    objects "profiles" snapshot
                                    |> List.tryFind (fun entry -> str "id" entry = Some profileId && str "version" entry = Some profileVersion)

                                match profileManifest |> Option.bind (fun e -> Option.map2 (fun p d -> p, d) (str "manifest" e) (str "sha256" e)) with
                                | None -> Error [ $"Catalog snapshot {snapshotPath} does not list profile {profileId}@{profileVersion}." ]
                                | Some(_, snapshotProfileSha) when snapshotProfileSha <> profileSha ->
                                    Error
                                        [ $"Catalog snapshot {snapshotPath} records profile {profileId}@{profileVersion} as sha256:{snapshotProfileSha}, but the channel index records sha256:{profileSha}." ]
                                | Some(profilePath, _) ->
                                    let profileResult =
                                        verifiedBytes root "Profile" profilePath profileSha
                                        |> Result.mapError List.singleton
                                        |> Result.bind (fun bytes ->
                                            parseJson profilePath bytes (fun profile ->
                                                let stages = strings "allowedReleaseStages" profile |> Set.ofList

                                                let ranges =
                                                    objects "components" profile
                                                    |> List.choose (fun entry ->
                                                        Option.map2
                                                            (fun id range -> id, range)
                                                            (str "systemId" entry)
                                                            (tryProperty "version" entry |> Option.bind (str "range")))
                                                    |> Map.ofList

                                                Ok(stages, ranges)))

                                    let releases =
                                        objects "releases" snapshot
                                        |> List.map (fun entry ->
                                            match str "systemId" entry, str "manifest" entry, str "sha256" entry with
                                            | Some id, Some manifest, Some sha ->
                                                verifiedBytes root "Release manifest" manifest sha
                                                |> Result.bind (parseRelease id manifest)
                                            | _ -> Error $"Catalog snapshot {snapshotPath} has a release entry without systemId, manifest or sha256.")

                                    let releaseErrors = releases |> List.choose (function Error e -> Some e | Ok _ -> None)

                                    match profileResult, releaseErrors with
                                    | Error errors, more -> Error(errors @ more)
                                    | Ok _, (_ :: _ as errors) -> Error errors
                                    | Ok(stages, ranges), [] ->
                                        let registered =
                                            releases
                                            |> List.choose (function Ok r -> Some r | Error _ -> None)
                                            |> List.filter (fun release ->
                                                release.LifecycleState = "active"
                                                && (stages.IsEmpty || stages.Contains release.ReleaseStage))
                                            |> List.groupBy _.SystemId
                                            |> List.map (fun (id, items) -> id, items |> List.sortBy _.Version)
                                            |> Map.ofList

                                        Ok(registered, ranges))

                        match selection, catalog with
                        | Error a, Error b -> Error(a @ b)
                        | Error a, _
                        | _, Error a -> Error a
                        | Ok(selected, overrides), Ok(registered, ranges) ->
                            Ok
                                { Root = root
                                  Channel = channel
                                  ChannelSha256 = sha256Bytes indexBytes
                                  Platform = platform
                                  ProfileId = profileId
                                  ProfileVersion = profileVersion
                                  ResolvedSetPath = setPath
                                  ResolvedSetSha256 = setSha
                                  CatalogSnapshotSha256 = snapshotSha
                                  Releases = registered
                                  Selected = selected
                                  SupportedRanges = ranges
                                  PolicyOverrides = overrides
                                  RegisteredAt = registrationTimes run root })

    // ---- Pin discovery (CON-301, CON-302) -----------------------------------

    let rec private walk (directory: string) (accept: string -> bool) : string list =
        let here =
            Directory.EnumerateFiles directory
            |> Seq.filter (fun path -> accept (fileName path))
            |> Seq.sort
            |> Seq.toList

        let below =
            Directory.EnumerateDirectories directory
            |> Seq.filter (fun child -> not (ignoredDirectories.Contains(fileName child)))
            |> Seq.filter (fun child -> not (File.GetAttributes(child).HasFlag FileAttributes.ReparsePoint))
            |> Seq.sort
            |> Seq.collect (fun child -> walk child accept)
            |> Seq.toList

        here @ below

    /// The 1-based line of the first line at or after `anchor` that matches
    /// `target`, where `anchor` is the first line matching `anchorPattern`.
    let private lineOf (lines: string array) (anchorPattern: Regex) (targetPattern: Regex) =
        lines
        |> Array.tryFindIndex anchorPattern.IsMatch
        |> Option.bind (fun anchor ->
            lines
            |> Array.indexed
            |> Array.skip anchor
            |> Array.tryFind (fun (_, line) -> targetPattern.IsMatch line)
            |> Option.map (fun (index, _) -> index + 1))

    let private quoted (value: string) = "\"" + Regex.Escape value + "\""

    let private keyPattern (key: string) = Regex(quoted key + @"\s*:", RegexOptions.CultureInvariant)
    let private versionKey = Regex("\"version\"\\s*:", RegexOptions.CultureInvariant)

    let private readJson (path: string) =
        try
            Some(JsonDocument.Parse(File.ReadAllBytes path))
        with _ ->
            None

    let private conditorPins (root: string) =
        let path = Path.Combine(root, "conditor.json")

        match (if File.Exists path then readJson path else None) with
        | None -> []
        | Some document ->
            use document = document
            let lines = File.ReadAllLines path

            objects "components" document.RootElement
            |> List.choose (fun entry ->
                match str "id" entry, str "version" entry with
                | Some id, Some version ->
                    Some
                        { SystemId = id
                          Source = "conditor.json"
                          Path = "conditor.json"
                          Line = lineOf lines (Regex("\"id\"\\s*:\\s*" + quoted id, RegexOptions.CultureInvariant)) versionKey
                          Specifier = version
                          Version = Some version
                          PinnedSince = None }
                | _ -> None)

    let private foundationsPins (root: string) (systems: Set<string>) =
        let path = Path.Combine(root, ".echelon", "foundations.json")

        match (if File.Exists path then readJson path else None) with
        | None -> []
        | Some document ->
            use document = document
            let lines = File.ReadAllLines path

            match tryProperty "capabilities" document.RootElement with
            | Some capabilities when capabilities.ValueKind = JsonValueKind.Object ->
                capabilities.EnumerateObject()
                |> Seq.filter (fun capability -> systems.Contains capability.Name)
                |> Seq.choose (fun capability ->
                    str "version" capability.Value
                    |> Option.map (fun version ->
                        { SystemId = capability.Name
                          Source = "foundations"
                          Path = ".echelon/foundations.json"
                          Line = lineOf lines (keyPattern capability.Name) versionKey
                          Specifier = version
                          Version = Some version
                          PinnedSince = None }))
                |> Seq.toList
            | _ -> []

    let private exactVersion = Regex(@"^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$", RegexOptions.CultureInvariant)

    let private packageJsonPins (root: string) (packages: Map<string, string>) (tarballs: Map<string, string * string>) =
        walk root (fun name -> name = "package.json")
        |> List.collect (fun path ->
            match readJson path with
            | None -> []
            | Some document ->
                use document = document
                let lines = File.ReadAllLines path
                let shown = relative root path

                [ "dependencies"; "devDependencies"; "optionalDependencies"; "peerDependencies" ]
                |> List.collect (fun section ->
                    match tryProperty section document.RootElement with
                    | Some values when values.ValueKind = JsonValueKind.Object ->
                        values.EnumerateObject()
                        |> Seq.filter (fun dependency -> packages.ContainsKey dependency.Name && dependency.Value.ValueKind = JsonValueKind.String)
                        |> Seq.map (fun dependency ->
                            let specifier = dependency.Value.GetString() |> Option.ofObj |> Option.defaultValue ""

                            let version =
                                if exactVersion.IsMatch specifier then
                                    Some specifier
                                else
                                    match tarballs |> Map.tryFind specifier with
                                    | Some(system, version) when system = packages[dependency.Name] -> Some version
                                    | _ -> None

                            { SystemId = packages[dependency.Name]
                              Source = "package.json"
                              Path = shown
                              Line =
                                lines
                                |> Array.tryFindIndex (fun line -> (keyPattern dependency.Name).IsMatch line)
                                |> Option.map ((+) 1)
                              Specifier = specifier
                              Version = version
                              PinnedSince = None })
                        |> Seq.toList
                    | _ -> []))

    let private nugetLockPins (root: string) =
        let directory = Path.Combine(root, "vendor", "nuget")

        if not (Directory.Exists directory) then
            []
        else
            Directory.EnumerateFiles(directory, "*.lock")
            |> Seq.sort
            |> Seq.choose (fun path ->
                let lines = File.ReadAllLines path

                let value (key: string) =
                    lines
                    |> Array.tryFindIndex (fun line -> line.StartsWith(key + " ", StringComparison.Ordinal))
                    |> Option.map (fun index -> index + 1, lines[index].Substring(key.Length + 1).Trim())

                if lines |> Array.exists (fun line -> line.Contains "conditor.nuget-feed-lock/v1") then
                    match value "system", value "version" with
                    | Some(_, system), Some(line, version) ->
                        Some
                            { SystemId = system
                              Source = "nuget-feed-lock"
                              Path = relative root path
                              Line = Some line
                              Specifier = version
                              Version = Some version
                              PinnedSince = None }
                    | _ -> None
                else
                    None)
            |> Seq.toList

    let private msbuildPin =
        Regex(
            """<(?:PackageVersion|PackageReference)\b[^>]*?\bInclude\s*=\s*"(?<id>[^"]+)"[^>]*?\bVersion\s*=\s*"(?<version>[^"]*)""",
            RegexOptions.CultureInvariant ||| RegexOptions.IgnoreCase
        )

    let private msbuildPins (root: string) (nugetIds: Map<string, string>) =
        walk root (fun name ->
            name = "Directory.Packages.props"
            || name = "Directory.Build.props"
            || name.EndsWith(".fsproj", StringComparison.Ordinal)
            || name.EndsWith(".csproj", StringComparison.Ordinal))
        |> List.collect (fun path ->
            File.ReadAllLines path
            |> Array.toList
            |> List.indexed
            |> List.collect (fun (index, line) ->
                msbuildPin.Matches line
                |> Seq.choose (fun matched ->
                    let id = matched.Groups["id"].Value

                    nugetIds
                    |> Map.tryFind (id.ToLowerInvariant())
                    |> Option.map (fun system ->
                        let version = matched.Groups["version"].Value

                        { SystemId = system
                          Source = "msbuild"
                          Path = relative root path
                          Line = Some(index + 1)
                          Specifier = $"{id} {version}"
                          Version = if exactVersion.IsMatch version then Some version else None
                          PinnedSince = None }))
                |> Seq.toList))

    /// The committer time of the commit that last changed a pin's line; None
    /// when Git history is unavailable or the line is uncommitted.
    let pinnedSince (run: string -> string -> string list -> ProcessResult) (root: string) (pin: DriftPin) =
        match pin.Line with
        | None -> None
        | Some line ->
            let result = run root "git" [ "blame"; "--porcelain"; "-L"; $"{line},{line}"; "--"; pin.Path ]

            if result.ExitCode <> 0 then
                None
            else
                let lines = result.StandardOutput.Split('\n')

                match lines |> Array.tryHead with
                | Some header when header.StartsWith(String.replicate 40 "0", StringComparison.Ordinal) -> None
                | _ ->
                    lines
                    |> Array.tryPick (fun text ->
                        if text.StartsWith("committer-time ", StringComparison.Ordinal) then
                            match Int64.TryParse(text.Substring("committer-time ".Length).Trim()) with
                            | true, seconds -> Some(DateTimeOffset.FromUnixTimeSeconds seconds)
                            | _ -> None
                        else
                            None)

    /// Every pin of a Registry component in the target repository.
    let discover (registry: DriftRegistry) (root: string) : DriftPin list =
        let all = registry.Releases |> Map.toList |> List.collect snd

        let packages =
            all
            |> List.collect (fun release -> release.NpmPackages |> List.map (fun package -> package, release.SystemId))
            |> List.distinctBy fst
            |> Map.ofList

        let tarballs =
            all
            |> List.collect (fun release -> release.TarballUrls |> List.map (fun url -> url, (release.SystemId, release.Version.ToString())))
            |> Map.ofList

        let nugetIds =
            all
            |> List.collect (fun release -> release.NugetPackages |> List.map (fun id -> id.ToLowerInvariant(), release.SystemId))
            |> List.distinctBy fst
            |> Map.ofList

        let systems = registry.Releases |> Map.keys |> Set.ofSeq

        conditorPins root
        @ packageJsonPins root packages tarballs
        @ foundationsPins root systems
        @ nugetLockPins root
        @ msbuildPins root nugetIds

    // ---- Assessment (pure) ------------------------------------------------------

    /// The new specifier of `pin` for `next`.
    let private updateFor (next: RegisteredRelease) (pin: DriftPin) : PinUpdate option =
        let target =
            match pin.Source with
            | "package.json" when pin.Specifier.StartsWith("https://", StringComparison.Ordinal) ->
                next.TarballUrls |> List.tryHead
            | "msbuild" ->
                let id = pin.Specifier.Split(' ')[0]
                Some $"{id} {next.Version}"
            | _ -> Some(next.Version.ToString())

        target
        |> Option.filter (fun value -> value <> pin.Specifier)
        |> Option.map (fun value ->
            { Source = pin.Source
              Path = pin.Path
              Line = pin.Line
              From = pin.Specifier
              To = value })

    /// One component's drift under `policy` at `now` (CON-300, CON-305,
    /// CON-307, CON-316..319).
    let assess
        (registry: DriftRegistry)
        (policy: StalenessPolicy)
        (now: DateTimeOffset)
        (stepwiseCommand: string option)
        (id: string)
        (pins: DriftPin list)
        : ComponentDrift =
        let registered = registry.Releases |> Map.tryFind id |> Option.defaultValue []
        let declared = pins |> List.exists (fun pin -> pin.Source = "conditor.json")

        let resolved =
            pins |> List.choose (fun pin -> pin.Version |> Option.bind SemanticVersion.tryParse |> Option.map (fun v -> pin, v))

        let unresolved = pins |> List.filter (fun pin -> pin.Version |> Option.bind SemanticVersion.tryParse |> Option.isNone)
        let pinned = resolved |> List.map snd |> List.sort |> List.tryHead
        let consistent = (resolved |> List.map snd |> List.distinct |> List.length) <= 1
        let selected = registry.Selected |> Map.tryFind id
        let selectedVersion = selected |> Option.bind SemanticVersion.tryParse
        let latest = registered |> List.tryLast
        let range = registry.SupportedRanges |> Map.tryFind id

        let newer =
            match pinned with
            | Some version -> registered |> List.filter (fun release -> release.Version > version)
            | None -> []

        let releasesBehind = pinned |> Option.map (fun _ -> newer.Length)

        let minorsBehind =
            pinned
            |> Option.map (fun version ->
                newer |> List.map _.Version.Line |> List.filter (fun line -> line > version.Line) |> List.distinct |> List.length)

        let majorsBehind =
            Option.map2 (fun (version: SemanticVersion) (release: RegisteredRelease) -> max 0 (release.Version.Major - version.Major)) pinned latest

        let below =
            Option.map2
                (fun (version: SemanticVersion) (text: string) -> VersionRange.tryParse text |> Option.map (fun comparators -> VersionRange.isBelow comparators version))
                pinned
                range
            |> Option.flatten

        let isRegistered =
            pinned |> Option.exists (fun version -> registered |> List.exists (fun release -> release.Version = version))

        let pinAgeDays =
            pinned
            |> Option.bind (fun version ->
                resolved
                |> List.filter (fun (_, v) -> v = version)
                |> List.choose (fun (pin, _) -> pin.PinnedSince)
                |> List.sort
                |> List.tryHead)
            |> Option.map (fun since -> int (floor (now - since).TotalDays) |> max 0)

        let behindSince =
            newer |> List.tryHead |> Option.bind (fun release -> registry.RegisteredAt |> Map.tryFind release.ManifestPath)

        // One registered minor line at a time, bounded by the channel's
        // selection when it selects something at or above the pin.
        let nextStep =
            pinned
            |> Option.bind (fun version ->
                let bound =
                    match selectedVersion with
                    | Some chosen when chosen >= version -> Some chosen
                    | Some _ -> None
                    | None -> latest |> Option.map _.Version

                let candidates =
                    match bound with
                    | Some limit -> newer |> List.filter (fun release -> release.Version <= limit)
                    | None -> []

                if candidates.IsEmpty then
                    None
                else
                    let above = candidates |> List.filter (fun release -> release.Version.Line > version.Line)

                    let line =
                        if above.IsEmpty then version.Line else above |> List.map _.Version.Line |> List.min

                    let next = candidates |> List.filter (fun release -> release.Version.Line = line) |> List.last
                    let crossed = candidates |> List.filter (fun release -> release.Version <= next.Version)

                    Some
                        { Version = next.Version.ToString()
                          Crosses =
                            crossed
                            |> List.map (fun release ->
                                { Version = release.Version.ToString()
                                  MigrationNotes = release.MigrationNotes })
                          PinUpdates =
                            pins
                            |> List.filter (fun pin -> pin.Source <> "conditor.json")
                            |> List.choose (updateFor next)
                          Command = if declared then stepwiseCommand else None
                          Declare =
                            if not declared && pins |> List.exists (fun pin -> pin.Source = "package.json") then
                                Some $"{{ \"id\": \"{id}\", \"version\": \"{version}\", \"required\": true }}"
                            else
                                None })

        let exceeds (threshold: int option) (value: int option) =
            match threshold, value with
            | Some limit, Some actual -> actual > limit
            | _ -> false

        let behind = releasesBehind |> Option.exists (fun count -> count > 0)

        let reasons =
            [ if registered.IsEmpty then
                  yield DriftInfo, $"'{id}' has no registered release in the {registry.Channel} catalog."
              if pins |> List.isEmpty |> not && pinned.IsNone then
                  yield DriftWarn, "no pin names a parseable version."
              for pin in unresolved do
                  yield DriftWarn, $"{pin.Path} pins \"{pin.Specifier}\", which is not a registered release; Conditor will not guess its version."
              if not consistent then
                  let versions =
                      resolved |> List.map (fun (pin, v) -> $"{pin.Path} {v}") |> String.concat ", "

                  yield DriftWarn, $"pins disagree ({versions}); the oldest is the effective pin."
              if pinned.IsSome && not registered.IsEmpty && not isRegistered then
                  yield DriftWarn, $"the pinned {pinned.Value} is not a registered release."
              if policy.FailBelowSupportedRange && below = Some true then
                  yield DriftFail, $"{pinned.Value} is below the supported range {range.Value} of profile {registry.ProfileId}@{registry.ProfileVersion}."
              if exceeds policy.WarnAfterMinorsBehind minorsBehind then
                  yield DriftWarn, $"{minorsBehind.Value} registered minor lines behind (warn above {policy.WarnAfterMinorsBehind.Value})."
              if exceeds policy.WarnAfterMajorsBehind majorsBehind then
                  yield DriftWarn, $"{majorsBehind.Value} major versions behind (warn above {policy.WarnAfterMajorsBehind.Value})."
              if exceeds policy.WarnAfterReleasesBehind releasesBehind then
                  yield DriftWarn, $"{releasesBehind.Value} registered releases behind (warn above {policy.WarnAfterReleasesBehind.Value})."
              if behind && exceeds policy.WarnAfterPinAgeDays pinAgeDays then
                  yield DriftWarn, $"pinned for {pinAgeDays.Value} days while behind (warn above {policy.WarnAfterPinAgeDays.Value})."
              if behind then
                  yield DriftInfo, $"{releasesBehind.Value} registered release(s) newer than {pinned.Value}."
              match pinned, latest with
              | Some version, Some newest when version > newest.Version ->
                  yield DriftInfo, $"{version} is newer than every registered release (newest {newest.Version})."
              | _ -> ()
              match selected, pinned with
              | None, Some _ when not registered.IsEmpty ->
                  yield DriftInfo, $"the {registry.Channel} channel does not select '{id}'."
              | _ -> () ]

        let level =
            match reasons |> List.map fst with
            | [] -> DriftOk
            | levels -> List.max levels

        { Id = id
          Pins = pins
          Declared = declared
          PinnedVersion = pinned |> Option.map string
          Consistent = consistent && unresolved.IsEmpty
          ChannelSelected = selected
          LatestRegistered = latest |> Option.map (fun release -> release.Version.ToString())
          ReleasesBehind = releasesBehind
          MinorsBehind = minorsBehind
          MajorsBehind = majorsBehind
          SupportedRange = range
          BelowSupportedRange = below
          PinAgeDays = pinAgeDays
          BehindSince = behindSince
          Level = level
          Reasons = reasons |> List.map snd
          NextStep = nextStep }

    /// The repository's `stalenessPolicy` overrides from conditor.json, if any.
    let repositoryPolicy (root: string) : Result<StalenessPolicy.Override list, string list> =
        let path = Path.Combine(root, "conditor.json")

        if not (File.Exists path) then
            Ok []
        else
            try
                use document = JsonDocument.Parse(File.ReadAllBytes path)

                match tryProperty "stalenessPolicy" document.RootElement with
                | None -> Ok []
                | Some policy -> StalenessPolicy.parse "conditor.json" policy
            with :? JsonException as ex ->
                Error [ $"conditor.json is not valid JSON: {ex.Message}" ]

    let private stepwiseCommand (registry: DriftRegistry) (target: string) =
        let setPath = Path.Combine(registry.Root, registry.ResolvedSetPath)

        $"conditor upgrade --current --check --stepwise --registry \"{registry.Root}\" --channel {registry.Channel} --resolved-set \"{setPath}\" --resolved-set-sha256 {registry.ResolvedSetSha256} --target \"{target}\""

    /// The whole advisory for `target` (read-only).
    let report
        (run: string -> string -> string list -> ProcessResult)
        (now: DateTimeOffset)
        (registry: DriftRegistry)
        (target: string)
        : Result<DriftReport, string list> =
        let root = Path.GetFullPath target

        if not (Directory.Exists root) then
            Error [ $"Target repository not found: {root}" ]
        else
            repositoryPolicy root
            |> Result.map (fun repositoryOverrides ->
                let policy, sources = StalenessPolicy.resolve registry.PolicyOverrides repositoryOverrides
                let gitAvailable = (run root "git" [ "rev-parse"; "--is-inside-work-tree" ]).ExitCode = 0

                let pins =
                    discover registry root
                    |> List.map (fun pin ->
                        if gitAvailable then { pin with PinnedSince = pinnedSince run root pin } else pin)

                let command = stepwiseCommand registry root

                let components =
                    pins
                    |> List.groupBy _.SystemId
                    |> List.sortBy fst
                    |> List.map (fun (id, items) -> assess registry policy now (Some command) id items)

                { Target = root
                  ObservedAt = now
                  Registry = registry
                  Policy = policy
                  PolicySources = sources
                  Components = components
                  Level =
                    match components |> List.map _.Level with
                    | [] -> DriftOk
                    | levels -> List.max levels })

    /// The advisory's exit code (CON-308): 0 unless `failOn` is `warn` or
    /// `fail` and the overall level reaches it, then 3.
    let exitCodeFor (failOn: string) (level: DriftLevel) =
        match failOn, level with
        | "warn", (DriftWarn | DriftFail)
        | "fail", DriftFail -> 3
        | _ -> 0

    // ---- Rendering (CON-306) ------------------------------------------------

    let private timestamp (value: DateTimeOffset) =
        value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", Globalization.CultureInfo.InvariantCulture)

    let toJson (report: DriftReport) : string =
        use stream = new MemoryStream()

        do
            use writer = new Utf8JsonWriter(stream, JsonWriterOptions(Indented = true))

            let optString (name: string) (value: string option) =
                match value with
                | Some text -> writer.WriteString(name, text)
                | None -> writer.WriteNull name

            let optInt (name: string) (value: int option) =
                match value with
                | Some number -> writer.WriteNumber(name, number)
                | None -> writer.WriteNull name

            let optBool (name: string) (value: bool option) =
                match value with
                | Some flag -> writer.WriteBoolean(name, flag)
                | None -> writer.WriteNull name

            let optTime (name: string) (value: DateTimeOffset option) = optString name (value |> Option.map timestamp)

            writer.WriteStartObject()
            writer.WriteString("schema", Schema)
            writer.WriteString("target", report.Target)
            writer.WriteString("observedAt", timestamp report.ObservedAt)
            writer.WriteStartObject "registry"
            writer.WriteString("path", report.Registry.Root)
            writer.WriteString("channel", report.Registry.Channel)
            writer.WriteString("channelSha256", report.Registry.ChannelSha256)
            writer.WriteString("platform", report.Registry.Platform)
            writer.WriteStartObject "profile"
            writer.WriteString("id", report.Registry.ProfileId)
            writer.WriteString("version", report.Registry.ProfileVersion)
            writer.WriteEndObject()
            writer.WriteString("resolvedSet", report.Registry.ResolvedSetPath)
            writer.WriteString("resolvedSetSha256", report.Registry.ResolvedSetSha256)
            writer.WriteString("catalogSnapshotSha256", report.Registry.CatalogSnapshotSha256)
            writer.WriteEndObject()
            writer.WriteStartObject "policy"
            optInt "warnAfterMinorsBehind" report.Policy.WarnAfterMinorsBehind
            optInt "warnAfterMajorsBehind" report.Policy.WarnAfterMajorsBehind
            optInt "warnAfterReleasesBehind" report.Policy.WarnAfterReleasesBehind
            optInt "warnAfterPinAgeDays" report.Policy.WarnAfterPinAgeDays
            writer.WriteBoolean("failBelowSupportedRange", report.Policy.FailBelowSupportedRange)
            writer.WriteStartObject "sources"

            for field, source in report.PolicySources do
                writer.WriteString(field, source)

            writer.WriteEndObject()
            writer.WriteEndObject()
            writer.WriteString("level", DriftLevel.toWire report.Level)
            writer.WriteStartArray "components"

            for item in report.Components do
                writer.WriteStartObject()
                writer.WriteString("id", item.Id)
                writer.WriteBoolean("declared", item.Declared)
                writer.WriteStartArray "pins"

                for pin in item.Pins do
                    writer.WriteStartObject()
                    writer.WriteString("source", pin.Source)
                    writer.WriteString("path", pin.Path)
                    optInt "line" pin.Line
                    writer.WriteString("specifier", pin.Specifier)
                    optString "version" pin.Version
                    optTime "pinnedSince" pin.PinnedSince
                    writer.WriteEndObject()

                writer.WriteEndArray()
                optString "pinnedVersion" item.PinnedVersion
                writer.WriteBoolean("consistent", item.Consistent)
                optString "channelSelected" item.ChannelSelected
                optString "latestRegistered" item.LatestRegistered
                optInt "releasesBehind" item.ReleasesBehind
                optInt "minorsBehind" item.MinorsBehind
                optInt "majorsBehind" item.MajorsBehind
                optString "supportedRange" item.SupportedRange
                optBool "belowSupportedRange" item.BelowSupportedRange
                optInt "pinAgeDays" item.PinAgeDays
                optTime "behindSince" item.BehindSince
                writer.WriteString("level", DriftLevel.toWire item.Level)
                writer.WriteStartArray "reasons"

                for reason in item.Reasons do
                    writer.WriteStringValue reason

                writer.WriteEndArray()

                match item.NextStep with
                | None -> writer.WriteNull "nextStep"
                | Some step ->
                    writer.WriteStartObject "nextStep"
                    writer.WriteString("version", step.Version)
                    writer.WriteStartArray "crosses"

                    for crossed in step.Crosses do
                        writer.WriteStartObject()
                        writer.WriteString("version", crossed.Version)
                        writer.WriteString("migrationNotes", crossed.MigrationNotes)
                        writer.WriteEndObject()

                    writer.WriteEndArray()
                    writer.WriteStartArray "pinUpdates"

                    for update in step.PinUpdates do
                        writer.WriteStartObject()
                        writer.WriteString("source", update.Source)
                        writer.WriteString("path", update.Path)
                        optInt "line" update.Line
                        writer.WriteString("from", update.From)
                        writer.WriteString("to", update.To)
                        writer.WriteEndObject()

                    writer.WriteEndArray()
                    optString "command" step.Command
                    optString "declare" step.Declare
                    writer.WriteEndObject()

                writer.WriteEndObject()

            writer.WriteEndArray()
            writer.WriteEndObject()

        Encoding.UTF8.GetString(stream.ToArray()) + "\n"

    let toText (report: DriftReport) : string =
        let builder = StringBuilder()
        let line (text: string) = builder.AppendLine text |> ignore
        let registry = report.Registry
        let orUnavailable value = value |> Option.defaultValue "unavailable"

        line $"Conditor drift: {report.Target}"
        line $"Registry: {registry.Root} channel {registry.Channel} (channel.json sha256:{registry.ChannelSha256})"
        line $"  profile {registry.ProfileId}@{registry.ProfileVersion}, platform {registry.Platform}, set sha256:{registry.ResolvedSetSha256}"

        let policyText =
            let value =
                function
                | Some (n: int) -> string n
                | None -> "off"

            let sources = report.PolicySources |> Map.ofList
            let source (field: string) = sources |> Map.tryFind field |> Option.defaultValue "default"

            let minors = value report.Policy.WarnAfterMinorsBehind
            let majors = value report.Policy.WarnAfterMajorsBehind
            let releases = value report.Policy.WarnAfterReleasesBehind
            let age = value report.Policy.WarnAfterPinAgeDays
            let fail = report.Policy.FailBelowSupportedRange.ToString().ToLowerInvariant()

            [ "warnAfterMinorsBehind", minors
              "warnAfterMajorsBehind", majors
              "warnAfterReleasesBehind", releases
              "warnAfterPinAgeDays", age
              "failBelowSupportedRange", fail ]
            |> List.map (fun (field, setting) -> $"{field}={setting} ({source field})")
            |> String.concat ", "

        line $"Policy: {policyText}"
        line $"Overall level: {DriftLevel.toWire report.Level}"

        for item in report.Components do
            line ""

            let sources =
                item.Pins |> List.map (fun pin -> pin.Path) |> List.distinct |> String.concat ", "

            line $"{item.Id} [{DriftLevel.toWire item.Level}] pinned {orUnavailable item.PinnedVersion} ({sources})"

            let count (value: int option) = value |> Option.map string |> Option.defaultValue "?"

            line
                $"  channel selects {orUnavailable item.ChannelSelected}; latest registered {orUnavailable item.LatestRegistered}; behind: {count item.ReleasesBehind} release(s), {count item.MinorsBehind} minor line(s), {count item.MajorsBehind} major(s)"

            let range =
                match item.SupportedRange, item.BelowSupportedRange with
                | Some text, Some true -> $"BELOW supported range {text}"
                | Some text, _ -> $"supported range {text}"
                | None, _ -> "no supported range"

            let age = item.PinAgeDays |> Option.map (fun days -> $"{days} day(s)") |> Option.defaultValue "unavailable"
            let since = item.BehindSince |> Option.map timestamp |> Option.defaultValue "unavailable"
            line $"  {range}; pin age {age}; behind since {since}"

            for reason in item.Reasons do
                line $"  - {reason}"

            match item.NextStep with
            | None -> line "  next step: none"
            | Some step ->
                line $"  next step: {step.Version}"

                for crossed in step.Crosses do
                    line $"    crosses {crossed.Version}: {crossed.MigrationNotes}"

                for update in step.PinUpdates do
                    let at = update.Line |> Option.map (fun n -> $":{n}") |> Option.defaultValue ""
                    line $"    {update.Path}{at}: \"{update.From}\" -> \"{update.To}\""

                match step.Command, step.Declare with
                | Some command, _ -> line $"    governed by Conditor: {command}"
                | None, Some entry ->
                    line $"    not declared in conditor.json: add {entry} to its components, then run upgrade --current --check (docs/web-package-upgrade-contract.md)"
                | None, None -> line "    not declared in conditor.json; Conditor does not move these pins"

        builder.ToString()
