namespace Conditor.Core

open System.IO
open System.Text
open System.Text.Json

/// The public component inventory projection emitted by `conditor components --json`
/// and described by `schemas/conditor-components.schema.json`.
module ComponentInventory =
    let distributionText =
        function
        | HostTool -> "host-tool"
        | LifecycleNpm -> "lifecycle-npm"
        | NpmPackage -> "npm"
        | NugetPackage -> "nuget"

    let bindingText =
        function
        | NpmDependency -> "npm"
        | NugetReference -> "nuget"

    let sourceText =
        function
        | RegistryPackage -> "registry"
        | GitHubSource source ->
            let entrypoint =
                match source.Entrypoint with
                | NodeScript path -> $"node-script:{path}"
                | FileArtifact path -> $"file-artifact:{path}"

            $"github:{source.Repository}#{source.Commit}:{entrypoint}"

    let private writeStringArray (writer: Utf8JsonWriter) (name: string) (values: string seq) =
        writer.WriteStartArray name
        values |> Seq.iter writer.WriteStringValue
        writer.WriteEndArray()

    let private writeOptionalString (writer: Utf8JsonWriter) (name: string) (value: string option) =
        value |> Option.iter (fun text -> writer.WriteString(name, text))

    let private writeComponent (writer: Utf8JsonWriter) (descriptor: ComponentDescriptor) =
        let definition = descriptor.Definition
        writer.WriteStartObject()
        writer.WriteString("id", definition.Id)
        writer.WriteString("displayName", definition.DisplayName)
        writer.WriteString("distribution", distributionText definition.Distribution)
        writer.WriteString("package", definition.Package)
        writer.WriteString("defaultVersion", definition.DefaultVersion)
        writer.WriteString("descriptorSha256", descriptor.Sha256)
        writeStringArray writer "qualifiedVersions" (descriptor.QualifiedVersions |> Seq.sort)

        if not definition.HistoricalPackages.IsEmpty then
            writer.WriteStartArray("historicalPackages")

            for historical in definition.HistoricalPackages do
                writer.WriteStartObject()
                writer.WriteString("package", historical.Package)
                writeStringArray writer "versions" (historical.Versions |> Seq.sort)
                writer.WriteEndObject()

            writer.WriteEndArray()

        writeOptionalString writer "lifecycleSource" (definition.LifecycleSource |> Option.map sourceText)
        writeOptionalString writer "applicationBinding" (definition.ApplicationBinding |> Option.map bindingText)
        writeOptionalString writer "command" definition.Command
        writer.WriteEndObject()

    /// Writes the inventory document for `descriptors`, ordered by component id.
    let writeJson (stream: Stream) (descriptors: ComponentDescriptor list) =
        let mutable options = JsonWriterOptions()
        options.Indented <- true
        use writer = new Utf8JsonWriter(stream, options)
        writer.WriteStartObject()
        writer.WriteNumber("schemaVersion", 1)
        writer.WriteStartArray("components")
        descriptors |> List.sortBy _.Definition.Id |> List.iter (writeComponent writer)
        writer.WriteEndArray()
        writer.WriteEndObject()
        writer.Flush()

    /// Renders the inventory document for `descriptors` as text.
    let toJson (descriptors: ComponentDescriptor list) =
        use stream = new MemoryStream()
        writeJson stream descriptors
        Encoding.UTF8.GetString(stream.ToArray())
