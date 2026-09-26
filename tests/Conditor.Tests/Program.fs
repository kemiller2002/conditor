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

// CON-065 / CON-066: Conditor's own Praxis transitions are declared automation,
// and establishing the mission never starts an execution on the agent's behalf.
withTarget
    (fun target ->
        let logPath = Path.Combine(target, "ros-invocations.log")

        File.WriteAllText(
            Path.Combine(target, "ros"),
            "const fs = require('fs');\n"
            + "fs.appendFileSync(" + System.Text.Json.JsonSerializer.Serialize(logPath) + ", JSON.stringify(process.argv.slice(2)) + '\\n');\n"
        )

        let queueDirectory = Path.Combine(target, ".ros", "work")
        Directory.CreateDirectory queueDirectory |> ignore
        File.WriteAllText(Path.Combine(queueDirectory, "queue.json"), """{"items":[]}""")

        let mission =
            { Id = "COND-MISSION-001"
              Title = "Build demo from the Conditor execution contract"
              Description = "Build the governed application."
              ContractPath = "requirements/contract.md" }

        let captured = Mission.captureArguments mission "2026-09-26T00:00:00Z"
        let pairs = captured |> List.pairwise |> Set.ofList

        check "mission capture declares automation actor kind" (pairs.Contains("--actor-kind", "automation"))
        check "mission capture declares conditor actor" (pairs.Contains("--actor", "conditor"))
        check "mission capture never declares an agent" (not (captured |> List.contains "agent"))

        match Mission.ensure target mission with
        | Error errors ->
            let details = String.concat "; " errors
            check $"mission ensure runs against recording ros launcher: {details}" false
        | Ok() ->
            let invocations =
                File.ReadAllLines logPath
                |> Array.map (fun line ->
                    System.Text.Json.JsonSerializer.Deserialize<string array> line
                    |> Option.ofObj
                    |> Option.map List.ofArray
                    |> Option.defaultValue [])
                |> List.ofArray

            let subcommands =
                invocations |> List.map (fun arguments -> arguments |> List.truncate 2 |> String.concat " ")

            check "mission establishment captures then marks ready" (subcommands = [ "work capture"; "work backlog-transition" ])
            check
                "mission establishment never starts or begins an execution"
                (subcommands |> List.forall (fun command -> command <> "work start" && command <> "work begin"))
            check
                "recorded capture carries automation identity"
                (invocations
                 |> List.head
                 |> List.pairwise
                 |> List.contains ("--actor-kind", "automation")))

// CON-067: the launch instruction tells the agent to own its execution and provenance.
let readyExecution state =
    ({ Mission =
        { Id = "COND-MISSION-001"
          Title = "Build demo"
          Description = "Build the governed application."
          ContractPath = "requirements/contract.md" }
       Launcher = "claude"
       ContractPath = "requirements/contract.md"
       MissionState = state }: Readiness.ReadyExecution)

let readyInstruction = Launcher.instruction (readyExecution "ready")
let activeInstruction = Launcher.instruction (readyExecution "active")

let contains (text: string) (value: string) = text.Contains(value, StringComparison.Ordinal)

check "ready instruction tells agent to begin its own execution" (contains readyInstruction "./ros work start --id COND-MISSION-001")
check "ready instruction says Conditor did not start the mission" (contains readyInstruction "did not start it for you")
check "instruction asks agent to check provenance identity" (contains readyInstruction "./ros provenance identity")
check "instruction asks agent to record provenance" (contains readyInstruction "./ros provenance record --path")
check "instruction forbids invented model identity" (contains readyInstruction "never invent a provider, model, or runtime")
check "instruction keeps completion evidence rule" (contains readyInstruction "Do not treat your own statement that the work is finished as completion")
check "active instruction does not ask for a second start" (not (contains activeInstruction "./ros work start"))
check "active instruction forbids recording into another run's execution" (contains activeInstruction "Do not record your work into another run's execution")
check "active instruction keeps provenance guidance" (contains activeInstruction "./ros provenance record --path")

// CON-068: descriptor capability parsing and consistency.
let descriptorJson (capabilities: string) (qualified: string) =
    $$"""{"schemaVersion":1,"id":"praxis-test","displayName":"Praxis test","distribution":"lifecycle-npm","package":"@example/ros","defaultVersion":"3.1.4","qualifiedVersions":[{{qualified}}],"lifecycleSource":{"kind":"registry"},"command":"ros","initArguments":["init"],"verifyArguments":["verify"],"doctorArguments":["doctor"],"upgradeArguments":["upgrade"]{{capabilities}}}"""

