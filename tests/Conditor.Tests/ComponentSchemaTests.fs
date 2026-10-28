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

let private manifestSchema = lazy (loadSchema "conditor.schema.json")

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

/// The nested object `name` of a descriptor fixture.
let private child (name: string) (parent: JsonObject) = (nonNull parent[name]).AsObject()

let private loaderAccepts (text: string) =
    ComponentDescriptors.parse "mutated.component.json" text |> Result.isOk

let private schemaAccepts (text: string) =
    validate componentSchema text |> Result.isOk

let run (check: string -> bool -> unit) =
    // Every committed example manifest, the embedded presets included,
    // conforms to the manifest schema, requirementsImport sections too.
    let manifestSchema = manifestSchema.Value

    for path in Directory.GetFiles(Path.Combine(repositoryRoot, "examples"), "*.conditor.json") |> Array.sort do
        let name = Path.GetFileName path

        match validate manifestSchema (File.ReadAllText path) with
        | Ok() -> check $"example manifest {name} conforms to conditor.schema.json" true
        | Error errors ->
            let details = String.concat "; " errors
            check $"example manifest {name} conforms to conditor.schema.json: {details}" false

    let files = descriptorFiles ()
    check "component schema test discovers built-in descriptors" (files.Length = Registry.descriptors.Length)

    for path in files do
        let name = Path.GetFileName path
        use document = path |> File.ReadAllText |> JsonDocument.Parse

        check
            $"{name} has no structural descriptor violations"
            (ComponentDescriptors.structuralViolations document.RootElement |> List.isEmpty)

        let result = path |> File.ReadAllText |> validate componentSchema
        check $"{name} conforms to conditor-component.schema.json: {describe result}" (Result.isOk result)

    let inventory = ComponentInventory.toJson Registry.descriptors
    let inventoryResult = validate inventorySchema inventory

    check
        $"components --json inventory conforms to conditor-components.schema.json: {describe inventoryResult}"
        (Result.isOk inventoryResult)

    // The inventory is projected from loaded descriptors, so it can never carry
    // a value the loader refuses; its schema says so too.
    let blankInventoryCases =
        [ "displayName"; "package"; "defaultVersion"; "id"; "command" ]
        |> List.map (fun field ->
            let document = (inventory |> JsonNode.Parse |> nonNull).AsObject()
            let components = (nonNull document["components"]).AsArray()
            (nonNull components[0]).AsObject()[field] <- JsonValue.Create " "
            field, document.ToJsonString())

    for field, text in blankInventoryCases do
        check
            $"inventory schema rejects a whitespace-only {field}"
            (validate inventorySchema text |> Result.isError)

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
          mutateDescriptor "ordo.component.json" (fun d -> d["versionArguments"] <- JsonNode.Parse """["--version", 1]""")
          "a whitespace-only displayName",
          mutateDescriptor "praxis.component.json" (fun d -> d["displayName"] <- JsonValue.Create "   ")
          "a whitespace-only package", mutateDescriptor "forma.component.json" (fun d -> d["package"] <- JsonValue.Create "\t")
          "a whitespace-only defaultVersion",
          mutateDescriptor "praxis.component.json" (fun d -> d["defaultVersion"] <- JsonValue.Create " ")
          "a whitespace-only historical package",
          mutateDescriptor "limen.component.json" (fun d ->
              d["historicalPackages"] <- JsonNode.Parse """[{"package":"  ","versions":["0.6.1"]}]""")
          "a whitespace-only historical version",
          mutateDescriptor "limen.component.json" (fun d ->
              d["historicalPackages"] <- JsonNode.Parse """[{"package":"@echelon-foundry/typescript-wasm-kernel","versions":[" "]}]""")
          "a whitespace-only lifecycleSource entrypoint path",
          mutateDescriptor "tutela.component.json" (fun d ->
              (d |> child "lifecycleSource" |> child "entrypoint")["path"] <- JsonValue.Create " \n") ]

    for name, text in parityCases do
        check $"loader rejects {name}" (not (loaderAccepts text))
        check $"component schema rejects {name}" (not (schemaAccepts text))

    // The loader fails closed exactly where the schema does: unknown properties,
    // malformed identifiers, duplicate or empty versions, and malformed optional
    // strings are refused with a specific error instead of being ignored.
    let failClosedCases =
        [ "a descriptor with an unknown top-level property",
          mutateDescriptor "praxis.component.json" (fun d -> d["homepage"] <- JsonValue.Create "https://example.com"),
          UnknownProperty("descriptor", "homepage")
          "a registry lifecycleSource with an unknown property",
          mutateDescriptor "visual-engineering.component.json" (fun d ->
              d["lifecycleSource"] <- JsonNode.Parse """{"kind":"registry","branch":"main"}"""),
          UnknownProperty("lifecycleSource", "branch")
          "a github lifecycleSource with an unknown property",
          mutateDescriptor "tutela.component.json" (fun d -> (child "lifecycleSource" d)["ref"] <- JsonValue.Create "main"),
          UnknownProperty("lifecycleSource", "ref")
          "a lifecycleSource entrypoint with an unknown property",
          mutateDescriptor "tutela.component.json" (fun d ->
              (d |> child "lifecycleSource" |> child "entrypoint")["args"] <- JsonArray()),
          UnknownProperty("lifecycleSource.entrypoint", "args")
          "an id outside the identifier format",
          mutateDescriptor "praxis.component.json" (fun d -> d["id"] <- JsonValue.Create "Praxis_Tool"),
          InvalidFormat("id", "Praxis_Tool", "^[a-z0-9-]+$")
          "a lifecycleSource repository that is not owner/name",
          mutateDescriptor "tutela.component.json" (fun d ->
              (child "lifecycleSource" d)["repository"] <- JsonValue.Create "kemiller2002"),
          InvalidFormat("lifecycleSource.repository", "kemiller2002", "^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$")
          "a lifecycleSource commit that is not a full SHA",
          mutateDescriptor "tutela.component.json" (fun d -> (child "lifecycleSource" d)["commit"] <- JsonValue.Create "main"),
          InvalidFormat("lifecycleSource.commit", "main", "^[0-9a-fA-F]{40}$")
          "duplicate qualifiedVersions",
          mutateDescriptor "praxis.component.json" (fun d ->
              d["qualifiedVersions"] <- JsonNode.Parse """["3.1.3","3.6.0","3.6.0"]"""),
          DuplicateQualifiedVersion "3.6.0"
          "an empty qualifiedVersions entry",
          mutateDescriptor "praxis.component.json" (fun d -> d["qualifiedVersions"] <- JsonNode.Parse """["","3.6.0"]"""),
          EmptyQualifiedVersion
          "a non-string command",
          mutateDescriptor "forma.component.json" (fun d -> d["command"] <- JsonValue.Create 42),
          InvalidCommand
          "an empty command",
          mutateDescriptor "forma.component.json" (fun d -> d["command"] <- JsonValue.Create ""),
          InvalidCommand
          "a whitespace-only command",
          mutateDescriptor "praxis.component.json" (fun d -> d["command"] <- JsonValue.Create "  \t"),
          InvalidCommand
          "a whitespace-only qualifiedVersions entry",
          mutateDescriptor "praxis.component.json" (fun d -> d["qualifiedVersions"] <- JsonNode.Parse """[" ","3.6.0"]"""),
          EmptyQualifiedVersion
          "a non-string applicationBinding",
          mutateDescriptor "praxis.component.json" (fun d -> d["applicationBinding"] <- JsonValue.Create true),
          InvalidApplicationBinding ]

    for name, text, expected in failClosedCases do
        let outcome = ComponentDescriptors.parse "mutated.component.json" text
        use document = JsonDocument.Parse text

        check
            $"loader reports {name} as {expected}"
            (ComponentDescriptors.structuralViolations document.RootElement |> List.contains expected)

        check
            $"loader rejects {name} with a specific error"
            (match outcome with
             | Ok _ -> false
             | Error errors -> errors |> List.contains (DescriptorViolation.describe expected))

        check $"component schema rejects {name}" (not (schemaAccepts text))

    // ECMA-262 anchors (which JSON Schema `pattern` uses) do not match before a
    // trailing newline, but .NET and Python regex `$` does, so both off-the-shelf
    // validators accept "praxis\n". The loader follows the specification.
    check
        "loader rejects an id with a trailing newline with a specific error"
        (mutateDescriptor "praxis.component.json" (fun d -> d["id"] <- JsonValue.Create "praxis\n")
         |> ComponentDescriptors.parse "mutated.component.json"
         |> function
             | Ok _ -> false
             | Error errors ->
                 errors
                 |> List.exists (fun error -> error.Contains("does not match the required format ^[a-z0-9-]+$.", StringComparison.Ordinal)))

    let withMarkers (markers: string) =
        mutateDescriptor "visual-engineering.component.json" (fun d -> d["installationMarkers"] <- JsonNode.Parse markers)

    for markers in [ "[]"; """["/etc/passwd"]"""; """["../outside"]"""; """["a/../b"]"""; """["./a"]"""; """["a//b"]"""; """["a\\b"]"""; """["dir/"]"""; """["a","a"]"""; "[42]" ] do
        check $"loader rejects installationMarkers {markers}" (not (loaderAccepts (withMarkers markers)))
        check $"component schema rejects installationMarkers {markers}" (not (schemaAccepts (withMarkers markers)))

    let validMarkers = withMarkers """[".visual-engineering", ".echelon/visual-engineering.json", "a"]"""
    check "loader accepts repository-relative installationMarkers" (loaderAccepts validMarkers)
    check "component schema accepts repository-relative installationMarkers" (schemaAccepts validMarkers)

    // CON-367: releaseArtifacts declare which qualified versions of an npm
    // binding are distributed as a release asset tarball.
    let withArtifacts (fileName: string) (artifacts: string) =
        mutateDescriptor fileName (fun d -> d["releaseArtifacts"] <- JsonNode.Parse artifacts)

    let folioAsset = "https://github.com/kemiller2002/folio/releases/download/v0.4.0/echelon-foundry-print-components-0.4.0.tgz"

    let folioAsset041 = "https://github.com/kemiller2002/folio/releases/download/v0.4.1/echelon-foundry-print-components-0.4.1.tgz"

    check "the built-in Folio descriptor declares its release-asset distributions"
        (Registry.tryFind "folio"
         |> Option.exists (fun folio ->
             folio.ReleaseArtifacts |> Map.tryFind "0.4.0" = Some folioAsset
             && folio.ReleaseArtifacts |> Map.tryFind "0.4.1" = Some folioAsset041
             && folio.ReleaseArtifacts.Count = 3))

    check "npm-registry-distributed Limen and Forma declare no release-asset distribution"
        ([ "limen"; "forma" ] |> List.forall (fun id -> Registry.tryFind id |> Option.exists _.ReleaseArtifacts.IsEmpty))

    for name, text, schemaRejects in
        [ "a releaseArtifacts version that is not qualified",
          withArtifacts "folio.component.json" $"""[{{"version":"9.9.9","url":"{folioAsset}"}}]""",
          false
          "a releaseArtifacts version declared twice",
          withArtifacts "folio.component.json" $"""[{{"version":"0.4.0","url":"{folioAsset}"}},{{"version":"0.4.0","url":"{folioAsset}"}}]""",
          false
          "a releaseArtifacts url that is not an https tarball",
          withArtifacts "folio.component.json" """[{"version":"0.4.0","url":"http://example.org/folio.zip"}]""",
          true
          "a releaseArtifacts entry with an unknown property",
          withArtifacts "folio.component.json" $"""[{{"version":"0.4.0","url":"{folioAsset}","sha":"x"}}]""",
          true
          "releaseArtifacts on a component that is not an npm binding",
          withArtifacts "praxis.component.json" """[{"version":"3.6.0","url":"https://example.org/praxis-3.6.0.tgz"}]""",
          false ] do
        check $"loader rejects {name}" (not (loaderAccepts text))

        if schemaRejects then
            check $"component schema rejects {name}" (not (schemaAccepts text))

    for id in [ "communication-engineering"; "visual-engineering" ] do
        check
            $"{id} declares where its repository installation lives"
            (Registry.descriptors
             |> List.exists (fun descriptor ->
                 descriptor.Definition.Id = id
                 && descriptor.Definition.InstallationMarkers |> List.contains $".echelon/{id}.json"))

    let acceptedCase =
        mutateDescriptor "forma.component.json" (fun d -> d["versionArguments"] <- JsonArray())

    check "loader accepts an npm descriptor with empty versionArguments" (loaderAccepts acceptedCase)
    check "component schema accepts an npm descriptor with empty versionArguments" (schemaAccepts acceptedCase)

