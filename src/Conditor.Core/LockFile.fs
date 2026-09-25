namespace Conditor.Core

open System
open System.IO
open System.Security.Cryptography
open System.Text.Json

module LockFile =
    let private manifestHash manifestPath =
        File.ReadAllBytes manifestPath
        |> SHA256.HashData
        |> Convert.ToHexString
        |> fun value -> value.ToLowerInvariant()


    let readManifestSnapshot target =
        let path = Path.Combine(target, ".conditor", "lock.json")

        if not (File.Exists path) then
            Error [ $"Conditor lock is missing: {path}. Run 'conditor init' first." ]
        else
            try
                use document = JsonDocument.Parse(File.ReadAllText path)
                let root = document.RootElement
                let mutable schemaElement = Unchecked.defaultof<JsonElement>
                let mutable manifestElement = Unchecked.defaultof<JsonElement>

                let schemaVersion =
                    if root.TryGetProperty("schemaVersion", &schemaElement)
                       && schemaElement.ValueKind = JsonValueKind.Number then
                        match schemaElement.TryGetInt32() with
                        | true, value -> value
                        | _ -> 0
                    else
                        0

                if schemaVersion < 2 then
                    Error
                        [ $"Conditor lock schema {schemaVersion} does not contain the prior declaration required for safe upgrade."
                          "Run 'conditor repair' against the unchanged manifest to migrate the lock before upgrading." ]
                elif not (root.TryGetProperty("manifest", &manifestElement))
                     || manifestElement.ValueKind <> JsonValueKind.Object then
                    Error [ "Conditor lock schema v2 is missing its manifest snapshot." ]
                else
                    Ok(manifestElement.GetRawText())
            with
            | :? JsonException as ex -> Error [ $"Conditor lock is not valid JSON: {ex.Message}" ]
            | ex -> Error [ $"Unable to read Conditor lock: {ex.Message}" ]

    let verifyManifest target manifestPath =
        let path = Path.Combine(target, ".conditor", "lock.json")

        if not (File.Exists path) then
            Error [ $"Conditor lock is missing: {path}. Run 'conditor init' first." ]
        elif not (File.Exists manifestPath) then
            Error [ $"Conditor manifest is missing: {manifestPath}." ]
        else
            try
                use document = JsonDocument.Parse(File.ReadAllText path)
                let root = document.RootElement
                let mutable hashElement = Unchecked.defaultof<JsonElement>

                if not (root.TryGetProperty("manifestSha256", &hashElement))
                   || hashElement.ValueKind <> JsonValueKind.String then
                    Error [ "Conditor lock does not contain a valid manifestSha256." ]
                else
                    let recorded = hashElement.GetString() |> Option.ofObj |> Option.defaultValue String.Empty
                    let current = manifestHash manifestPath

                    if String.Equals(recorded, current, StringComparison.OrdinalIgnoreCase) then
                        Ok()
                    else
                        Error
                            [ "Conditor manifest has changed since the environment was established."
                              "Run 'conditor plan' and 'conditor init' to reconcile the repository before execution." ]
            with
            | :? JsonException as ex -> Error [ $"Conditor lock is not valid JSON: {ex.Message}" ]
            | ex -> Error [ $"Unable to verify Conditor lock: {ex.Message}" ]

    let private distributionText =
        function
        | LifecycleNpm -> "lifecycle-npm"
        | NpmPackage -> "npm"
        | NugetPackage -> "nuget"

    let verifyResolvedComponents target (resolved: ResolvedComponent list) =
        let path = Path.Combine(target, ".conditor", "lock.json")

        if not (File.Exists path) then
            Error [ $"Conditor lock is missing: {path}. Run 'conditor init' first." ]
        else
            try
                use document = JsonDocument.Parse(File.ReadAllText path)
                let root = document.RootElement
                let mutable componentsElement = Unchecked.defaultof<JsonElement>

                if not (root.TryGetProperty("components", &componentsElement))
                   || componentsElement.ValueKind <> JsonValueKind.Array then
                    Error [ "Conditor lock does not contain a valid components array." ]
                else
                    let locked =
                        componentsElement.EnumerateArray()
                        |> Seq.map (fun item ->
                            let getString name =
                                let mutable value = Unchecked.defaultof<JsonElement>

                                if item.TryGetProperty(name, &value)
                                   && value.ValueKind = JsonValueKind.String then
                                    value.GetString() |> Option.ofObj
                                else
                                    None

                            let id = getString "id" |> Option.defaultValue String.Empty

                            id,
                            (getString "version",
                             getString "distribution",
                             getString "package",
                             getString "sourceReference"))
                        |> Map.ofSeq

                    let current =
                        resolved
                        |> List.map (fun component ->
                            component.Id,
                            (Some component.Version,
                             Some(distributionText component.Distribution),
                             Some component.Package,
                             component.SourceReference))
                        |> Map.ofList

                    let errors = ResizeArray<string>()
                    let lockedIds = locked |> Map.toSeq |> Seq.map fst |> Set.ofSeq
                    let currentIds = current |> Map.toSeq |> Seq.map fst |> Set.ofSeq

                    for missing in Set.difference lockedIds currentIds do
                        errors.Add $"Locked component '{missing}' is no longer resolved by the current declaration/registry."

                    for added in Set.difference currentIds lockedIds do
                        errors.Add $"Resolved component '{added}' is not present in the Conditor lock."

                    for id in Set.intersect lockedIds currentIds do
                        if locked[id] <> current[id] then
                            errors.Add
                                $"Resolved identity for component '{id}' differs from the Conditor lock; Conditor will not silently substitute a different version/package/source."

                    if errors.Count = 0 then Ok() else Error(List.ofSeq errors)
            with
            | :? JsonException as ex -> Error [ $"Conditor lock is not valid JSON: {ex.Message}" ]
            | ex -> Error [ $"Unable to verify locked component identities: {ex.Message}" ]

    let write target manifestPath (plan: InstallationPlan) =
        let directory = Path.Combine(target, ".conditor")
        Directory.CreateDirectory directory |> ignore
        let path = Path.Combine(directory, "lock.json")

        use stream = File.Create path
        let mutable options = JsonWriterOptions()
        options.Indented <- true
        use writer = new Utf8JsonWriter(stream, options)
        writer.WriteStartObject()
        writer.WriteNumber("schemaVersion", 2)
        writer.WriteString("project", plan.ProjectName)
        writer.WriteString("manifestSha256", manifestHash manifestPath)
        writer.WritePropertyName("manifest")

        use manifestDocument = JsonDocument.Parse(File.ReadAllText manifestPath)
        manifestDocument.RootElement.WriteTo writer

        writer.WriteStartArray("components")

        for resolved in plan.Components do
            writer.WriteStartObject()
            writer.WriteString("id", resolved.Id)
            writer.WriteString("version", resolved.Version)
            writer.WriteString(
                "distribution",
                distributionText resolved.Distribution
            )
            writer.WriteString("package", resolved.Package)

            match resolved.SourceReference with
            | Some source -> writer.WriteString("sourceReference", source)
            | None -> ()

            writer.WriteEndObject()

        writer.WriteEndArray()
        writer.WriteStartArray("requirements")

        for action in plan.Actions do
            match action.Kind, action.Execution with
            | RequirementFile, MaterializeSourceFile(source, targetPath) ->
                writer.WriteStartObject()
                writer.WriteString("id", action.ComponentId.Replace("requirements:", String.Empty))
                writer.WriteString("sourceReference", SourceCache.sourceReference source)
                writer.WriteString("targetPath", targetPath)
                writer.WriteEndObject()
            | _ -> ()

        writer.WriteEndArray()
        writer.WriteEndObject()
        writer.Flush()
        path
