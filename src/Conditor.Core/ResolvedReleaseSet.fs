namespace Conditor.Core

open System
open System.IO
open System.Security.Cryptography
open System.Text.Json
open System.Text.Json.Nodes

type ResolvedSetArtifact =
    { Name: string
      Purpose: string
      Platform: string option
      Sha256: string }

type ResolvedSetComponent =
    { Id: string
      Role: string
      Required: bool
      Version: string
      DistributionClass: string
      Mechanism: string
      Package: string option
      Executable: string option
      Repository: string
      Tag: string
      Commit: string
      Artifacts: ResolvedSetArtifact list }

type VerifiedResolvedReleaseSet =
    { ProfileId: string
      ProfileVersion: string
      ProfileSha256: string
      CatalogSha256: string
      ResolvedSetSha256: string
      Platform: string
      Components: ResolvedSetComponent list }

module ResolvedReleaseSet =
    let private tryProperty (name: string) (element: JsonElement) =
        let mutable value = Unchecked.defaultof<JsonElement>
        if element.ValueKind = JsonValueKind.Object && element.TryGetProperty(name, &value) then Some value else None

    let private str name element =
        match tryProperty name element with
        | Some value when value.ValueKind = JsonValueKind.String -> value.GetString() |> Option.ofObj
        | _ -> None

    let private requiredString context name element =
        match str name element with
        | Some value when not (String.IsNullOrWhiteSpace value) -> Ok value
        | _ -> Error $"{context}.{name} must be a non-empty string"

    let private boolValue name element =
        match tryProperty name element with
        | Some value when value.ValueKind = JsonValueKind.True -> Some true
        | Some value when value.ValueKind = JsonValueKind.False -> Some false
        | _ -> None

    let private objects name element =
        match tryProperty name element with
        | Some value when value.ValueKind = JsonValueKind.Array -> value.EnumerateArray() |> Seq.toList
        | _ -> []

    let private isSha256 (value: string) =
        value.Length = 64 && value |> Seq.forall Char.IsAsciiHexDigitLower

    let private normalizeSha256 (value: string) =
        if value.StartsWith("sha256:", StringComparison.Ordinal) then value.Substring("sha256:".Length) else value

    let private sha256Bytes (bytes: byte array) =
        SHA256.HashData bytes
        |> Convert.ToHexString
        |> fun value -> value.ToLowerInvariant()

    let private parseArtifact context (element: JsonElement) =
        match requiredString context "name" element,
              requiredString context "purpose" element,
              requiredString context "sha256" element with
        | Ok name, Ok purpose, Ok digest when isSha256 digest ->
            Ok
                { Name = name
                  Purpose = purpose
                  Platform = str "platform" element
                  Sha256 = digest }
        | _, _, Ok digest when not (isSha256 digest) ->
            Error $"{context}.sha256 must be a lowercase SHA-256"
        | Error e, _, _
        | _, Error e, _
        | _, _, Error e -> Error e
        | _ -> Error $"{context} is invalid"

    let private parseComponent expectedPlatform index (element: JsonElement) =
        let context = $"components[{index}]"

        match requiredString context "systemId" element,
              requiredString context "role" element,
              requiredString context "version" element,
              requiredString context "distributionClass" element,
              requiredString context "repository" element,
              requiredString context "tag" element,
              requiredString context "commit" element,
              requiredString context "lifecycleState" element,
              tryProperty "distribution" element with
        | Ok id, Ok role, Ok version, Ok distributionClass, Ok repository, Ok tag, Ok commit, Ok lifecycle, Some distribution ->
            if lifecycle <> "active" then
                Error $"resolved component '{id}' is {lifecycle}; normal consumption accepts only active releases"
            else
                match requiredString $"{context}.distribution" "mechanism" distribution with
                | Error e -> Error e
                | Ok mechanism ->
                    let artifactResults =
                        objects "artifacts" element
                        |> List.mapi (fun artifactIndex artifact -> parseArtifact $"{context}.artifacts[{artifactIndex}]" artifact)

                    let errors =
                        artifactResults
                        |> List.choose (function Error e -> Some e | Ok _ -> None)

                    if not errors.IsEmpty then
                        Error(String.concat "; " errors)
                    else
                        let artifacts = artifactResults |> List.choose (function Ok a -> Some a | Error _ -> None)
                        let required = boolValue "required" element |> Option.defaultValue true
                        let packageName = str "package" distribution
                        let executable = str "executable" element

                        let primaryCount =
                            match role, distributionClass with
                            | ("host-tool" | "repository-lifecycle"), "self-contained-native-cli" ->
                                artifacts
                                |> List.filter (fun artifact -> artifact.Purpose = "executable" && artifact.Platform = Some expectedPlatform)
                                |> List.length
                            | "project-binding", ("web-package" | "nuget-library") ->
                                artifacts
                                |> List.filter (fun artifact -> artifact.Purpose = "package" && artifact.Platform.IsNone)
                                |> List.length
                            | _ -> -1

                        match role, distributionClass, mechanism, packageName, executable, primaryCount with
                        | ("host-tool" | "repository-lifecycle"), "self-contained-native-cli", "github-release", _, Some _, 1 ->
                            Ok
                                { Id = id
                                  Role = role
                                  Required = required
                                  Version = version
                                  DistributionClass = distributionClass
                                  Mechanism = mechanism
                                  Package = packageName
                                  Executable = executable
                                  Repository = repository
                                  Tag = tag
                                  Commit = commit
                                  Artifacts = artifacts }
                        | "project-binding", ("web-package" | "nuget-library"), ("github-release" | "npm" | "nuget"), Some _, _, 1 ->
                            Ok
                                { Id = id
                                  Role = role
                                  Required = required
                                  Version = version
                                  DistributionClass = distributionClass
                                  Mechanism = mechanism
                                  Package = packageName
                                  Executable = executable
                                  Repository = repository
                                  Tag = tag
                                  Commit = commit
                                  Artifacts = artifacts }
                        | ("host-tool" | "repository-lifecycle"), "self-contained-native-cli", _, _, None, _ ->
                            Error $"resolved native component '{id}' needs executable identity"
                        | ("host-tool" | "repository-lifecycle"), "self-contained-native-cli", _, _, _, count ->
                            Error $"resolved native component '{id}' needs exactly one executable artifact for {expectedPlatform}; found {count}"
                        | "project-binding", ("web-package" | "nuget-library"), _, None, _, _ ->
                            Error $"resolved project binding '{id}' needs distribution.package"
                        | "project-binding", ("web-package" | "nuget-library"), _, _, _, count ->
                            Error $"resolved project binding '{id}' needs exactly one primary package artifact; found {count}"
                        | _ ->
                            Error $"resolved component '{id}' role '{role}' and distribution class '{distributionClass}' are not supported by this Conditor slice"
        | Error e, _, _, _, _, _, _, _, _
        | _, Error e, _, _, _, _, _, _, _
        | _, _, Error e, _, _, _, _, _, _
        | _, _, _, Error e, _, _, _, _, _
        | _, _, _, _, Error e, _, _, _, _
        | _, _, _, _, _, Error e, _, _, _
        | _, _, _, _, _, _, Error e, _, _
        | _, _, _, _, _, _, _, Error e, _ -> Error e
        | _, _, _, _, _, _, _, _, None -> Error $"{context}.distribution must be an object"

    let parseVerified expectedPlatform expectedSha256 (bytes: byte array) =
        let expected = normalizeSha256 expectedSha256

        if not (isSha256 expected) then
            Error [ "resolved release set requires an expected 64-character lowercase SHA-256" ]
        else
            let actual = sha256Bytes bytes

            if actual <> expected then
                Error [ $"resolved release set digest mismatch: expected sha256:{expected}, observed sha256:{actual}" ]
            else
                try
                    use document = JsonDocument.Parse bytes
                    let root = document.RootElement

                    if str "schema" root <> Some "echelon.resolved-release-set/v1" then
                        Error [ "resolved release set must declare schema echelon.resolved-release-set/v1" ]
                    else
                        let profile = tryProperty "profile" root
                        let catalog = tryProperty "catalogSnapshot" root
                        let platform = str "platform" root

                        match profile, catalog, platform with
                        | Some profileRef, Some catalogRef, Some resolvedPlatform when resolvedPlatform <> expectedPlatform ->
                            Error [ $"resolved release set targets {resolvedPlatform}, but this host is {expectedPlatform}" ]
                        | Some profileRef, Some catalogRef, Some resolvedPlatform ->
                            match requiredString "profile" "id" profileRef,
                                  requiredString "profile" "version" profileRef,
                                  requiredString "profile" "sha256" profileRef,
                                  requiredString "catalogSnapshot" "sha256" catalogRef with
                            | Ok profileId, Ok profileVersion, Ok profileSha, Ok catalogSha when isSha256 profileSha && isSha256 catalogSha ->
                                let results =
                                    objects "components" root
                                    |> List.mapi (parseComponent resolvedPlatform)

                                let errors =
                                    results
                                    |> List.choose (function Error e -> Some e | Ok _ -> None)

                                if not errors.IsEmpty then
                                    Error errors
                                else
                                    let components = results |> List.choose (function Ok value -> Some value | Error _ -> None)
                                    let ids = components |> List.map _.Id

                                    if components.IsEmpty then
                                        Error [ "resolved release set contains no components" ]
                                    elif ids.Length <> (ids |> List.distinct |> List.length) then
                                        Error [ "resolved release set contains duplicate system ids" ]
                                    else
                                        Ok
                                            { ProfileId = profileId
                                              ProfileVersion = profileVersion
                                              ProfileSha256 = profileSha
                                              CatalogSha256 = catalogSha
                                              ResolvedSetSha256 = actual
                                              Platform = resolvedPlatform
                                              Components = components }
                            | _ -> Error [ "resolved release set profile/catalog identity is incomplete or has an invalid SHA-256" ]
                        | _ -> Error [ "resolved release set needs profile, catalogSnapshot and platform" ]
                with :? JsonException as ex ->
                    Error [ $"resolved release set is not valid JSON: {ex.Message}" ]

    let loadFile expectedPlatform path expectedSha256 =
        if not (File.Exists path) then
            Error [ $"resolved release set not found: {path}" ]
        else
            parseVerified expectedPlatform expectedSha256 (File.ReadAllBytes path)

    let private validateAgainstDescriptor (selection: ResolvedSetComponent) =
        match Registry.tryFind selection.Id with
        | None -> Error $"resolved component '{selection.Id}' has no Conditor component descriptor"
        | Some definition ->
            match definition.Distribution, selection.Role, selection.DistributionClass, selection.Package with
            | NativeLifecycle, ("host-tool" | "repository-lifecycle"), "self-contained-native-cli", _ ->
                Ok()
            | NpmPackage, "project-binding", "web-package", Some package when package = definition.Package ->
                Ok()
            | NugetPackage, "project-binding", "nuget-library", Some package when package = definition.Package ->
                Ok()
            | LifecycleNpm, _, _, _ ->
                Error $"resolved component '{selection.Id}' still maps to legacy lifecycle-npm; migrate its descriptor before Registry-driven use"
            | NpmPackage, "project-binding", "web-package", Some package ->
                Error $"resolved component '{selection.Id}' package '{package}' does not match Conditor descriptor '{definition.Package}'"
            | NugetPackage, "project-binding", "nuget-library", Some package ->
                Error $"resolved component '{selection.Id}' package '{package}' does not match Conditor descriptor '{definition.Package}'"
            | _ ->
                Error $"resolved component '{selection.Id}' role/class '{selection.Role}/{selection.DistributionClass}' does not match its Conditor descriptor"

    let bindManifest (resolved: VerifiedResolvedReleaseSet) (manifest: ProjectManifest) =
        let errors = ResizeArray<string>()
        let byId = resolved.Components |> List.map (fun item -> item.Id, item) |> Map.ofList
        let manifestIds = manifest.Components |> List.map _.Id |> Set.ofList

        for selection in resolved.Components do
            match validateAgainstDescriptor selection with
            | Error error -> errors.Add error
            | Ok() -> ()

            if selection.Required && not (manifestIds.Contains selection.Id) then
                errors.Add $"resolved profile requires '{selection.Id}', but the Conditor project manifest does not declare it"

        let components =
            manifest.Components
            |> List.map (fun request ->
                match Map.tryFind request.Id byId with
                | Some selection -> { request with Version = Some selection.Version }
                | None ->
                    if request.Required then
                        errors.Add $"required Conditor component '{request.Id}' is absent from the verified resolved release set"
                    request)

        if errors.Count > 0 then Error(List.ofSeq errors)
        else Ok { manifest with Components = components }

    let bindPresetJson (resolved: VerifiedResolvedReleaseSet) (json: string) =
        try
            let parsed = JsonNode.Parse(json)

            if isNull parsed then
                Error [ "unable to bind resolved release set into empty Conditor preset JSON" ]
            else
                let root = parsed.AsObject()
                let components = root["components"].AsArray()
                let byId = resolved.Components |> List.map (fun item -> item.Id, item) |> Map.ofList
                let seen = Collections.Generic.HashSet<string>(StringComparer.Ordinal)
                let errors = ResizeArray<string>()

                for node in components do
                    let item = node.AsObject()
                    let id = item["id"].GetValue<string>()
                    seen.Add id |> ignore

                    match Map.tryFind id byId with
                    | None ->
                        errors.Add $"Conditor preset component '{id}' is absent from the verified resolved release set"
                    | Some selection ->
                        match validateAgainstDescriptor selection with
                        | Error error -> errors.Add error
                        | Ok() -> item["version"] <- JsonValue.Create selection.Version

                for selection in resolved.Components do
                    if selection.Required && not (seen.Contains selection.Id) then
                        errors.Add $"verified resolved set requires '{selection.Id}', but the Conditor preset does not declare it"

                if errors.Count > 0 then
                    Error(List.ofSeq errors)
                else
                    let resolution = JsonObject()
                    resolution["schema"] <- JsonValue.Create "echelon.resolution/v1"
                    resolution["profile"] <- JsonValue.Create $"{resolved.ProfileId}@{resolved.ProfileVersion}"
                    resolution["profileSha256"] <- JsonValue.Create resolved.ProfileSha256
                    resolution["catalogSha256"] <- JsonValue.Create resolved.CatalogSha256
                    resolution["resolvedSetSha256"] <- JsonValue.Create resolved.ResolvedSetSha256
                    resolution["platform"] <- JsonValue.Create resolved.Platform
                    root["resolution"] <- resolution

                    Ok(root.ToJsonString(JsonSerializerOptions(WriteIndented = true)) + "\n")
        with ex ->
            Error [ $"unable to bind resolved release set into Conditor preset: {ex.Message}" ]
