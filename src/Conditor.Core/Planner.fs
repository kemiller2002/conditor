namespace Conditor.Core

open System

module Planner =
    let private npxExecutable () =
        if OperatingSystem.IsWindows() then "npx.cmd" else "npx"

    let private lifecycleArguments target version (definition: ComponentDefinition) arguments =
        let command = definition.Command |> Option.defaultWith (fun () -> invalidOp "Lifecycle component has no command.")

        [ "--yes"
          $"--package={definition.Package}@{version}"
          command ]
        @ (arguments |> List.map (fun argument -> if argument = "{target}" then target else argument))

    let private actionKind operation phase =
        match operation, phase with
        | Init, "install" -> InstallLifecycle
        | Init, "verify"
        | Verify, "verify" -> VerifyLifecycle
        | Doctor, "doctor" -> DiagnoseLifecycle
        | _ -> invalidArg (nameof phase) $"Unsupported plan phase '{phase}'."

    let create target operation (manifest: ProjectManifest) : Result<InstallationPlan, string list> =
        let errors = ResizeArray<string>()
        let resolved = ResizeArray<ResolvedComponent>()
        let actions = ResizeArray<PlanAction>()
        let mutable sequence = 1

        let addAction
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
                  Executable = npxExecutable ()
                  Arguments = lifecycleArguments target version definition arguments }

            sequence <- sequence + 1

        for request in manifest.Components do
            match Registry.tryFind request.Id with
            | None ->
                if request.Required then
                    errors.Add $"Unknown required component '{request.Id}'."
            | Some definition ->
                let version = request.Version |> Option.defaultValue definition.DefaultVersion

                resolved.Add
                    { Id = definition.Id
                      Version = version
                      Distribution = definition.Distribution
                      Package = definition.Package }

                match definition.Distribution with
                | LifecycleNpm ->
                    match operation with
                    | Init ->
                        addAction request version definition "install" definition.InitArguments
                        addAction request version definition "verify" definition.VerifyArguments
                    | Verify -> addAction request version definition "verify" definition.VerifyArguments
                    | Doctor -> addAction request version definition "doctor" definition.DoctorArguments
                | NpmPackage
                | NugetPackage ->
                    if request.Required then
                        errors.Add
                            $"Component '{request.Id}' is an application dependency. Project binding is not implemented yet, so Conditor will not guess where to install it."

        if errors.Count > 0 then
            Error(List.ofSeq errors)
        else
            Ok
                { ProjectName = manifest.Name
                  Operation = operation
                  Components = List.ofSeq resolved
                  Actions = List.ofSeq actions }
