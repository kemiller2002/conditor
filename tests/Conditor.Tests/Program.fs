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
                        check "scaffold plans seven project files" (plan.Actions |> List.filter (fun action -> action.Kind = ScaffoldFile) |> List.length = 7)
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
            """{"schemaVersion":1,"name":"conflict-demo","components":[],"scaffold":{"kind":"fsharp-limen-web"}}"""
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
                    (plan.Components[0].SourceReference = Some "@echelon-foundry/tutela@0.1.0"))

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
            """{"schemaVersion":1,"name":"contract-demo","components":[],"execution":{"enabled":false,"mission":"Build the governed app.","contractPath":".echelon/kickoff/project.json"},"scaffold":{"kind":"fsharp-limen-web"}}"""
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
                                | EnsureFile("AGENTS.md", content) -> Some content
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

let exitCode =
    if failures = 0 then
        Console.WriteLine "All Conditor tests passed."
        0
    else
        Console.Error.WriteLine $"{failures} Conditor test(s) failed."
        1

[<EntryPoint>]
let main _ = exitCode
