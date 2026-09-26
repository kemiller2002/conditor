open System
open System.IO
open Conditor.Core

let mutable failures = 0

let check name condition =
    if condition then
        Console.WriteLine $"PASS {name}"
    else
        failures <- failures + 1
        Console.Error.WriteLine $"FAIL {name}"

let withManifest (json: string) (test: string -> unit) =
    let path = Path.Combine(Path.GetTempPath(), $"conditor-{Guid.NewGuid():N}.json")

    try
        File.WriteAllText(path, json)
        test path
    finally
        if File.Exists path then
            File.Delete path

withManifest
    """{"schemaVersion":1,"name":"demo","components":[{"id":"praxis","version":"3.1.4"},{"id":"ordo"}]}"""
    (fun path ->
        match Manifest.load path with
        | Error errors ->
            let details = String.concat "; " errors
            check $"valid manifest parses: {details}" false
        | Ok manifest ->
            check "manifest name" (manifest.Name = "demo")
            check "manifest component count" (manifest.Components.Length = 2)

            match Planner.create "/tmp/demo" Init manifest with
            | Error errors ->
                let details = String.concat "; " errors
                check $"plan succeeds: {details}" false
            | Ok plan ->
                check "init emits install and verify per lifecycle component" (plan.Actions.Length = 4)
                check "explicit version is preserved" (plan.Components[0].Version = "3.1.4")
                check "default version resolves" (plan.Components[1].Version = "1.3.0")
                check
                    "registry package source is immutable"
                    (plan.Components[0].SourceReference = Some "@echelon-foundry/repository-operating-system@3.1.4"))

withManifest
    """{"schemaVersion":1,"name":"demo","components":[{"id":"communication-engineering","version":"1.0.0"}]}"""
    (fun path ->
        match Manifest.load path with
        | Error _ ->
            check "communication manifest parses" false
        | Ok manifest ->
            match Planner.create "/tmp/demo" Init manifest with
            | Error _ ->
                check "fixed source resolves" false
            | Ok plan ->
                let expectedSource =
                    Some
                        "github:kemiller2002/communication-engineering#4590d2fe6f7e80b339117d3fbee5803f2dd39122|node:bin/communication-engineering.mjs"

                check "fixed source resolves to commit and entrypoint" (plan.Components[0].SourceReference = expectedSource)

                match plan.Actions[0].Execution with
                | GitHubSourceProcess(source, arguments) ->
                    check "fixed source uses direct GitHub execution" (source.Repository = "kemiller2002/communication-engineering")
                    check "fixed source carries lifecycle arguments" (arguments = [ "init" ])
                | _ ->
                    check "fixed source uses direct GitHub execution" false)

withManifest
    """{"schemaVersion":1,"name":"demo","components":[{"id":"communication-engineering","version":"9.9.9"}]}"""
    (fun path ->
        match Manifest.load path with
        | Error _ ->
            check "unmapped version manifest parses" false
        | Ok manifest ->
            match Planner.create "/tmp/demo" Init manifest with
            | Error errors ->
                check
                    "unmapped GitHub-source version rejected"
                    (errors |> List.exists (fun error -> error.Contains("no immutable distribution mapping")))
            | Ok _ ->
                check "unmapped fixed-source version rejected" false)

withManifest
    """{"schemaVersion":1,"name":"demo","components":[{"id":"praxis"},{"id":"praxis"}]}"""
    (fun path ->
        match Manifest.load path with
        | Error errors ->
            check "duplicate component rejected" (errors |> List.exists (fun error -> error.Contains("more than once")))
        | Ok _ ->
            check "duplicate component rejected" false)

withManifest
    """{"schemaVersion":1,"name":"demo","components":[{"id":"forma"}]}"""
    (fun path ->
        match Manifest.load path with
        | Error _ ->
            check "forma manifest parses" false
        | Ok manifest ->
            match Planner.create "/tmp/demo" Init manifest with
            | Error errors ->
                check
                    "application package binding requires scaffold"
                    (errors |> List.exists (fun error -> error.Contains("A scaffold is required")))
            | Ok _ ->
                check "application package binding is explicit" false)


withManifest
    """{"schemaVersion":1,"name":"limen-bootstrap","components":[{"id":"limen","version":"0.6.1"}]}"""
    (fun path ->
        match Manifest.load path with
        | Error _ ->
            check "limen bootstrap manifest parses" false
        | Ok manifest ->
            match Planner.create "/tmp/limen-bootstrap" Init manifest with
            | Error _ ->
                check "limen bootstrap plan succeeds" false
            | Ok plan ->
                check "limen init plus verify planned" (plan.Actions.Length = 2)

                match plan.Actions[1].Execution with
                | ExternalProcess(_, arguments) ->
                    check "limen bootstrap verification is non-strict" (arguments |> List.contains "--strict" |> not)
                | _ ->
                    check "limen bootstrap verification is non-strict" false)

let withTarget test =
    let path = Path.Combine(Path.GetTempPath(), $"conditor-target-{Guid.NewGuid():N}")
    Directory.CreateDirectory path |> ignore

    try
        test path
    finally
        if Directory.Exists path then
            Directory.Delete(path, true)

