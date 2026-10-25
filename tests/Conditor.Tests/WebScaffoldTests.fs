module WebScaffoldTests

open System
open System.IO
open System.Text.Json
open Conditor.Core

// The fsharp-limen-web scaffold establishes what a new browser application
// repository needs before an agent can work against CI: a buildable engine
// and its tests, a real-browser smoke suite, the build-and-test and
// foundations workflows, an inert Pages deployment, and the branch protection
// those checks back.

let private withTarget action =
    let target = Path.Combine(Path.GetTempPath(), $"conditor-web-{Guid.NewGuid():N}")
    Directory.CreateDirectory target |> ignore

    try
        action target
    finally
        if Directory.Exists target then
            Directory.Delete(target, true)

let private planFor target (json: string) =
    let path = Path.Combine(Path.GetTempPath(), $"conditor-web-{Guid.NewGuid():N}.json")

    try
        File.WriteAllText(path, json)

        Manifest.load path
        |> Result.bind (Planner.create target Init)
    finally
        if File.Exists path then
            File.Delete path

let private scaffoldFiles (plan: InstallationPlan) =
    plan.Actions
    |> List.choose (fun action ->
        match action.Kind, action.Execution with
        | ScaffoldFile, EnsureFile(path, content) -> Some(path, content)
        | ScaffoldFile, EnsureManagedRegion(path, _, content) -> Some(path, content)
        | _ -> None)
    |> Map.ofList

let private webManifest =
    """{"schemaVersion":1,"name":"Indy Web","components":[{"id":"praxis","version":"3.7.2"},{"id":"ordo","version":"1.5.0"},{"id":"percepta","version":"0.1.0"},{"id":"aegis","version":"1.0.0"},{"id":"limen","version":"0.7.1"},{"id":"forma","version":"0.5.0"},{"id":"folio","version":"0.3.0"}],"requirements":[],"execution":{"enabled":false},"scaffold":{"kind":"fsharp-limen-web","name":"Indy Web"}}"""

let private joined (errors: string list) = String.concat "; " errors

let private expectedPaths =
    [ "Directory.Build.props"
      "App.slnx"
      "src/engine/App.Engine.fsproj"
      "src/engine/Operational.fs"
      "src/engine/Routes.fs"
      "src/engine/Domain.fs"
      "tests/App.Engine.Tests/App.Engine.Tests.fsproj"
      "tests/App.Engine.Tests/EngineTests.fs"
      "tests/App.Engine.Tests/RouteTests.fs"
      ".echelon/foundations.json"
      "aegis-boundaries.json"
      ".echelon/routes.json"
      "requirements/URL-ADDRESSABLE-STATE.md"
      "package.json"
      "tsconfig.json"
      "limen.config.json"
      "src/kernel/bootstrap.ts"
      "src/kernel/index.html"
      "src/kernel/print.html"
      "playwright.config.js"
      "tests/browser/smoke.spec.js"
      ".github/workflows/build-and-test.yml"
      ".github/workflows/echelon-foundations.yml"
      ".github/workflows/deploy-pages.yml"
      ".github/branch-protection.json"
      "DEPLOYMENT.md"
      ".claude/settings.json"
      "SDE-MAP.md"
      "context/CURRENT-STATE.md"
      ".gitignore" ]

let private contains (needle: string) (files: Map<string, string>) path =
    files |> Map.tryFind path |> Option.exists (fun text -> text.Contains(needle, StringComparison.Ordinal))

