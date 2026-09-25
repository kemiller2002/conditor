namespace Conditor.Core

open System
open System.IO
open System.Reflection
open System.Text.Json

type ComponentDescriptor =
    { Definition: ComponentDefinition
      QualifiedVersions: Set<string> }

module ComponentDescriptors =
    let private assembly = typeof<ComponentDefinition>.Assembly

    let private resources =
        assembly.GetManifestResourceNames()
        |> Array.filter (fun name ->
            name.StartsWith("Conditor.Components.", StringComparison.Ordinal)
            && name.EndsWith(".component.json", StringComparison.Ordinal))
        |> Array.sort
        |> Array.toList

    let private tryProperty (name: string) (element: JsonElement) =
        let mutable value = Unchecked.defaultof<JsonElement>
        if element.TryGetProperty(name, &value) then Some value else None

    let private requiredString name (element: JsonElement) =
        match tryProperty name element with
        | Some value when value.ValueKind = JsonValueKind.String ->
            match value.GetString() |> Option.ofObj with
            | Some text when not (String.IsNullOrWhiteSpace text) -> Ok text
            | _ -> Error $"'{name}' must be a non-empty string."
        | _ -> Error $"'{name}' must be a non-empty string."

    let private optionalString name (element: JsonElement) =
        match tryProperty name element with
        | Some value when value.ValueKind = JsonValueKind.String ->
            value.GetString() |> Option.ofObj
        | _ -> None

    let private stringArray name (element: JsonElement) =
        match tryProperty name element with
        | Some value when value.ValueKind = JsonValueKind.Array ->
            let errors = ResizeArray<string>()
            let items = ResizeArray<string>()

            for item in value.EnumerateArray() do
                if item.ValueKind <> JsonValueKind.String then
                    errors.Add $"'{name}' entries must be strings."
                else
                    match item.GetString() |> Option.ofObj with
                    | Some text -> items.Add text
                    | None -> errors.Add $"'{name}' entries must not be null."

            if errors.Count = 0 then Ok(List.ofSeq items) else Error(List.ofSeq errors)
        | _ -> Error [ $"'{name}' must be an array." ]

    let private parseDistribution value =
        match value with
        | "lifecycle-npm" -> Ok LifecycleNpm
        | "npm" -> Ok NpmPackage
        | "nuget" -> Ok NugetPackage
        | other -> Error $"Unknown component distribution '{other}'."

    let private parseBinding (element: JsonElement) =
        match optionalString "applicationBinding" element with
        | None -> Ok None
        | Some "npm" -> Ok(Some NpmDependency)
        | Some "nuget" -> Ok(Some NugetReference)
        | Some other -> Error $"Unknown applicationBinding '{other}'."

    let private parseEntrypoint (element: JsonElement) =
        match requiredString "kind" element, requiredString "path" element with
        | Ok "node-script", Ok path -> Ok(NodeScript path)
        | Ok "file-artifact", Ok path -> Ok(FileArtifact path)
        | Ok other, Ok _ -> Error $"Unknown lifecycle source entrypoint kind '{other}'."
        | Error error, _
        | _, Error error -> Error error

    let private parseLifecycleSource (element: JsonElement) =
        match tryProperty "lifecycleSource" element with
        | None -> Ok None
        | Some value when value.ValueKind = JsonValueKind.Object ->
            match requiredString "kind" value with
            | Error error -> Error error
            | Ok "registry" -> Ok(Some RegistryPackage)
            | Ok "github" ->
                match requiredString "repository" value, requiredString "commit" value, tryProperty "entrypoint" value with
                | Ok repository, Ok commit, Some entrypoint when entrypoint.ValueKind = JsonValueKind.Object ->
                    match parseEntrypoint entrypoint with
                    | Error error -> Error error
                    | Ok parsed ->
                        Ok(
                            Some(
                                GitHubSource
                                    { Repository = repository
                                      Commit = commit.ToLowerInvariant()
                                      Entrypoint = parsed }
                            )
                        )
                | Error error, _, _
                | _, Error error, _ -> Error error
                | _, _, _ -> Error "'lifecycleSource.entrypoint' must be an object."
            | Ok other -> Error $"Unknown lifecycleSource kind '{other}'."
        | Some _ -> Error "'lifecycleSource' must be an object."

    let private readResource resourceName =
        match assembly.GetManifestResourceStream(resourceName) |> Option.ofObj with
        | None -> Error [ $"Embedded component descriptor is missing: {resourceName}" ]
        | Some stream ->
            use value = stream
            use reader = new StreamReader(value)
            Ok(reader.ReadToEnd())

    let private parse (resourceName: string) (text: string) =
        try
            use document = JsonDocument.Parse(text)
            let root = document.RootElement
            let errors = ResizeArray<string>()

            let schemaVersion =
                match tryProperty "schemaVersion" root with
                | Some value when value.ValueKind = JsonValueKind.Number ->
                    match value.TryGetInt32() with
                    | true, parsed -> parsed
                    | _ -> 0
                | _ -> 0

            if schemaVersion <> 1 then
                errors.Add $"Unsupported component descriptor schemaVersion '{schemaVersion}' in {resourceName}."

            let valueOrEmpty result =
                match result with
                | Ok value -> value
                | Error error ->
                    errors.Add error
                    String.Empty

            let id = requiredString "id" root |> valueOrEmpty
            let displayName = requiredString "displayName" root |> valueOrEmpty
            let packageName = requiredString "package" root |> valueOrEmpty
            let defaultVersion = requiredString "defaultVersion" root |> valueOrEmpty
            let command = optionalString "command" root

            let distribution =
                requiredString "distribution" root
                |> Result.bind parseDistribution
                |> function
                    | Ok value -> value
                    | Error error ->
                        errors.Add error
                        LifecycleNpm

            let lifecycleSource =
                match parseLifecycleSource root with
                | Ok value -> value
                | Error error ->
                    errors.Add error
                    None

            let binding =
                match parseBinding root with
                | Ok value -> value
                | Error error ->
                    errors.Add error
                    None

            let collectArray name =
                match stringArray name root with
                | Ok values -> values
                | Error arrayErrors ->
                    arrayErrors |> List.iter errors.Add
                    []

            let qualifiedVersions = collectArray "qualifiedVersions" |> Set.ofList
            let initArguments = collectArray "initArguments"
            let verifyArguments = collectArray "verifyArguments"
            let doctorArguments = collectArray "doctorArguments"
            let upgradeArguments = collectArray "upgradeArguments"

            if not (String.IsNullOrWhiteSpace defaultVersion)
               && not (qualifiedVersions.Contains defaultVersion) then
                errors.Add $"defaultVersion '{defaultVersion}' is not present in qualifiedVersions for '{id}'."

            match distribution with
            | LifecycleNpm ->
                if lifecycleSource.IsNone then
                    errors.Add $"Lifecycle component '{id}' must declare lifecycleSource."

                if command.IsNone then
                    errors.Add $"Lifecycle component '{id}' must declare command."
            | NpmPackage ->
                if binding <> Some NpmDependency then
                    errors.Add $"Npm component '{id}' must declare applicationBinding 'npm'."
            | NugetPackage ->
                if binding <> Some NugetReference then
                    errors.Add $"NuGet component '{id}' must declare applicationBinding 'nuget'."

            if errors.Count > 0 then
                Error(List.ofSeq errors)
            else
                Ok
                    { Definition =
                        { Id = id
                          DisplayName = displayName
                          Distribution = distribution
                          Package = packageName
                          LifecycleSource = lifecycleSource
                          ApplicationBinding = binding
                          Command = command
                          DefaultVersion = defaultVersion
                          InitArguments = initArguments
                          VerifyArguments = verifyArguments
                          DoctorArguments = doctorArguments
                          UpgradeArguments = upgradeArguments }
                      QualifiedVersions = qualifiedVersions }
        with
        | :? JsonException as ex -> Error [ $"Component descriptor '{resourceName}' is invalid JSON: {ex.Message}" ]
        | ex -> Error [ $"Unable to parse component descriptor '{resourceName}': {ex.Message}" ]

    let loadAll () =
        let descriptors = ResizeArray<ComponentDescriptor>()
        let errors = ResizeArray<string>()

        for resource in resources do
            match readResource resource with
            | Error resourceErrors -> resourceErrors |> List.iter errors.Add
            | Ok text ->
                match parse resource text with
                | Ok descriptor -> descriptors.Add descriptor
                | Error descriptorErrors -> descriptorErrors |> List.iter errors.Add

        let duplicateIds =
            descriptors
            |> Seq.countBy (fun descriptor -> descriptor.Definition.Id)
            |> Seq.choose (fun (id, count) -> if count > 1 then Some id else None)

        for duplicate in duplicateIds do
            errors.Add $"Component descriptor '{duplicate}' is declared more than once."

        if errors.Count = 0 then Ok(List.ofSeq descriptors) else Error(List.ofSeq errors)