withTarget
    (fun target ->
        withManifest
            """{"schemaVersion":1,"name":"scaffold-demo","components":[{"id":"limen","version":"0.6.1"},{"id":"forma","version":"0.2.0"},{"id":"folio","version":"0.3.0"},{"id":"aegis","version":"1.0.0"}],"scaffold":{"kind":"fsharp-limen-web","name":"scaffold-demo"}}"""
            (fun path ->
                match Manifest.load path with
                | Error _ ->
                    check "scaffold manifest parses" false
                | Ok manifest ->
                    match Planner.create target Init manifest with
                    | Error errors ->
                        let details = String.concat "; " errors
                        check $"scaffold plan succeeds: {details}" false
                    | Ok plan ->
                        check "scaffold application bindings resolve" (plan.Components.Length = 4)
                        check "scaffold plans foundation-ready project files" (plan.Actions |> List.filter (fun action -> action.Kind = ScaffoldFile) |> List.length = 12)
                        check "scaffold ends in strict Limen readiness" (plan.Actions |> List.exists (fun action -> action.Kind = ReadinessVerify))

                        let packageJson =
                            plan.Actions
                            |> List.tryPick (fun action ->
                                match action.Execution with
                                | EnsureFile("src/kernel/package.json", fileContent) -> Some fileContent
                                | _ -> None)

                        let projectFile =
                            plan.Actions
                            |> List.tryPick (fun action ->
                                match action.Execution with
                                | EnsureFile("src/engine/App.Engine.fsproj", fileContent) -> Some fileContent
                                | _ -> None)


                        let foundations =
                            plan.Actions
                            |> List.tryPick (fun action ->
                                match action.Execution with
                                | EnsureFile(".echelon/foundations.json", fileContent) -> Some fileContent
                                | _ -> None)

                        let operational =
                            plan.Actions
                            |> List.tryPick (fun action ->
                                match action.Execution with
                                | EnsureFile("src/engine/Operational.fs", fileContent) -> Some fileContent
                                | _ -> None)

                        let boundaries =
                            plan.Actions
                            |> List.tryPick (fun action ->
                                match action.Execution with
                                | EnsureFile("aegis-boundaries.json", fileContent) -> Some fileContent
                                | _ -> None)

                        let screen =
                            plan.Actions
                            |> List.tryPick (fun action ->
                                match action.Execution with
                                | EnsureFile("src/kernel/index.html", fileContent) -> Some fileContent
                                | _ -> None)

                        let printSurface =
                            plan.Actions
                            |> List.tryPick (fun action ->
                                match action.Execution with
                                | EnsureFile("src/kernel/print.html", fileContent) -> Some fileContent
                                | _ -> None)

                        check
                            "scaffold binds Limen Forma and Folio to npm target"
                            (packageJson
                             |> Option.exists (fun text ->
                                 text.Contains("@echelon-foundry/typescript-wasm-kernel")
                                 && text.Contains("@echelon-foundry/design-system")
                                 && text.Contains("@echelon-foundry/print-components")))

                        check
                            "scaffold binds Aegis to F# target"
                            (projectFile
                             |> Option.exists (fun text -> text.Contains("EchelonFoundry.Aegis.Core")))


                        check
                            "Folio dependency is immutable"
                            (packageJson
                             |> Option.exists (fun text ->
                                 text.Contains("github:kemiller2002/folio#273b18f5b23db15cddd173c05af5d1a8484fc4cf")))

                        check
                            "scaffold declares application foundations"
                            (foundations
                             |> Option.exists (fun text ->
                                 text.Contains("\"aegis\"")
                                 && text.Contains("\"forma\"")
                                 && text.Contains("\"folio\"")
                                 && text.Contains("273b18f5b23db15cddd173c05af5d1a8484fc4cf")))

                        check
                            "scaffold configures Aegis"
                            (operational
                             |> Option.exists (fun text ->
                                 text.Contains("Aegis.configure")
                                 && text.Contains("Bootstrap.validate")))

                        check
                            "scaffold declares Aegis boundaries"
                            (boundaries
                             |> Option.exists (fun text ->
                                 text.Contains("aegis/boundaries/v1")
                                 && text.Contains("Limen interop")))

                        check
                            "scaffold uses Forma presentation"
                            (screen
                             |> Option.exists (fun text ->
                                 text.Contains("@echelon-foundry/design-system")
                                 && text.Contains("<ef-button>")))

                        check
                            "scaffold uses Folio print presentation"
                            (printSurface
                             |> Option.exists (fun text ->
                                 text.Contains("@echelon-foundry/print-components")
                                 && text.Contains("<ef-print-document>")))

                        let readiness =
                            plan.Actions
                            |> List.tryFind (fun action -> action.Kind = ReadinessVerify)

                        check
                            "readiness uses strict Limen verification"
                            (readiness
                             |> Option.exists (fun action ->
                                 match action.Execution with
                                 | ExternalProcess(_, arguments) ->
                                     arguments |> List.contains "--strict"
                                 | _ -> false))))

withTarget
    (fun target ->
        File.WriteAllText(Path.Combine(target, "Directory.Build.props"), "user-owned")
        withManifest
            """{"schemaVersion":1,"name":"conflict-demo","components":[{"id":"limen","version":"0.6.1"}],"scaffold":{"kind":"fsharp-limen-web"}}"""
            (fun path ->
                match Manifest.load path with
                | Error _ ->
                    check "conflict scaffold manifest parses" false
                | Ok manifest ->
                    match Planner.create target Init manifest with
                    | Error errors ->
                        check
                            "scaffold conflict rejected before execution"
                            (errors |> List.exists (fun error -> error.Contains("will not overwrite it")))
                    | Ok _ ->
                        check "scaffold conflict rejected before execution" false))

withTarget
    (fun target ->
        withManifest
            """{"schemaVersion":1,"name":"unknown-scaffold","components":[],"scaffold":{"kind":"not-a-scaffold"}}"""
            (fun path ->
                match Manifest.load path with
                | Error _ ->
                    check "unknown scaffold manifest parses" false
                | Ok manifest ->
                    match Planner.create target Init manifest with
                    | Error errors ->
                        check
                            "unknown scaffold kind rejected"
                            (errors |> List.exists (fun error -> error.Contains("Unsupported scaffold kind")))
                    | Ok _ ->
                        check "unknown scaffold kind rejected" false))

withManifest
    """{"schemaVersion":1,"name":"secure-demo","components":[{"id":"tutela"}]}"""
    (fun path ->
        match Manifest.load path with
        | Error _ -> check "tutela manifest parses" false
        | Ok manifest ->
            match Planner.create "/tmp/secure-demo" Init manifest with
            | Error _ -> check "tutela lifecycle plan succeeds" false
            | Ok plan ->
                check "tutela default version resolves" (plan.Components[0].Version = "0.1.0")
                check "tutela init plus verify planned" (plan.Actions.Length = 2)
                check
                    "tutela package source is immutable"
                    (plan.Components[0].SourceReference =
                        Some "github:kemiller2002/tutela#1acf421e7d940665c134012f11b082a502e17537|node:bin/tutela.mjs"))

withManifest
    """{"schemaVersion":1,"name":"requirements-demo","components":[],"requirements":[{"id":"spec","source":{"repository":"kemiller2002/communication-engineering","commit":"4590d2fe6f7e80b339117d3fbee5803f2dd39122","path":"README.md"},"targetPath":"requirements/SPEC.md"}]}"""
    (fun path ->
        match Manifest.load path with
        | Error _ ->
            check "requirements manifest parses" false
        | Ok manifest ->
            check "requirements source count" (manifest.Requirements.Length = 1)

            match Planner.create "/tmp/requirements-demo" Init manifest with
            | Error _ ->
                check "requirements plan succeeds" false
            | Ok plan ->
                let requirementActions =
                    plan.Actions
                    |> List.filter (fun action -> action.Kind = RequirementFile)

                check "requirement materialization planned" (requirementActions.Length = 1)

                match requirementActions[0].Execution with
                | MaterializeSourceFile(source, targetPath) ->
                    check "requirement source commit is pinned" (source.Commit = "4590d2fe6f7e80b339117d3fbee5803f2dd39122")
                    check "requirement target path preserved" (targetPath = "requirements/SPEC.md")
                | _ ->
                    check "requirement materialization planned" false)

withManifest
    """{"schemaVersion":1,"name":"bad-requirements","components":[],"requirements":[{"id":"spec","source":{"repository":"kemiller2002/communication-engineering","commit":"main","path":"README.md"},"targetPath":"../SPEC.md"}]}"""
    (fun path ->
        match Manifest.load path with
        | Error _ ->
            check "invalid requirement manifest still parses structurally" false
        | Ok manifest ->
            match Planner.create "/tmp/requirements-demo" Init manifest with
            | Error errors ->
                check "unpinned requirement source rejected" (errors |> List.exists (fun error -> error.Contains("40-character commit SHA")))
                check "escaping requirement target rejected" (errors |> List.exists (fun error -> error.Contains("escapes the target repository")))
            | Ok _ ->
                check "invalid requirements rejected before mutation" false)



