namespace Conditor.Core

open System
open System.IO

module Planner =
    let private npxExecutable () =
        if OperatingSystem.IsWindows() then "npx.cmd" else "npx"

    let private sourceReference version (definition: ComponentDefinition) =
        match definition.LifecycleSource with
        | Some RegistryPackage -> Ok $"{definition.Package}@{version}"
        | Some(GitHubSource source) when version = definition.DefaultVersion ->
            Ok(SourceCache.sourceReference source)
        | Some(GitHubSource _) ->
            Error
                $"Component '{definition.Id}' version '{version}' has no immutable distribution mapping. Available mapped version: '{definition.DefaultVersion}'."
        | None -> Error $"Lifecycle component '{definition.Id}' has no distribution source."

    let private bindingReference version (definition: ComponentDefinition) =
        match definition.ApplicationBinding with
        | Some NpmDependency -> Some $"npm:{definition.Package}@{version}"
        | Some NugetReference -> Some $"nuget:{definition.Package}@{version}"
        | None -> None

    let private replaceTarget target arguments =
        arguments
        |> List.map (fun argument -> if argument = "{target}" then target else argument)

    let private lifecycleExecution target version (definition: ComponentDefinition) arguments =
        let command =
            definition.Command
            |> Option.defaultWith (fun () -> invalidOp "Lifecycle component has no command.")

        let resolvedArguments = replaceTarget target arguments

        match definition.LifecycleSource with
        | Some RegistryPackage ->
            ExternalProcess(
                npxExecutable (),
                [ "--yes"
                  $"--package={definition.Package}@{version}"
                  command ]
                @ resolvedArguments
            )
        | Some(GitHubSource source) ->
            GitHubSourceProcess(source, resolvedArguments)
        | None ->
            invalidOp $"Lifecycle component '{definition.Id}' has no distribution source."

    let private actionKind operation phase =
        match operation, phase with
        | Init, "install" -> InstallLifecycle
        | Init, "verify"
        | Verify, "verify" -> VerifyLifecycle
        | Doctor, "doctor" -> DiagnoseLifecycle
        | _ -> invalidArg (nameof phase) $"Unsupported plan phase '{phase}'."

    let private validateRelativeTarget target relativePath =
        if String.IsNullOrWhiteSpace relativePath || Path.IsPathRooted relativePath then
            Error $"Requirement targetPath '{relativePath}' must be a repository-relative file path."
        else
            let root = Path.GetFullPath target
            let full = Path.GetFullPath(Path.Combine(root, relativePath))
            let rootPrefix =
                root.TrimEnd(Path.DirectorySeparatorChar) + string Path.DirectorySeparatorChar

            let comparison =
                if OperatingSystem.IsWindows() then
                    StringComparison.OrdinalIgnoreCase
                else
                    StringComparison.Ordinal

            if full.StartsWith(rootPrefix, comparison) then
                Ok()
            else
                Error $"Requirement targetPath '{relativePath}' escapes the target repository."

    let private validateRequirement target (requirement: RequirementSource) =
        let errors = ResizeArray<string>()

        SourceCache.validate requirement.Source
        |> List.iter (fun error -> errors.Add $"Requirement '{requirement.Id}': {error}")

        match requirement.Source.Entrypoint with
        | FileArtifact _ -> ()
        | NodeScript _ ->
            errors.Add $"Requirement '{requirement.Id}' must reference a file artifact."

        match validateRelativeTarget target requirement.TargetPath with
        | Ok() -> ()
        | Error error -> errors.Add error

        List.ofSeq errors

    let create target operation (manifest: ProjectManifest) : Result<InstallationPlan, string list> =
        let errors = ResizeArray<string>()
        let resolved = ResizeArray<ResolvedComponent>()
        let actions = ResizeArray<PlanAction>()
        let mutable sequence = 1

        for requirement in manifest.Requirements do
            validateRequirement target requirement |> List.iter errors.Add

        let plannedMission =
            manifest.Execution
            |> Option.bind (fun execution ->
                execution.ContractPath
                |> Option.filter (String.IsNullOrWhiteSpace >> not)
                |> Option.map (fun contractPath ->
                    let description =
                        execution.Mission
                        |> Option.filter (String.IsNullOrWhiteSpace >> not)
                        |> Option.defaultValue "Execute the canonical Conditor contract and prove completion through repository evidence."

                    { Id = "COND-MISSION-001"
                      Title = $"Build {manifest.Name} from the Conditor execution contract"
                      Description = description
                      ContractPath = contractPath }))

        match manifest.Execution |> Option.bind (fun execution -> execution.ContractPath) with
        | Some contractPath ->
            match validateRelativeTarget target contractPath with
            | Error error -> errors.Add $"Execution contractPath: {error}"
            | Ok() ->
                let materializedByManifest =
                    manifest.Requirements
                    |> List.exists (fun requirement -> requirement.TargetPath = contractPath)

                let alreadyExists =
                    Path.Combine(Path.GetFullPath target, contractPath)
                    |> Path.GetFullPath
                    |> File.Exists

                if not materializedByManifest && not alreadyExists then
                    errors.Add
                        $"Execution contractPath '{contractPath}' must already exist or match a requirements[].targetPath so agents are never handed a missing canonical contract."
        | None -> ()

        let addLifecycleAction
            (request: ComponentRequest)
            (version: string)
            (definition: ComponentDefinition)
            (phase: string)
            (arguments: string list)
            =
            actions.Add
                { Sequence = sequence
                  ComponentId = request.Id
                  ComponentVersion = version
                  Kind = actionKind operation phase
                  Execution = lifecycleExecution target version definition arguments }

            sequence <- sequence + 1

        let mutable limenReadiness: (string * ComponentDefinition) option = None

        for request in manifest.Components do
            match Registry.tryFind request.Id with
            | None ->
                if request.Required then
                    errors.Add $"Unknown required component '{request.Id}'."
            | Some definition ->
                let version = request.Version |> Option.defaultValue definition.DefaultVersion

                match definition.Distribution with
                | LifecycleNpm ->
                    match sourceReference version definition with
                    | Error error ->
                        if request.Required then
                            errors.Add error
                    | Ok resolvedSource ->
                        resolved.Add
                            { Id = definition.Id
                              Version = version
                              Distribution = definition.Distribution
                              Package = definition.Package
                              SourceReference =
                                match bindingReference version definition with
                                | Some binding -> Some $"{resolvedSource};{binding}"
                                | None -> Some resolvedSource }

                        match operation with
                        | Init ->
                            addLifecycleAction request version definition "install" definition.InitArguments
                            addLifecycleAction request version definition "verify" definition.VerifyArguments
                        | Verify ->
                            addLifecycleAction request version definition "verify" definition.VerifyArguments
                        | Doctor ->
                            addLifecycleAction request version definition "doctor" definition.DoctorArguments

                        if definition.Id = "limen" && manifest.Scaffold.IsSome then
                            limenReadiness <- Some(version, definition)
                | NpmPackage
                | NugetPackage ->
                    resolved.Add
                        { Id = definition.Id
                          Version = version
                          Distribution = definition.Distribution
                          Package = definition.Package
                          SourceReference = bindingReference version definition }

                    if request.Required && manifest.Scaffold.IsNone then
                        errors.Add
                            $"Component '{request.Id}' is an application dependency. A scaffold is required so Conditor knows exactly where to bind it."

        if errors.Count = 0 && operation = Init then
            match Scaffolding.plan target manifest with
            | Error scaffoldErrors ->
                scaffoldErrors |> List.iter errors.Add
            | Ok scaffoldFiles ->
                let scaffoldId =
                    manifest.Scaffold
                    |> Option.map (fun scaffold -> $"scaffold:{scaffold.Kind}")
                    |> Option.defaultValue "scaffold"

                for relativePath, fileContent in scaffoldFiles do
                    actions.Add
                        { Sequence = sequence
                          ComponentId = scaffoldId
                          ComponentVersion = "1"
                          Kind = ScaffoldFile
                          Execution = EnsureFile(relativePath, fileContent) }

                    sequence <- sequence + 1

        if errors.Count = 0 && operation = Init then
            for requirement in manifest.Requirements do
                actions.Add
                    { Sequence = sequence
                      ComponentId = $"requirements:{requirement.Id}"
                      ComponentVersion = requirement.Source.Commit
                      Kind = RequirementFile
                      Execution = MaterializeSourceFile(requirement.Source, requirement.TargetPath) }

                sequence <- sequence + 1

        if errors.Count = 0 then
            match limenReadiness, operation with
            | Some(version, definition), (Init | Verify) ->
                actions.Add
                    { Sequence = sequence
                      ComponentId = "limen"
                      ComponentVersion = version
                      Kind = ReadinessVerify
                      Execution = lifecycleExecution target version definition [ "verify"; "--strict" ] }

                sequence <- sequence + 1
            | _ -> ()

        if errors.Count = 0 && operation = Init then
            match plannedMission with
            | None -> ()
            | Some mission ->
                match resolved |> Seq.tryFind (fun component -> component.Id = "praxis") with
                | None ->
                    let executionEnabled =
                        manifest.Execution |> Option.exists (fun execution -> execution.Enabled)

                    if executionEnabled then
                        errors.Add "Enabled execution requires the Praxis component so Conditor can establish attributable initial work."
                | Some praxis ->
                    actions.Add
                        { Sequence = sequence
                          ComponentId = "praxis:mission"
                          ComponentVersion = praxis.Version
                          Kind = MissionWorkItem
                          Execution = EnsurePraxisMission mission }

                    sequence <- sequence + 1

        if errors.Count > 0 then
            Error(List.ofSeq errors)
        else
            Ok
                { ProjectName = manifest.Name
                  Operation = operation
                  Components = List.ofSeq resolved
                  Actions = List.ofSeq actions }