/// Validates a `conditor drift --json` document against
/// `schemas/conditor-drift-report.schema.json` (CON-306).
let private driftReportSchema = lazy (loadSchema "conditor-drift-report.schema.json")

let validateDriftReport (text: string) = validate driftReportSchema.Value text

/// A manifest declaring a staleness policy conforms to the manifest schema,
/// and a misspelt policy field does not (CON-316..317).
let runStalenessPolicy (check: string -> bool -> unit) =
    let manifestSchema = manifestSchema.Value

    let manifest policy =
        $$"""{"schemaVersion":1,"name":"policy","components":[{"id":"praxis","version":"3.11.0"}],"stalenessPolicy":{{policy}}}"""

    check
        "a manifest stalenessPolicy conforms to conditor.schema.json"
        (validate manifestSchema (manifest """{"warnAfterMinorsBehind":2,"warnAfterPinAgeDays":null,"failBelowSupportedRange":true}""") = Ok())

    check
        "a misspelt stalenessPolicy field is rejected by conditor.schema.json"
        (validate manifestSchema (manifest """{"warnAfterMinors":2}""") |> Result.isError)

/// Validates a `conditor.test-environment.json` contract against
/// `schemas/conditor-test-environment.schema.json` (CON-368).
let private testEnvironmentSchema = lazy (loadSchema "conditor-test-environment.schema.json")

let validateTestEnvironmentContract (text: string) = validate testEnvironmentSchema.Value text

/// Validates a `conditor test-env preflight --json` report against
/// `schemas/conditor-test-environment-report.schema.json` (CON-371).
let private testEnvironmentReportSchema = lazy (loadSchema "conditor-test-environment-report.schema.json")

let validateTestEnvironmentReport (text: string) = validate testEnvironmentReportSchema.Value text