let run (check: string -> bool -> unit) =
    withTarget (fun target ->
        match planFor target webManifest with
        | Error errors -> check $"web scaffold plans: {joined errors}" false
        | Ok plan ->
            let files = scaffoldFiles plan
            let has needle path = contains needle files path
            let build = ".github/workflows/build-and-test.yml"
            let deploy = ".github/workflows/deploy-pages.yml"

            check "web scaffold emits exactly the application repository surface"
                (Set.ofList expectedPaths = (files |> Map.keys |> Set.ofSeq))

            check "no unrendered template tokens remain"
                (files |> Map.forall (fun _ text -> not (text.Contains("@@", StringComparison.Ordinal))))

            // Engine and its tests.
            check "the solution builds the engine and its tests"
                (has "src/engine/App.Engine.fsproj" "App.slnx"
                 && has "tests/App.Engine.Tests/App.Engine.Tests.fsproj" "App.slnx")

            check "the engine test project is a dotnet test (xUnit) project"
                (has "<IsTestProject>true</IsTestProject>" "tests/App.Engine.Tests/App.Engine.Tests.fsproj"
                 && has "Include=\"Microsoft.NET.Test.Sdk\" Version=\"17.11.1\"" "tests/App.Engine.Tests/App.Engine.Tests.fsproj"
                 && has "Include=\"xunit\" Version=\"2.9.2\"" "tests/App.Engine.Tests/App.Engine.Tests.fsproj"
                 && has "../../src/engine/App.Engine.fsproj" "tests/App.Engine.Tests/App.Engine.Tests.fsproj")

            check "the engine tests exercise the scaffold engine and its Aegis configuration"
                (has "open IndyWeb.Engine" "tests/App.Engine.Tests/EngineTests.fs"
                 && has "State.initial" "tests/App.Engine.Tests/EngineTests.fs"
                 && has "Operational.validateConfiguration ()" "tests/App.Engine.Tests/EngineTests.fs")

            // The npm project sits at the root, where the foundations verifier reads it.
            match files |> Map.tryFind "package.json" with
            | None -> check "package.json is generated at the repository root" false
            | Some text ->
                use document = JsonDocument.Parse text
                let root = document.RootElement
                let dependencies = root.GetProperty "dependencies"
                let devDependencies = root.GetProperty "devDependencies"

                check "the root package binds Limen, Forma and Folio"
                    (dependencies.GetProperty("@echelon-foundry/limen").GetString() = "0.7.1"
                     && dependencies.GetProperty("@echelon-foundry/design-system").GetString() = "0.5.0"
                     && (dependencies.GetProperty("@echelon-foundry/print-components").GetString()
                         |> Option.ofObj
                         |> Option.exists (fun spec -> spec.Contains("/v0.3.0/", StringComparison.Ordinal))))

                check "the browser runner is pinned exactly"
                    (devDependencies.GetProperty("@playwright/test").GetString() = Scaffolding.PlaywrightVersion
                     && devDependencies.GetProperty("typescript").GetString() = "5.9.3")

                check "the package scripts type-check and run the browser suite"
                    (root.GetProperty("scripts").GetProperty("check").GetString() = "tsc --noEmit"
                     && root.GetProperty("scripts").GetProperty("test:browser").GetString() = "playwright test")

            check "the pages reach node_modules at the repository root"
                (has "../../node_modules/@echelon-foundry/design-system/dist/all.css" "src/kernel/index.html"
                 && has "../../node_modules/@echelon-foundry/print-components/src/styles/print.css" "src/kernel/print.html"
                 && has "../../node_modules/@echelon-foundry/print-components/src/components/register.js" "src/kernel/print.html")

            check "the kernel imports the Limen protocol under its 0.7 package identity"
                (has "\"@echelon-foundry/limen/protocol\"" "src/kernel/bootstrap.ts")

            check "tsconfig type-checks the kernel sources" (has "\"include\": [\"src/kernel/**/*.ts\"]" "tsconfig.json")

            match files |> Map.tryFind "limen.config.json" with
            | None -> check "the Limen boundary is configured" false
            | Some text ->
                use document = JsonDocument.Parse text
                let boundary = document.RootElement.GetProperty "boundary"
                let paths name = boundary.GetProperty(name: string).EnumerateArray() |> Seq.map _.GetString() |> List.ofSeq

                check "the Limen boundary separates the engine from the kernel"
                    (document.RootElement.GetProperty("configurationVersion").GetInt32() = 1
                     && paths "engine" = [ "src/engine" ]
                     && paths "kernel" = [ "src/kernel" ])

            // Browser suite.
            check "the browser suite serves the repository root and loads both pages"
                (has "python3 -m http.server" "playwright.config.js"
                 && has "testDir: \"./tests/browser\"" "playwright.config.js"
                 && has "src/kernel/index.html" "tests/browser/smoke.spec.js"
                 && has "src/kernel/print.html" "tests/browser/smoke.spec.js"
                 && has "response.status() >= 400" "tests/browser/smoke.spec.js"
                 && has "pageerror" "tests/browser/smoke.spec.js")

            // Build-and-test workflow.
            check "build-and-test builds the engine and runs its tests"
                (has "dotnet build App.slnx -c Release" build && has "dotnet test App.slnx -c Release --no-build" build)

            check "the test step fails an empty or skipped run"
                (has "Passed:[[:space:]]*[1-9]" build && has "Skipped:[[:space:]]*[1-9]" build)

            check "build-and-test type-checks the kernel and drives the pages in a browser"
                (has "run: npm run check" build && has "run: npx playwright test" build)

            check "npm installs from the committed lockfile when there is one"
                (has "if [ -f package-lock.json ]; then\n            npm ci" build)

            check "Chromium is never installed with --with-deps (the apt hang)"
                (not (has "install --with-deps" build))

            check "Chromium is cached under the installed Playwright version"
                (has "node_modules/playwright-core/package.json" build
                 && has "uses: actions/cache@v4" build
                 && has "path: ~/.cache/ms-playwright" build
                 && has "key: playwright-chromium-${{ runner.os }}-${{ runner.arch }}-${{ steps.playwright.outputs.version }}" build
                 && has "SEGMENT_DOWNLOAD_TIMEOUT_MINS: \"2\"" build)

            check "Chromium downloads only on a cache miss, within a deadline"
                (has "if: steps.chromium-cache.outputs.cache-hit != 'true'" build
                 && has "timeout-minutes: 5\n        run: npx playwright install chromium" build)

            check "OS libraries are proved offline and apt runs only with a deadline"
                (has "xargs -0 --no-run-if-empty ldd" build
                 && has "timeout 300 npx playwright install-deps chromium" build
                 && has "Acquire::http::Timeout" build)

            check "the build job has a ceiling far below the six-hour default" (has "timeout-minutes: 20" build)

            check "foundations CI pins the Praxis verifier commit"
                (has $"foundations-verify.yml@{Scaffolding.PraxisFoundationsRef}" ".github/workflows/echelon-foundations.yml"
                 && has $"praxis_ref: {Scaffolding.PraxisFoundationsRef}" ".github/workflows/echelon-foundations.yml")

            // Deployment placeholder.
            check "the Pages deployment is inert until a target is chosen"
                (has "if: vars.DEPLOY_TARGET == 'github-pages'" deploy
                 && has "npm run build" deploy
                 && has "dist/index.html" deploy
                 && has "uses: actions/upload-pages-artifact@v3" deploy
                 && has "uses: actions/deploy-pages@v4" deploy)

            check "only the deploy job may write Pages"
                (has "permissions:\n  contents: read" deploy && has "    permissions:\n      pages: write\n      id-token: write" deploy)

            check "the deployment guide states the open decision and how to enable it"
                (has "RQR-005" "DEPLOYMENT.md" && has "gh variable set DEPLOY_TARGET --body github-pages" "DEPLOYMENT.md")

            // Branch protection.
            match files |> Map.tryFind ".github/branch-protection.json" with
            | None -> check "branch protection is declared" false
            | Some text ->
                match BranchProtection.parse text with
                | Error errors -> check $"branch protection declaration parses: {joined errors}" false
                | Ok protection ->
                    check "branch protection requires exactly the scaffold's checks"
                        (protection.Branch = "main" && protection.RequiredChecks = Scaffolding.webRequiredChecks)

                    check "branch protection binds administrators and requires pull requests"
                        (protection.EnforceAdmins && protection.RequirePullRequest
                         && protection.RequiredApprovingReviewCount = 0
                         && not protection.AllowForcePushes && not protection.AllowDeletions)

            // `validate` is the job of praxis-validation.yml, which praxis init owns.
            check "each scaffold-owned required check is a job the scaffold's workflows define"
                (has "jobs:\n  build-and-test:" build
                 && has "jobs:\n  foundations:\n    uses: kemiller2002/praxis/.github/workflows/foundations-verify.yml@" ".github/workflows/echelon-foundations.yml")

            // Foundations declaration.
            match files |> Map.tryFind ".echelon/foundations.json" with
            | None -> check "foundations are declared" false
            | Some text ->
                use document = JsonDocument.Parse text
                let keys =
                    document.RootElement.GetProperty("capabilities").EnumerateObject()
                    |> Seq.map _.Name
                    |> Set.ofSeq

                check "foundations declare only the capabilities the Praxis schema allows, routing included for a web application"
                    (keys = set [ "aegis"; "forma"; "folio"; "limen"; "ordo"; "praxis"; "routing" ])

                let routing = document.RootElement.GetProperty("capabilities").GetProperty("routing")

                check "a web application requires the routing foundation on static hosting, against the scaffolded inventory (SAF-URL-6, SAF-URL-8)"
                    (routing.GetProperty("required").GetBoolean()
                     && routing.GetProperty("hosting").GetString() = "static"
                     && routing.GetProperty("inventory").GetString() = Scaffolding.RouteInventoryPath)

            // URL-addressable state (SAF-URL-1..10).
            match files |> Map.tryFind Scaffolding.RouteInventoryPath with
            | None -> check "the route inventory is scaffolded" false
            | Some text ->
                use document = JsonDocument.Parse text
                let root = document.RootElement
                let names = root.GetProperty("routes").EnumerateArray() |> Seq.map (fun route -> route.GetProperty("name").GetString()) |> List.ofSeq

                check "the route inventory is echelon.routes/v1 in hash mode with home and not-found routes (DF-LIMEN-2026-0006)"
                    (root.GetProperty("schema").GetString() = "echelon.routes/v1"
                     && root.GetProperty("mode").GetString() = "hash"
                     && root.GetProperty("home").GetString() = "home"
                     && root.GetProperty("notFound").GetString() = "notFound"
                     && names = [ "home"; "notFound" ])

                let hasKeys (element: JsonElement) (required: string list) =
                    let present = element.EnumerateObject() |> Seq.map _.Name |> Set.ofSeq
                    required |> List.forall present.Contains

                let routes = root.GetProperty("routes").EnumerateArray() |> List.ofSeq
                let parameters = routes |> List.collect (fun route -> route.GetProperty("params").EnumerateArray() |> List.ofSeq)

                check "the route inventory carries every key Limen 0.9.0's contract/routes.schema.json requires"
                    (hasKeys root [ "schema"; "mode"; "home"; "signIn"; "notFound"; "routes"; "legacy" ]
                     && routes |> List.forall (fun route -> hasKeys route [ "name"; "pattern"; "params"; "guards"; "requires"; "returnTarget" ])
                     && parameters |> List.forall (fun parameter -> hasKeys parameter [ "name"; "in"; "type"; "required"; "default"; "values" ]))

                let keys = root.EnumerateObject() |> Seq.map _.Name |> List.ofSeq
                check "the route inventory is written as Limen renders it: sorted keys, final newline"
                    (keys = List.sort keys && text.EndsWith("}\n", StringComparison.Ordinal))

            check "the engine owns a pure route codec, placeholder for Limen.Routing, compiled and round-trip tested (SAF-URL-9)"
                (has "let parse (location: string) : Route" "src/engine/Routes.fs"
                 && has "let format (route: Route) : string" "src/engine/Routes.fs"
                 && has "PLACEHOLDER until the project declares limen-fsharp 0.9.0 or later" "src/engine/Routes.fs"
                 && has "<Compile Include=\"Routes.fs\" />" "src/engine/App.Engine.fsproj"
                 && has "<Compile Include=\"RouteTests.fs\" />" "tests/App.Engine.Tests/App.Engine.Tests.fsproj"
                 && has "format (parse url) is the canonical url" "tests/App.Engine.Tests/RouteTests.fs")

            check "the web application's requirements include every deep-linking requirement"
                ([ 1..10 ] |> List.forall (fun number -> has $"**SAF-URL-{number}**" "requirements/URL-ADDRESSABLE-STATE.md"))

            match files |> Map.tryFind ".claude/settings.json" with
            | None -> check "Claude Code settings are scaffolded" false
            | Some text ->
                use document = JsonDocument.Parse text
                let root = document.RootElement
                let list (element: JsonElement) = element.EnumerateArray() |> Seq.choose (fun item -> item.GetString() |> Option.ofObj) |> List.ofSeq
                let allow = list (root.GetProperty("permissions").GetProperty("allow"))
                let deny = list (root.GetProperty("permissions").GetProperty("deny"))
                let autoMode = list (root.GetProperty("autoMode").GetProperty("allow"))

                check "the agent may run the repository toolchain and pull-request flow unattended"
                    ([ "Bash(git *)"; "Bash(gh pr *)"; "Bash(dotnet *)"; "Bash(npm *)"; "Bash(npx *)"; "Bash(./praxis *)" ] |> List.forall (fun rule -> List.contains rule allow))

                check "force pushes and repository deletion stay denied"
                    ([ "Bash(git push --force *)"; "Bash(git push -f *)"; "Bash(gh repo delete *)" ] |> List.forall (fun rule -> List.contains rule deny))

                check "auto mode may merge the agent's own pull requests only once the required checks are green"
                    (autoMode.Head = "$defaults"
                     && autoMode |> List.exists (fun rule -> rule.Contains("every required check is green", StringComparison.Ordinal)))

            check "the scaffold ignores build, test, package and browser outputs"
                ([ "bin/"; "obj/"; "dist/"; "TestResults/"; "node_modules/"; "test-results/"; "playwright-report/" ]
                 |> List.forall (fun entry -> has (entry + "\n") ".gitignore")))

