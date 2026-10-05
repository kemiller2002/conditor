module ComponentSchemaTests

// The published JSON Schemas must describe what Conditor actually accepts and
// emits. Every built-in descriptor under `components/` is validated against
// `schemas/conditor-component.schema.json`, and the `conditor components --json`
// inventory against `schemas/conditor-components.schema.json`, so a field or
// distribution added to the loader cannot drift away from the published contract.

open System
open System.IO
open System.Text.Json
open System.Text.Json.Nodes
open Json.Schema
open Conditor.Core

let private repositoryRoot =
    let rec up (directory: DirectoryInfo option) =
        match directory with
        | Some d when File.Exists(Path.Combine(d.FullName, "Conditor.slnx")) -> d.FullName
        | Some d -> up (Option.ofObj d.Parent)
        | None -> invalidOp "Conditor repository root (Conditor.slnx) was not found."

    up (Some(DirectoryInfo AppContext.BaseDirectory))

let private loadSchema (fileName: string) =
    Path.Combine(repositoryRoot, "schemas", fileName) |> File.ReadAllText |> JsonSchema.FromText

let private componentSchema = loadSchema "conditor-component.schema.json"
let private inventorySchema = loadSchema "conditor-components.schema.json"

let private evaluationOptions =
    EvaluationOptions(OutputFormat = OutputFormat.List)

/// Validates `text` against `schema`, returning the schema errors as
/// `instanceLocation: message` lines.
let private validate (schema: JsonSchema) (text: string) =
    use document = JsonDocument.Parse text
    let results = schema.Evaluate(document.RootElement, evaluationOptions)

    if results.IsValid then
        Ok()
    else
        results.Details
        |> Option.ofObj
        |> Option.map List.ofSeq
        |> Option.defaultValue []
        |> List.append [ results ]
        |> Seq.collect (fun (detail: EvaluationResults) ->
            detail.Errors
            |> Option.ofObj
            |> Option.map (Seq.map (fun error -> $"{detail.InstanceLocation}: {error.Key}: {error.Value}"))
            |> Option.defaultValue Seq.empty)
        |> List.ofSeq
        |> Error

let private describe =
    function
    | Ok() -> "valid"
    | Error errors -> String.concat "; " errors

let private descriptorFiles () =
    Directory.GetFiles(Path.Combine(repositoryRoot, "components"), "*.component.json")
    |> Array.sort
    |> List.ofArray

/// Applies `edit` to a copy of the named built-in descriptor.
let private mutateDescriptor (fileName: string) (edit: JsonObject -> unit) =
    let node =
        Path.Combine(repositoryRoot, "components", fileName)
        |> File.ReadAllText
        |> JsonNode.Parse
        |> nonNull

    let descriptor = node.AsObject()
    edit descriptor
    descriptor.ToJsonString()

let private loaderAccepts (text: string) =
    ComponentDescriptors.parse "mutated.component.json" text |> Result.isOk

let private schemaAccepts (text: string) =
    validate componentSchema text |> Result.isOk

let run (check: string -> bool -> unit) =
    let files = descriptorFiles ()
    check "component schema test discovers built-in descriptors" (files.Length = Registry.descriptors.Length)

    for path in files do
        let name = Path.GetFileName path
        let result = path |> File.ReadAllText |> validate componentSchema
        check $"{name} conforms to conditor-component.schema.json: {describe result}" (Result.isOk result)

    let inventory = ComponentInventory.toJson Registry.descriptors
    let inventoryResult = validate inventorySchema inventory

    check
        $"components --json inventory conforms to conditor-components.schema.json: {describe inventoryResult}"
        (Result.isOk inventoryResult)

    // The schema and the loader must agree on the distribution-specific rules,
    // not merely on the descriptors that happen to exist today.
    let parityCases =
        [ "host-tool descriptor without versionArguments",
          mutateDescriptor "praxis.component.json" (fun d -> d.Remove "versionArguments" |> ignore)
          "host-tool descriptor with empty versionArguments",
          mutateDescriptor "praxis.component.json" (fun d -> d["versionArguments"] <- JsonArray())
          "host-tool descriptor without command", mutateDescriptor "praxis.component.json" (fun d -> d.Remove "command" |> ignore)
          "host-tool descriptor with a lifecycleSource",
          mutateDescriptor "praxis.component.json" (fun d -> d["lifecycleSource"] <- JsonNode.Parse """{"kind":"registry"}""")
          "lifecycle-npm descriptor without lifecycleSource",
          mutateDescriptor "tutela.component.json" (fun d -> d.Remove "lifecycleSource" |> ignore)
          "lifecycle-npm descriptor without versionArguments",
          mutateDescriptor "tutela.component.json" (fun d -> d.Remove "versionArguments" |> ignore)
          "npm descriptor without applicationBinding",
          mutateDescriptor "forma.component.json" (fun d -> d.Remove "applicationBinding" |> ignore)
          "nuget descriptor bound to npm",
          mutateDescriptor "aegis.component.json" (fun d -> d["applicationBinding"] <- JsonValue.Create "npm")
          "descriptor with an unknown distribution",
          mutateDescriptor "praxis.component.json" (fun d -> d["distribution"] <- JsonValue.Create "brew")
          "versionArguments with a non-string entry",
          mutateDescriptor "ordo.component.json" (fun d -> d["versionArguments"] <- JsonNode.Parse """["--version", 1]""") ]

    for name, text in parityCases do
        check $"loader rejects {name}" (not (loaderAccepts text))
        check $"component schema rejects {name}" (not (schemaAccepts text))

    let acceptedCase =
        mutateDescriptor "forma.component.json" (fun d -> d["versionArguments"] <- JsonArray())

    check "loader accepts an npm descriptor with empty versionArguments" (loaderAccepts acceptedCase)
    check "component schema accepts an npm descriptor with empty versionArguments" (schemaAccepts acceptedCase)
