namespace Conditor.Core

open System

module Status =
    type CheckState =
        | Healthy
        | Failed
        | Informational

    type StatusCheck =
        { Name: string
          State: CheckState
          Detail: string }

    type ProjectStatus =
        { Project: string
          Components: string list
          Checks: StatusCheck list }

    let private requestedVersion (request: ComponentRequest) =
        match Registry.tryFind request.Id with
        | Some definition ->
            request.Version |> Option.defaultValue definition.DefaultVersion
        | None ->
            request.Version |> Option.defaultValue "unknown"

    let private componentSummary (manifest: ProjectManifest) =
        manifest.Components
        |> List.map (fun request ->
            let suffix = if request.Required then String.Empty else " (optional)"
            $"{request.Id}@{requestedVersion request}{suffix}")

    let private check name (result: Result<'a, string list>) successDetail =
        match result with
        | Ok _ ->
            { Name = name
              State = Healthy
              Detail = successDetail }
        | Error errors ->
            { Name = name
              State = Failed
              Detail = String.Join(" | ", errors) }

    let private verifyComponents target manifestPath manifest =
        match Planner.create target Verify manifest with
        | Error errors -> Error errors
        | Ok plan ->
            Installer.execute target manifestPath plan |> Result.map ignore

    let inspect target manifestPath (manifest: ProjectManifest) =
        let checks = ResizeArray<StatusCheck>()

        checks.Add(
            check
                "lock"
                (LockFile.verifyManifest target manifestPath)
                "current and matches conditor.json"
        )

        checks.Add(
            check
                "requirements"
                (Requirements.verify target manifest)
                $"{manifest.Requirements.Length} pinned governing artifact(s) current"
        )

        checks.Add(
            check
                "components"
                (verifyComponents target manifestPath manifest)
                $"{manifest.Components.Length} declared component(s) verified"
        )

        match manifest.Execution with
        | None ->
            checks.Add
                { Name = "execution"
                  State = Informational
                  Detail = "not declared" }
        | Some execution when not execution.Enabled ->
            checks.Add
                { Name = "execution"
                  State = Informational
                  Detail = "disabled by conditor.json" }
        | Some _ ->
            match Readiness.check target manifestPath manifest with
            | Ok ready ->
                checks.Add
                    { Name = "execution"
                      State = Healthy
                      Detail =
                        $"ready; launcher={ready.Launcher}; mission={ready.Mission.Id}; missionState={ready.MissionState}; contract={ready.ContractPath}" }
            | Error errors ->
                checks.Add
                    { Name = "execution"
                      State = Failed
                      Detail = String.Join(" | ", errors) }

        { Project = manifest.Name
          Components = componentSummary manifest
          Checks = List.ofSeq checks }

    let isHealthy report =
        report.Checks |> List.forall (fun check -> check.State <> Failed)

    let stateText =
        function
        | Healthy -> "ok"
        | Failed -> "failed"
        | Informational -> "info"