/// A project on the current channel declares limen-fsharp (the F# Limen
/// packages through the NuGet release-asset feed). From 0.9.0 the engine then
/// routes through EchelonFoundry.Limen.Routing (CON-293), not the placeholder.
let runLimenRouting (check: string -> bool -> unit) =
    withTarget (fun target ->
        let json = webManifest.Replace("{\"id\":\"limen\",\"version\":\"0.7.1\"}", "{\"id\":\"limen\",\"version\":\"0.9.0\"}")
        let path = Path.Combine(Path.GetTempPath(), $"conditor-web-{Guid.NewGuid():N}.json")
        File.WriteAllText(path, json)

        try
            match Manifest.load path with
            | Error errors -> check $"limen-routing manifest loads: {joined errors}" false
            | Ok manifest ->
                let declared: ComponentRequest = { Id = Scaffolding.LimenFsharpSystem; Version = Some "0.9.0"; Required = false }

                let withFeed version =
                    { manifest with Components = manifest.Components @ [ { declared with Version = Some version } ] }

                check "limen-fsharp 0.9.0 or later selects Limen.Routing; earlier, absent or unparsable does not"
                    (Scaffolding.limenRoutingVersion (withFeed "0.9.0") = Some "0.9.0"
                     && Scaffolding.limenRoutingVersion (withFeed "0.10.2") = Some "0.10.2"
                     && Scaffolding.limenRoutingVersion (withFeed "0.8.0") = None
                     && Scaffolding.limenRoutingVersion (withFeed "latest") = None
                     && Scaffolding.limenRoutingVersion manifest = None)

                match Scaffolding.plan target Scaffolding.EmbeddedCatalog (withFeed "0.9.0") with
                | Error errors -> check $"limen-routing scaffold plans: {joined errors}" false
                | Ok planned ->
                    let files = Map.ofList planned
                    let has needle path = contains needle files path

                    check "the engine references EchelonFoundry.Limen.Routing at the declared limen-fsharp version"
                        (has "<PackageReference Include=\"EchelonFoundry.Limen.Routing\" Version=\"0.9.0\" />" "src/engine/App.Engine.fsproj"
                         && has "<Compile Include=\"Routes.fs\" />" "src/engine/App.Engine.fsproj")

                    check "the engine routes through Limen.Routing: table, codec, adopt/navigate/refine, ReturnTo, Link.share and Inventory.render"
                        ([ "open Limen.Routing"
                           "RouteTable.define"
                           "RouteCodec.create"
                           "RouteCodec.parse"
                           "RouteCodec.format"
                           "RouteCodec.adopt"
                           "RouteCodec.navigate"
                           "RouteCodec.refine"
                           "ReturnTo.capture"
                           "ReturnTo.resume"
                           "Link.share"
                           "Inventory.render"
                           "LocationMode.Hash" ]
                         |> List.forall (fun needle -> has needle "src/engine/Routes.fs")
                         && not (has "PLACEHOLDER" "src/engine/Routes.fs"))

                    check "the generated tests round-trip every view and hold the inventory byte-equal to Inventory.render"
                        ([ "parse (format view) is the view, for every view"
                           "format (parse url) is the canonical url"
                           "navigation pushes, refinement replaces"
                           "sign-in keeps the target"
                           "copy link is the absolute hash url"
                           "the committed route inventory is the table's Inventory.render output, byte for byte" ]
                         |> List.forall (fun needle -> has needle "tests/App.Engine.Tests/RouteTests.fs"))

                    check "the route inventory is the Limen.Routing table's rendered echelon.routes/v1 document"
                        (files |> Map.tryFind Scaffolding.RouteInventoryPath = Some Scaffolding.webLimenRouteInventory
                         && has "\"signIn\": \"signIn\"" Scaffolding.RouteInventoryPath
                         && has "\"mode\": \"hash\"" Scaffolding.RouteInventoryPath)

                    check "no unrendered template tokens remain in the Limen.Routing scaffold"
                        (files |> Map.forall (fun _ text -> not (text.Contains("@@", StringComparison.Ordinal))))
        finally
            if File.Exists path then File.Delete path)

