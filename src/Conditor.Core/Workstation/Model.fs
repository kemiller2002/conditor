namespace Conditor.Core.Workstation

open System
open System.IO
open System.Reflection
open System.Runtime.InteropServices
open System.Text.Json

// Workstation bootstrap (CON-130..254): versioned profiles, host prerequisite
// discovery, plan-before-authorization, per-step expected/observed receipts
// (match / mismatch / indeterminate, consistent with Ordo's receipt
// semantics), a durable ownership ledger, deterministic uninstall and
// ledger-driven rollback. Conditor establishes the host; Praxis remains the
// runtime authority for work after handoff (CON-240..245).

/// A host prerequisite the profile expects (CON-170..175).
type Prerequisite =
    { Id: string
      MinimumVersion: string option
      Required: bool }

/// A pinned native release asset for one runtime identifier.
type ReleaseAsset = { Name: string; Sha256: string }

/// The Registry-defined generic repository lifecycle contract a resolved
/// release declared (echelon-registry spec/repository-lifecycle-contract.md).
/// Conditor knows only the contract, never the component's repository state.
type RepositoryLifecycle =
    { ContractVersion: int
      /// The immutable source commit the release was built from; the
      /// installed executable must report it as its identity.
      SourceCommit: string }

/// A component the profile installs from a checksummed GitHub release
/// (CON-133 pinned artifacts with integrity checks).
type ProfileComponent =
    { Id: string
      Version: string
      Executable: string
      VersionProbe: string list
      Repository: string
      Tag: string
      Assets: Map<string, ReleaseAsset>
      /// The Registry environment role, when the component came from a
      /// resolved release set.
      Role: string option
      /// Present only when the selected release declared a supported
      /// repository lifecycle contract.
      Lifecycle: RepositoryLifecycle option }

module RepositoryLifecycleContract =
    [<Literal>]
    let Capability = "echelon.repository-lifecycle"

    /// Contract versions this Conditor implements.
    let supportedVersions = set [ 1 ]

    /// Every v1 operation's executable-level verb.
    [<Literal>]
    let VersionOperation = "version"

/// A versioned, declarative, composable workstation profile (CON-160..166).
type WorkstationProfile =
    { Id: string
      Version: string
      /// Immutable source identity for externally resolved profiles. This is
      /// included in the plan digest so authorization is bound to the exact
      /// Registry decision, not only to the resulting component list.
      SourceIdentity: string option
      Extends: string option
      Description: string
      Prerequisites: Prerequisite list
      Components: ProfileComponent list
      /// Capabilities that exist but are installed only when selected.
      Optional: (string * string) list
      Providers: string list }

/// What Conditor may claim about a resource (CON-200..206).
[<RequireQualifiedAccess>]
type Ownership =
    | ConditorCreated
    | Adopted
    | Shared
    | External
    | OtherEchelon
    | UserOwned

[<RequireQualifiedAccess>]
module Ownership =
    let toWire o =
        match o with
        | Ownership.ConditorCreated -> "conditor-created"
        | Ownership.Adopted -> "adopted"
        | Ownership.Shared -> "shared"
        | Ownership.External -> "external"
        | Ownership.OtherEchelon -> "other-echelon"
        | Ownership.UserOwned -> "user-owned"

    let fromWire raw =
        [ Ownership.ConditorCreated; Ownership.Adopted; Ownership.Shared; Ownership.External; Ownership.OtherEchelon; Ownership.UserOwned ]
        |> List.tryFind (fun o -> toWire o = raw)

/// Classification of a discovered prerequisite (CON-170).
[<RequireQualifiedAccess>]
type PrerequisiteState =
    | Satisfied of version: string
    | Installable
    | ExternalOnly of reason: string
    | Unsupported of reason: string
    | Unknown of reason: string

