namespace Conditor.Core

open System
open System.IO
open System.Text.Json
open System.Text.RegularExpressions

module Manifest =
    let private tryProperty (name: string) (element: JsonElement) =
        let mutable value = Unchecked.defaultof<JsonElement>
        if element.TryGetProperty(name, &value) then Some value else None

    let private optionalString name (element: JsonElement) =
        match tryProperty name element with
        | Some value when value.ValueKind = JsonValueKind.String -> value.GetString() |> Option.ofObj
        | _ -> None

    let private requiredString name (element: JsonElement) =
        match optionalString name element with
        | Some value when not (String.IsNullOrWhiteSpace value) -> Ok value
        | _ -> Error $"'{name}' must be a non-empty string."

    let private optionalBool defaultValue name (element: JsonElement) =
        match tryProperty name element with
        | Some value when value.ValueKind = JsonValueKind.True -> true
        | Some value when value.ValueKind = JsonValueKind.False -> false
        | _ -> defaultValue

    let private parseComponent (element: JsonElement) =
        match requiredString "id" element with
        | Error error -> Error error
        | Ok id ->
            Ok
                { Id = id.Trim().ToLowerInvariant()
                  Version = optionalString "version" element
                  Required = optionalBool true "required" element }

    let private parseScaffold (root: JsonElement) =
        match tryProperty "scaffold" root with
        | None -> Ok None
        | Some value when value.ValueKind = JsonValueKind.Object ->
            match requiredString "kind" value with
            | Error error -> Error error
            | Ok kind ->
                Ok(
                    Some
                        { Kind = kind.Trim().ToLowerInvariant()
                          Name = optionalString "name" value }
                )
        | Some _ -> Error "'scaffold' must be an object."

    let private parseRequirementSource (element: JsonElement) =
        let errors = ResizeArray<string>()

        let id =
            match requiredString "id" element with
            | Ok value -> value.Trim()
            | Error error ->
                errors.Add error
                String.Empty

        let targetPath =
            match requiredString "targetPath" element with
            | Ok value -> value.Trim()
            | Error error ->
                errors.Add error
                String.Empty

        let source =
            match tryProperty "source" element with
            | Some value when value.ValueKind = JsonValueKind.Object ->
                let repository =
                    match requiredString "repository" value with
                    | Ok parsed -> parsed.Trim()
                    | Error error ->
                        errors.Add $"requirements[{id}].source {error}"
                        String.Empty

                let commit =
                    match requiredString "commit" value with
                    | Ok parsed -> parsed.Trim().ToLowerInvariant()
                    | Error error ->
                        errors.Add $"requirements[{id}].source {error}"
                        String.Empty

                let sourcePath =
                    match requiredString "path" value with
                    | Ok parsed -> parsed.Trim()
                    | Error error ->
                        errors.Add $"requirements[{id}].source {error}"
                        String.Empty

                { Repository = repository
                  Commit = commit
                  Entrypoint = FileArtifact sourcePath }
            | _ ->
                errors.Add $"requirements[{id}].source must be an object."

                { Repository = String.Empty
                  Commit = String.Empty
                  Entrypoint = FileArtifact String.Empty }

        if errors.Count > 0 then
            Error(List.ofSeq errors)
        else
            Ok
                { Id = id
                  Source = source
                  TargetPath = targetPath }

    let private parseRequirements (root: JsonElement) =
        match tryProperty "requirements" root with
        | None -> Ok []
        | Some value when value.ValueKind = JsonValueKind.Array ->
            let errors = ResizeArray<string>()
            let sources = ResizeArray<RequirementSource>()

            for item in value.EnumerateArray() do
                match parseRequirementSource item with
                | Ok parsed -> sources.Add parsed
                | Error itemErrors -> itemErrors |> List.iter errors.Add

            let duplicates =
                sources
                |> Seq.countBy _.Id
                |> Seq.choose (fun (id, count) -> if count > 1 then Some id else None)

            for duplicate in duplicates do
                errors.Add $"Requirement source '{duplicate}' is declared more than once."

            let duplicateTargets =
                sources
                |> Seq.countBy (fun source -> source.TargetPath)
                |> Seq.choose (fun (targetPath, count) -> if count > 1 then Some targetPath else None)

            for duplicateTarget in duplicateTargets do
                errors.Add $"Requirement targetPath '{duplicateTarget}' is declared more than once."

            if errors.Count > 0 then Error(List.ofSeq errors) else Ok(List.ofSeq sources)
        | Some _ -> Error [ "'requirements' must be an array." ]

    let private parseExecution (root: JsonElement) =
        match tryProperty "execution" root with
        | None -> Ok None
        | Some value when value.ValueKind = JsonValueKind.Object ->
            let enabled = optionalBool false "enabled" value
            let launcher = optionalString "launcher" value
            let mission = optionalString "mission" value
            let contractPath = optionalString "contractPath" value
            let model = optionalString "model" value
            let errors = ResizeArray<string>()

            match model with
            | Some text when not (Regex.IsMatch(text, "^[A-Za-z0-9][A-Za-z0-9._:/@-]{0,127}\\z")) ->
                errors.Add "'execution.model' must be a model identifier (letters, digits, '.', '_', ':', '/', '@', '-')."
            | _ -> ()

            match launcher with
            | Some provider when provider <> "codex" && provider <> "claude" ->
                errors.Add $"Unsupported execution launcher '{provider}'. Supported launchers: codex, claude."
            | _ -> ()

            if enabled && launcher.IsNone then
                errors.Add "Enabled execution requires 'execution.launcher'."

            if enabled && contractPath.IsNone then
                errors.Add "Enabled execution requires 'execution.contractPath'."

            if errors.Count > 0 then
                Error(String.Join(" ", errors))
            else
                Ok(
                    Some
                        { Enabled = enabled
                          Launcher = launcher
                          Mission = mission
                          ContractPath = contractPath
                          Model = model }
                )
        | Some _ -> Error "'execution' must be an object."

    let load path : Result<ProjectManifest, string list> =
        if not (File.Exists path) then
            Error [ $"Manifest not found: {path}" ]
        else
            try
                use document = JsonDocument.Parse(File.ReadAllText path)
                let root = document.RootElement
                let errors = ResizeArray<string>()

                let schemaVersion =
                    match tryProperty "schemaVersion" root with
                    | Some value when value.ValueKind = JsonValueKind.Number ->
                        match value.TryGetInt32() with
                        | true, version -> version
                        | _ ->
                            errors.Add "'schemaVersion' must be an integer."
                            0
                    | _ ->
                        errors.Add "'schemaVersion' is required."
                        0

                if schemaVersion <> 1 then
                    errors.Add $"Unsupported schemaVersion '{schemaVersion}'. Conditor currently supports schemaVersion 1."

                let name =
                    match requiredString "name" root with
                    | Ok value -> value
                    | Error error ->
                        errors.Add error
                        String.Empty

                let components =
                    match tryProperty "components" root with
                    | Some value when value.ValueKind = JsonValueKind.Array ->
                        [ for item in value.EnumerateArray() do
                              match parseComponent item with
                              | Ok parsed -> yield parsed
                              | Error error -> errors.Add error ]
                    | _ ->
                        errors.Add "'components' must be an array."
                        []

                let duplicates =
                    components
                    |> List.countBy _.Id
                    |> List.choose (fun (id, count) -> if count > 1 then Some id else None)

                for duplicate in duplicates do
                    errors.Add $"Component '{duplicate}' is declared more than once."

                let scaffold =
                    match parseScaffold root with
                    | Ok value -> value
                    | Error error ->
                        errors.Add error
                        None

                let requirements =
                    match parseRequirements root with
                    | Ok value -> value
                    | Error requirementErrors ->
                        requirementErrors |> List.iter errors.Add
                        []

                let execution =
                    match parseExecution root with
                    | Ok value -> value
                    | Error error ->
                        errors.Add error
                        None

                if errors.Count > 0 then
                    Error(List.ofSeq errors)
                else
                    Ok
                        { SchemaVersion = schemaVersion
                          Name = name
                          Components = components
                          Scaffold = scaffold
                          Requirements = requirements
                          Execution = execution }
            with
            | :? JsonException as ex -> Error [ $"Manifest is not valid JSON: {ex.Message}" ]
            | ex -> Error [ $"Unable to read manifest: {ex.Message}" ]

    let parseText (text: string) : Result<ProjectManifest, string list> =
        let path = Path.Combine(Path.GetTempPath(), $"conditor-manifest-{Guid.NewGuid():N}.json")

        try
            File.WriteAllText(path, text)
            load path
        finally
            if File.Exists path then
                File.Delete path