// SD-1: the scaffold pins each npm-bound component through the distribution
// its authority declares: an npm registry release by exact version, a GitHub
// release asset by its exact tarball URL. Conditor 0.10.8 rendered Folio
// 0.4.0 as the npm reference "0.4.0", which npm cannot install: Folio is
// distributed only as a release asset (Registry echelon-current 1.25.0).

let private repositoryRoot =
    let rec up (directory: DirectoryInfo option) =
        match directory with
        | Some d when File.Exists(Path.Combine(d.FullName, "Conditor.slnx")) -> d.FullName
        | Some d -> up (d.Parent |> Option.ofObj)
        | None -> invalidOp "Conditor repository root (Conditor.slnx) was not found."

    up (Some(DirectoryInfo AppContext.BaseDirectory))

/// echelon-current 1.25.0 linux-x64, byte-identical to the authority Indy
/// init rehearsals 2 and 3 bootstrapped from (sha256 06026da5...).
let private echelonCurrent125 =
    Path.Combine(repositoryRoot, "tests", "fixtures", "registry", "echelon-current-1.25.0-linux-x64.json")

let private rehearsalComponents =
    """{"id":"praxis","version":"3.16.0"},{"id":"ordo","version":"1.17.0"},{"id":"percepta","version":"0.1.0"},{"id":"aegis","version":"1.0.0"},{"id":"limen","version":"0.9.0"},{"id":"forma","version":"0.9.0"},{"id":"folio","version":"0.4.0"},{"id":"limen-fsharp","version":"0.9.0"}"""