withTarget
    (fun target ->
        withManifest
            """{"schemaVersion":1,"name":"contract-demo","components":[{"id":"limen","version":"0.6.1"}],"requirements":[{"id":"contract","source":{"repository":"kemiller2002/communication-engineering","commit":"4590d2fe6f7e80b339117d3fbee5803f2dd39122","path":"package.json"},"targetPath":".echelon/kickoff/project.json"}],"execution":{"enabled":false,"mission":"Build the governed app.","contractPath":".echelon/kickoff/project.json"},"scaffold":{"kind":"fsharp-limen-web"}}"""
            (fun path ->
                match Manifest.load path with
                | Error errors ->
                    let details = String.concat "; " errors
                    check $"execution contract manifest parses: {details}" false
                | Ok manifest ->
                    check
                        "execution contract path parsed"
                        (manifest.Execution |> Option.bind (fun execution -> execution.ContractPath) = Some ".echelon/kickoff/project.json")

                    match Planner.create target Init manifest with
                    | Error errors ->
                        let details = String.concat "; " errors
                        check $"execution contract scaffold plans: {details}" false
                    | Ok plan ->
                        let agentFile =
                            plan.Actions
                            |> List.tryPick (fun action ->
                                match action.Execution with
                                | EnsureManagedRegion("AGENTS.md", "agent-entry", content) -> Some content
                                | _ -> None)

                        check "agent entry file generated" agentFile.IsSome
                        check
                            "agent entry points to canonical contract"
                            (agentFile
                             |> Option.exists (fun content ->
                                 content.Contains(".echelon/kickoff/project.json")
                                 && content.Contains("Build the governed app.")))))


withManifest
    """{"schemaVersion":1,"name":"missing-contract","components":[],"execution":{"enabled":false,"contractPath":".echelon/kickoff/missing.json"},"scaffold":{"kind":"fsharp-limen-web"}}"""
    (fun path ->
        match Manifest.load path with
        | Error _ -> check "missing contract manifest parses" false
        | Ok manifest ->
            match Planner.create "/tmp/missing-contract" Init manifest with
            | Error errors ->
                check
                    "missing execution contract rejected"
                    (errors |> List.exists (fun error -> error.Contains("must already exist or match")))
            | Ok _ ->
                check "missing execution contract rejected" false)

withTarget
    (fun target ->
        withManifest
            """{"schemaVersion":1,"name":"mission-demo","components":[{"id":"praxis","version":"3.1.4"}],"execution":{"enabled":false,"mission":"Build the governed application.","contractPath":"requirements/contract.json"},"requirements":[{"id":"contract","source":{"repository":"kemiller2002/communication-engineering","commit":"4590d2fe6f7e80b339117d3fbee5803f2dd39122","path":"README.md"},"targetPath":"requirements/contract.json"}]}"""
            (fun path ->
                match Manifest.load path with
                | Error errors ->
                    let details = String.concat "; " errors
                    check $"mission manifest parses: {details}" false
                | Ok manifest ->
                    match Planner.create target Init manifest with
                    | Error errors ->
                        let details = String.concat "; " errors
                        check $"mission plan succeeds: {details}" false
                    | Ok plan ->
                        let missionAction =
                            plan.Actions
                            |> List.tryFind (fun action -> action.Kind = MissionWorkItem)

                        check "Praxis mission planned" missionAction.IsSome

                        match missionAction |> Option.map _.Execution with
                        | Some(EnsurePraxisMission mission) ->
                            check "mission id deterministic" (mission.Id = "COND-MISSION-001")
                            check "mission contract preserved" (mission.ContractPath = "requirements/contract.json")
                            check "mission description preserved" (mission.Description = "Build the governed application.")
                        | _ ->
                            check "Praxis mission execution modeled" false))

withTarget
    (fun target ->
        let agentsPath = Path.Combine(target, "AGENTS.md")
        File.WriteAllText(agentsPath, "# Repository Notes\n\nKeep this user-owned guidance.\n")

        let plan =
            { ProjectName = "managed-region-test"
              Operation = Verify
              Components = []
              Actions =
                [ { Sequence = 1
                    ComponentId = "scaffold:fsharp-limen-web"
                    ComponentVersion = "1"
                    Kind = ScaffoldFile
                    Execution =
                        EnsureManagedRegion(
                            "AGENTS.md",
                            "agent-entry",
                            "# Agent Entry\n\nRead kickoff/project.json first.\n"
                        ) } ] }

        match Installer.execute target "test-manifest.json" plan with
        | Error errors ->
            let details = String.concat "; " errors
            check $"managed AGENTS region executes: {details}" false
        | Ok _ ->
            let first = File.ReadAllText agentsPath

            check
                "managed AGENTS region preserves user content"
                (first.Contains("# Repository Notes")
                 && first.Contains("Keep this user-owned guidance.")
                 && first.Contains("<!-- conditor:agent-entry:start -->")
                 && first.Contains("Read kickoff/project.json first.")
                 && first.Contains("<!-- conditor:agent-entry:end -->"))

            let updatedPlan =
                { plan with
                    Actions =
                        [ { plan.Actions.Head with
                              Execution =
                                EnsureManagedRegion(
                                    "AGENTS.md",
                                    "agent-entry",
                                    "# Agent Entry\n\nRead kickoff/project-v2.json first.\n"
                                ) } ] }

            match Installer.execute target "test-manifest.json" updatedPlan with
            | Error errors ->
                let details = String.concat "; " errors
                check $"managed AGENTS region updates: {details}" false
            | Ok _ ->
                let second = File.ReadAllText agentsPath

                check
                    "managed AGENTS region updates only owned content"
                    (second.Contains("# Repository Notes")
                     && second.Contains("Keep this user-owned guidance.")
                     && second.Contains("Read kickoff/project-v2.json first.")
                     && not (second.Contains("Read kickoff/project.json first."))))


withTarget
    (fun target ->
        withManifest
            """{"schemaVersion":1,"name":"ordo-baseline-demo","components":[{"id":"ordo","version":"1.3.0"},{"id":"limen","version":"0.6.1"}],"requirements":[{"id":"contract","source":{"repository":"kemiller2002/communication-engineering","commit":"4590d2fe6f7e80b339117d3fbee5803f2dd39122","path":"README.md"},"targetPath":"requirements/contract.md"}],"execution":{"enabled":false,"contractPath":"requirements/contract.md"},"scaffold":{"kind":"fsharp-limen-web","name":"ordo-baseline-demo"}}"""
            (fun path ->
                match Manifest.load path with
                | Error errors ->
                    let details = String.concat "; " errors
                    check $"Ordo baseline manifest parses: {details}" false
                | Ok manifest ->
                    match Planner.create target Init manifest with
                    | Error errors ->
                        let details = String.concat "; " errors
                        check $"Ordo baseline plans: {details}" false
                    | Ok plan ->
                        let semanticMap =
                            plan.Actions
                            |> List.tryPick (fun action ->
                                match action.Execution with
                                | EnsureFile("SDE-MAP.md", fileContent) -> Some fileContent
                                | _ -> None)

                        let currentState =
                            plan.Actions
                            |> List.tryPick (fun action ->
                                match action.Execution with
                                | EnsureManagedRegion("context/CURRENT-STATE.md", "ordo-baseline", fileContent) -> Some fileContent
                                | _ -> None)

                        check "Ordo semantic map is initialized" semanticMap.IsSome
                        check "Ordo current-state baseline is initialized" currentState.IsSome

                        check
                            "Ordo baseline routes to accepted requirements without inventing semantics"
                            (semanticMap
                             |> Option.exists (fun text ->
                                 text.Contains("requirements/contract.md")
                                 && text.Contains("intentionally unknown")
                                 && text.Contains("not established yet")))

                        check
                            "Ordo baseline preserves unknowns and obligations"
                            (currentState
                             |> Option.exists (fun text ->
                                 text.Contains("Application domain concepts have not yet been derived")
                                 && text.Contains("legal state and illegal states have not yet been identified")
                                 && text.Contains("Preserve unknowns explicitly")))))


