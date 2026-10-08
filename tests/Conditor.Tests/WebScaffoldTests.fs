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
    """{"schemaVersion":1,"name":"Indy Web","components":[{"id":"praxis","version":"3.7.2"},{"id":"ordo","version":"1.5.0"},{"id":"percepta","version":"0.1.0"},{"id":"aegis","version":"1.0.0"},{"id":"limen","version":"0.7.1"},{"id":"forma","version":"0.4.1"},{"id":"folio","version":"0.3.0"}],"requirements":[],"execution":{"enabled":false},"scaffold":{"kind":"fsharp-limen-web","name":"Indy Web"}}"""

let private joined (errors: string list) = String.concat "; " errors

let private expectedPaths =
    [ "Directory.Build.props"
      "App.slnx"
      "src/engine/App.Engine.fsproj"
      "src/engine/Operational.fs"
      "src/engine/Domain.fs"
      "tests/App.Engine.Tests/App.Engine.Tests.fsproj"
      "tests/App.Engine.Tests/EngineTests.fs"
      ".echelon/foundations.json"
      "aegis-boundaries.json"
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
                     && dependencies.GetProperty("@echelon-foundry/design-system").GetString() = "0.4.1"
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

                check "foundations declare only the capabilities the Praxis schema allows"
                    (keys = set [ "aegis"; "forma"; "folio"; "limen"; "ordo"; "praxis" ])

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