let private sha256Hex (bytes: byte array) =
    Convert.ToHexString(Security.Cryptography.SHA256.HashData bytes).ToLowerInvariant()

/// Plans the web scaffold in `target` under the resolved release set `set`.
let private planUnderAuthority target (set: byte array) (components: string) =
    let authority = Path.Combine(target, ".conditor", "authority", "resolved-release-set.json")
    Directory.CreateDirectory(Path.GetDirectoryName authority |> Option.ofObj |> Option.defaultValue target) |> ignore
    File.WriteAllBytes(authority, set)

    let authorityJson =
        $"\"registryAuthority\":{{\"kind\":\"resolved-release-set\",\"path\":\".conditor/authority/resolved-release-set.json\",\"sha256\":\"{sha256Hex set}\"}}"

    planFor
        target
        ("""{"schemaVersion":1,"name":"Indy Web","components":["""
         + components
         + """],"requirements":[],"execution":{"enabled":false},"scaffold":{"kind":"fsharp-limen-web","name":"Indy Web"},"""
         + authorityJson
         + "}")

let private dependenciesOf (plan: InstallationPlan) =
    scaffoldFiles plan
    |> Map.tryFind "package.json"
    |> Option.map (fun text ->
        use document = JsonDocument.Parse text

        document.RootElement.GetProperty("dependencies").EnumerateObject()
        |> Seq.map (fun property -> property.Name, property.Value.GetString() |> Option.ofObj |> Option.defaultValue "")
        |> Map.ofSeq)
    |> Option.defaultValue Map.empty

