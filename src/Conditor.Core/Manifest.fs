namespace Conditor.Core

open System
open System.IO
open System.Text.Json

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

    let private parseExecution (root: JsonElement) =
        match tryProperty "execution" root with
        | None -> Ok None
        | Some value when value.ValueKind = JsonValueKind.Object ->
            Ok(
                Some
                    { Enabled = optionalBool false "enabled" value
                      Launcher = optionalString "launcher" value
                      Mission = optionalString "mission" value }
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
                          Execution = execution }
            with
            | :? JsonException as ex -> Error [ $"Manifest is not valid JSON: {ex.Message}" ]
            | ex -> Error [ $"Unable to read manifest: {ex.Message}" ]
