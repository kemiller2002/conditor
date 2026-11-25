namespace Conditor.Core

open System

module Upgrade =
    type UpgradePreview =
        { ChangedComponents: string list
          Plan: InstallationPlan
          /// NuGet application bindings whose declaration and exact package
          /// pins move together (docs/nuget-binding-upgrade.md).
          NugetBindings: NugetBindingTransition list }

    type UpgradeResult =
        { ChangedComponents: string list
          LockPath: string }

    let private componentMap (manifest: ProjectManifest) =
        manifest.Components |> List.map (fun request -> request.Id, request) |> Map.ofList

    let private unchangedGovernance previous current =
        [ if previous.SchemaVersion <> current.SchemaVersion then
              yield "schemaVersion"
          if previous.Name <> current.Name then
              yield "name"
          if previous.RegistryAuthority <> current.RegistryAuthority then
              yield "registryAuthority"
          if previous.Scaffold <> current.Scaffold then
              yield "scaffold"
          if previous.Requirements <> current.Requirements then
              yield "requirements"
          if previous.Execution <> current.Execution then
              yield "execution" ]

    let private analyzeComponents previous current =
        let errors = ResizeArray<string>()
        let changed = ResizeArray<string>()
        let bindingMoves = ResizeArray<ComponentDefinition * string * string>()
        let oldComponents = componentMap previous
        let newComponents = componentMap current
        let oldIds = oldComponents |> Map.toSeq |> Seq.map fst |> Set.ofSeq
        let newIds = newComponents |> Map.toSeq |> Seq.map fst |> Set.ofSeq

        if oldIds <> newIds then
            errors.Add "Component membership changed. Initial upgrade support does not add or remove components."

        for id in Set.intersect oldIds newIds do
            let oldRequest = oldComponents[id]
            let newRequest = newComponents[id]

            if oldRequest.Required <> newRequest.Required then
                errors.Add $"Component '{id}' changed required/optional policy; this is not a version-only upgrade."

            if oldRequest.Version <> newRequest.Version then
                match oldRequest.Version, newRequest.Version with
                | Some oldVersion, Some newVersion ->
                    match Registry.tryFind id with
                    | None ->
                        errors.Add $"Component '{id}' is unknown to this Conditor version."
                    | Some definition ->
                        match definition.Distribution, definition.ApplicationBinding, definition.LifecycleSource with
                        | LifecycleNpm, None, Some RegistryPackage ->
                            changed.Add id
                        | _, Some NugetReference, _ ->
                            changed.Add id
                            bindingMoves.Add(definition, oldVersion, newVersion)
                        | HostTool, _, _ ->
                            errors.Add
                                $"Component '{id}' is a native host tool; change its version through the integrity-bound workstation resolved set, not a repository-only upgrade."
                        | _ ->
                            errors.Add
                                $"Component '{id}' cannot be upgraded by the initial version-only upgrader because it has an application binding or non-registry lifecycle source."
                | _ ->
                    errors.Add
                        $"Component '{id}' must declare both the old and new versions explicitly before Conditor can prove a version-only upgrade."

        if errors.Count > 0 then
            Error(List.ofSeq errors)
        else
            Ok(List.ofSeq changed, List.ofSeq bindingMoves)

    let private analyze target currentManifest =
        match LockFile.readManifestSnapshot target with
        | Error errors -> Error errors
        | Ok snapshot ->
            match Manifest.parseText snapshot with
            | Error errors ->
                Error("Unable to parse the prior declaration stored in the Conditor lock." :: errors)
            | Ok previousManifest ->
                let governanceChanges = unchangedGovernance previousManifest currentManifest

                if not governanceChanges.IsEmpty then
                    let changedFields = String.Join(", ", governanceChanges)

                    Error
                        [ $"Upgrade changes unsupported governing fields: {changedFields}."
                          "The initial Conditor upgrade path supports lifecycle component version changes only." ]
                else
                    analyzeComponents previousManifest currentManifest

    let private ensureScaffoldAlreadyEstablished target manifest =
        match manifest.Scaffold with
        | None -> Ok()
        | Some _ ->
            match Planner.scaffoldPlan target manifest with
            | Error errors -> Error errors
            | Ok changes ->
                let materialChanges =
                    changes
                    |> List.filter (fun (path, _) ->
                        path <> "AGENTS.md" && path <> "context/CURRENT-STATE.md" && path <> ".gitignore")

                if materialChanges.IsEmpty then
                    Ok()
                else
                    let paths = materialChanges |> List.map fst |> String.concat ", "

                    Error
                        [ $"Repository scaffold is not already at the declared state: {paths}."
                          "Run 'conditor repair' before upgrade; upgrade will not combine repair and version migration." ]

    let private upgradePlan target manifest changedIds =
        match Planner.create target Upgrade manifest with
        | Error errors -> Error errors
        | Ok plan ->
            let changed = Set.ofList changedIds

            let actions =
                plan.Actions
                |> List.filter (fun action -> changed.Contains action.ComponentId)

            Ok { plan with Actions = actions }

    let preview target manifest =
        match analyze target manifest with
        | Error errors -> Error errors
        | Ok(changedIds, bindingMoves) ->
            match ensureScaffoldAlreadyEstablished target manifest with
            | Error errors -> Error errors
            | Ok() ->
                let bindings =
                    bindingMoves
                    |> List.map (fun (definition, fromVersion, toVersion) -> NugetBinding.plan target definition fromVersion toVersion)

                match bindings |> List.collect (function Error errors -> errors | Ok _ -> []) with
                | _ :: _ as errors -> Error errors
                | [] ->
                    match upgradePlan target manifest changedIds with
                    | Error errors -> Error errors
                    | Ok plan ->
                        Ok
                            { ChangedComponents = changedIds
                              Plan = plan
                              NugetBindings = bindings |> List.choose (function Ok binding -> Some binding | Error _ -> None) }

    let private verifyAfterUpgrade target manifestPath manifest =
        match Requirements.verify target manifest with
        | Error errors -> Error errors
        | Ok() ->
            match Planner.create target Verify manifest with
            | Error errors -> Error errors
            | Ok plan ->
                Installer.executeWithSignals target manifestPath plan |> Result.map snd

    let apply target manifestPath manifest =
        match preview target manifest with
        | Error errors -> Error errors
        | Ok preview ->
            let movedBindings =
                preview.NugetBindings
                |> List.fold (fun state binding -> state |> Result.bind (fun () -> NugetBinding.apply target binding)) (Ok())

            match movedBindings |> Result.bind (fun () -> Installer.execute target String.Empty preview.Plan |> Result.map ignore) with
            | Error errors -> Error errors
            | Ok _ ->
                match verifyAfterUpgrade target manifestPath manifest with
                | Error errors -> Error errors
                | Ok reviewSignals ->
                    match Planner.create target Init manifest with
                    | Error errors -> Error errors
                    | Ok lockPlan ->
                        let lockPath = LockFile.writeWith target manifestPath lockPlan reviewSignals

                        Ok
                            { ChangedComponents = preview.ChangedComponents
                              LockPath = lockPath }
