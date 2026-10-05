namespace Conditor.Core

open System
open System.IO
open System.Reflection
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.RegularExpressions

type ComponentDescriptor =
    { Definition: ComponentDefinition
      QualifiedVersions: Set<string>
      Sha256: string }

/// A structural rule of `schemas/conditor-component.schema.json` that a
/// descriptor breaks. The loader refuses every violation rather than ignoring it.
type DescriptorViolation =
    /// `location` (the descriptor itself, `lifecycleSource` or
    /// `lifecycleSource.entrypoint`) carries a property the contract does not define.
    | UnknownProperty of location: string * property: string
    /// A string field does not match the schema `pattern`.
    | InvalidFormat of field: string * value: string * pattern: string
    | DuplicateQualifiedVersion of version: string
    | EmptyQualifiedVersion
    /// `command` is present but is not a non-empty string.
    | InvalidCommand
    /// `applicationBinding` is present but is not a string.
    | InvalidApplicationBinding

module DescriptorViolation =
    let describe =
        function
        | UnknownProperty("descriptor", property) -> $"Component descriptor does not support property '{property}'."
        | UnknownProperty(location, property) -> $"'{location}' does not support property '{property}'."
        | InvalidFormat(field, value, pattern) -> $"'{field}' value '{value}' does not match the required format {pattern}."
        | DuplicateQualifiedVersion version -> $"'qualifiedVersions' lists '{version}' more than once."
        | EmptyQualifiedVersion -> "'qualifiedVersions' entries must be non-empty strings."
        | InvalidCommand -> "'command' must be a non-empty string."
        | InvalidApplicationBinding -> "'applicationBinding' must be 'npm' or 'nuget'."

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

    /// A schema `pattern` and its .NET equivalent. JSON Schema patterns use
    /// ECMA-262 anchors, where `$` never matches before a trailing newline, so the
    /// .NET form anchors with `\A` and `\z`.
    let private schemaPattern (core: string) =
        $"^{core}$", Regex($@"\A{core}\z", RegexOptions.CultureInvariant)

    let private idPattern = schemaPattern "[a-z0-9-]+"
    let private repositoryPattern = schemaPattern "[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+"
    let private commitPattern = schemaPattern "[0-9a-fA-F]{40}"

    let private descriptorProperties =
        set
            [ "schemaVersion"
              "id"
              "displayName"
              "distribution"
              "package"
              "historicalPackages"
              "defaultVersion"
              "qualifiedVersions"
              "lifecycleSource"
              "applicationBinding"
              "command"
              "versionArguments"
              "initArguments"
              "verifyArguments"
              "doctorArguments"
              "upgradeArguments" ]

    let private registrySourceProperties = set [ "kind" ]
    let private githubSourceProperties = set [ "kind"; "repository"; "commit"; "entrypoint" ]
    let private entrypointProperties = set [ "kind"; "path" ]

    let private unknownProperties location (allowed: Set<string>) (element: JsonElement) =
        element.EnumerateObject()
        |> Seq.map _.Name
        |> Seq.filter (allowed.Contains >> not)
        |> Seq.map (fun property -> UnknownProperty(location, property))
        |> List.ofSeq

    /// Format violations of the string property `name`, when present. Missing or
    /// non-string values are reported by the field's own parser.
    let private formatViolations field name (pattern: string, regex: Regex) (element: JsonElement) =
        match tryProperty name element with
        | Some value when value.ValueKind = JsonValueKind.String ->
            match value.GetString() |> Option.ofObj with
            | Some text when text <> String.Empty && not (regex.IsMatch text) -> [ InvalidFormat(field, text, pattern) ]
            | _ -> []
        | _ -> []

    let private objectProperty name (element: JsonElement) =
        tryProperty name element |> Option.filter (fun value -> value.ValueKind = JsonValueKind.Object)

    let private lifecycleSourceViolations (root: JsonElement) =
        match objectProperty "lifecycleSource" root with
        | None -> []
        | Some source ->
            match optionalString "kind" source with
            | Some "registry" -> unknownProperties "lifecycleSource" registrySourceProperties source
            | Some "github" ->
                List.concat
                    [ unknownProperties "lifecycleSource" githubSourceProperties source
                      formatViolations "lifecycleSource.repository" "repository" repositoryPattern source
                      formatViolations "lifecycleSource.commit" "commit" commitPattern source
                      objectProperty "entrypoint" source
                      |> Option.map (unknownProperties "lifecycleSource.entrypoint" entrypointProperties)
                      |> Option.defaultValue [] ]
            | _ -> []

    let private qualifiedVersionViolations (root: JsonElement) =
        match tryProperty "qualifiedVersions" root with
        | Some value when value.ValueKind = JsonValueKind.Array ->
            let versions =
                value.EnumerateArray()
                |> Seq.filter (fun item -> item.ValueKind = JsonValueKind.String)
                |> Seq.choose (fun item -> item.GetString() |> Option.ofObj)
                |> List.ofSeq

            let empty = if versions |> List.contains String.Empty then [ EmptyQualifiedVersion ] else []

            let duplicates =
                versions
                |> List.countBy id
                |> List.filter (fun (version, count) -> count > 1 && version <> String.Empty)
                |> List.map (fst >> DuplicateQualifiedVersion)

            empty @ duplicates
        | _ -> []

    /// `violation` when the optional property `name` is present but `accepts` refuses it.
    let private optionalPropertyViolations name (accepts: JsonElement -> bool) violation (root: JsonElement) =
        match tryProperty name root with
        | Some value when not (accepts value) -> [ violation ]
        | _ -> []

    let private isString (value: JsonElement) = value.ValueKind = JsonValueKind.String

    let private isNonEmptyString (value: JsonElement) =
        isString value && value.GetString() <> String.Empty

    /// Every structural rule of the descriptor schema that `root` breaks, beyond
    /// the required-field and distribution checks `parse` performs itself.
    let structuralViolations (root: JsonElement) =
        if root.ValueKind <> JsonValueKind.Object then
            []
        else
            List.concat
                [ unknownProperties "descriptor" descriptorProperties root
                  formatViolations "id" "id" idPattern root
                  lifecycleSourceViolations root
                  qualifiedVersionViolations root
                  optionalPropertyViolations "command" isNonEmptyString InvalidCommand root
                  optionalPropertyViolations "applicationBinding" isString InvalidApplicationBinding root ]

    let private parseDistribution value =
        match value with
        | "host-tool" -> Ok HostTool
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

    let private parseHistoricalPackages (element: JsonElement) =
        match tryProperty "historicalPackages" element with
        | None -> Ok []
        | Some value when value.ValueKind = JsonValueKind.Array ->
            let errors = ResizeArray<string>()
            let entries = ResizeArray<HistoricalPackage>()

            for entry in value.EnumerateArray() do
                if entry.ValueKind <> JsonValueKind.Object then
                    errors.Add "'historicalPackages' entries must be objects."
                else
                    let unknown =
                        entry.EnumerateObject()
                        |> Seq.map _.Name
                        |> Seq.filter (fun name -> name <> "package" && name <> "versions")
                        |> List.ofSeq

                    for name in unknown do
                        errors.Add $"'historicalPackages' entries do not support property '{name}'."

                    let package = requiredString "package" entry

                    let versions =
                        match stringArray "versions" entry with
                        | Ok [] -> Error [ "'historicalPackages.versions' must list at least one version." ]
                        | Ok items when (items |> List.exists String.IsNullOrWhiteSpace) ->
                            Error [ "'historicalPackages.versions' entries must be non-empty strings." ]
                        | Ok items when (items |> List.distinct |> List.length) <> items.Length ->
                            Error [ "'historicalPackages.versions' entries must be unique." ]
                        | Ok items -> Ok items
                        | Error versionErrors ->
                            Error(versionErrors |> List.map (fun error -> error.Replace("'versions'", "'historicalPackages.versions'")))

                    match package, versions with
                    | Ok name, Ok items -> entries.Add { Package = name; Versions = Set.ofList items }
                    | Error error, Ok _ -> errors.Add(error.Replace("'package'", "'historicalPackages.package'"))
                    | Ok _, Error versionErrors -> versionErrors |> List.iter errors.Add
                    | Error error, Error versionErrors ->
                        errors.Add(error.Replace("'package'", "'historicalPackages.package'"))
                        versionErrors |> List.iter errors.Add

            if errors.Count = 0 then Ok(List.ofSeq entries) else Error(List.ofSeq errors)
        | Some _ -> Error [ "'historicalPackages' must be an array." ]

    /// Checks that historical package identities partition a subset of the
    /// qualified versions, never repeat the current identity, and leave the
    /// default version on the current identity.
    let private validateHistoricalPackages
        (id: string)
        (currentPackage: string)
        (defaultVersion: string)
        (qualifiedVersions: Set<string>)
        (historical: HistoricalPackage list)
        =
        [ for entry in historical do
              if entry.Package = currentPackage then
                  yield
                      $"historicalPackages for '{id}' repeats the current package '{currentPackage}'; list only earlier package identities."

              for version in entry.Versions |> Seq.sort do
                  if not (qualifiedVersions.Contains version) then
                      yield
                          $"historicalPackages for '{id}' names version '{version}' under '{entry.Package}', but it is not present in qualifiedVersions."

              if entry.Versions.Contains defaultVersion then
                  yield
                      $"defaultVersion '{defaultVersion}' for '{id}' is listed under historical package '{entry.Package}'; the default version must use the current package '{currentPackage}'."

          for package, count in historical |> List.countBy _.Package do
              if count > 1 then
                  yield $"historicalPackages for '{id}' declares package '{package}' more than once."

          let owners =
              historical
              |> List.collect (fun entry -> entry.Versions |> Set.toList |> List.map (fun version -> version, entry.Package))
              |> List.groupBy fst

          for version, claims in owners do
              if claims.Length > 1 then
                  let packages = claims |> List.map snd |> List.sort |> String.concat ", "
                  yield $"historicalPackages for '{id}' assigns version '{version}' to more than one package: {packages}." ]

    let private readResource resourceName =
        match assembly.GetManifestResourceStream(resourceName) |> Option.ofObj with
        | None -> Error [ $"Embedded component descriptor is missing: {resourceName}" ]
        | Some stream ->
            use value = stream
            use reader = new StreamReader(value)
            Ok(reader.ReadToEnd())

    /// Parses and validates one component descriptor document.
    let parse (resourceName: string) (text: string) =
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

            structuralViolations root |> List.map DescriptorViolation.describe |> List.iter errors.Add

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

            let historicalPackages =
                match parseHistoricalPackages root with
                | Ok value -> value
                | Error historicalErrors ->
                    historicalErrors |> List.iter errors.Add
                    []

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
            let versionArguments =
                match tryProperty "versionArguments" root with
                | None -> []
                | Some _ -> collectArray "versionArguments"
            let initArguments = collectArray "initArguments"
            let verifyArguments = collectArray "verifyArguments"
            let doctorArguments = collectArray "doctorArguments"
            let upgradeArguments = collectArray "upgradeArguments"

            if not (String.IsNullOrWhiteSpace defaultVersion)
               && not (qualifiedVersions.Contains defaultVersion) then
                errors.Add $"defaultVersion '{defaultVersion}' is not present in qualifiedVersions for '{id}'."

            validateHistoricalPackages id packageName defaultVersion qualifiedVersions historicalPackages
            |> List.iter errors.Add

            match distribution with
            | HostTool ->
                if lifecycleSource.IsSome then
                    errors.Add $"Host-tool component '{id}' must not declare a package lifecycleSource."

                if command.IsNone then
                    errors.Add $"Host-tool component '{id}' must declare command."

                if versionArguments.IsEmpty then
                    errors.Add $"Host-tool component '{id}' must declare versionArguments for safe adoption."
            | LifecycleNpm ->
                if lifecycleSource.IsNone then
                    errors.Add $"Lifecycle component '{id}' must declare lifecycleSource."

                if command.IsNone then
                    errors.Add $"Lifecycle component '{id}' must declare command."

                if versionArguments.IsEmpty then
                    errors.Add $"Lifecycle component '{id}' must declare versionArguments for safe adoption."
            | NpmPackage ->
                if binding <> Some NpmDependency then
                    errors.Add $"Npm component '{id}' must declare applicationBinding 'npm'."
            | NugetPackage ->
                if binding <> Some NugetReference then
                    errors.Add $"NuGet component '{id}' must declare applicationBinding 'nuget'."

            if errors.Count > 0 then
                Error(List.ofSeq errors)
            else
                let descriptorSha256 =
                    Encoding.UTF8.GetBytes(text)
                    |> SHA256.HashData
                    |> Convert.ToHexString
                    |> fun value -> value.ToLowerInvariant()

                Ok
                    { Definition =
                        { Id = id
                          DisplayName = displayName
                          Distribution = distribution
                          Package = packageName
                          HistoricalPackages = historicalPackages
                          LifecycleSource = lifecycleSource
                          ApplicationBinding = binding
                          Command = command
                          DefaultVersion = defaultVersion
                          VersionArguments = versionArguments
                          InitArguments = initArguments
                          VerifyArguments = verifyArguments
                          DoctorArguments = doctorArguments
                          UpgradeArguments = upgradeArguments }
                      QualifiedVersions = qualifiedVersions
                      Sha256 = descriptorSha256 }
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