/// The specifier a set's declared distribution calls for, computed here from
/// the set itself rather than from Conditor: npm -> the exact version;
/// github-release -> the release asset of the one package artifact.
let private declaredSpecifiers (set: byte array) =
    use document = JsonDocument.Parse set

    document.RootElement.GetProperty("components").EnumerateArray()
    |> Seq.filter (fun entry -> entry.GetProperty("distributionClass").GetString() = "web-package")
    |> Seq.map (fun entry ->
        let text (name: string) (element: JsonElement) = element.GetProperty(name).GetString() |> Option.ofObj |> Option.defaultValue ""
        let distribution = entry.GetProperty "distribution"

        let artifact =
            entry.GetProperty("artifacts").EnumerateArray()
            |> Seq.find (fun item -> text "purpose" item = "package")
            |> text "name"

        let specifier =
            match text "mechanism" distribution with
            | "npm" -> text "version" entry
            | "github-release" -> $"""https://github.com/{text "repository" entry}/releases/download/{text "tag" entry}/{artifact}"""
            | other -> $"<unexpected mechanism {other}>"

        text "package" distribution, specifier)
    |> Map.ofSeq

/// The set with one component's `distribution` replaced.
let private redistribute (systemId: string) (distribution: string) (set: byte array) =
    match Nodes.JsonNode.Parse(set) with
    | :? Nodes.JsonObject as root ->
        match root["components"] with
        | :? Nodes.JsonArray as components ->
            components
            |> Seq.iter (function
                | :? Nodes.JsonObject as entry when (entry["systemId"] |> Option.ofObj |> Option.map _.GetValue<string>()) = Some systemId ->
                    entry["distribution"] <- Nodes.JsonNode.Parse distribution
                | _ -> ())
        | _ -> ()

        Text.Encoding.UTF8.GetBytes(root.ToJsonString())
    | _ -> invalidOp "the resolved release set is not a JSON object"

[<Literal>]
let private FolioAsset040 =
    "https://github.com/kemiller2002/folio/releases/download/v0.4.0/echelon-foundry-print-components-0.4.0.tgz"

[<Literal>]
let private FormaAsset090 =
    "https://github.com/kemiller2002/forma/releases/download/v0.9.0/echelon-foundry-design-system-0.9.0.tgz"

[<Literal>]
let private LimenAsset090 =
    "https://github.com/kemiller2002/limen/releases/download/v0.9.0/echelon-foundry-limen-0.9.0.tgz"

/// echelon-current 1.27.0 linux-x64, byte-identical to the Registry's
/// prepared cut (echelon-registry WI-0062, sha256 7cfb3e2b...): 1.26.0 with
/// Dokimos 0.6.0 -> 0.7.0 and the `capabilities` record of the REG-PAIR-001
/// pairing verified-completion-attested (Praxis >=3.17.0 requires Dokimos
/// >=0.7.0). Indy-init OD-INDY-8 approves the kickoff set only after this
/// Conditor qualification (DF-CON-2026-A007).
let private echelonCurrent127 =
    Path.Combine(repositoryRoot, "tests", "fixtures", "registry", "echelon-current-1.27.0-linux-x64.json")