withTarget
    (fun target ->
        withManifest
            """{"schemaVersion":1,"name":"praxis-reconcile-demo","components":[{"id":"praxis","version":"3.1.4"},{"id":"limen","version":"0.6.1"}],"scaffold":{"kind":"fsharp-limen-web","name":"praxis-reconcile-demo"}}"""
            (fun path ->
                match Manifest.load path with
                | Error errors ->
                    let details = String.concat "; " errors
                    check $"Praxis reconciliation manifest parses: {details}" false
                | Ok manifest ->
                    match Planner.create target Init manifest with
                    | Error errors ->
                        let details = String.concat "; " errors
                        check $"Praxis reconciliation plan succeeds: {details}" false
                    | Ok plan ->
                        let praxisActions =
                            plan.Actions
                            |> List.filter (fun action -> action.ComponentId = "praxis")

                        let lastScaffoldSequence =
                            plan.Actions
                            |> List.filter (fun action -> action.Kind = ScaffoldFile)
                            |> List.map _.Sequence
                            |> List.max

                        check "Praxis reconciliation adds second init and verify" (praxisActions.Length = 4)
                        check
                            "Praxis reconciliation runs after shared scaffold integration"
                            (praxisActions[2].Sequence > lastScaffoldSequence
                             && praxisActions[3].Sequence > praxisActions[2].Sequence)))


match Presets.resolve "indy-init" with
| Error errors ->
    let details = String.concat "; " errors
    check $"embedded Indy preset resolves: {details}" false
| Ok preset ->
    check "embedded Indy preset content is present" (preset.Content.Contains("indy-init-root-cause-investigator"))

    match Manifest.load preset.ManifestPath with
    | Error errors ->
        let details = String.concat "; " errors
        check $"embedded Indy preset parses: {details}" false
    | Ok manifest ->
        check "embedded Indy preset is execution enabled" (manifest.Execution |> Option.exists _.Enabled)

        match Planner.create "/tmp/embedded-indy-plan" Init manifest with
        | Error errors ->
            let details = String.concat "; " errors
            check $"embedded Indy preset plans: {details}" false
        | Ok plan ->
            let bound = Presets.bindToTarget preset plan

            match bound.Actions.Head.Execution with
            | EnsureFile("conditor.json", fileContent) ->
                check "embedded preset is first-class target manifest" (fileContent = preset.Content)
            | _ ->
                check "embedded preset is first-class target manifest" false

match Presets.resolve "does-not-exist" with
| Error errors ->
    check "unknown embedded preset rejected" (errors |> List.exists (fun error -> error.Contains("Available presets")))
| Ok _ ->
    check "unknown embedded preset rejected" false


withTarget
    (fun target ->
        withManifest
            """{"schemaVersion":1,"name":"status-demo","components":[],"requirements":[],"execution":{"enabled":false}}"""
            (fun manifestPath ->
                match Manifest.load manifestPath with
                | Error errors ->
                    let details = String.concat "; " errors
                    check $"status manifest parses: {details}" false
                | Ok manifest ->
                    let plan =
                        { ProjectName = manifest.Name
                          Operation = Init
                          Components = []
                          Actions = [] }

                    let targetManifest = Path.Combine(target, "conditor.json")
                    File.Copy(manifestPath, targetManifest)
                    LockFile.write target targetManifest plan |> ignore

                    let report = Status.inspect target targetManifest manifest
                    check "status report is healthy for locked empty project" (Status.isHealthy report)
                    check "status reports project name" (report.Project = "status-demo")
                    check
                        "status reports disabled execution informationally"
                        (report.Checks
                         |> List.exists (fun item ->
                             item.Name = "execution"
                             && item.State = Status.Informational
                             && item.Detail.Contains("disabled")))))


withTarget
    (fun target ->
        withManifest
            """{"schemaVersion":1,"name":"repair-demo","components":[],"requirements":[],"execution":{"enabled":false}}"""
            (fun manifestPath ->
                match Manifest.load manifestPath with
                | Error errors ->
                    let details = String.concat "; " errors
                    check $"repair manifest parses: {details}" false
                | Ok manifest ->
                    let targetManifest = Path.Combine(target, "conditor.json")
                    File.Copy(manifestPath, targetManifest)

                    let plan =
                        { ProjectName = manifest.Name
                          Operation = Init
                          Components = []
                          Actions = [] }

                    LockFile.write target targetManifest plan |> ignore

                    match Repair.reconcile target targetManifest manifest with
                    | Error errors ->
                        let details = String.concat "; " errors
                        check $"intact locked project repairs: {details}" false
                    | Ok() ->
                        check "intact locked project repairs" true

                    File.AppendAllText(targetManifest, Environment.NewLine)

                    match Repair.reconcile target targetManifest manifest with
                    | Error errors ->
                        check
                            "repair refuses manifest drift"
                            (errors |> List.exists (fun error -> error.Contains("manifest has changed")))
                    | Ok() ->
                        check "repair refuses manifest drift" false))


withTarget
    (fun target ->
        withManifest
            """{"schemaVersion":1,"name":"lock-snapshot-demo","components":[],"requirements":[],"execution":{"enabled":false}}"""
            (fun manifestPath ->
                match Manifest.load manifestPath with
                | Error errors ->
                    let details = String.concat "; " errors
                    check $"lock snapshot manifest parses: {details}" false
                | Ok manifest ->
                    let targetManifest = Path.Combine(target, "conditor.json")
                    File.Copy(manifestPath, targetManifest)

                    let plan =
                        { ProjectName = manifest.Name
                          Operation = Init
                          Components = []
                          Actions = [] }

                    let lockPath = LockFile.write target targetManifest plan
                    use document = System.Text.Json.JsonDocument.Parse(File.ReadAllText lockPath)
                    let root = document.RootElement
                    let schemaVersion = root.GetProperty("schemaVersion").GetInt32()
                    let lockedManifest = root.GetProperty("manifest")

                    check "lock schema v3 is written" (schemaVersion = 3)
                    check
                        "lock stores the exact governing declaration"
                        (lockedManifest.GetProperty("name").GetString() = "lock-snapshot-demo"
                         && lockedManifest.GetProperty("schemaVersion").GetInt32() = 1)))


withTarget
    (fun target ->
        withManifest
            """{"schemaVersion":1,"name":"upgrade-noop","components":[],"requirements":[],"execution":{"enabled":false}}"""
            (fun manifestPath ->
                let targetManifest = Path.Combine(target, "conditor.json")
                File.Copy(manifestPath, targetManifest)

                match Manifest.load targetManifest with
                | Error errors ->
                    let details = String.concat "; " errors
                    check $"upgrade noop manifest parses: {details}" false
                | Ok manifest ->
                    let lockPlan =
                        { ProjectName = manifest.Name
                          Operation = Init
                          Components = []
                          Actions = [] }

                    LockFile.write target targetManifest lockPlan |> ignore

                    match Upgrade.apply target targetManifest manifest with
                    | Error errors ->
                        let details = String.concat "; " errors
                        check $"no-op lifecycle upgrade succeeds: {details}" false
                    | Ok result ->
                        check "no-op lifecycle upgrade succeeds" result.ChangedComponents.IsEmpty))

