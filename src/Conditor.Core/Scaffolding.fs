namespace Conditor.Core

open System
open System.IO
open System.Text.Json
open System.Text.Json.Nodes
open System.Text.RegularExpressions

module Scaffolding =
    let private normalize (value: string) =
        value.Replace("\r\n", "\n")

    let private jsonString value =
        JsonSerializer.Serialize(value)

    let private identifier (value: string) =
        let words =
            Regex.Split(value, "[^A-Za-z0-9]+")
            |> Array.filter (String.IsNullOrWhiteSpace >> not)

        let candidate =
            words
            |> Array.map (fun word ->
                if word.Length = 1 then
                    word.ToUpperInvariant()
                else
                    word.Substring(0, 1).ToUpperInvariant() + word.Substring(1))
            |> String.concat String.Empty

        let candidate =
            if String.IsNullOrWhiteSpace candidate then "Application" else candidate

        if Char.IsDigit candidate[0] then "Application" + candidate else candidate

    let private packageSlug (value: string) =
        let slug =
            Regex.Replace(value.ToLowerInvariant(), "[^a-z0-9-]+", "-").Trim('-')

        if String.IsNullOrWhiteSpace slug then "application" else slug

    let private resolvedBindings (manifest: ProjectManifest) =
        manifest.Components
        |> List.choose (fun request ->
            Registry.tryFind request.Id
            |> Option.bind (fun definition ->
                definition.ApplicationBinding
                |> Option.map (fun binding ->
                    let version =
                        request.Version |> Option.defaultValue definition.DefaultVersion

                    binding, definition.Package, version)))

    [<Literal>]
    let private Folio030Commit = "273b18f5b23db15cddd173c05af5d1a8484fc4cf"

    let private dependencySpecifier package version =
        if package = "@echelon-foundry/print-components" && version = "0.3.0" then
            $"github:kemiller2002/folio#{Folio030Commit}"
        else
            version

    let private renderDependencies dependencies =
        match dependencies with
        | [] -> "  }"
        | values ->
            values
            |> List.mapi (fun index (package, version) ->
                let comma = if index = values.Length - 1 then String.Empty else ","
                let encodedPackage = jsonString package
                let encodedVersion = dependencySpecifier package version |> jsonString
                $"    {encodedPackage}: {encodedVersion}{comma}")
            |> fun lines -> String.concat "\n" lines + "\n  }"

    let private packageJson projectName manifest =
        let dependencies =
            resolvedBindings manifest
            |> List.choose (fun (binding, package, version) ->
                match binding with
                | NpmDependency -> Some(package, version)
                | NugetReference -> None)
            |> List.sortBy fst

        let dependencyBody = renderDependencies dependencies
        let encodedName = jsonString (packageSlug projectName + "-kernel")

        $"{{\n  \"name\": {encodedName},\n  \"private\": true,\n  \"type\": \"module\",\n  \"scripts\": {{\n    \"check\": \"tsc --noEmit\"\n  }},\n  \"dependencies\": {{\n{dependencyBody},\n  \"devDependencies\": {{\n    \"typescript\": \"5.9.3\"\n  }}\n}}\n"

    let private projectFile projectName manifest =
        let packageReferences =
            resolvedBindings manifest
            |> List.choose (fun (binding, package, version) ->
                match binding with
                | NugetReference -> Some(package, version)
                | NpmDependency -> None)
            |> List.sortBy fst

        let itemGroup =
            match packageReferences with
            | [] -> String.Empty
            | references ->
                let lines =
                    references
                    |> List.map (fun (package, version) ->
                        $"    <PackageReference Include=\"{package}\" Version=\"{version}\" />")
                    |> String.concat "\n"

                $"\n  <ItemGroup>\n{lines}\n  </ItemGroup>"

        let assembly = identifier projectName + ".Engine"

        $"<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>net10.0</TargetFramework>\n    <Nullable>enable</Nullable>\n    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>\n    <AssemblyName>{assembly}</AssemblyName>\n  </PropertyGroup>{itemGroup}\n  <ItemGroup>\n    <Compile Include=\"Operational.fs\" />\n    <Compile Include=\"Domain.fs\" />\n  </ItemGroup>\n</Project>\n"


    let private requestedComponent id (manifest: ProjectManifest) =
        manifest.Components |> List.tryFind (fun request -> request.Id = id)

    let private resolvedVersion id (manifest: ProjectManifest) =
        requestedComponent id manifest
        |> Option.bind (fun request ->
            Registry.tryFind id
            |> Option.map (fun definition -> request.Version |> Option.defaultValue definition.DefaultVersion))

    let private foundationManifest (projectName: string) (manifest: ProjectManifest) =
        let capabilities = JsonObject()

        let addCapability (id: string) (configure: JsonObject -> unit) =
            let node = JsonObject()
            let request = requestedComponent id manifest
            node["required"] <- JsonValue.Create(request |> Option.exists _.Required)

            resolvedVersion id manifest
            |> Option.iter (fun (version: string) -> node["version"] <- JsonValue.Create version)

            configure node
            capabilities[id] <- node

        addCapability "aegis" (fun node -> node["boundaryManifest"] <- JsonValue.Create "aegis-boundaries.json")
        addCapability "forma" ignore

        addCapability "folio" (fun node ->
            match resolvedVersion "folio" manifest with
            | Some "0.3.0" -> node["sourceCommit"] <- JsonValue.Create Folio030Commit
            | _ -> ())

        addCapability "limen" ignore
        addCapability "ordo" ignore
        addCapability "praxis" ignore

        let root = JsonObject()
        root["schemaVersion"] <- JsonValue.Create 1
        root["application"] <- JsonValue.Create projectName
        root["capabilities"] <- capabilities
        root.ToJsonString(JsonSerializerOptions(WriteIndented = true, IndentSize = 2)) + "\n"

    let private aegisBoundaryManifest (projectName: string) =
        let root = JsonObject()
        root["schema"] <- JsonValue.Create "aegis/boundaries/v1"
        root["application"] <- JsonValue.Create projectName

        let boundaries = JsonArray()

        let startup = JsonObject()
        startup["name"] <- JsonValue.Create "Configuration and startup"
        startup["kind"] <- JsonValue.Create "startup"
        startup["owner"] <- JsonValue.Create "application"
        startup["codes"] <- JsonArray(JsonValue.Create "AEGIS.CONFIG.INVALID_CONFIGURATION")
        startup["guarded"] <- JsonValue.Create true
        boundaries.Add startup

        let limen = JsonObject()
        limen["name"] <- JsonValue.Create "Limen interop"
        limen["kind"] <- JsonValue.Create "ui-interop"
        limen["owner"] <- JsonValue.Create "application"
        limen["codes"] <- JsonArray(JsonValue.Create "AEGIS.LIMEN.INTEROP_FAILED")
        limen["guarded"] <- JsonValue.Create false
        boundaries.Add limen

        root["boundaries"] <- boundaries
        root.ToJsonString(JsonSerializerOptions(WriteIndented = true, IndentSize = 2)) + "\n"

    let private operationalFile projectName =
        let ns = identifier projectName

        $"namespace {ns}.Engine\n\nopen Aegis\n\nmodule Operational =\n    let aegis = Aegis.configure {jsonString projectName} None [ Sinks.standardError ]\n\n    let validateConfiguration () =\n        Bootstrap.validate None aegis\n"

    let private agentEntryFile (manifest: ProjectManifest) =
        manifest.Execution
        |> Option.bind (fun execution ->
            execution.ContractPath
            |> Option.filter (String.IsNullOrWhiteSpace >> not)
            |> Option.map (fun contractPath ->
                let mission =
                    execution.Mission
                    |> Option.filter (String.IsNullOrWhiteSpace >> not)
                    |> Option.defaultValue "Execute the project contract and prove completion through repository evidence."

                "AGENTS.md",
                $"# Agent Entry\n\nThis repository was initialized by Conditor.\n\n## Canonical execution contract\n\nRead and obey `{contractPath}` before implementation.\n\n## Mission\n\n{mission}\n\n## Rules\n\n- Treat the contract and its referenced normative documents as authoritative.\n- Do not invent architecture decisions that the contract classifies as locked, experimental, or deferred.\n- Use installed lifecycle/component tooling rather than copying framework implementations.\n- Completion requires deterministic verification and repository evidence, not an agent completion statement.\n" ))

    let private markdownRequirementList (manifest: ProjectManifest) =
        match manifest.Requirements with
        | [] -> "- none declared"
        | requirements ->
            requirements
            |> List.map (fun requirement -> $"- `{requirement.Id}`: `{requirement.TargetPath}`")
            |> String.concat "\n"

    let private contractPathText (manifest: ProjectManifest) =
        manifest.Execution
        |> Option.bind _.ContractPath
        |> Option.filter (String.IsNullOrWhiteSpace >> not)
        |> Option.defaultValue "none declared"

    let private ordoBaselineFiles (manifest: ProjectManifest) =
        let hasOrdo =
            manifest.Components
            |> List.exists (fun request -> request.Id = "ordo" && request.Required)

        if not hasOrdo then
            []
        else
            let contractPath = contractPathText manifest
            let requirements = markdownRequirementList manifest

            let semanticMap =
                $"# Repository Semantic Map\n\nThis initial routing map was established by Conditor from accepted governing inputs. It does not infer application semantics.\n\n## Governing inputs\n\n- Canonical execution contract: `{contractPath}`\n- Accepted requirement artifacts:\n{requirements}\n\n## Semantic areas\n\n| Semantic area / feature | Purpose | Location | Manifest | Notes |\n|---|---|---|---|---|\n| Initial application mission | Establish the smallest semantic model required by the accepted contract | `src/engine/` | not established yet | Domain concepts, legal state, transitions, invariants, and guards are intentionally unknown until the governing inputs are interpreted. |\n\n## Repository-wide composition\n\n- Composition/root entry point: `src/kernel/bootstrap.ts`\n- Shared contracts: see the governing inputs above.\n- Architecture checks: installed Ordo/Praxis verification plus repository build/tests.\n- Boundary checks: installed Limen and Aegis contracts where required.\n\n## Areas without separate manifests\n\nNo feature manifest is generated merely to satisfy structure. Create one when the initial mission establishes a real semantic ownership boundary.\n"

            let currentState =
                $"# Current State — Conditor Baseline\n\nThis is the initial greenfield baseline. It records what Conditor can prove before application implementation begins.\n\n## Established facts\n\n- The declared Echelon capabilities were planned and installed through their supported lifecycle contracts.\n- Accepted requirement artifacts were materialized from immutable sources declared by `conditor.json`.\n- Canonical execution contract: `{contractPath}`.\n- The generated `src/engine/Domain.fs` state is a scaffold placeholder, not a claim that the application domain has been modeled.\n\n## Accepted requirement artifacts\n\n{requirements}\n\n## Unknowns\n\n- Application domain concepts have not yet been derived.\n- Important legal state and illegal states have not yet been identified.\n- Legal transitions, invariants, guards, capabilities, and effect obligations have not yet been established.\n- Semantic feature boundaries and ownership are not yet known.\n\n## Obligations before implementation expands\n\n1. Read the canonical execution contract and its normative references.\n2. Identify the smallest domain concepts and legal state required by the first meaningful vertical behavior.\n3. Establish legal transitions, invariants, guards, capabilities, and explicit effects required by that behavior.\n4. Update `SDE-MAP.md` and create feature manifests only when real semantic ownership is known.\n5. Preserve unknowns explicitly rather than converting missing knowledge into assumptions.\n\n## Next action\n\nExecute the initial Praxis mission using the accepted governing inputs and establish the first evidence-backed Ordo semantic slice.\n"

            [ "SDE-MAP.md", semanticMap
              "context/CURRENT-STATE.md", currentState ]

    let private desiredFiles (manifest: ProjectManifest) (scaffold: ScaffoldRequest) =
        let projectName =
            scaffold.Name
            |> Option.filter (String.IsNullOrWhiteSpace >> not)
            |> Option.defaultValue manifest.Name

        let ns = identifier projectName

        match scaffold.Kind with
        | "fsharp-limen-web" ->
            let files =
                [ "Directory.Build.props",
                  "<Project>\n  <PropertyGroup>\n    <TargetFramework>net10.0</TargetFramework>\n    <LangVersion>latest</LangVersion>\n    <Nullable>enable</Nullable>\n    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>\n    <Deterministic>true</Deterministic>\n  </PropertyGroup>\n</Project>\n"
                  "App.slnx",
                  "<Solution>\n  <Folder Name=\"/src/\">\n    <Project Path=\"src/engine/App.Engine.fsproj\" />\n  </Folder>\n</Solution>\n"
                  "src/engine/App.Engine.fsproj", projectFile projectName manifest
                  ".echelon/foundations.json", foundationManifest projectName manifest
                  "aegis-boundaries.json", aegisBoundaryManifest projectName
                  "src/engine/Operational.fs", operationalFile projectName
                  "src/engine/Domain.fs",
                  $"namespace {ns}.Engine\n\ntype State =\n    | Uninitialized\n\nmodule State =\n    let initial = Uninitialized\n"
                  "src/kernel/package.json", packageJson projectName manifest
                  "src/kernel/tsconfig.json",
                  "{\n  \"compilerOptions\": {\n    \"target\": \"ES2022\",\n    \"module\": \"ES2022\",\n    \"moduleResolution\": \"Bundler\",\n    \"strict\": true,\n    \"noEmit\": true,\n    \"lib\": [\"ES2022\", \"DOM\"]\n  },\n  \"include\": [\"**/*.ts\"]\n}\n"
                  "src/kernel/bootstrap.ts",
                  "import type { ViewState } from \"@echelon-foundry/typescript-wasm-kernel/protocol\";\n\nexport const scaffoldReady = true as const;\nexport type ScaffoldView = ViewState;\n"
                  "src/kernel/index.html",
                  "<!doctype html>\n<html lang=\"en\">\n<head>\n  <meta charset=\"utf-8\">\n  <meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">\n  <link rel=\"stylesheet\" href=\"./node_modules/@echelon-foundry/design-system/dist/all.css\">\n  <title>Application</title>\n</head>\n<body>\n  <main>\n    <ef-button><button type=\"button\">Ready</button></ef-button>\n  </main>\n</body>\n</html>\n"
                  "src/kernel/print.html",
                  "<!doctype html>\n<html lang=\"en\">\n<head>\n  <meta charset=\"utf-8\">\n  <link rel=\"stylesheet\" href=\"./node_modules/@echelon-foundry/print-components/src/styles/print.css\">\n  <script type=\"module\" src=\"./node_modules/@echelon-foundry/print-components/src/components/register.js\"></script>\n  <title>Printable document</title>\n</head>\n<body>\n  <ef-print-document><main><h1>Printable document</h1></main></ef-print-document>\n</body>\n</html>\n" ]

            let filesWithOrdoBaseline = files @ ordoBaselineFiles manifest

            match agentEntryFile manifest with
            | Some agentFile -> Ok(agentFile :: filesWithOrdoBaseline)
            | None -> Ok filesWithOrdoBaseline
        | unknown ->
            Error [ $"Unsupported scaffold kind '{unknown}'." ]

    let private safeFullPath target relativePath =
        let root = Path.GetFullPath target
        let full = Path.GetFullPath(Path.Combine(root, relativePath))
        let rootPrefix =
            root.TrimEnd(Path.DirectorySeparatorChar) + string Path.DirectorySeparatorChar

        let comparison =
            if OperatingSystem.IsWindows() then
                StringComparison.OrdinalIgnoreCase
            else
                StringComparison.Ordinal

        if full.StartsWith(rootPrefix, comparison) then Some full else None

    let plan target (manifest: ProjectManifest) =
        match manifest.Scaffold with
        | None -> Ok []
        | Some scaffold ->
            match desiredFiles manifest scaffold with
            | Error errors -> Error errors
            | Ok desired ->
                let errors = ResizeArray<string>()
                let changes = ResizeArray<string * string>()

                for relativePath, content in desired do
                    match safeFullPath target relativePath with
                    | None ->
                        errors.Add $"Scaffold path escapes the target repository: {relativePath}"
                    | Some fullPath when relativePath = "AGENTS.md" || relativePath = "context/CURRENT-STATE.md" ->
                        // These are shared integration surfaces. The installer owns only
                        // bounded Conditor regions and resolves current file contents at execution time.
                        changes.Add(relativePath, content)
                    | Some fullPath ->
                        if File.Exists fullPath then
                            let existing = File.ReadAllText fullPath

                            if normalize existing <> normalize content then
                                errors.Add
                                    $"Scaffold file '{relativePath}' already exists with different content; Conditor will not overwrite it."
                        else
                            changes.Add(relativePath, content)

                if errors.Count > 0 then
                    Error(List.ofSeq errors)
                else
                    Ok(List.ofSeq changes)
