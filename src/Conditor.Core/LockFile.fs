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

    let write target manifestPath (plan: InstallationPlan) =
        let directory = Path.Combine(target, ".conditor")
        Directory.CreateDirectory directory |> ignore
        let path = Path.Combine(directory, "lock.json")

        use stream = File.Create path
        let mutable options = JsonWriterOptions()
        options.Indented <- true
        use writer = new Utf8JsonWriter(stream, options)
        writer.WriteStartObject()
        writer.WriteNumber("schemaVersion", 1)
        writer.WriteString("project", plan.ProjectName)
        writer.WriteString("manifestSha256", manifestHash manifestPath)
        writer.WriteStartArray("components")

        for resolved in plan.Components do
            writer.WriteStartObject()
            writer.WriteString("id", resolved.Id)
            writer.WriteString("version", resolved.Version)
            writer.WriteString(
                "distribution",
                match resolved.Distribution with
                | LifecycleNpm -> "lifecycle-npm"
                | NpmPackage -> "npm"
                | NugetPackage -> "nuget"
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