withTarget
    (fun target ->
        withManifest
            """{"schemaVersion":1,"name":"upgrade-governance","components":[],"requirements":[],"execution":{"enabled":false}}"""
            (fun manifestPath ->
                let targetManifest = Path.Combine(target, "conditor.json")
                File.Copy(manifestPath, targetManifest)

                match Manifest.load targetManifest with
                | Error _ ->
                    check "upgrade governance baseline parses" false
                | Ok manifest ->
                    let lockPlan =
                        { ProjectName = manifest.Name
                          Operation = Init
                          Components = []
                          Actions = [] }

                    LockFile.write target targetManifest lockPlan |> ignore

                    File.WriteAllText(
                        targetManifest,
                        """{"schemaVersion":1,"name":"upgrade-governance-renamed","components":[],"requirements":[],"execution":{"enabled":false}}"""
                    )

                    match Manifest.load targetManifest with
                    | Error _ ->
                        check "upgrade changed governance parses" false
                    | Ok changedManifest ->
                        match Upgrade.apply target targetManifest changedManifest with
                        | Error errors ->
                            check
                                "upgrade rejects non-version governance changes"
                                (errors |> List.exists (fun error -> error.Contains("unsupported governing fields")))
                        | Ok _ ->
                            check "upgrade rejects non-version governance changes" false))

withTarget
    (fun target ->
        withManifest
            """{"schemaVersion":1,"name":"upgrade-app-binding","components":[{"id":"forma","version":"0.2.0"}],"requirements":[],"execution":{"enabled":false}}"""
            (fun manifestPath ->
                let targetManifest = Path.Combine(target, "conditor.json")
                File.Copy(manifestPath, targetManifest)

                match Manifest.load targetManifest with
                | Error _ ->
                    check "upgrade app binding baseline parses" false
                | Ok manifest ->
                    let lockPlan =
                        { ProjectName = manifest.Name
                          Operation = Init
                          Components = []
                          Actions = [] }

                    LockFile.write target targetManifest lockPlan |> ignore

                    File.WriteAllText(
                        targetManifest,
                        """{"schemaVersion":1,"name":"upgrade-app-binding","components":[{"id":"forma","version":"0.3.0"}],"requirements":[],"execution":{"enabled":false}}"""
                    )

                    match Manifest.load targetManifest with
                    | Error _ ->
                        check "upgrade app binding changed manifest parses" false
                    | Ok changedManifest ->
                        match Upgrade.apply target targetManifest changedManifest with
                        | Error errors ->
                            check
                                "upgrade rejects application-bound version changes"
                                (errors |> List.exists (fun error -> error.Contains("application binding")))
                        | Ok _ ->
                            check "upgrade rejects application-bound version changes" false))

withManifest
    """{"schemaVersion":1,"name":"upgrade-plan","components":[{"id":"praxis","version":"3.1.4"}],"requirements":[],"execution":{"enabled":false}}"""
    (fun path ->
        match Manifest.load path with
        | Error _ ->
            check "upgrade planner manifest parses" false
        | Ok manifest ->
            match Planner.create "/tmp/upgrade-plan" Upgrade manifest with
            | Error errors ->
                let details = String.concat "; " errors
                check $"upgrade planner succeeds: {details}" false
            | Ok plan ->
                check
                    "upgrade planner emits lifecycle upgrade and verify"
                    (plan.Actions.Length = 2
                     && plan.Actions[0].Kind = UpgradeLifecycle
                     && plan.Actions[1].Kind = VerifyLifecycle))


withTarget
    (fun target ->
        withManifest
            """{"schemaVersion":1,"name":"source-lock-demo","components":[{"id":"praxis","version":"3.1.4"}],"requirements":[],"execution":{"enabled":false}}"""
            (fun manifestPath ->
                let targetManifest = Path.Combine(target, "conditor.json")
                File.Copy(manifestPath, targetManifest)

                match Manifest.load targetManifest with
                | Error errors ->
                    let details = String.concat "; " errors
                    check $"source lock manifest parses: {details}" false
                | Ok manifest ->
                    match Planner.create target Init manifest with
                    | Error errors ->
                        let details = String.concat "; " errors
                        check $"source lock plan succeeds: {details}" false
                    | Ok plan ->
                        LockFile.write target targetManifest plan |> ignore

                        check
                            "locked component identity verifies when unchanged"
                            (LockFile.verifyResolvedComponents target plan.Components |> Result.isOk)

                        let drifted =
                            plan.Components
                            |> List.map (fun resolved ->
                                if resolved.Id = "praxis" then
                                    { resolved with SourceReference = Some "different-source" }
                                else
                                    resolved)

                        match LockFile.verifyResolvedComponents target drifted with
                        | Error errors ->
                            check
                                "locked component identity rejects source drift"
                                (errors |> List.exists (fun error -> error.Contains("silently substitute")))
                        | Ok() ->
                            check "locked component identity rejects source drift" false

                        match Registry.descriptorSha256 "praxis" with
                        | None ->
                            check "locked component identity rejects descriptor drift" false
                        | Some descriptorSha ->
                            let lockPath = Path.Combine(target, ".conditor", "lock.json")
                            let lockText = File.ReadAllText lockPath
                            let differentSha = String.replicate 64 "0"
                            File.WriteAllText(lockPath, lockText.Replace(descriptorSha, differentSha))

                            match LockFile.verifyResolvedComponents target plan.Components with
                            | Error errors ->
                                check
                                    "locked component identity rejects descriptor drift"
                                    (errors |> List.exists (fun error -> error.Contains("descriptor")))
                            | Ok() ->
                                check "locked component identity rejects descriptor drift" false))


withManifest
    """{"schemaVersion":1,"name":"bad-launcher","components":[],"execution":{"enabled":true,"launcher":"mystery","contractPath":"requirements/contract.md"}}"""
    (fun path ->
        match Manifest.load path with
        | Error errors ->
            check
                "unsupported execution launcher rejected during manifest load"
                (errors |> List.exists (fun error -> error.Contains("Unsupported execution launcher")))
        | Ok _ ->
            check "unsupported execution launcher rejected during manifest load" false)

withManifest
    """{"schemaVersion":1,"name":"missing-contract","components":[],"execution":{"enabled":true,"launcher":"codex"}}"""
    (fun path ->
        match Manifest.load path with
        | Error errors ->
            check
                "enabled execution requires canonical contract during manifest load"
                (errors |> List.exists (fun error -> error.Contains("execution.contractPath")))
        | Ok _ ->
            check "enabled execution requires canonical contract during manifest load" false)


withManifest
    """{"schemaVersion":1,"name":"unsupported-version","components":[{"id":"praxis","version":"99.0.0"}],"requirements":[],"execution":{"enabled":false}}"""
    (fun path ->
        match Manifest.load path with
        | Error _ ->
            check "unsupported compatibility version manifest parses" false
        | Ok manifest ->
            match Planner.create "/tmp/compatibility-version" Init manifest with
            | Error errors ->
                check
                    "compatibility graph rejects unqualified component version"
                    (errors
                     |> List.exists (fun error ->
                         error.Contains("not qualified by this Conditor compatibility graph")))
            | Ok _ ->
                check "compatibility graph rejects unqualified component version" false)

withManifest
    """{"schemaVersion":1,"name":"missing-limen","components":[],"requirements":[],"execution":{"enabled":false},"scaffold":{"kind":"fsharp-limen-web"}}"""
    (fun path ->
        match Manifest.load path with
        | Error _ ->
            check "missing Limen compatibility manifest parses" false
        | Ok manifest ->
            match Planner.create "/tmp/compatibility-limen" Init manifest with
            | Error errors ->
                check
                    "fsharp-limen-web requires Limen"
                    (errors |> List.exists (fun error -> error.Contains("requires component 'limen'")))
            | Ok _ ->
                check "fsharp-limen-web requires Limen" false)