match ComponentDescriptors.parse "test" (descriptorJson ""","capabilities":{"provenance":{"since":"3.5.0","status":"unreleased"}}""" "\"3.1.4\"") with
| Ok descriptor ->
    check
        "descriptor parses provenance capability"
        (descriptor.Capabilities = [ { Name = "provenance"; Since = "3.5.0"; Status = CapabilityUnreleased } ])
| Error errors ->
    let details = String.concat "; " errors
    check $"descriptor parses provenance capability: {details}" false

check
    "descriptor without capabilities parses with none"
    (ComponentDescriptors.parse "test" (descriptorJson "" "\"3.1.4\"")
     |> Result.map _.Capabilities = Ok [])

check
    "descriptor rejects unknown capability status"
    (ComponentDescriptors.parse "test" (descriptorJson ""","capabilities":{"provenance":{"since":"3.5.0","status":"soon"}}""" "\"3.1.4\"")
     |> Result.isError)

check
    "descriptor rejects capability without since"
    (ComponentDescriptors.parse "test" (descriptorJson ""","capabilities":{"provenance":{"status":"released"}}""" "\"3.1.4\"")
     |> Result.isError)

check
    "descriptor refuses an unreleased capability satisfied by a qualified version"
    (match ComponentDescriptors.parse "test" (descriptorJson ""","capabilities":{"provenance":{"since":"3.5.0","status":"unreleased"}}""" "\"3.1.4\",\"3.5.0\"") with
     | Error errors -> errors |> List.exists (fun error -> contains error "still marked unreleased")
     | Ok _ -> false)

check
    "embedded Praxis descriptor declares the first provenance-capable version"
    (Registry.descriptors
     |> List.tryFind (fun descriptor -> descriptor.Definition.Id = "praxis")
     |> Option.exists (fun descriptor ->
         descriptor.Capabilities
         |> List.exists (fun capability -> capability.Name = PraxisProvenance.CapabilityName && capability.Since = "3.5.0")))

// CON-068: version gate diagnostics.
let provenanceCapability status =
    [ { Name = "provenance"; Since = "3.5.0"; Status = status } ]

check "Praxis 3.1.4 predates provenance" (PraxisProvenance.gate (provenanceCapability CapabilityUnreleased) "3.1.4" = PraxisProvenance.Predates("3.5.0", CapabilityUnreleased))
check "Praxis 3.5.0 is provenance-capable" (PraxisProvenance.gate (provenanceCapability CapabilityReleased) "3.5.0" = PraxisProvenance.Capable "3.5.0")
check "Praxis 3.10.0 is provenance-capable" (PraxisProvenance.gate (provenanceCapability CapabilityReleased) "3.10.0" = PraxisProvenance.Capable "3.5.0")
check "Praxis 3.5.0 prerelease predates provenance" (PraxisProvenance.gate (provenanceCapability CapabilityReleased) "3.5.0-rc.1" = PraxisProvenance.Predates("3.5.0", CapabilityReleased))
check "undeclared capability produces no gate" (PraxisProvenance.gate [] "3.1.4" = PraxisProvenance.NotDeclared)
check "non-semantic version is incomparable" (PraxisProvenance.gate (provenanceCapability CapabilityReleased) "latest" = PraxisProvenance.Incomparable "3.5.0")
check "capable version produces no diagnostic" (PraxisProvenance.diagnostic "3.5.0" (PraxisProvenance.Capable "3.5.0") = None)
check
    "incomparable version produces a warning"
    (PraxisProvenance.diagnostic "latest" (PraxisProvenance.Incomparable "3.5.0")
     |> Option.exists (fun text -> text.StartsWith("WARNING", StringComparison.Ordinal)))

