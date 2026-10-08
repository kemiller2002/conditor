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
            | Some "0.3.0" -> node["releaseArtifact"] <- JsonValue.Create Folio030Release
            | _ -> ())

        addCapability "limen" ignore
        addCapability "ordo" ignore
        addCapability "percepta" ignore
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

    let private libraryTestProject =
        """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="../../src/@@NS@@/@@NS@@.fsproj" />
  </ItemGroup>
  <ItemGroup>
    <Compile Include="Program.fs" />
  </ItemGroup>
</Project>
"""

    let private libraryTests =
        """// Dependency-free test runner: every check prints PASS/FAIL and the
// process exits non-zero when any check failed, so CI cannot read a skipped
// or empty run as success.
open System

let mutable failures = 0

let check name condition =
    if condition then
        Console.WriteLine $"PASS {name}"
    else
        failures <- failures + 1
        Console.Error.WriteLine $"FAIL {name}"

check "scaffold builds and links the library" @@NS@@.Library.scaffoldReady

[<EntryPoint>]
let main _ =
    if failures = 0 then 0
    else
        Console.Error.WriteLine $"{failures} check(s) failed."
        1
"""

    let private libraryBuildWorkflow =
        """name: Build and test

# Build the library and run its tests on every pull request and on main.
# The test project is a plain executable that exits non-zero on any failed
# check, so an empty or skipped run cannot pass.

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
      - name: Test
        run: dotnet run --project tests/@@NS@@.Tests/@@NS@@.Tests.fsproj -c Release --no-build
      - name: Pack
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
      - name: Test
        run: dotnet run --project tests/@@NS@@.Tests/@@NS@@.Tests.fsproj -c Release --no-build
      - name: Pack
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

            let render = fill tokens

            Ok(
                [ "Directory.Build.props", render libraryProps
                  $"{ns}.slnx", render librarySolution
                  $"src/{ns}/{ns}.fsproj", render (libraryProject manifest)
                  $"src/{ns}/Library.fs", render librarySource
                  $"tests/{ns}.Tests/{ns}.Tests.fsproj", render libraryTestProject
                  $"tests/{ns}.Tests/Program.fs", render libraryTests
                  ".echelon/foundations.json", foundationManifest projectName manifest
                  ".github/workflows/build-and-test.yml", render libraryBuildWorkflow
                  ".github/workflows/echelon-foundations.yml", render foundationsWorkflow
                  ".github/workflows/release.yml", render libraryReleaseWorkflow
                  "release/echelon.release-input.json", render libraryReleaseInput ]
                @ ordoBaselineFiles (libraryBaselineLocations ns) manifest
            )

    let private desiredFiles (manifest: ProjectManifest) (scaffold: ScaffoldRequest) =
        let projectName =
            scaffold.Name
            |> Option.filter (String.IsNullOrWhiteSpace >> not)
            |> Option.defaultValue manifest.Name

        let ns = identifier projectName

        match scaffold.Kind, limenPackage manifest with
        | "fsharp-limen-web", None ->
            Error [ "Scaffold 'fsharp-limen-web' requires an embedded Limen descriptor to name the kernel's protocol package." ]
        | "fsharp-limen-web", Some limen ->
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
                  "src/kernel/bootstrap.ts", kernelBootstrap limen
                  "src/kernel/index.html",
                  "<!doctype html>\n<html lang=\"en\">\n<head>\n  <meta charset=\"utf-8\">\n  <meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">\n  <link rel=\"stylesheet\" href=\"./node_modules/@echelon-foundry/design-system/dist/all.css\">\n  <title>Application</title>\n</head>\n<body>\n  <main>\n    <ef-button><button type=\"button\">Ready</button></ef-button>\n  </main>\n</body>\n</html>\n"
                  "src/kernel/print.html",
                  "<!doctype html>\n<html lang=\"en\">\n<head>\n  <meta charset=\"utf-8\">\n  <link rel=\"stylesheet\" href=\"./node_modules/@echelon-foundry/print-components/src/styles/print.css\">\n  <script type=\"module\" src=\"./node_modules/@echelon-foundry/print-components/src/components/register.js\"></script>\n  <title>Printable document</title>\n</head>\n<body>\n  <ef-print-document><main><h1>Printable document</h1></main></ef-print-document>\n</body>\n</html>\n" ]

            let filesWithOrdoBaseline = files @ ordoBaselineFiles webBaselineLocations manifest

            match agentEntryFile manifest with
            | Some agentFile -> Ok(agentFile :: filesWithOrdoBaseline)
            | None -> Ok filesWithOrdoBaseline
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