withManifest
    """{"schemaVersion":1,"name":"missing-praxis","components":[],"requirements":[{"id":"contract","source":{"repository":"kemiller2002/communication-engineering","commit":"4590d2fe6f7e80b339117d3fbee5803f2dd39122","path":"README.md"},"targetPath":"requirements/contract.md"}],"execution":{"enabled":true,"launcher":"codex","contractPath":"requirements/contract.md"}}"""
    (fun path ->
        match Manifest.load path with
        | Error _ ->
            check "missing Praxis compatibility manifest parses" false
        | Ok manifest ->
            match Planner.create "/tmp/compatibility-praxis" Init manifest with
            | Error errors ->
                check
                    "enabled execution requires Praxis through compatibility graph"
                    (errors |> List.exists (fun error -> error.Contains("requires component 'praxis'")))
            | Ok _ ->
                check "enabled execution requires Praxis through compatibility graph" false)


withTarget
    (fun target ->
        withManifest
            """{"schemaVersion":1,"name":"doctor-demo","components":[],"requirements":[],"execution":{"enabled":false}}"""
            (fun manifestPath ->
                let targetManifest = Path.Combine(target, "conditor.json")
                File.Copy(manifestPath, targetManifest)

                match Manifest.load targetManifest with
                | Error errors ->
                    let details = String.concat "; " errors
                    check $"doctor manifest parses: {details}" false
                | Ok manifest ->
                    let lockPlan =
                        { ProjectName = manifest.Name
                          Operation = Init
                          Components = []
                          Actions = [] }

                    LockFile.write target targetManifest lockPlan |> ignore

                    let report = Doctor.inspect target targetManifest manifest
                    check "doctor reports healthy locked project" report.Healthy
                    check
                        "doctor exposes stable lock finding code"
                        (report.Findings
                         |> List.exists (fun finding ->
                             finding.Code = "COND-DOC-LOCK"
                             && finding.Severity = Doctor.Info))

                    File.AppendAllText(targetManifest, Environment.NewLine)

                    match Manifest.load targetManifest with
                    | Error _ ->
                        check "doctor drifted manifest still parses" false
                    | Ok drifted ->
                        let driftReport = Doctor.inspect target targetManifest drifted

                        check "doctor detects lock drift" (not driftReport.Healthy)
                        check
                            "doctor lock drift includes remediation"
                            (driftReport.Findings
                             |> List.exists (fun finding ->
                                 finding.Code = "COND-DOC-LOCK"
                                 && finding.Severity = Doctor.Error
                                 && finding.Remediation.IsSome))))


match ComponentDescriptors.loadAll () with
| Error errors ->
    let details = String.concat "; " errors
    check $"embedded component descriptors load: {details}" false
| Ok descriptors ->
    let ids = descriptors |> List.map (fun descriptor -> descriptor.Definition.Id) |> Set.ofList
    let expected =
        Set.ofList
            [ "praxis"
              "ordo"
              "visual-engineering"
              "communication-engineering"
              "limen"
              "forma"
              "folio"
              "aegis"
              "tutela" ]

    check "embedded component descriptor count" (descriptors.Length = 9)
    check "embedded component descriptor ids" (ids = expected)
    check "registry is projected from component descriptors" (Registry.all.Length = descriptors.Length)

    let compatibilityMatchesDescriptors =
        descriptors
        |> List.forall (fun descriptor ->
            Registry.qualifiedVersions descriptor.Definition.Id = Some descriptor.QualifiedVersions
            && (Compatibility.supported
                |> List.tryFind (fun item -> item.Id = descriptor.Definition.Id)
                |> Option.exists (fun item -> item.Versions = descriptor.QualifiedVersions)))

    check "compatibility versions are descriptor-derived" compatibilityMatchesDescriptors

    let communication =
        descriptors
        |> List.tryFind (fun descriptor -> descriptor.Definition.Id = "communication-engineering")

    check
        "communication descriptor preserves immutable source identity"
        (communication
         |> Option.exists (fun descriptor ->
             match descriptor.Definition.LifecycleSource with
             | Some(GitHubSource source) ->
                 source.Repository = "kemiller2002/communication-engineering"
                 && source.Commit = "4590d2fe6f7e80b339117d3fbee5803f2dd39122"
                 && source.Entrypoint = NodeScript "bin/communication-engineering.mjs"
             | _ -> false))

    let tutela =
        descriptors
        |> List.tryFind (fun descriptor -> descriptor.Definition.Id = "tutela")

    check
        "Tutela descriptor preserves immutable source identity"
        (tutela
         |> Option.exists (fun descriptor ->
             match descriptor.Definition.LifecycleSource with
             | Some(GitHubSource source) ->
                 source.Repository = "kemiller2002/tutela"
                 && source.Commit = "1acf421e7d940665c134012f11b082a502e17537"
                 && source.Entrypoint = NodeScript "bin/tutela.mjs"
             | _ -> false))


withTarget
    (fun target ->
        let queueDirectory = Path.Combine(target, ".ros", "work")
        let contextDirectory = Path.Combine(target, ".ros", "context")
        Directory.CreateDirectory queueDirectory |> ignore
        Directory.CreateDirectory contextDirectory |> ignore

        File.WriteAllText(
            Path.Combine(queueDirectory, "queue.json"),
            """{
  "items": [
    {
      "id": "COND-MISSION-001",
      "title": "Build the governed application.",
      "description": "Build the governed application.",
      "status": "ready",
      "source": "conditor",
      "sourceReference": "requirements/contract.md"
    }
  ]
}"""
        )

        File.WriteAllText(
            Path.Combine(contextDirectory, "current.json"),
            """{
  "workItems": [
    {
      "id": "COND-MISSION-001",
      "state": "active",
      "semanticState": "active"
    }
  ]
}"""
        )

        let mission =
            { Id = "COND-MISSION-001"
              Title = "Build the governed application."
              Description = "Build the governed application."
              ContractPath = "requirements/contract.md" }

        check
            "Praxis active execution context overrides ready backlog projection"
            (Mission.launchState target mission = Ok "active"))


// --- Praxis provenance readiness (CON-128, CON-129) -------------------------

let provenanceFixture = Path.Combine(AppContext.BaseDirectory, "fixtures", "praxis-provenance")

let sha256File (path: string) =
    File.ReadAllBytes path
    |> System.Security.Cryptography.SHA256.HashData
    |> Convert.ToHexString
    |> fun value -> value.ToLowerInvariant()

do
    use source = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(provenanceFixture, "SOURCE.json")))
    let files = source.RootElement.GetProperty("files")

    check
        "vendored Praxis fixture records the contract commit"
        (source.RootElement.GetProperty("commit").GetString() = "c2657efb4d54f11d0fd0617cc1bcd5b8418601d5")

    for file in files.EnumerateObject() do
        let expected = file.Value.GetProperty("sha256").GetString() |> Option.ofObj |> Option.defaultValue String.Empty
        check $"vendored Praxis fixture {file.Name} is unchanged (SHA-256)" (sha256File (Path.Combine(provenanceFixture, file.Name)) = expected)

let withProvenanceTarget (includeProvenance: bool) test =
    withTarget (fun target ->
        if includeProvenance then
            File.Copy(Path.Combine(provenanceFixture, "ros.json"), Path.Combine(target, "ros.json"))
            File.Copy(Path.Combine(provenanceFixture, "AGENTS.md"), Path.Combine(target, "AGENTS.md"))
            Directory.CreateDirectory(Path.Combine(target, "docs")) |> ignore
            File.WriteAllText(Path.Combine(target, "docs", "agent-provenance.md"), "# Agent provenance\n")
        else
            // Shape of a Praxis 3.1.4 install: no provenance policy or guidance.
            File.WriteAllText(Path.Combine(target, "ros.json"), """{"name":"demo","workProtocol":{"version":"1.0.0"}}""")
            File.WriteAllText(Path.Combine(target, "AGENTS.md"), "# Agent Startup Guide\n\n## Work Protocol\n")

        test target)