withManifest
    """{"schemaVersion":1,"name":"demo","components":[{"id":"praxis","version":"3.1.4"}]}"""
    (fun path ->
        match Manifest.load path |> Result.bind (Planner.create "/tmp/demo" Init) with
        | Error errors ->
            let details = String.concat "; " errors
            check $"Praxis 3.1.4 plan still succeeds: {details}" false
        | Ok plan ->
            let diagnostics = PraxisProvenance.planDiagnostics plan

            check "Praxis 3.1.4 plan is not refused for lacking provenance" true
            check "Praxis 3.1.4 plan warns once about provenance" (diagnostics.Length = 1)
            check
                "Praxis 3.1.4 warning names versions and consequence"
                (diagnostics
                 |> List.exists (fun text ->
                     text.StartsWith("WARNING Praxis 3.1.4", StringComparison.Ordinal)
                     && contains text "3.5.0"
                     && contains text PraxisProvenance.AgentsSection))
            check
                "Praxis 3.1.4 plan does not schedule provenance verification"
                (plan.Actions |> List.forall (fun action -> action.Kind <> ProvenanceVerify)))

withManifest
    """{"schemaVersion":1,"name":"demo","components":[{"id":"ordo"}]}"""
    (fun path ->
        match Manifest.load path |> Result.bind (Planner.create "/tmp/demo" Init) with
        | Error _ -> check "plan without Praxis succeeds" false
        | Ok plan -> check "plan without Praxis has no provenance diagnostic" (PraxisProvenance.planDiagnostics plan = []))

// CON-069: installed-policy verification over temporary target repositories.
let provenanceRosJson enforce =
    """{"version":"1.0.0","provenance":{"version":"1.0.0","enforce":"""
    + enforce
    + ""","requiredFrom":"2026-09-25","requireOriginator":["RQ"]}}"""

let agentsWithSection = "# Agents\n\n## Agent Identity and Provenance\n\nRecord provenance.\n"

let verifyTargetWith (rosJson: string option) (agents: string option) =
    let mutable result = []

    withTarget (fun target ->
        rosJson |> Option.iter (fun text -> File.WriteAllText(Path.Combine(target, "ros.json"), text))
        agents |> Option.iter (fun text -> File.WriteAllText(Path.Combine(target, "AGENTS.md"), text))
        result <- PraxisProvenance.verifyTarget target)

    result

check "installed provenance policy and guidance verify" (verifyTargetWith (Some(provenanceRosJson "true")) (Some agentsWithSection) = [])
check "unenforced provenance policy fails verification" (verifyTargetWith (Some(provenanceRosJson "false")) (Some agentsWithSection) |> List.exists (fun error -> contains error "enforce"))
check "missing provenance policy fails verification" (verifyTargetWith (Some """{"version":"1.0.0"}""") (Some agentsWithSection) |> List.exists (fun error -> contains error "no \"provenance\" policy"))
check "missing ros.json fails verification" (verifyTargetWith None (Some agentsWithSection) |> List.exists (fun error -> contains error "ros.json is missing"))
check "invalid ros.json fails verification" (verifyTargetWith (Some "{not json") (Some agentsWithSection) |> List.exists (fun error -> contains error "not valid JSON"))
check "missing agent provenance section fails verification" (verifyTargetWith (Some(provenanceRosJson "true")) (Some "# Agents\n") |> List.exists (fun error -> contains error PraxisProvenance.AgentsSection))
check "missing AGENTS.md fails verification" (verifyTargetWith (Some(provenanceRosJson "true")) None |> List.exists (fun error -> contains error "AGENTS.md is missing"))

withTarget
    (fun target ->
        let plan =
            { ProjectName = "provenance-verify-test"
              Operation = Verify
              Components = []
              Actions =
                [ { Sequence = 1
                    ComponentId = "praxis:provenance"
                    ComponentVersion = "3.5.0"
                    Kind = ProvenanceVerify
                    Execution = VerifyPraxisProvenance } ] }

        check
            "installer stops on failed provenance verification"
            (match Installer.execute target (Path.Combine(target, "conditor.json")) plan with
             | Error errors -> errors |> List.exists (fun error -> contains error "praxis:provenance")
             | Ok _ -> false)

        File.WriteAllText(Path.Combine(target, "ros.json"), provenanceRosJson "true")
        File.WriteAllText(Path.Combine(target, "AGENTS.md"), agentsWithSection)

        check
            "installer passes provenance verification once Praxis installed the policy"
            (Installer.execute target (Path.Combine(target, "conditor.json")) plan = Ok None))

let exitCode =
    if failures = 0 then
        Console.WriteLine "All Conditor tests passed."
        0
    else
        Console.Error.WriteLine $"{failures} Conditor test(s) failed."
        1

[<EntryPoint>]
let main _ = exitCode