/// The Rehearsal 3 bootstrap manifest moved to the 1.27.0 selections: only
/// Praxis 3.16.0 -> 3.20.0 differs, because Dokimos is not declared.
let private kickoffComponents =
    rehearsalComponents.Replace("""{"id":"praxis","version":"3.16.0"}""", """{"id":"praxis","version":"3.20.0"}""")

let runKickoffSet127 (check: string -> bool -> unit) =
    if Workstation.Platform.runtimeIdentifier () <> "linux-x64" then
        check "echelon-current 1.27.0 kickoff-set qualification (linux-x64 set) is skipped on this platform" true
    else
        let set = File.ReadAllBytes echelonCurrent127

        check "the vendored echelon-current 1.27.0 set is the Registry's prepared cut"
            (sha256Hex set = "7cfb3e2beab0777e849e090bc85c0755f2b841e3430b9c125addfe332d091734")

        use document = JsonDocument.Parse set
        let root = document.RootElement

        let selected systemId =
            root.GetProperty("components").EnumerateArray()
            |> Seq.tryFind (fun c -> c.GetProperty("systemId").GetString() = systemId)
            |> Option.map (fun c -> c.GetProperty("version").GetString())

        check "1.27.0 selects Praxis 3.20.0 with Dokimos 0.7.0" (selected "praxis" = Some "3.20.0" && selected "dokimos" = Some "0.7.0")

        check "1.27.0 records the verified-completion-attested pairing as enabled"
            (root.GetProperty("capabilities").EnumerateArray()
             |> Seq.exists (fun c -> c.GetProperty("id").GetString() = "verified-completion-attested" && c.GetProperty("state").GetString() = "enabled"))

        // The workstation reads the set, its new `capabilities` property
        // included, and installs Dokimos 0.7.0 under its lifecycle identity.
        match Workstation.ResolvedReleaseSets.loadFile "linux-x64" echelonCurrent127 (sha256Hex set) with
        | Error error -> check $"the workstation loads echelon-current 1.27.0: {error}" false
        | Ok profile ->
            let dokimos = profile.Components |> List.tryFind (fun c -> c.Id = "dokimos")

            check "the workstation installs Dokimos 0.7.0 from 1.27.0 with its release commit as identity"
                (dokimos
                 |> Option.exists (fun c ->
                     c.Version = "0.7.0"
                     && c.Lifecycle |> Option.exists (fun l -> l.SourceCommit = "04b5f856367b47a087285109c1fc5923ec351389")))

        withTarget (fun target ->
            match planUnderAuthority target set kickoffComponents with
            | Error errors -> check $"the kickoff manifest plans under echelon-current 1.27.0: {joined errors}" false
            | Ok plan ->
                check "under 1.27.0 the kickoff scaffold keeps the SD-1 pins its Registry distributions declare"
                    (let declared = declaredSpecifiers set
                     let dependencies = dependenciesOf plan
                     dependencies.Count = 3
                     && dependencies |> Map.forall (fun package specifier -> declared |> Map.tryFind package = Some specifier)))

        withTarget (fun target ->
            // A repository that wants attested verified completion declares
            // Dokimos; Conditor takes it on Registry authority through the
            // generic repository-lifecycle contract, with no descriptor.
            match planUnderAuthority target set (kickoffComponents + """,{"id":"dokimos","version":"0.7.0"}""") with
            | Error errors -> check $"a kickoff manifest that declares Dokimos 0.7.0 plans under 1.27.0: {joined errors}" false
            | Ok plan ->
                let dokimos =
                    plan.Actions
                    |> List.filter (fun action -> action.ComponentId = "dokimos")
                    |> List.choose (fun action ->
                        match action.Execution with
                        | ExternalProcess(command, arguments) -> Some(action.ComponentVersion, command, List.tryHead arguments)
                        | _ -> None)

                check "Dokimos 0.7.0 is initialized and verified through its repository lifecycle"
                    (dokimos = [ "0.7.0", "dokimos", Some "init"; "0.7.0", "dokimos", Some "verify" ]))