match PraxisProvenance.praxisCapabilities.Force() with
| Error errors ->
    let details = String.concat "; " errors
    check $"embedded Praxis capability table loads: {details}" false
| Ok table ->
    check "capability table declares provenance" (table.ContainsKey PraxisProvenance.CapabilityName)
    check
        "provenance has no qualified Praxis release yet (unpublished)"
        (table[PraxisProvenance.CapabilityName] = None && PraxisProvenance.provenanceMinimumVersion () = None)

check "qualified Praxis 3.1.4 does not support provenance" (not (PraxisProvenance.supports None (Some "3.1.4")))
check "version at threshold supports" (PraxisProvenance.supports (Some "3.2.0") (Some "3.2.0"))
check "version below threshold does not support" (not (PraxisProvenance.supports (Some "3.2.0") (Some "3.1.4")))
check "versions compare numerically" (PraxisProvenance.supports (Some "3.2.0") (Some "3.10"))
check "version with a trailing newline never counts as supporting" (not (PraxisProvenance.supports (Some "3.2.0") (Some "3.2.0\n")))
check "prerelease never counts as supporting" (not (PraxisProvenance.supports (Some "3.2.0") (Some "3.3.0-beta.1")))
check "unknown installed version never supports" (not (PraxisProvenance.supports (Some "3.2.0") None))

check
    "capability table rejects a non-version threshold"
    (PraxisProvenance.parseCapabilities """{"schemaVersion":1,"capabilities":{"provenance":{"minimumVersion":"latest"}}}"""
     |> Result.isError)

let parsedThreshold =
    PraxisProvenance.parseCapabilities """{"schemaVersion":1,"capabilities":{"provenance":{"minimumVersion":"3.2.0"}}}"""

check "capability threshold becomes data-driven once set" (parsedThreshold = Ok(Map.ofList [ "provenance", Some "3.2.0" ]))

withProvenanceTarget true (fun target ->
    let observation = PraxisProvenance.observe target
    let current = PraxisProvenance.assess None (Some "3.1.4") observation
    let future = PraxisProvenance.assess (Some "3.2.0") (Some "3.2.0") observation

    check "Praxis starter policy is enforced" (PraxisProvenance.evaluatePolicy observation.RosJson = PraxisProvenance.PolicyEnforced "2026-09-25")
    check "Praxis AGENTS.md carries provenance guidance" (PraxisProvenance.hasAgentGuidance observation.AgentsMarkdown)
    check "provenance-enabled repository reports enabled" (current.Status = PraxisProvenance.Enabled && current.Missing.IsEmpty)
    check "provenance-enabled repository at a supporting version reports enabled" (future.Status = PraxisProvenance.Enabled)

    match PraxisProvenance.inspect target [ { Id = "praxis"; Version = "3.1.4"; Distribution = LifecycleNpm; Package = "p"; SourceReference = None } ] with
    | Some readiness -> check "inspect reads the target and plan version" (readiness.Status = PraxisProvenance.Enabled && readiness.InstalledVersion = Some "3.1.4")
    | None -> check "inspect reads the target and plan version" false

    check
        "inspect skips plans without Praxis"
        (PraxisProvenance.inspect target [ { Id = "ordo"; Version = "1.3.0"; Distribution = LifecycleNpm; Package = "o"; SourceReference = None } ] = None)

    File.WriteAllText(Path.Combine(target, "ros.json"), """{"provenance":{"version":"1.0.0","enforce":false,"requiredFrom":"2026-09-25"}}""")
    let unenforced = PraxisProvenance.assess (Some "3.2.0") (Some "3.2.0") (PraxisProvenance.observe target)
    check
        "unenforced policy at a supporting version is missing-when-expected"
        (unenforced.Status = PraxisProvenance.MissingWhenExpected && unenforced.Missing.Length = 1)

    File.WriteAllText(Path.Combine(target, "ros.json"), """{"provenance":{"enforce":true}}""")
    check
        "enforced policy without requiredFrom is invalid"
        (match PraxisProvenance.evaluatePolicy (Some(File.ReadAllText(Path.Combine(target, "ros.json")))) with
         | PraxisProvenance.PolicyInvalid _ -> true
         | _ -> false))

withProvenanceTarget false (fun target ->
    let observation = PraxisProvenance.observe target
    let current = PraxisProvenance.assess (PraxisProvenance.provenanceMinimumVersion ()) (Some "3.1.4") observation
    let expected = PraxisProvenance.assess (Some "3.2.0") (Some "3.2.0") observation

    check "Praxis 3.1.4 install reports not-supported-by-installed-version" (current.Status = PraxisProvenance.NotSupportedByInstalledVersion)
    check
        "not-supported diagnostic names the version and is not silent"
        (current.Detail.Contains "3.1.4" && current.Detail.Contains "no qualified Praxis release provides provenance yet")
    check "supporting version without provenance reports missing-when-expected" (expected.Status = PraxisProvenance.MissingWhenExpected)
    check "missing-when-expected lists policy, guidance, and documentation" (expected.Missing.Length = 3)

    let warning = Doctor.provenanceFinding current
    let error = Doctor.provenanceFinding expected
    check "doctor reports unsupported provenance as a warning" (warning.Severity = Doctor.Warning && warning.Code = "COND-DOC-PROVENANCE" && warning.Remediation.IsSome)
    check "doctor reports missing provenance as an error" (error.Severity = Doctor.Error))

let guidanceVariants =
    [ "exact Praxis heading", "# Guide\n\n## Agent Identity and Provenance\n\nText.\n", true
      "reworded heading", "# Guide\n\n### Agent identity & provenance rules\n", true
      "CRLF heading", "# Guide\r\n\r\n## Identity and Provenance\r\n", true
      "stable marker", "# Guide\n\n<!-- praxis:agent-identity-provenance -->\n## Who you are\n", true
      "key phrases", "# Guide\n\nDeclare ROS_ACTOR_KIND; see docs/agent-provenance.md.\n", true
      "no guidance", "# Guide\n\n## Work Protocol\n\nRecord provenance of records.\n", false
      "provenance document only", "# Guide\n\nSee docs/agent-provenance.md.\n", false ]

for name, text, expected in guidanceVariants do
    check $"agent guidance detection tolerates wording: {name}" (PraxisProvenance.hasAgentGuidance (Some text) = expected)

withProvenanceTarget true (fun target ->
    File.WriteAllText(
        Path.Combine(target, "AGENTS.md"),
        File.ReadAllText(Path.Combine(target, "AGENTS.md")).Replace("## Agent Identity and Provenance", "## Agent identity & provenance")
    )

    let reworded = PraxisProvenance.assess (Some "3.2.0") (Some "3.2.0") (PraxisProvenance.observe target)
    check "reworded Praxis heading does not fail verification at a supporting version" (reworded.Status = PraxisProvenance.Enabled))

withTarget (fun target ->
    let readiness = PraxisProvenance.assess None (Some "3.1.4") (PraxisProvenance.observe target)
    check "empty target without Praxis files is not-supported for 3.1.4" (readiness.Status = PraxisProvenance.NotSupportedByInstalledVersion)
    check "doctor reports enabled provenance as info" ((Doctor.provenanceFinding { readiness with Status = PraxisProvenance.Enabled }).Severity = Doctor.Info))

