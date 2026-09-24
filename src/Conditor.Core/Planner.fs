namespace Conditor.Core

open System

module Planner =
    let private npxExecutable () =
        if OperatingSystem.IsWindows() then "npx.cmd" else "npx"

    let private npmExecutable () =
        if OperatingSystem.IsWindows() then "npm.cmd" else "npm"

    let private packageSpec version (definition: ComponentDefinition) =
        match definition.LifecycleSource with
        | Some RegistryPackage -> Ok $"{definition.Package}@{version}"
        | Some(FixedPackageSpec specification) when version = definition.DefaultVersion -> Ok specification
        | Some(FixedPackageSpec _) ->
            Error
                $"Component '{definition.Id}' version '{version}' has no immutable distribution mapping. Available mapped version: '{definition.DefaultVersion}'."
        | None -> Error $"Lifecycle component '{definition.Id}' has no distribution source."

    let private replaceTarget target arguments =
        arguments
        |> List.map (fun argument -> if argument = "{target}" then target else argument)

    let private lifecycleInvocation
        target
        packageReference
        (definition: ComponentDefinition)
        arguments
        =
        let command =
            definition.Command
            |> Option.defaultWith (fun () -> invalidOp "Lifecycle component has no command.")

        let resolvedArguments = replaceTarget target arguments

        match definition.LifecycleSource with
        | Some RegistryPackage ->
            npxExecutable (),
            [ "--yes"
              $"--package={packageReference}"
              command ]
            @ resolvedArguments
        | Some(FixedPackageSpec _) ->
            npmExecutable (),
            [ "exec"
              "--yes"
              $"--package={packageReference}"
              command
              "--" ]
            @ resolvedArguments
        | None ->
            invalidOp $"Lifecycle component '{definition.Id}' has no distribution source."

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
            (packageReference: string)
            (definition: ComponentDefinition)
            (phase: string)
            (arguments: string list)
            =
            let executable, invocationArguments =
                lifecycleInvocation target packageReference definition arguments

            actions.Add
                { Sequence = sequence
                  ComponentId = request.Id
                  ComponentVersion = version
                  Kind = actionKind operation phase
                  Executable = executable
                  Arguments = invocationArguments }

            sequence <- sequence + 1

        for request in manifest.Components do
            match Registry.tryFind request.Id with
            | None ->
                if request.Required then
                    errors.Add $"Unknown required component '{request.Id}'."
            | Some definition ->
                let version = request.Version |> Option.defaultValue definition.DefaultVersion

                match definition.Distribution with
                | LifecycleNpm ->
                    match packageSpec version definition with
                    | Error error ->
                        if request.Required then
                            errors.Add error
                    | Ok packageReference ->
                        resolved.Add
                            { Id = definition.Id
                              Version = version
                              Distribution = definition.Distribution
                              Package = definition.Package
                              SourceReference = Some packageReference }

                        match operation with
                        | Init ->
                            addAction request version packageReference definition "install" definition.InitArguments
                            addAction request version packageReference definition "verify" definition.VerifyArguments
                        | Verify ->
                            addAction request version packageReference definition "verify" definition.VerifyArguments
                        | Doctor ->
                            addAction request version packageReference definition "doctor" definition.DoctorArguments
                | NpmPackage
                | NugetPackage ->
                    resolved.Add
                        { Id = definition.Id
                          Version = version
                          Distribution = definition.Distribution
                          Package = definition.Package
                          SourceReference = None }

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
