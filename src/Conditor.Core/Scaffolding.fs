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

                    binding, ComponentDefinition.packageFor version definition, version)))

    [<Literal>]
    let private Folio030Release =
        "https://github.com/kemiller2002/folio/releases/download/v0.3.0/echelon-foundry-print-components-0.3.0.tgz"

    let private dependencySpecifier package version =
        if package = "@echelon-foundry/print-components" && version = "0.3.0" then
            Folio030Release
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

    /// The Playwright release the browser suite pins exactly (the version
    /// Signal pins). The pin fixes the Chromium build CI caches.
    [<Literal>]
    let PlaywrightVersion = "1.63.0"

    let private packageJson projectName manifest =
        let dependencies =
            resolvedBindings manifest
            |> List.choose (fun (binding, package, version) ->
                match binding with
                | NpmDependency -> Some(package, version)
                | NugetReference -> None)
            |> List.sortBy fst

        let dependencyBody = renderDependencies dependencies
        let encodedName = jsonString (packageSlug projectName)

        $"{{\n  \"name\": {encodedName},\n  \"private\": true,\n  \"type\": \"module\",\n  \"scripts\": {{\n    \"check\": \"tsc --noEmit\",\n    \"test:browser\": \"playwright test\"\n  }},\n  \"dependencies\": {{\n{dependencyBody},\n  \"devDependencies\": {{\n    \"@playwright/test\": \"{PlaywrightVersion}\",\n    \"typescript\": \"5.9.3\"\n  }}\n}}\n"

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

        $"<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>net10.0</TargetFramework>\n    <Nullable>enable</Nullable>\n    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>\n    <AssemblyName>{assembly}</AssemblyName>\n  </PropertyGroup>{itemGroup}\n  <ItemGroup>\n    <Compile Include=\"Operational.fs\" />\n    <Compile Include=\"Routes.fs\" />\n    <Compile Include=\"Domain.fs\" />\n  </ItemGroup>\n</Project>\n"


    let private requestedComponent id (manifest: ProjectManifest) =
        manifest.Components |> List.tryFind (fun request -> request.Id = id)

    let private resolvedVersion id (manifest: ProjectManifest) =
        requestedComponent id manifest
        |> Option.bind (fun request ->
            Registry.tryFind id
            |> Option.map (fun definition -> request.Version |> Option.defaultValue definition.DefaultVersion))

    /// The Limen package the scaffold's browser kernel imports: the identity
    /// under which the requested Limen version is distributed.
    let private limenPackage (manifest: ProjectManifest) =
        Registry.tryFind "limen"
        |> Option.map (fun definition ->
            let version =
                resolvedVersion "limen" manifest |> Option.defaultValue definition.DefaultVersion

            ComponentDefinition.packageFor version definition)

    let private kernelBootstrap (limen: string) =
        let protocolModule = jsonString (limen + "/protocol")

        $"import type {{ ViewState }} from {protocolModule};\n\nexport const scaffoldReady = true as const;\nexport type ScaffoldView = ViewState;\n"

    // ---------------------------------------------------------------------
    // URL-addressable state (Praxis SAF-URL-1..10; Limen LCP-088..112 and
    // DF-LIMEN-2026-0006). A web application keeps its navigable state in
    // the URL, publishes a route inventory, and routes through Limen's
    // routing module from Limen 0.9.0. Until the application can install
    // 0.9.0, the scaffold gives it a pure placeholder codec with round-trip
    // tests, and `praxis foundations verify` reports Limen routing as pending
    // (ECHELON-FND-ROUTING-005), not failed.
    // ---------------------------------------------------------------------

    /// Where every Echelon web application publishes its route inventory.
    [<Literal>]
    let RouteInventoryPath = ".echelon/routes.json"

    /// The starting `echelon.routes/v1` inventory: hash mode for a static
    /// site, a home route and a not-found route. Written as Limen's
    /// Inventory.render writes it (sorted keys, two-space indent, final
    /// newline), so regenerating it with Limen later changes only content.
    let webRouteInventory =
        """{
  "home": "home",
  "legacy": [],
  "mode": "hash",
  "notFound": "notFound",
  "routes": [
    {
      "guard": null,
      "name": "home",
      "params": [],
      "pattern": "/",
      "requires": [],
      "returnTarget": true
    },
    {
      "guard": null,
      "name": "notFound",
      "params": [
        {
          "default": null,
          "in": "path",
          "name": "rest",
          "required": true,
          "type": "string",
          "values": []
        }
      ],
      "pattern": "/{*rest}",
      "requires": [],
      "returnTarget": false
    }
  ],
  "schema": "echelon.routes/v1",
  "signIn": null
}
"""

    /// The engine's route codec: pure, total, and replaced by Limen.Routing
    /// once the application is on Limen 0.9.0.
    let private webRoutesSource =
        """namespace @@NS@@.Engine

open System

/// The application's navigable views (SAF-URL-1..10). The URL is the source
/// of truth: the engine derives the view from `Routes.parse` and writes the
/// URL with `Routes.format`. Routes live in the hash (`index.html#/...`), so a
/// reloaded or pasted deep link never 404s on GitHub Pages
/// (DF-LIMEN-2026-0006).
///
/// PLACEHOLDER until the application installs Limen 0.9.0: replace this
/// module with `Limen.Routing` (package EchelonFoundry.Limen.Routing:
/// RouteTable.define, RouteCodec.create/parse/format, Navigation.adopt,
/// navigate and refine, ReturnTo.capture/resume, Link.share), and regenerate
/// `.echelon/routes.json` with `Inventory.render`. Keep the inventory in step
/// with this table meanwhile.
type Route =
    | Home
    | NotFound of path: string

[<RequireQualifiedAccess>]
module Routes =
    let private pathOf (location: string) =
        let fragment = if location.StartsWith("#", StringComparison.Ordinal) then location.Substring 1 else location

        match fragment.IndexOf '?' with
        | -1 -> fragment
        | index -> fragment.Substring(0, index)

    /// Pure and total: a location hash (with or without the leading '#') to
    /// its route. Unknown paths are a value, never an exception.
    let parse (location: string) : Route =
        match pathOf location with
        | ""
        | "/" -> Home
        | path when path.StartsWith("/", StringComparison.Ordinal) -> NotFound path
        | path -> NotFound("/" + path)

    /// Pure: the canonical location hash of a route.
    let format (route: Route) : string =
        match route with
        | Home -> "#/"
        | NotFound path -> "#" + path
"""

    let private webRouteTests =
        """module App.Engine.Tests.RouteTests

open System
open Xunit
open @@NS@@.Engine

// SAF-URL-9: format (parse url) is the canonical url, and parse (format route)
// is the route, for generated input.
let private generated count =
    let random = Random 20261008
    let alphabet = "abcdefghijklmnopqrstuvwxyz0123456789-"
    let segment () = String(Array.init (random.Next(1, 9)) (fun _ -> alphabet[random.Next alphabet.Length]))

    List.init count (fun _ -> "/" + String.Join("/", Array.init (random.Next(1, 4)) (fun _ -> segment ())))

[<Fact>]
let ``the home route is the empty and the root hash`` () =
    Assert.Equal(Home, Routes.parse "")
    Assert.Equal(Home, Routes.parse "#/")
    Assert.Equal("#/", Routes.format Home)

[<Fact>]
let ``format (parse url) is the canonical url`` () =
    for path in generated 500 do
        Assert.Equal("#" + path, Routes.format (Routes.parse ("#" + path + "?undeclared=1")))

[<Fact>]
let ``parse (format route) is the route`` () =
    for path in generated 500 do
        let route = NotFound path
        Assert.Equal(route, Routes.parse (Routes.format route))
"""

    /// The web application's deep-linking requirements, as a checklist the
    /// application's agent works through (Praxis SAF-URL-1..10).
    let private webUrlRequirements =
        """# URL-addressable state (deep linking)

Status: **Required** for this application. The canonical text is
[Praxis `requirements/SHARED-APPLICATION-FOUNDATIONS.md`](https://github.com/kemiller2002/praxis/blob/main/requirements/SHARED-APPLICATION-FOUNDATIONS.md)
("URL-addressable state (deep linking)", SAF-URL-1..10). Limen 0.9.0
implements it (`@echelon-foundry/limen/routing`, `EchelonFoundry.Limen.Routing`;
LCP-088..112, DF-LIMEN-2026-0006).

Navigable state means the screen or route, entity IDs, and view parameters
such as the selected date or period, filters, sort, search, tab and page. It
lives in the URL, so a copied URL opens the same view in another browser.

## Checklist

- [ ] **SAF-URL-1** The URL is the source of truth. A cold load of a copied
      URL reproduces the view, and sign-in returns to the target.
- [ ] **SAF-URL-2** IDs are path parameters and view parameters are query
      parameters, typed (`string`, `int`, `bool`, `date`, `month`, `enum`,
      `set`). The canonical form has declared parameters only, in
      declaration order, with defaults omitted, sets sorted and
      de-duplicated, and `%20` with upper-case hex.
- [ ] **SAF-URL-3** Navigation pushes history and refinement replaces it.
      Back, Forward and reload restore the view from the URL alone.
- [ ] **SAF-URL-4** An unknown, deleted or forbidden ID shows not found or
      not permitted, with a way back. The page is never blank and never
      shows another record.
- [ ] **SAF-URL-5** No secrets, tokens or sensitive personal or financial
      data in any URL. Limen's reserved parameter names are refused. No
      transient UI state goes in the URL.
- [ ] **SAF-URL-6** Hash routes (`index.html#/...`), relative links and no
      `<base href>`, so a reloaded deep link never 404s on GitHub Pages.
- [ ] **SAF-URL-7** Renamed routes keep their old URLs as legacy redirects.
- [ ] **SAF-URL-8** `.echelon/routes.json` (`echelon.routes/v1`) lists every
      addressable view and stays current with the route table.
- [ ] **SAF-URL-9** One pure parse/format codec with round-trip property
      tests (`src/engine/Routes.fs` until Limen 0.9.0, then `Limen.Routing`).
- [ ] **SAF-URL-10** A Copy link action wherever sharing is natural.

## Verification

`praxis foundations verify` (the `routing` capability in
`.echelon/foundations.json`) checks the inventory on every pull request.
Until the application can install Limen 0.9.0, it reports Limen routing as
pending (`ECHELON-FND-ROUTING-005`), which is not a failure.
"""

    let private foundationManifest (webApplication: bool) (projectName: string) (manifest: ProjectManifest) =
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
            | Some "0.3.0" -> node["releaseArtifact"] <- JsonValue.Create Folio030Release
            | _ -> ())

        addCapability "limen" ignore
        addCapability "ordo" ignore
        // Exactly the capabilities of Praxis' echelon-foundations-v1 schema
        // (plus routing, below, for a web application), which forbids any
        // other key. Percepta verifies through its own
        // repository lifecycle (percepta-repo verify), not the foundations.
        addCapability "praxis" ignore

        // URL-addressable state (Praxis SAF-URL-1..10, Praxis 3.9.0 and later):
        // every web application publishes its route inventory and, from
        // Limen 0.9.0, routes through Limen. A static (GitHub Pages) site
        // uses hash routes (DF-LIMEN-2026-0006).
        if webApplication then
            let routing = JsonObject()
            routing["required"] <- JsonValue.Create true
            routing["hosting"] <- JsonValue.Create "static"
            routing["inventory"] <- JsonValue.Create RouteInventoryPath
            capabilities["routing"] <- routing

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

    /// Where the Ordo baseline routes its first semantic area, per scaffold
    /// kind: the code location and the composition root it names.
    type private BaselineLocations =
        { SemanticArea: string
          CompositionRoot: string
          BoundaryChecks: string
          Placeholder: string }

    let private webBaselineLocations =
        { SemanticArea = "src/engine/"
          CompositionRoot = "src/kernel/bootstrap.ts"
          BoundaryChecks = "installed Limen and Aegis contracts where required"
          Placeholder = "The generated `src/engine/Domain.fs` state" }

    let private ordoBaselineFiles (locations: BaselineLocations) (manifest: ProjectManifest) =
        let hasOrdo =
            manifest.Components
            |> List.exists (fun request -> request.Id = "ordo" && request.Required)

        if not hasOrdo then
            []
        else
            let contractPath = contractPathText manifest
            let requirements = markdownRequirementList manifest

            let semanticMap =
                $"# Repository Semantic Map\n\nThis initial routing map was established by Conditor from accepted governing inputs. It does not infer application semantics.\n\n## Governing inputs\n\n- Canonical execution contract: `{contractPath}`\n- Accepted requirement artifacts:\n{requirements}\n\n## Semantic areas\n\n| Semantic area / feature | Purpose | Location | Manifest | Notes |\n|---|---|---|---|---|\n| Initial application mission | Establish the smallest semantic model required by the accepted contract | `{locations.SemanticArea}` | not established yet | Domain concepts, legal state, transitions, invariants, and guards are intentionally unknown until the governing inputs are interpreted. |\n\n## Repository-wide composition\n\n- Composition/root entry point: `{locations.CompositionRoot}`\n- Shared contracts: see the governing inputs above.\n- Architecture checks: installed Ordo/Praxis verification plus repository build/tests.\n- Boundary checks: {locations.BoundaryChecks}.\n\n## Areas without separate manifests\n\nNo feature manifest is generated merely to satisfy structure. Create one when the initial mission establishes a real semantic ownership boundary.\n"

            let currentState =
                $"# Current State — Conditor Baseline\n\nThis is the initial greenfield baseline. It records what Conditor can prove before application implementation begins.\n\n## Established facts\n\n- The declared Echelon capabilities were planned and installed through their supported lifecycle contracts.\n- Accepted requirement artifacts were materialized from immutable sources declared by `conditor.json`.\n- Canonical execution contract: `{contractPath}`.\n- {locations.Placeholder} is a scaffold placeholder, not a claim that the application domain has been modeled.\n\n## Accepted requirement artifacts\n\n{requirements}\n\n## Unknowns\n\n- Application domain concepts have not yet been derived.\n- Important legal state and illegal states have not yet been identified.\n- Legal transitions, invariants, guards, capabilities, and effect obligations have not yet been established.\n- Semantic feature boundaries and ownership are not yet known.\n\n## Obligations before implementation expands\n\n1. Read the canonical execution contract and its normative references.\n2. Identify the smallest domain concepts and legal state required by the first meaningful vertical behavior.\n3. Establish legal transitions, invariants, guards, capabilities, and explicit effects required by that behavior.\n4. Update `SDE-MAP.md` and create feature manifests only when real semantic ownership is known.\n5. Preserve unknowns explicitly rather than converting missing knowledge into assumptions.\n\n## Next action\n\nExecute the initial Praxis mission using the accepted governing inputs and establish the first evidence-backed Ordo semantic slice.\n"

            [ "SDE-MAP.md", semanticMap
              "context/CURRENT-STATE.md", currentState ]

    // ---------------------------------------------------------------------
    // fsharp-nuget-library: a new project-bound NuGet library repository.
    //
    // Everything the ecosystem expects of a package repository that the
    // lifecycle components themselves do not install: a buildable library
    // and test project, the foundations declaration with its CI check, a
    // build-and-test workflow, and the Registry release-contract workflow for
    // the `nuget-library` distribution class
    // (echelon-registry spec/ecosystem-release-workflow-requirements.md).
    // ---------------------------------------------------------------------

    /// The Praxis commit whose reusable foundations-verify workflow the
    /// generated CI pins (the same commit sibling package repositories pin).
    [<Literal>]
    let PraxisFoundationsRef = "a95dbf238e561eaac4b38ca7011efc1a496cf1c6"

    /// The echelon-registry commit whose release-contract action and
    /// release-manifest schema the generated release workflow pins.
    [<Literal>]
    let RegistryReleaseContractRef = "abb470a2bf3cf838e4f4324c3b5c4f6fdfcfea0a"

    /// The version a fresh library declares. The release workflow treats it as
    /// "not released yet", so merging the scaffold publishes nothing.
    [<Literal>]
    let UnreleasedVersion = "0.0.0"

    let private libraryBaselineLocations (ns: string) =
        { SemanticArea = $"src/{ns}/"
          CompositionRoot = $"src/{ns}/{ns}.fsproj"
          BoundaryChecks = "installed Ordo/Praxis verification and the declared Echelon foundations"
          Placeholder = $"The generated `src/{ns}/Library.fs` value" }

    /// The NuGet identity of the library: Echelon packages publish under the
    /// `EchelonFoundry.` prefix (for example EchelonFoundry.Aegis.Core).
    let libraryPackageId (projectName: string) = "EchelonFoundry." + identifier projectName

    let private fill (tokens: (string * string) list) (template: string) =
        tokens |> List.fold (fun (text: string) (token, value) -> text.Replace(token, value)) template

    let private libraryProps =
        """<Project>
  <PropertyGroup>
    <!-- The release version. 0.0.0 means "not released yet": the release
         workflow builds, tests and packs it but never publishes it. -->
    <Version>@@VERSION@@</Version>
    <TargetFramework>net10.0</TargetFramework>
    <LangVersion>latest</LangVersion>
    <Nullable>enable</Nullable>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <Deterministic>true</Deterministic>
  </PropertyGroup>
</Project>
"""

    let private librarySolution =
        """<Solution>
  <Folder Name="/src/">
    <Project Path="src/@@NS@@/@@NS@@.fsproj" />
  </Folder>
  <Folder Name="/tests/">
    <Project Path="tests/@@NS@@.Tests/@@NS@@.Tests.fsproj" />
  </Folder>
</Solution>
"""

    let private libraryProject (manifest: ProjectManifest) =
        let references =
            resolvedBindings manifest
            |> List.choose (fun (binding, package, version) ->
                match binding with
                | NugetReference -> Some(package, version)
                | NpmDependency -> None)
            |> List.sortBy fst
            |> List.map (fun (package, version) -> $"    <PackageReference Include=\"{package}\" Version=\"{version}\" />")

        let itemGroup =
            match references with
            | [] -> String.Empty
            | lines -> "\n  <ItemGroup>\n" + String.concat "\n" lines + "\n  </ItemGroup>"

        """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <IsPackable>true</IsPackable>
    <PackageId>@@PACKAGE@@</PackageId>
    <AssemblyName>@@NS@@</AssemblyName>
    <RootNamespace>@@NS@@</RootNamespace>
    <Description>@@NAME@@ (Echelon Foundry).</Description>
    <GenerateDocumentationFile>true</GenerateDocumentationFile>
  </PropertyGroup>@@ITEMS@@
  <ItemGroup>
    <Compile Include="Library.fs" />
  </ItemGroup>
</Project>
"""
        |> fill [ "@@ITEMS@@", itemGroup ]

    let private librarySource =
        """namespace @@NS@@

/// Scaffold placeholder established by Conditor. It is not a claim that the
/// library's domain has been modeled.
module Library =
    /// True once the scaffold builds; replace with the first real slice.
    let scaffoldReady = true
"""

    /// The test project is a real `dotnet test` project (xUnit, the
    /// versions Chrona and Signal pin) so test evidence reaches every TRX
    /// consumer, quality gates that read test results included.
    let private libraryTestProject =
        """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <IsPackable>false</IsPackable>
    <IsTestProject>true</IsTestProject>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="../../src/@@NS@@/@@NS@@.fsproj" />
  </ItemGroup>
  <ItemGroup>
    <Compile Include="LibraryTests.fs" />
  </ItemGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.11.1" />
    <PackageReference Include="xunit" Version="2.9.2" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2" />
  </ItemGroup>
</Project>
"""

    let private libraryTests =
        """module @@NS@@.Tests.LibraryTests

open Xunit

[<Fact>]
let ``scaffold builds and links the library`` () =
    Assert.True @@NS@@.Library.scaffoldReady
"""

    /// `dotnet test` exits 0 for an empty or fully skipped run, so the step
    /// also requires at least one passed test and no skipped ones.
    let private dotnetTestStep (solution: string) =
        """      - name: Test
        shell: bash
        run: |
          set -uo pipefail
          output=$(dotnet test @@SOLUTION@@ -c Release --no-build 2>&1)
          status=$?
          echo "$output"
          if [ "$status" -ne 0 ]; then exit "$status"; fi
          if ! echo "$output" | grep -qE 'Passed:[[:space:]]*[1-9]'; then
            echo "::error::No test passed. An empty test run is not a pass."; exit 1
          fi
          if echo "$output" | grep -qE 'Skipped:[[:space:]]*[1-9]'; then
            echo "::error::Tests were skipped. A run that skips tests is not a pass."; exit 1
          fi
"""
        |> fun step -> step.Replace("@@SOLUTION@@", solution)

    let private libraryTestStep = dotnetTestStep "@@NS@@.slnx"

    let private libraryBuildWorkflow =
        """name: Build and test

# Build the library and run its tests on every pull request and on main.
# The test step fails an empty or skipped run as well as a failing one.

on:
  pull_request:
  push:
    branches: [main]

permissions:
  contents: read

concurrency:
  group: ${{ github.workflow }}-${{ github.ref }}
  cancel-in-progress: true

jobs:
  build-and-test:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: "10.0.x"
      - name: Build
        run: dotnet build @@NS@@.slnx -c Release
@@TEST_STEP@@      - name: Pack
        run: dotnet pack src/@@NS@@/@@NS@@.fsproj -c Release --no-build -o dist
"""

    let private foundationsWorkflow =
        """name: Echelon foundations

on:
  push:
  pull_request:

permissions:
  contents: read

jobs:
  foundations:
    uses: kemiller2002/praxis/.github/workflows/foundations-verify.yml@@@PRAXIS_REF@@
    with:
      praxis_ref: @@PRAXIS_REF@@
"""

    let private libraryReleaseInput =
        """{
  "schema": "echelon.release-input/v1",
  "systemId": "@@SYSTEM@@",
  "distributionClass": "nuget-library",
  "executable": null,
  "compatibilityAliases": [],
  "provides": [],
  "distribution": {
    "mechanism": "nuget",
    "package": "@@PACKAGE@@",
    "url": null
  },
  "artifacts": [
    {
      "name": "@@PACKAGE@@.{version}.nupkg",
      "purpose": "package",
      "platform": null,
      "mediaType": "application/zip"
    },
    {
      "name": "checksums.txt",
      "purpose": "checksums",
      "platform": null,
      "mediaType": "text/plain"
    }
  ]
}
"""

    let private libraryReleaseWorkflow =
        """name: NuGet library release

# The Echelon release pattern for a `nuget-library` system
# (echelon-registry spec/ecosystem-release-workflow-requirements.md):
#
#   - the package is built, tested and packed once; checksums.txt holds the
#     SHA-256 of the exact bytes that ship;
#   - echelon-release.json, the echelon.release/v2 manifest, is generated by
#     the Registry's release-contract action from those same bytes and
#     validated against the Registry schema before anything is published;
#   - the package goes to nuget.org through Trusted Publishing (OIDC; no API
#     key is stored) and the same assets go to an immutable GitHub release
#     with build-provenance attestations.
#
# The version source is <Version> in Directory.Build.props. 0.0.0 means "not
# released yet": every job except publication still runs, so packaging is
# proven before the first release. A push to main that changes the version
# publishes `v<version>`; a dispatch from main publishes the current version if
# it is not out yet. A published version is never replaced.
#
# Publication needs the repository variable NUGET_USER (the nuget.org account
# that owns the Trusted Publishing policy for this repository and this
# workflow file). Without it publication fails rather than guessing.

on:
  pull_request:
    paths:
      - ".github/workflows/release.yml"
      - "release/**"
      - "Directory.Build.props"
      - "src/**"
  push:
    branches: [main]
    paths:
      - "Directory.Build.props"
      - ".github/workflows/release.yml"
      - "release/**"
  workflow_dispatch: {}

permissions:
  contents: read

concurrency:
  group: @@SYSTEM@@-release-${{ github.ref }}
  cancel-in-progress: ${{ github.event_name == 'pull_request' }}

env:
  DOTNET_CLI_TELEMETRY_OPTOUT: "1"
  # The Registry commit whose release-contract action and schema this uses.
  ECHELON_REGISTRY_REF: @@REGISTRY_REF@@

jobs:
  version:
    name: Resolve the release version
    runs-on: ubuntu-latest
    outputs:
      version: ${{ steps.read.outputs.version }}
      publishable: ${{ steps.read.outputs.publishable }}
    steps:
      - uses: actions/checkout@v4
      - id: read
        shell: bash
        run: |
          set -euo pipefail
          declared=$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' Directory.Build.props)
          if ! [[ "$declared" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
            echo "::error::Directory.Build.props declares '$declared', not an X.Y.Z release version."; exit 1
          fi
          if [ "$GITHUB_EVENT_NAME" != "pull_request" ] && [ "$GITHUB_REF" != "refs/heads/main" ]; then
            echo "::error::Releases are published from main only, not $GITHUB_REF."; exit 1
          fi
          echo "version=$declared" >> "$GITHUB_OUTPUT"
          if [ "$declared" = "@@UNRELEASED@@" ]; then
            echo "publishable=false" >> "$GITHUB_OUTPUT"
          else
            echo "publishable=true" >> "$GITHUB_OUTPUT"
          fi

  build:
    name: Build, test, pack and describe the release
    needs: version
    runs-on: ubuntu-latest
    env:
      VERSION: ${{ needs.version.outputs.version }}
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: "10.0.x"
      - name: Build
        run: dotnet build @@NS@@.slnx -c Release -p:ContinuousIntegrationBuild=true
@@TEST_STEP@@      - name: Pack
        shell: bash
        run: |
          set -euo pipefail
          mkdir -p dist/release
          dotnet pack src/@@NS@@/@@NS@@.fsproj -c Release --no-build -p:ContinuousIntegrationBuild=true -o dist/release
          test -f "dist/release/@@PACKAGE@@.${VERSION}.nupkg"
          (cd dist/release && sha256sum *.nupkg > checksums.txt)
          cat dist/release/checksums.txt

      - name: Generate the Echelon release manifest
        uses: kemiller2002/echelon-registry/actions/release-contract@@@REGISTRY_REF@@
        with:
          release-input: release/echelon.release-input.json
          artifact-dir: dist/release
          output: dist/release/echelon-release.json
          version: ${{ needs.version.outputs.version }}
          tag: v${{ needs.version.outputs.version }}
          commit: ${{ github.sha }}
          release-stage: stable

      - name: Validate the release manifest against the Registry schema
        shell: bash
        run: |
          set -euo pipefail
          python3 -m pip install --quiet --disable-pip-version-check jsonschema
          curl -fsSL "https://raw.githubusercontent.com/kemiller2002/echelon-registry/${ECHELON_REGISTRY_REF}/schemas/release-manifest-v2.schema.json" -o "$RUNNER_TEMP/release-manifest-v2.schema.json"
          python3 - "$RUNNER_TEMP/release-manifest-v2.schema.json" dist/release/echelon-release.json <<'PY'
          import json, sys, hashlib, pathlib
          from jsonschema import Draft202012Validator
          schema = json.load(open(sys.argv[1]))
          manifest = json.load(open(sys.argv[2]))
          Draft202012Validator(schema).validate(manifest)
          for artifact in manifest["artifacts"]:
              digest = hashlib.sha256(pathlib.Path("dist/release", artifact["name"]).read_bytes()).hexdigest()
              assert digest == artifact["sha256"], f"{artifact['name']}: manifest {artifact['sha256']} != file {digest}"
          print(json.dumps(manifest, indent=2))
          PY

      - uses: actions/upload-artifact@v4
        with:
          name: @@SYSTEM@@-release-${{ needs.version.outputs.version }}
          path: dist/release
          if-no-files-found: error

  publish:
    name: Publish to nuget.org and GitHub
    needs: [version, build]
    if: github.event_name != 'pull_request' && needs.version.outputs.publishable == 'true'
    runs-on: ubuntu-latest
    permissions:
      contents: write
      id-token: write
      attestations: write
    env:
      VERSION: ${{ needs.version.outputs.version }}
      GH_TOKEN: ${{ github.token }}
      NUGET_USER: ${{ vars.NUGET_USER }}
    steps:
      - uses: actions/download-artifact@v4
        with:
          name: @@SYSTEM@@-release-${{ needs.version.outputs.version }}
          path: dist/release

      - name: Verify the artifacts against their checksums
        working-directory: dist/release
        run: sha256sum -c checksums.txt

      - name: Decide whether this version still needs publishing
        id: release
        shell: bash
        run: |
          set -euo pipefail
          if gh release view "v${VERSION}" --repo "$GITHUB_REPOSITORY" >/dev/null 2>&1; then
            echo "::warning::v${VERSION} is already published. Released versions are immutable; bump <Version> to publish a new build. Nothing was uploaded."
            echo "publish=false" >> "$GITHUB_OUTPUT"
          else
            if [ -z "${NUGET_USER}" ]; then
              echo "::error::Repository variable NUGET_USER is not set, so Trusted Publishing cannot be established truthfully. Nothing was published."; exit 1
            fi
            echo "publish=true" >> "$GITHUB_OUTPUT"
          fi

      - uses: actions/setup-dotnet@v4
        if: steps.release.outputs.publish == 'true'
        with:
          dotnet-version: "10.0.x"

      - name: NuGet login (OIDC -> short-lived API key)
        if: steps.release.outputs.publish == 'true'
        id: login
        uses: NuGet/login@v1
        with:
          user: ${{ vars.NUGET_USER }}

      - name: Push the package
        if: steps.release.outputs.publish == 'true'
        run: dotnet nuget push "dist/release/@@PACKAGE@@.${VERSION}.nupkg" --api-key "${{ steps.login.outputs.NUGET_API_KEY }}" --source https://api.nuget.org/v3/index.json

      - name: Attest build provenance
        if: steps.release.outputs.publish == 'true'
        uses: actions/attest-build-provenance@4d101475d8b20a2381f78447822ac1eab6504dd8 # v4
        with:
          subject-path: |
            dist/release/*.nupkg
            dist/release/checksums.txt
            dist/release/echelon-release.json

      - name: Create the GitHub release
        if: steps.release.outputs.publish == 'true'
        shell: bash
        run: |
          set -euo pipefail
          gh release create "v${VERSION}" --repo "$GITHUB_REPOSITORY" --target "$GITHUB_SHA" --title "@@NAME@@ v${VERSION}" \
            --notes "@@PACKAGE@@ ${VERSION} built from ${GITHUB_SHA}: the NuGet package, its SHA-256 checksums and the Echelon release manifest, each with a GitHub build-provenance attestation." \
            dist/release/*.nupkg dist/release/checksums.txt dist/release/echelon-release.json
"""

    /// Build and pack outputs never belong in the repository. The lifecycle
    /// components own the rest of .gitignore, so the scaffold adds only its
    /// own bounded region.
    let private libraryIgnores =
        "# .NET build, test and pack outputs (Conditor fsharp-nuget-library scaffold)\nbin/\nobj/\ndist/\nTestResults/\n*.nupkg\n"

    let private libraryFiles (projectName: string) (manifest: ProjectManifest) =
        let npmBindings =
            resolvedBindings manifest
            |> List.choose (fun (binding, package, _) ->
                match binding with
                | NpmDependency -> Some package
                | NugetReference -> None)

        match npmBindings with
        | _ :: _ ->
            let packages = String.Join(", ", npmBindings)
            Error
                [ $"Scaffold 'fsharp-nuget-library' binds NuGet packages only; it has no target for npm packages ({packages}). Use 'fsharp-limen-web' for a browser application." ]
        | [] ->
            let ns = identifier projectName
            let package = libraryPackageId projectName
            let system = packageSlug projectName

            let tokens =
                [ "@@NS@@", ns
                  "@@PACKAGE@@", package
                  "@@SYSTEM@@", system
                  "@@NAME@@", projectName
                  "@@VERSION@@", UnreleasedVersion
                  "@@UNRELEASED@@", UnreleasedVersion
                  "@@PRAXIS_REF@@", PraxisFoundationsRef
                  "@@REGISTRY_REF@@", RegistryReleaseContractRef ]

            let render = fill (("@@TEST_STEP@@", libraryTestStep) :: tokens)

            Ok(
                [ "Directory.Build.props", render libraryProps
                  $"{ns}.slnx", render librarySolution
                  $"src/{ns}/{ns}.fsproj", render (libraryProject manifest)
                  $"src/{ns}/Library.fs", render librarySource
                  $"tests/{ns}.Tests/{ns}.Tests.fsproj", render libraryTestProject
                  $"tests/{ns}.Tests/LibraryTests.fs", render libraryTests
                  ".echelon/foundations.json", foundationManifest false projectName manifest
                  ".github/workflows/build-and-test.yml", render libraryBuildWorkflow
                  ".github/workflows/echelon-foundations.yml", render foundationsWorkflow
                  ".github/workflows/release.yml", render libraryReleaseWorkflow
                  "release/echelon.release-input.json", render libraryReleaseInput
                  ".gitignore", libraryIgnores ]
                @ ordoBaselineFiles (libraryBaselineLocations ns) manifest
            )

    // ---------------------------------------------------------------------
    // fsharp-limen-web: a new browser application repository.
    //
    // The npm project lives at the repository root, as in Signal: the Praxis
    // foundations verifier reads the root package.json, and the pages under
    // src/kernel reach node_modules through relative paths. Beyond the
    // lifecycle components the scaffold establishes an engine test project,
    // a real-browser smoke suite, a build-and-test workflow (with the cached,
    // hang-proof Chromium install from signal#24), the foundations check, a
    // GitHub Pages deployment that stays inert until a target is chosen, and
    // the branch protection those checks back.
    // ---------------------------------------------------------------------

    /// The check runs a protected main requires: the job each scaffold
    /// workflow defines (build-and-test.yml; the reusable foundations
    /// workflow's job as echelon-foundations.yml calls it) and `validate`,
    /// the job of the praxis-validation.yml workflow `praxis init` installs.
    let webRequiredChecks =
        [ "build-and-test"; "foundations / verify-foundations"; "validate" ]

    /// A pull request merges into main only when the engine builds, its tests
    /// pass, the pages work in a browser, the foundations verify and Praxis
    /// validates. Administrators are bound too, so an agent working with the
    /// owner's credentials cannot merge around CI. No human review is needed.
    let webBranchProtection =
        { Branch = "main"
          RequiredChecks = webRequiredChecks
          StrictStatusChecks = false
          EnforceAdmins = true
          RequirePullRequest = true
          RequiredApprovingReviewCount = 0
          AllowForcePushes = false
          AllowDeletions = false }

    let private webSolution =
        """<Solution>
  <Folder Name="/src/">
    <Project Path="src/engine/App.Engine.fsproj" />
  </Folder>
  <Folder Name="/tests/">
    <Project Path="tests/App.Engine.Tests/App.Engine.Tests.fsproj" />
  </Folder>
</Solution>
"""

    let private webTestProject =
        """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <IsPackable>false</IsPackable>
    <IsTestProject>true</IsTestProject>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="../../src/engine/App.Engine.fsproj" />
  </ItemGroup>
  <ItemGroup>
    <Compile Include="EngineTests.fs" />
    <Compile Include="RouteTests.fs" />
  </ItemGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.11.1" />
    <PackageReference Include="xunit" Version="2.9.2" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2" />
  </ItemGroup>
</Project>
"""

    let private webEngineTests =
        """module App.Engine.Tests.EngineTests

open Xunit
open @@NS@@.Engine

[<Fact>]
let ``the scaffold engine starts uninitialized`` () =
    Assert.Equal(State.Uninitialized, State.initial)

[<Fact>]
let ``the Aegis configuration validates at startup`` () =
    Assert.True(Result.isOk (Operational.validateConfiguration ()))
"""

    let private webTsconfig =
        "{\n  \"compilerOptions\": {\n    \"target\": \"ES2022\",\n    \"module\": \"ES2022\",\n    \"moduleResolution\": \"Bundler\",\n    \"strict\": true,\n    \"noEmit\": true,\n    \"lib\": [\"ES2022\", \"DOM\"]\n  },\n  \"include\": [\"src/kernel/**/*.ts\"]\n}\n"

    /// Limen's user-owned boundary configuration. `limen init` adopts an
    /// existing file (`preserved-existing`) and never overwrites it.
    let private webLimenConfig =
        "{\n  \"configurationVersion\": 1,\n  \"boundary\": {\n    \"engine\": [\n      \"src/engine\"\n    ],\n    \"kernel\": [\n      \"src/kernel\"\n    ]\n  }\n}\n"

    let private webIndexPage =
        "<!doctype html>\n<html lang=\"en\">\n<head>\n  <meta charset=\"utf-8\">\n  <meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">\n  <link rel=\"stylesheet\" href=\"../../node_modules/@echelon-foundry/design-system/dist/all.css\">\n  <title>Application</title>\n</head>\n<body>\n  <main>\n    <ef-button><button type=\"button\">Ready</button></ef-button>\n  </main>\n</body>\n</html>\n"

    let private webPrintPage =
        "<!doctype html>\n<html lang=\"en\">\n<head>\n  <meta charset=\"utf-8\">\n  <link rel=\"stylesheet\" href=\"../../node_modules/@echelon-foundry/print-components/src/styles/print.css\">\n  <script type=\"module\" src=\"../../node_modules/@echelon-foundry/print-components/src/components/register.js\"></script>\n  <title>Printable document</title>\n</head>\n<body>\n  <ef-print-document><main><h1>Printable document</h1></main></ef-print-document>\n</body>\n</html>\n"

    let private webPlaywrightConfig =
        """// Real-browser checks for the application pages.
//
// The .NET tests prove the engine decides correctly; they cannot prove a page
// loads. These tests serve the repository root (the pages reach into
// node_modules/ for Forma and Folio) and drive the pages in Chromium.
import { existsSync } from "node:fs";
import { defineConfig, devices } from "@playwright/test";

const port = 4321;
const origin = `http://127.0.0.1:${port}`;

// Some environments ship a Chromium that Playwright did not download itself.
// Where that binary exists it is used as-is; everywhere else Playwright
// resolves its own, so CI needs no special case.
const preinstalledChromium = "/opt/pw-browsers/chromium";
const launchOptions = existsSync(preinstalledChromium) ? { executablePath: preinstalledChromium } : {};

export default defineConfig({
  testDir: "./tests/browser",
  fullyParallel: false,
  workers: 1,
  forbidOnly: !!process.env.CI,
  retries: 0,
  timeout: 60_000,
  reporter: process.env.CI ? [["github"], ["list"]] : [["list"]],
  use: { baseURL: origin, trace: "retain-on-failure" },
  projects: [{ name: "chromium", use: { ...devices["Desktop Chrome"], launchOptions } }],
  webServer: {
    command: `python3 -m http.server ${port} --bind 127.0.0.1`,
    url: `${origin}/src/kernel/index.html`,
    reuseExistingServer: !process.env.CI,
    timeout: 60_000
  }
});
"""

    let private webSmokeSpec =
        """// Scaffold smoke suite: every page loads every resource it references and
// raises no script error. Extend it with the application's real journeys.
import { expect, test } from "@playwright/test";

const pages = ["src/kernel/index.html", "src/kernel/print.html"];

for (const path of pages) {
  test(`${path} loads every resource it references`, async ({ page }) => {
    const failures = [];
    page.on("response", (response) => {
      if (response.status() >= 400) failures.push(`${response.status()} ${response.url()}`);
    });
    page.on("requestfailed", (request) => failures.push(`failed ${request.url()}`));
    page.on("pageerror", (error) => failures.push(`script error: ${error.message}`));

    await page.goto(`/${path}`);
    await page.waitForLoadState("networkidle");

    expect(failures).toEqual([]);
  });
}

test("the application page renders its first control", async ({ page }) => {
  await page.goto("/src/kernel/index.html");
  await expect(page.getByRole("button", { name: "Ready" })).toBeVisible();
});
"""

    /// Installs from the lockfile when one is committed. A fresh scaffold has
    /// none until the first `npm install`; the step then says so loudly.
    let private npmInstallStep =
        """      - name: Install npm dependencies
        shell: bash
        run: |
          if [ -f package-lock.json ]; then
            npm ci
          else
            echo "::warning::package-lock.json is not committed; installing without a lockfile. Commit it so every run installs the same tree."
            npm install --no-audit --no-fund
          fi
"""

    let private webBuildWorkflow =
        """name: Build and test

# Every pull request and every push to main builds the F# engine and runs its
# tests, type-checks the browser kernel, and drives the pages in a real
# browser. Branch protection requires this job (.github/branch-protection.json).

on:
  pull_request:
  push:
    branches: [main]

permissions:
  contents: read

concurrency:
  group: ${{ github.workflow }}-${{ github.ref }}
  cancel-in-progress: true

jobs:
  build-and-test:
    runs-on: ubuntu-latest
    # A normal run takes a few minutes. Anything that stalls fails here
    # instead of holding a runner for the six-hour default.
    timeout-minutes: 20
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: "10.0.x"
      - uses: actions/setup-node@v4
        with:
          node-version: "22"

      # Warnings are errors (Directory.Build.props), so this also gates
      # warning regressions.
      - name: Build
        run: dotnet build App.slnx -c Release
@@TEST_STEP@@@@NPM_INSTALL@@
      - name: Type-check the browser kernel
        run: npm run check

      # Chromium is never installed with `--with-deps`: that runs `sudo
      # apt-get update` on every run, and apt has no overall deadline, so an
      # unanswered mirror hung browser suites until the six-hour job limit
      # (signal#21, signal#22, summa#21; fixed in signal#24). The browser is
      # cached under the installed Playwright version (which fixes the
      # browser build), and the OS libraries are proved present offline.
      - name: Resolve the installed Playwright version
        id: playwright
        run: echo "version=$(node -p "require('./node_modules/playwright-core/package.json').version")" >> "$GITHUB_OUTPUT"

      # A stalled cache download falls back to a fresh browser download
      # after two minutes instead of the default ten.
      - name: Restore the Chromium build for that version
        id: chromium-cache
        uses: actions/cache@v4
        env:
          SEGMENT_DOWNLOAD_TIMEOUT_MINS: "2"
        with:
          path: ~/.cache/ms-playwright
          key: playwright-chromium-${{ runner.os }}-${{ runner.arch }}-${{ steps.playwright.outputs.version }}

      - name: Download Chromium for the browser suite
        if: steps.chromium-cache.outputs.cache-hit != 'true'
        timeout-minutes: 5
        run: npx playwright install chromium

      # `ldd` proves offline that every shared library the Chromium binaries
      # link against is on the runner. Only when a future runner image drops
      # one does apt run at all, with per-request timeouts, retries and an
      # overall deadline, and the result is checked again. A missing library
      # fails the step by name; it can no longer hang.
      - name: Verify Chromium's OS libraries
        timeout-minutes: 8
        run: |
          missing_libraries() {
            find ~/.cache/ms-playwright -type f \( -name chrome -o -name chrome-headless-shell \) -print0 \
              | xargs -0 --no-run-if-empty ldd \
              | awk '/not found/ { print $1 }' \
              | sort -u
          }

          missing="$(missing_libraries)"
          if [ -n "$missing" ]; then
            echo "::warning::Runner image lacks Chromium libraries: $(echo $missing). Installing Chromium's OS dependencies."
            printf '%s\n' 'Acquire::Retries "3";' 'Acquire::http::Timeout "30";' 'Acquire::https::Timeout "30";' \
              | sudo tee /etc/apt/apt.conf.d/99-bounded-network > /dev/null
            timeout 300 npx playwright install-deps chromium \
              || echo "::error::Installing Chromium's OS dependencies failed or took longer than five minutes."
          fi

          still_missing="$(missing_libraries)"
          if [ -n "$still_missing" ]; then
            echo "::error::Chromium cannot start; missing shared libraries: $(echo $still_missing)"
            exit 1
          fi
          echo "Every shared library Chromium links against is present."

      - name: Verify the pages in a real browser
        run: npx playwright test

      - name: Upload browser traces on failure
        if: failure()
        uses: actions/upload-artifact@v4
        with:
          name: playwright-traces
          path: test-results/
          retention-days: 7
"""

    let private webDeployWorkflow =
        """name: Deploy to GitHub Pages

# The deployment target is not decided yet (Indy-init RQR-005; D-203 prefers
# GitHub Pages "where practical"). This workflow is wired but inert: it runs
# only when the repository variable DEPLOY_TARGET is `github-pages` and Pages
# is enabled with source "GitHub Actions". It deploys what `npm run build`
# writes to dist/. See DEPLOYMENT.md.

on:
  push:
    branches: [main]
  workflow_dispatch: {}

permissions:
  contents: read

concurrency:
  group: pages
  cancel-in-progress: false

jobs:
  build:
    if: vars.DEPLOY_TARGET == 'github-pages'
    runs-on: ubuntu-latest
    timeout-minutes: 15
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-node@v4
        with:
          node-version: "22"
@@NPM_INSTALL@@
      - name: Build the site
        run: npm run build

      - name: Require the site bundle
        run: |
          if [ ! -f dist/index.html ]; then
            echo "::error::npm run build must write the deployable site, including dist/index.html. See DEPLOYMENT.md."
            exit 1
          fi

      - uses: actions/upload-pages-artifact@v3
        with:
          path: dist

  deploy:
    needs: build
    runs-on: ubuntu-latest
    timeout-minutes: 10
    permissions:
      pages: write
      id-token: write
    environment:
      name: github-pages
      url: ${{ steps.deployment.outputs.page_url }}
    steps:
      - id: deployment
        uses: actions/deploy-pages@v4
"""

    let private webDeploymentGuide =
        """# Deployment

Conditor scaffolded a GitHub Pages deployment, `.github/workflows/deploy-pages.yml`.
It stays inert until a deployment target is chosen.

## Status

The target is not decided. The Indy-init planning set prefers GitHub Pages
"where practical" (D-203) and leaves the decision open (RQR-005). A static
Pages site cannot hold secrets: an AI provider key or an OAuth token exchange
needs a backend, which this workflow does not provide.

## Contract

- `npm run build` writes the complete deployable site to `dist/`, including
  `dist/index.html`. The scaffold defines no `build` script; add one with the
  first bundling decision.
- Every push to `main` deploys `dist/` once the workflow is enabled.

## Enable GitHub Pages

1. Settings, Pages: set the source to "GitHub Actions"
   (`gh api -X POST repos/OWNER/REPO/pages -f build_type=workflow`).
2. Set the repository variable: `gh variable set DEPLOY_TARGET --body github-pages`.
3. Push to `main`, or run the workflow from the Actions tab.

To choose another target, replace the workflow and update this file in the
same change.
"""

    /// Claude Code project settings for an unattended agent. In the `auto`
    /// permission mode the classifier still refuses destructive actions;
    /// these rules pre-approve the repository's own toolchain and pull-request
    /// flow, and let the agent merge its own pull requests once the required
    /// checks are green (the pattern the Praxis repository uses). Force pushes
    /// and repository deletion stay denied.
    let private webClaudeSettings =
        """{
  "permissions": {
    "allow": [
      "Bash(git *)",
      "Bash(gh pr *)",
      "Bash(gh run *)",
      "Bash(gh api *)",
      "Bash(dotnet *)",
      "Bash(npm *)",
      "Bash(npx *)",
      "Bash(node *)",
      "Bash(./praxis *)",
      "Bash(praxis *)",
      "Bash(ordo *)",
      "Bash(percepta-repo *)",
      "Bash(conditor *)"
    ],
    "deny": [
      "Bash(git push --force *)",
      "Bash(git push -f *)",
      "Bash(git push --force-with-lease *)",
      "Bash(gh repo delete *)",
      "Bash(gh api --method DELETE *)",
      "Bash(gh api -X DELETE *)"
    ]
  },
  "autoMode": {
    "allow": [
      "$defaults",
      "Opening pull requests in this repository, pushing their branches, merging the base branch into them, and merging them without separate human review once every required check is green and the branch is conflict-free."
    ]
  }
}
"""

    let private webIgnores =
        "# Build, test, package and browser outputs (Conditor fsharp-limen-web scaffold)\nbin/\nobj/\ndist/\nTestResults/\nnode_modules/\ntest-results/\nplaywright-report/\n"

    let private webFiles (projectName: string) (manifest: ProjectManifest) (limen: string) =
        let ns = identifier projectName

        let render =
            fill
                [ "@@TEST_STEP@@", dotnetTestStep "App.slnx"
                  "@@NPM_INSTALL@@", npmInstallStep
                  "@@PRAXIS_REF@@", PraxisFoundationsRef
                  "@@NS@@", ns ]

        [ "Directory.Build.props",
          "<Project>\n  <PropertyGroup>\n    <TargetFramework>net10.0</TargetFramework>\n    <LangVersion>latest</LangVersion>\n    <Nullable>enable</Nullable>\n    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>\n    <Deterministic>true</Deterministic>\n  </PropertyGroup>\n</Project>\n"
          "App.slnx", webSolution
          "src/engine/App.Engine.fsproj", projectFile projectName manifest
          "src/engine/Operational.fs", operationalFile projectName
          "src/engine/Routes.fs", render webRoutesSource
          "src/engine/Domain.fs",
          $"namespace {ns}.Engine\n\ntype State =\n    | Uninitialized\n\nmodule State =\n    let initial = Uninitialized\n"
          "tests/App.Engine.Tests/App.Engine.Tests.fsproj", webTestProject
          "tests/App.Engine.Tests/EngineTests.fs", render webEngineTests
          "tests/App.Engine.Tests/RouteTests.fs", render webRouteTests
          ".echelon/foundations.json", foundationManifest true projectName manifest
          "aegis-boundaries.json", aegisBoundaryManifest projectName
          RouteInventoryPath, webRouteInventory
          "requirements/URL-ADDRESSABLE-STATE.md", webUrlRequirements
          "package.json", packageJson projectName manifest
          "tsconfig.json", webTsconfig
          "limen.config.json", webLimenConfig
          "src/kernel/bootstrap.ts", kernelBootstrap limen
          "src/kernel/index.html", webIndexPage
          "src/kernel/print.html", webPrintPage
          "playwright.config.js", webPlaywrightConfig
          "tests/browser/smoke.spec.js", webSmokeSpec
          ".github/workflows/build-and-test.yml", render webBuildWorkflow
          ".github/workflows/echelon-foundations.yml", render foundationsWorkflow
          ".github/workflows/deploy-pages.yml", render webDeployWorkflow
          BranchProtection.RelativePath, BranchProtection.render webBranchProtection
          "DEPLOYMENT.md", webDeploymentGuide
          ".claude/settings.json", webClaudeSettings
          ".gitignore", webIgnores ]
        @ ordoBaselineFiles webBaselineLocations manifest

    let private desiredFiles (manifest: ProjectManifest) (scaffold: ScaffoldRequest) =
        let projectName =
            scaffold.Name
            |> Option.filter (String.IsNullOrWhiteSpace >> not)
            |> Option.defaultValue manifest.Name

        match scaffold.Kind, limenPackage manifest with
        | "fsharp-limen-web", None ->
            Error [ "Scaffold 'fsharp-limen-web' requires an embedded Limen descriptor to name the kernel's protocol package." ]
        | "fsharp-limen-web", Some limen ->
            let files = webFiles projectName manifest limen

            match agentEntryFile manifest with
            | Some agentFile -> Ok(agentFile :: files)
            | None -> Ok files
        | "fsharp-nuget-library", _ ->
            libraryFiles projectName manifest
            |> Result.map (fun files ->
                match agentEntryFile manifest with
                | Some agentFile -> agentFile :: files
                | None -> files)
        | unknown, _ ->
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

    /// The scaffold the Conditor lock records as established, if any.
    let establishedScaffold (target: string) : ScaffoldRequest option =
        let path = Path.Combine(target, ".conditor", "lock.json")

        if not (File.Exists path) then
            None
        else
            try
                let root = JsonNode.Parse(File.ReadAllText path)

                match root with
                | null -> None
                | node ->
                    match node["manifest"] with
                    | null -> None
                    | manifest ->
                        match manifest["scaffold"] with
                        | :? JsonObject as scaffold ->
                            let text (name: string) =
                                match scaffold[name] with
                                | :? JsonValue as value ->
                                    match value.TryGetValue<string>() with
                                    | true, found -> Option.ofObj found
                                    | _ -> None
                                | _ -> None

                            text "kind" |> Option.map (fun kind -> { Kind = kind; Name = text "name" })
                        | _ -> None
            with _ ->
                None

    /// The scaffold's files to write. A scaffold is a seed: once the lock
    /// records it as established for this manifest, its files belong to the
    /// project, which may grow, rename or remove them. Conditor then neither
    /// recreates nor compares them, and keeps ensuring only its bounded
    /// managed regions. Before that, an existing file with other content is
    /// refused rather than overwritten.
    let plan target (manifest: ProjectManifest) =
        match manifest.Scaffold with
        | None -> Ok []
        | Some scaffold ->
            match desiredFiles manifest scaffold with
            | Error errors -> Error errors
            | Ok desired ->
                let errors = ResizeArray<string>()
                let changes = ResizeArray<string * string>()
                let established = establishedScaffold target = Some scaffold

                for relativePath, content in desired do
                    match safeFullPath target relativePath with
                    | None ->
                        errors.Add $"Scaffold path escapes the target repository: {relativePath}"
                    | Some fullPath when relativePath = "AGENTS.md" || relativePath = "context/CURRENT-STATE.md" || relativePath = ".gitignore" ->
                        // These are shared integration surfaces. The installer owns only
                        // bounded Conditor regions and resolves current file contents at execution time.
                        changes.Add(relativePath, content)
                    | Some _ when established -> ()
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