let runRegistryDistribution (check: string -> bool -> unit) =
    if Workstation.Platform.runtimeIdentifier () <> "linux-x64" then
        check "Registry distribution scaffold fixtures (linux-x64 sets) are skipped on this platform" true
    else
        let set = File.ReadAllBytes echelonCurrent125

        check "the vendored echelon-current 1.25.0 set is the rehearsal authority"
            (sha256Hex set = "06026da529d5408a7363793790a0eed32b3f5414246fe4509a216adb27d403e3")

        withTarget (fun target ->
            match planUnderAuthority target set rehearsalComponents with
            | Error errors -> check $"the rehearsal manifest plans under echelon-current 1.25.0: {joined errors}" false
            | Ok plan ->
                let dependencies = dependenciesOf plan

                check "SD-1: Folio 0.4.0 is pinned to its Registry release asset, not the npm reference \"0.4.0\""
                    (dependencies |> Map.tryFind "@echelon-foundry/print-components" = Some FolioAsset040)

                check "Limen 0.9.0, which the Registry distributes through npm, keeps its exact npm version"
                    (dependencies |> Map.tryFind "@echelon-foundry/limen" = Some "0.9.0")

                check "Forma 0.9.0, which the Registry distributes as a GitHub release asset, is pinned to that asset"
                    (dependencies |> Map.tryFind "@echelon-foundry/design-system" = Some FormaAsset090)

                check "every scaffold dependency is exactly the specifier its Registry distribution declares"
                    (let declared = declaredSpecifiers set
                     dependencies.Count = 3
                     && dependencies |> Map.forall (fun package specifier -> declared |> Map.tryFind package = Some specifier)))

        withTarget (fun target ->
            // The same releases with their distributions exchanged: the pin
            // follows the declaration, not the component's identity.
            let exchanged =
                set
                |> redistribute "folio" """{"mechanism":"npm","package":"@echelon-foundry/print-components","url":"https://registry.npmjs.org/@echelon-foundry/print-components/-/print-components-0.4.0.tgz"}"""
                |> redistribute "limen" """{"mechanism":"github-release","package":"@echelon-foundry/limen","url":"https://github.com/kemiller2002/limen/releases/tag/v0.9.0"}"""

            match planUnderAuthority target exchanged rehearsalComponents with
            | Error errors -> check $"a set with exchanged distributions plans: {joined errors}" false
            | Ok plan ->
                let dependencies = dependenciesOf plan

                check "a component the Registry declares as npm-distributed is pinned by exact version"
                    (dependencies |> Map.tryFind "@echelon-foundry/print-components" = Some "0.4.0")

                check "a component the Registry declares as a GitHub release asset is pinned to that asset"
                    (dependencies |> Map.tryFind "@echelon-foundry/limen" = Some LimenAsset090))

        withTarget (fun target ->
            let unsupported =
                set |> redistribute "folio" """{"mechanism":"container","package":"@echelon-foundry/print-components","url":null}"""

            match planUnderAuthority target unsupported rehearsalComponents with
            | Ok _ -> check "a scaffold binding with an unsupported Registry distribution must be refused" false
            | Error errors ->
                check "a scaffold binding with an unsupported Registry distribution is refused before mutation, naming it"
                    (errors |> List.exists (fun error -> error.Contains("'folio'", StringComparison.Ordinal))))

        withTarget (fun target ->
            let unlocated =
                set |> redistribute "folio" """{"mechanism":"npm","package":"@echelon-foundry/print-components","url":null}"""

            match planUnderAuthority target unlocated rehearsalComponents with
            | Ok _ -> check "an npm distribution with no npmjs.org tarball must be refused" false
            | Error errors ->
                check "an npm distribution with no npmjs.org tarball is refused, not pinned by a guessed version"
                    (errors |> List.exists (fun error -> error.Contains("'folio'@0.4.0", StringComparison.Ordinal) && error.Contains("npmjs.org", StringComparison.Ordinal))))

        withTarget (fun target ->
            let renamed =
                set |> redistribute "folio" """{"mechanism":"github-release","package":"@echelon-foundry/print","url":null}"""

            match planUnderAuthority target renamed rehearsalComponents with
            | Ok _ -> check "a Registry package identity that disagrees with Conditor's must be refused" false
            | Error errors ->
                check "a Registry package identity that disagrees with Conditor's is refused, not guessed"
                    (errors |> List.exists (fun error -> error.Contains("@echelon-foundry/print", StringComparison.Ordinal) && error.Contains("folio", StringComparison.Ordinal))))

    // Without a Registry authority the embedded descriptors' declarations
    // govern: a release-asset version by its declared asset, every other
    // version by its exact npm version.
    withTarget (fun target ->
        let json =
            webManifest
                .Replace("{\"id\":\"folio\",\"version\":\"0.3.0\"}", "{\"id\":\"folio\",\"version\":\"0.4.0\"}")

        match planFor target json with
        | Error errors -> check $"an embedded-catalog web scaffold with Folio 0.4.0 plans: {joined errors}" false
        | Ok plan ->
            let dependencies = dependenciesOf plan

            check "without an authority, Folio 0.4.0 is pinned to the release asset its descriptor declares"
                (dependencies |> Map.tryFind "@echelon-foundry/print-components" = Some FolioAsset040)

            check "without an authority, npm-distributed Limen and Forma keep their exact npm versions"
                (dependencies |> Map.tryFind "@echelon-foundry/limen" = Some "0.7.1"
                 && dependencies |> Map.tryFind "@echelon-foundry/design-system" = Some "0.5.0"))