module Profiles =
    let private assembly = typeof<Prerequisite>.Assembly

    let embeddedNames = [ "minimal"; "echelon-engineering" ]

    let private tryProperty (name: string) (element: JsonElement) =
        let mutable value = Unchecked.defaultof<JsonElement>
        if element.ValueKind = JsonValueKind.Object && element.TryGetProperty(name, &value) then Some value else None

    let private str name element =
        match tryProperty name element with
        | Some v when v.ValueKind = JsonValueKind.String -> v.GetString() |> Option.ofObj
        | _ -> None

    let private strings name element =
        match tryProperty name element with
        | Some v when v.ValueKind = JsonValueKind.Array ->
            v.EnumerateArray() |> Seq.choose (fun x -> if x.ValueKind = JsonValueKind.String then x.GetString() |> Option.ofObj else None) |> Seq.toList
        | _ -> []

    let private objects name element =
        match tryProperty name element with
        | Some v when v.ValueKind = JsonValueKind.Array -> v.EnumerateArray() |> Seq.toList
        | _ -> []

    let private isSha256 (value: string) = value.Length = 64 && value |> Seq.forall (fun c -> Char.IsAsciiHexDigitLower c)

    let private parseComponent (e: JsonElement) : Result<ProfileComponent, string> =
        let source = tryProperty "source" e

        let assets =
            match tryProperty "assets" e with
            | Some a when a.ValueKind = JsonValueKind.Object ->
                a.EnumerateObject()
                |> Seq.choose (fun p ->
                    match str "name" p.Value, str "sha256" p.Value with
                    | Some n, Some s -> Some(p.Name, { Name = n; Sha256 = s })
                    | _ -> None)
                |> Map.ofSeq
            | _ -> Map.empty

        match str "id" e, str "version" e, str "executable" e, source |> Option.bind (str "repository"), source |> Option.bind (str "tag") with
        | Some id, Some version, Some exe, Some repo, Some tag ->
            match source |> Option.bind (str "kind") with
            | Some "github-release" when assets |> Map.forall (fun _ a -> isSha256 a.Sha256) && not assets.IsEmpty ->
                Ok
                    { Id = id
                      Version = version
                      Executable = exe
                      VersionProbe = (match strings "versionProbe" e with [] -> [ "--version" ] | p -> p)
                      Repository = repo
                      Tag = tag
                      Assets = assets
                      Role = None
                      Lifecycle = None }
            | _ -> Error $"component '{id}' must pin a github-release source with sha256 digests for every asset"
        | _ -> Error "a profile component needs id, version, executable and a source repository/tag"

    let parse (json: string) : Result<WorkstationProfile, string> =
        try
            use document = JsonDocument.Parse json
            let root = document.RootElement

            if str "schema" root <> Some "conditor.workstation-profile/v1" then
                Error "a workstation profile must declare schema conditor.workstation-profile/v1"
            else
                let components =
                    objects "components" root
                    |> List.map parseComponent
                    |> List.fold (fun acc r -> acc |> Result.bind (fun xs -> r |> Result.map (fun x -> x :: xs))) (Ok [])
                    |> Result.map List.rev

                let version =
                    match tryProperty "version" root with
                    | Some v when v.ValueKind = JsonValueKind.Number ->
                        match v.TryGetInt32() with
                        | true, value when value > 0 -> Some(string value)
                        | _ -> None
                    | Some v when v.ValueKind = JsonValueKind.String ->
                        v.GetString() |> Option.ofObj |> Option.filter (String.IsNullOrWhiteSpace >> not)
                    | _ -> None

                match str "id" root, version, components with
                | None, _, _ -> Error "a workstation profile needs an id"
                | _, _, Error e -> Error e
                | _, None, _ -> Error "a workstation profile needs a positive integer or non-empty semantic version"
                | Some id, Some version, Ok cs ->
                    Ok
                        { Id = id
                          Version = version
                          SourceIdentity = None
                          Extends = str "extends" root
                          Description = str "description" root |> Option.defaultValue ""
                          Prerequisites =
                            objects "prerequisites" root
                            |> List.choose (fun p ->
                                str "id" p
                                |> Option.map (fun pid ->
                                    { Id = pid
                                      MinimumVersion = str "minimumVersion" p
                                      Required = (match tryProperty "required" p with Some v -> v.ValueKind = JsonValueKind.True | None -> true) }))
                          Components = cs
                          Optional = objects "optional" root |> List.choose (fun o -> str "id" o |> Option.map (fun oid -> oid, str "reason" o |> Option.defaultValue ""))
                          Providers = tryProperty "providers" root |> Option.map (strings "selectable") |> Option.defaultValue [] }
        with :? JsonException as ex ->
            Error $"workstation profile is not valid JSON: {ex.Message}"

    let private readEmbedded (name: string) =
        assembly.GetManifestResourceStream($"Conditor.Profiles.{name}.profile.json")
        |> Option.ofObj
        |> Option.map (fun stream ->
            use reader = new StreamReader(stream)
            reader.ReadToEnd())

    /// Merge a profile over its base: later declarations win by id.
    let private compose (baseProfile: WorkstationProfile) (profile: WorkstationProfile) =
        let byId (f: 'a -> string) (xs: 'a list) (ys: 'a list) =
            let overridden = ys |> List.map f |> Set.ofList
            (xs |> List.filter (fun x -> not (overridden.Contains(f x)))) @ ys

        { profile with
            Prerequisites = byId (fun p -> p.Id) baseProfile.Prerequisites profile.Prerequisites
            Components = byId (fun c -> c.Id) baseProfile.Components profile.Components
            Optional = byId fst baseProfile.Optional profile.Optional
            Providers = baseProfile.Providers @ profile.Providers |> List.distinct }

    /// Resolve a profile by embedded name or file path, following `extends`.
    let rec resolve (nameOrPath: string) : Result<WorkstationProfile, string> =
        let text =
            if File.Exists nameOrPath then Some(File.ReadAllText nameOrPath)
            else readEmbedded nameOrPath

        match text with
        | None ->
            let known = String.concat ", " embeddedNames
            Error $"unknown workstation profile '{nameOrPath}' (embedded: {known})"
        | Some json ->
            parse json
            |> Result.bind (fun profile ->
                match profile.Extends with
                | None -> Ok profile
                | Some parent when parent = profile.Id -> Error $"profile '{profile.Id}' extends itself"
                | Some parent -> resolve parent |> Result.map (fun b -> compose b profile))

/// A Registry-resolved release set adapted to the existing workstation
/// installation engine. This is deliberately strict: the first integration
/// supports native host tools only. Other distribution classes require their
/// own explicit binding/install semantics and are refused here.
module ResolvedReleaseSets =
    let private tryProperty (name: string) (element: JsonElement) =
        let mutable value = Unchecked.defaultof<JsonElement>
        if element.ValueKind = JsonValueKind.Object && element.TryGetProperty(name, &value) then Some value else None

    let private str name element =
        match tryProperty name element with
        | Some v when v.ValueKind = JsonValueKind.String -> v.GetString() |> Option.ofObj
        | _ -> None

    let private boolValue name element =
        match tryProperty name element with
        | Some v when v.ValueKind = JsonValueKind.True -> Some true
        | Some v when v.ValueKind = JsonValueKind.False -> Some false
        | _ -> None

    let private objects name element =
        match tryProperty name element with
        | Some v when v.ValueKind = JsonValueKind.Array -> v.EnumerateArray() |> Seq.toList
        | _ -> []

    let private isSha256 (value: string) =
        value.Length = 64 && value |> Seq.forall Char.IsAsciiHexDigitLower

    let private normalizeSha256 (value: string) =
        if value.StartsWith("sha256:", StringComparison.Ordinal) then value.Substring("sha256:".Length) else value

    let private sha256Bytes (bytes: byte array) =
        Security.Cryptography.SHA256.HashData bytes
        |> Convert.ToHexString
        |> fun value -> value.ToLowerInvariant()

    let private isCommit (value: string) =
        value.Length = 40 && value |> Seq.forall Char.IsAsciiHexDigitLower

    /// The declared repository lifecycle contract, refused unless this
    /// Conditor implements its exact version. Absent means the release
    /// declared none.
    let private parseLifecycle (id: string) (element: JsonElement) : Result<RepositoryLifecycle option, string> =
        match tryProperty "repositoryLifecycle" element with
        | None -> Ok None
        | Some declared ->
            let contractVersion =
                match tryProperty "contractVersion" declared with
                | Some v when v.ValueKind = JsonValueKind.Number ->
                    match v.TryGetInt32() with
                    | true, parsed -> Some parsed
                    | _ -> None
                | _ -> None

            match str "contract" declared, contractVersion, str "commit" element with
            | Some RepositoryLifecycleContract.Capability, Some version, Some commit when RepositoryLifecycleContract.supportedVersions.Contains version && isCommit commit ->
                Ok(Some { ContractVersion = version; SourceCommit = commit })
            | Some RepositoryLifecycleContract.Capability, Some version, _ when not (RepositoryLifecycleContract.supportedVersions.Contains version) ->
                let supported = RepositoryLifecycleContract.supportedVersions |> Seq.map string |> String.concat ", "
                Error $"resolved component '{id}' declares unsupported repository lifecycle contract version {version}; this Conditor supports {supported}"
            | Some RepositoryLifecycleContract.Capability, Some _, _ ->
                Error $"resolved component '{id}' declares a repository lifecycle contract without a 40-character source commit"
            | Some other, _, _ when other <> RepositoryLifecycleContract.Capability ->
                Error $"resolved component '{id}' declares unknown repository lifecycle contract '{other}'"
            | _ ->
                Error $"resolved component '{id}' has a malformed repositoryLifecycle declaration"

    let private parseComponent (platform: string) (element: JsonElement) : Result<ProfileComponent option, string> =
        let id = str "systemId" element |> Option.defaultValue "<unknown>"
        let role = str "role" element
        let distributionClass = str "distributionClass" element
        let lifecycleState = str "lifecycleState" element
        let executable = str "executable" element

        let distributionMechanism =
            tryProperty "distribution" element
            |> Option.bind (str "mechanism")

        let executableArtifacts =
            objects "artifacts" element
            |> List.choose (fun artifact ->
                match str "purpose" artifact, str "platform" artifact, str "name" artifact, str "sha256" artifact with
                | Some "executable", Some artifactPlatform, Some name, Some digest when artifactPlatform = platform && isSha256 digest ->
                    Some { Name = name; Sha256 = digest }
                | _ -> None)

        match role with
        | Some "project-binding" ->
            match distributionClass, lifecycleState, distributionMechanism with
            | Some ("web-package" | "nuget-library"), Some "active", Some ("github-release" | "npm" | "nuget") ->
                // The full environment set stays integrity-bound, but workstation
                // bootstrap must not guess a project target for libraries/assets.
                Ok None
            | Some other, _, _ ->
                Error $"resolved project binding '{id}' has unsupported distribution class '{other}'"
            | _, Some state, _ when state <> "active" ->
                Error $"resolved project binding '{id}' is {state}; normal binding accepts only active releases"
            | _, _, Some mechanism ->
                Error $"resolved project binding '{id}' uses unsupported distribution mechanism '{mechanism}'"
            | _ ->
                Error $"resolved project binding '{id}' is missing required release facts"
        | Some(("host-tool" | "repository-lifecycle") as environmentRole) ->
            match distributionClass, lifecycleState, distributionMechanism, executable, str "version" element, str "repository" element, str "tag" element with
            | Some "self-contained-native-cli", Some "active", Some "github-release", Some exe, Some version, Some repository, Some tag ->
                match executableArtifacts, parseLifecycle id element with
                | _, Error e -> Error e
                | [ asset ], Ok lifecycle ->
                    Ok(
                        Some
                            { Id = id
                              Version = version
                              Executable = exe
                              // The contract defines the identity probe; other releases keep the conventional flag.
                              VersionProbe =
                                match lifecycle with
                                | Some _ -> [ RepositoryLifecycleContract.VersionOperation ]
                                | None -> [ "--version" ]
                              Repository = repository
                              Tag = tag
                              Assets = Map.ofList [ platform, asset ]
                              Role = Some environmentRole
                              Lifecycle = lifecycle }
                    )
                | [], _ -> Error $"resolved component '{id}' has no digest-verified executable artifact for {platform}"
                | _, _ -> Error $"resolved component '{id}' has more than one executable artifact for {platform}; selection is ambiguous"
            | Some "self-contained-native-cli", Some "active", Some "github-release", None, _, _, _ ->
                Error $"resolved component '{id}' has no executable; a native release must name the executable Conditor installs"
            | Some other, _, _, _, _, _, _ when other <> "self-contained-native-cli" ->
                Error $"resolved component '{id}' has distribution class '{other}'; workstation native installation accepts only self-contained-native-cli"
            | _, Some state, _, _, _, _, _ when state <> "active" ->
                Error $"resolved component '{id}' is {state}; normal installation accepts only active releases"
            | _, _, Some mechanism, _, _, _, _ when mechanism <> "github-release" ->
                Error $"resolved component '{id}' uses distribution mechanism '{mechanism}'; native workstation installation accepts only github-release"
            | _ ->
                Error $"resolved component '{id}' is missing required native-host release facts"
        | Some other ->
            Error $"resolved component '{id}' has unsupported environment role '{other}'"
        | None ->
            Error $"resolved component '{id}' is missing role"

    let parseVerified (expectedRuntimeIdentifier: string) (expectedSha256: string) (bytes: byte array) : Result<WorkstationProfile, string> =
        let expected = normalizeSha256 expectedSha256

        if not (isSha256 expected) then
            Error "resolved release set requires an expected 64-character lowercase SHA-256"
        else
            let actual = sha256Bytes bytes

            if actual <> expected then
                Error $"resolved release set digest mismatch: expected sha256:{expected}, observed sha256:{actual}"
            else
                try
                    use document = JsonDocument.Parse bytes
                    let root = document.RootElement

                    if str "schema" root <> Some "echelon.resolved-release-set/v1" then
                        Error "resolved release set must declare schema echelon.resolved-release-set/v1"
                    else
                        let profile = tryProperty "profile" root
                        let snapshot = tryProperty "catalogSnapshot" root
                        let platform = str "platform" root

                        match profile, snapshot, platform with
                        | Some profileRef, Some snapshotRef, Some resolvedPlatform when resolvedPlatform <> expectedRuntimeIdentifier ->
                            Error $"resolved release set targets {resolvedPlatform}, but this host is {expectedRuntimeIdentifier}"
                        | Some profileRef, Some snapshotRef, Some resolvedPlatform ->
                            match str "id" profileRef, str "version" profileRef, str "sha256" profileRef, str "sha256" snapshotRef with
                            | Some profileId, Some profileVersion, Some profileSha, Some snapshotSha when isSha256 profileSha && isSha256 snapshotSha ->
                                let parsedComponents =
                                    objects "components" root
                                    |> List.map (parseComponent resolvedPlatform)
                                    |> List.fold
                                        (fun state item ->
                                            state
                                            |> Result.bind (fun values ->
                                                item |> Result.map (fun value -> value |> Option.map (fun parsed -> parsed :: values) |> Option.defaultValue values)))
                                        (Ok [])
                                    |> Result.map List.rev

                                parsedComponents
                                |> Result.bind (fun components ->
                                    if components.IsEmpty then
                                        Error "resolved release set contains no installable host components"
                                    else
                                        let ids = components |> List.map _.Id
                                        if (ids |> List.distinct |> List.length) <> ids.Length then
                                            Error "resolved release set contains duplicate system ids"
                                        else
                                            Ok
                                                { Id = profileId
                                                  Version = profileVersion
                                                  SourceIdentity =
                                                    Some $"resolved-set=sha256:{actual};profile=sha256:{profileSha};catalog=sha256:{snapshotSha}"
                                                  Extends = None
                                                  Description = $"Registry-resolved {profileId}@{profileVersion}"
                                                  Prerequisites = []
                                                  Components = components
                                                  Optional = []
                                                  Providers = [] })
                            | _ -> Error "resolved release set profile/catalog identity is incomplete or has an invalid SHA-256"
                        | _ -> Error "resolved release set needs profile, catalogSnapshot and platform"
                with :? JsonException as ex ->
                    Error $"resolved release set is not valid JSON: {ex.Message}"

    let loadFile (expectedRuntimeIdentifier: string) (path: string) (expectedSha256: string) =
        if not (File.Exists path) then
            Error $"resolved release set not found: {path}"
        else
            parseVerified expectedRuntimeIdentifier expectedSha256 (File.ReadAllBytes path)

/// The runtime identifier used to pick a release asset.
module Platform =
    let runtimeIdentifier () =
        let arch =
            match RuntimeInformation.OSArchitecture with
            | Architecture.Arm64 -> "arm64"
            | _ -> "x64"

        if RuntimeInformation.IsOSPlatform OSPlatform.Windows then $"win-{arch}"
        elif RuntimeInformation.IsOSPlatform OSPlatform.OSX then $"osx-{arch}"
        elif File.Exists "/etc/alpine-release" then $"linux-musl-{arch}"
        else $"linux-{arch}"
