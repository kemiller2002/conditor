namespace Conditor.Core

open System

module Upgrade =
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
          if previous.Scaffold <> current.Scaffold then
              yield "scaffold"
          if previous.Requirements <> current.Requirements then
              yield "requirements"
          if previous.Execution <> current.Execution then
              yield "execution" ]

    let private analyzeComponents previous current =
        let errors = ResizeArray<string>()
        let changed = ResizeArray<string>()
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
                | Some _, Some _ ->
                    match Registry.tryFind id with
                    | None ->
                        errors.Add $"Component '{id}' is unknown to this Conditor version."
                    | Some definition ->
                        match definition.Distribution, definition.ApplicationBinding, definition.LifecycleSource with
                        | LifecycleNpm, None, Some RegistryPackage ->
                            changed.Add id
                        | _ ->
                            errors.Add
                                $"Component '{id}' cannot be upgraded by the initial version-only upgrader because it has an application binding or non-registry lifecycle source."
                | _ ->
                    errors.Add
                        $"Component '{id}' must declare both the old and new versions explicitly before Conditor can prove a version-only upgrade."

        if errors.Count > 0 then Error(List.ofSeq errors) else Ok(List.ofSeq changed)

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
            match Scaffolding.plan target manifest with
            | Error errors -> Error errors
            | Ok changes ->
                let materialChanges =
                    changes
                    |> List.filter (fun (path, _) ->
                        path <> "AGENTS.md" && path <> "context/CURRENT-STATE.md")

                if materialChanges.IsEmpty then
                    Ok()
                else
                    let paths = materialChanges |> List.map fst |> String.concat ", "

                    Error
                        [ $"Repository scaffold is not already at the declared state: {paths}."
                          "Run 'conditor repair' before upgrade; upgrade will not combine repair and version migration." ]

    let private executeChanged target manifest (changedIds: string list) =
        if changedIds.IsEmpty then
            Ok()
        else
            match Planner.create target Upgrade manifest with
            | Error errors -> Error errors
            | Ok plan ->
                let changed = Set.ofList changedIds

                let actions =
                    plan.Actions
                    |> List.filter (fun action ->
                        changed.Contains action.ComponentId)

                Installer.execute target String.Empty { plan with Actions = actions }
                |> Result.map ignore

    let private verifyAfterUpgrade target manifestPath manifest =
        match Requirements.verify target manifest with
        | Error errors -> Error errors
        | Ok() ->
            match Planner.create target Verify manifest with
            | Error errors -> Error errors
            | Ok plan ->
                Installer.execute target manifestPath plan |> Result.map ignore

    let apply target manifestPath manifest =
        match analyze target manifest with
        | Error errors -> Error errors
        | Ok changedIds ->
            match ensureScaffoldAlreadyEstablished target manifest with
            | Error errors -> Error errors
            | Ok() ->
                match executeChanged target manifest changedIds with
                | Error errors -> Error errors
                | Ok() ->
                    match verifyAfterUpgrade target manifestPath manifest with
                    | Error errors -> Error errors
                    | Ok() ->
                        match Planner.create target Init manifest with
                        | Error errors -> Error errors
                        | Ok lockPlan ->
                            let lockPath = LockFile.write target manifestPath lockPlan

                            Ok
                                { ChangedComponents = changedIds
                                  LockPath = lockPath }
