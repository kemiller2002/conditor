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

/// A component the profile installs from a checksummed GitHub release
/// (CON-133 pinned artifacts with integrity checks).
type ProfileComponent =
    { Id: string
      Version: string
      Executable: string
      VersionProbe: string list
      Repository: string
      Tag: string
      Assets: Map<string, ReleaseAsset> }

/// A versioned, declarative, composable workstation profile (CON-160..166).
type WorkstationProfile =
    { Id: string
      Version: int
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
                      Assets = assets }
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
                    | Some v when v.ValueKind = JsonValueKind.Number -> v.GetInt32()
                    | _ -> 0

                match str "id" root, components with
                | None, _ -> Error "a workstation profile needs an id"
                | _, Error e -> Error e
                | _ when version < 1 -> Error "a workstation profile needs a positive integer version"
                | Some id, Ok cs ->
                    Ok
                        { Id = id
                          Version = version
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