// --- Identity propagation (CON-130, CON-131) --------------------------------

let secretLike (name: string, value: string option) =
    let upper = name.ToUpperInvariant()
    upper.Contains "KEY" || upper.Contains "TOKEN" || upper.Contains "SECRET" || upper.Contains "PASSWORD"
    || upper.Contains "CREDENTIAL" || upper.StartsWith "OPENAI_" || upper.StartsWith "ANTHROPIC_"
    || (value |> Option.exists (fun text -> text.StartsWith "sk-" || text.StartsWith "ghp_"))

let ambient =
    Map.ofList
        [ "PATH", "/usr/bin"
          "OPENAI_API_KEY", "sk-test-not-a-real-key"
          "ROS_ACTOR", "conditor"
          "ROS_ACTOR_KIND", "automation"
          "ROS_EXECUTION_ID", "EXE-20260926T000000000Z-deadbeef"
          "ROS_TELEMETRY_MODEL", "some-outer-model"
          "ROS_TELEMETRY_SESSION_ID", "outer-session"
          "CLAUDE_CODE_SESSION_ID", "operator-claude-session"
          "CODEX_SESSION_ID", "outer-codex-session"
          "CODEX_THREAD_ID", "outer-thread"
          "GEMINI_SESSION_ID", "outer-gemini"
          "COPILOT_SESSION_ID", "outer-copilot"
          "GITHUB_ACTIONS", "true"
          "GITHUB_RUN_ID", "ci-run-77"
          "OLLAMA_HOST", "127.0.0.1:11434" ]

let identityFixture =
    use document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(provenanceFixture, "identity-environment.json")))
    document.RootElement.GetProperty("variables").EnumerateArray()
    |> Seq.map (fun item -> item.GetString() |> Option.ofObj |> Option.defaultValue "")
    |> List.ofSeq

check "Conditor clears exactly the Praxis identity-environment variables" (AgentIdentity.inheritedIdentityVariables = identityFixture)

do
    let conditor = AgentIdentity.apply AgentIdentity.conditorRosEnvironment ambient
    check "Conditor ros calls declare automation" (conditor.TryFind "ROS_ACTOR_KIND" = Some "automation")
    check "Conditor ros calls declare the conditor actor" (conditor.TryFind "ROS_ACTOR" = Some "conditor")
    check "Conditor ros calls declare the conditor runtime" (conditor.TryFind "ROS_TELEMETRY_RUNTIME" = Some "conditor")
    check "Conditor ros calls do not inherit an outer execution or model" (not (conditor.ContainsKey "ROS_EXECUTION_ID") && not (conditor.ContainsKey "ROS_TELEMETRY_MODEL"))
    check "Conditor ros environment adds no secrets" (AgentIdentity.conditorRosEnvironment |> List.forall (secretLike >> not))

    let conditorExplicit = Map [ "ROS_ACTOR_KIND", "automation"; "ROS_ACTOR", "conditor"; "ROS_TELEMETRY_RUNTIME", "conditor" ]
    check
        "Conditor ros calls carry no inherited session, CI run, or local-model signal"
        (identityFixture |> List.forall (fun name -> conditor.TryFind name = conditorExplicit.TryFind name))

    for launcher, provider, runtime in [ "codex", "openai", "codex"; "claude", "anthropic", "claude-code" ] do
        let changes = AgentIdentity.agentEnvironment launcher None
        let child = AgentIdentity.apply changes ambient
        check $"{launcher} agent is declared as an agent" (child.TryFind "ROS_ACTOR_KIND" = Some "agent")
        check $"{launcher} agent carries its provider and runtime" (child.TryFind "ROS_TELEMETRY_PROVIDER" = Some provider && child.TryFind "ROS_TELEMETRY_RUNTIME" = Some runtime)
        check $"{launcher} agent never receives Conditor's identity" (not (child.ContainsKey "ROS_ACTOR") && (changes |> List.forall (fun (_, value) -> value <> Some "conditor" && value <> Some "automation")))
        check $"{launcher} agent does not inherit an outer execution or session" (not (child.ContainsKey "ROS_EXECUTION_ID") && not (child.ContainsKey "ROS_TELEMETRY_SESSION_ID"))
        check $"{launcher} agent model is unknown unless configured" (not (child.ContainsKey "ROS_TELEMETRY_MODEL"))
        check $"{launcher} agent environment adds no secrets" (changes |> List.forall (secretLike >> not))
        check $"{launcher} agent keeps inherited launcher credentials untouched" (child.TryFind "OPENAI_API_KEY" = ambient.TryFind "OPENAI_API_KEY")

        let agentExplicit =
            Map [ "ROS_ACTOR_KIND", "agent"; "ROS_TELEMETRY_PROVIDER", provider; "ROS_TELEMETRY_RUNTIME", runtime ]
        check
            $"{launcher} agent receives no identity variable except its explicit declaration"
            (identityFixture |> List.forall (fun name -> child.TryFind name = agentExplicit.TryFind name))

    let configured = AgentIdentity.apply (AgentIdentity.agentEnvironment "claude" (Some "claude-opus-4-1")) ambient
    check "configured model is declared" (configured.TryFind "ROS_TELEMETRY_MODEL" = Some "claude-opus-4-1")
    check "blank model is not declared" (not ((AgentIdentity.apply (AgentIdentity.agentEnvironment "codex" (Some "  ")) ambient).ContainsKey "ROS_TELEMETRY_MODEL"))

    let unknown = AgentIdentity.apply (AgentIdentity.agentEnvironment "other" None) ambient
    check "unknown launcher gets no fabricated provider or runtime" (unknown.TryFind "ROS_ACTOR_KIND" = Some "agent" && not (unknown.ContainsKey "ROS_TELEMETRY_PROVIDER") && not (unknown.ContainsKey "ROS_TELEMETRY_RUNTIME"))

    check "codex command line is unchanged without a model" (Launcher.commandLine "codex" None "go" = Some("codex", [ "exec"; "--full-auto"; "go" ]))
    check "claude command line is unchanged without a model" (Launcher.commandLine "claude" None "go" = Some("claude", [ "-p"; "--output-format"; "text"; "go" ]))
    check "configured model is passed to the launched CLI" (Launcher.commandLine "codex" (Some "gpt-5-codex") "go" = Some("codex", [ "exec"; "--full-auto"; "--model"; "gpt-5-codex"; "go" ]))

withManifest
    """{"schemaVersion":1,"name":"demo","components":[{"id":"praxis"}],"execution":{"enabled":false,"launcher":"claude","model":"claude-opus-4-1"}}"""
    (fun path ->
        match Manifest.load path with
        | Ok manifest -> check "execution.model parses" (manifest.Execution |> Option.bind _.Model = Some "claude-opus-4-1")
        | Error _ -> check "execution.model parses" false)

withManifest
    """{"schemaVersion":1,"name":"demo","components":[{"id":"praxis"}],"execution":{"enabled":false,"launcher":"claude","model":"not a model; rm -rf"}}"""
    (fun path -> check "invalid execution.model is refused" (Manifest.load path |> Result.isError))

let exitCode =
    if failures = 0 then
        Console.WriteLine "All Conditor tests passed."
        0
    else
        Console.Error.WriteLine $"{failures} Conditor test(s) failed."
        1

[<EntryPoint>]
let main _ = exitCode
