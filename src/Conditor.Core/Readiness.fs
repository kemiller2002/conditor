namespace Conditor.Core

open System
open System.IO

module Readiness =
    type ReadyExecution =
        { Mission: PraxisMission
          Launcher: string
          ContractPath: string
          MissionState: string }

    let private missionFor (manifest: ProjectManifest) contractPath =
        let description =
            manifest.Execution
            |> Option.bind _.Mission
            |> Option.filter (String.IsNullOrWhiteSpace >> not)
            |> Option.defaultValue "Execute the canonical Conditor contract and prove completion through repository evidence."

        { Id = "COND-MISSION-001"
          Title = $"Build {manifest.Name} from the Conditor execution contract"
          Description = description
          ContractPath = contractPath }

    let private safeExistingContract target contractPath =
        if String.IsNullOrWhiteSpace contractPath || Path.IsPathRooted contractPath then
            Error [ $"Execution contractPath '{contractPath}' must be repository-relative." ]
        else
            let root = Path.GetFullPath target
            let full = Path.GetFullPath(Path.Combine(root, contractPath))
            let rootPrefix =
                root.TrimEnd(Path.DirectorySeparatorChar) + string Path.DirectorySeparatorChar

            let comparison =
                if OperatingSystem.IsWindows() then
                    StringComparison.OrdinalIgnoreCase
                else
                    StringComparison.Ordinal

            if not (full.StartsWith(rootPrefix, comparison)) then
                Error [ $"Execution contractPath '{contractPath}' escapes the target repository." ]
            elif not (File.Exists full) then
                Error [ $"Canonical execution contract is missing: {contractPath}" ]
            else
                Ok()

    let check target manifestPath (manifest: ProjectManifest) =
        let errors = ResizeArray<string>()

        let execution =
            match manifest.Execution with
            | None ->
                errors.Add "The Conditor manifest does not define execution."
                None
            | Some value when not value.Enabled ->
                errors.Add "Execution is disabled in the Conditor manifest."
                Some value
            | Some value -> Some value

        let launcher =
            execution
            |> Option.bind _.Launcher
            |> Option.filter (String.IsNullOrWhiteSpace >> not)

        if execution.IsSome && launcher.IsNone then
            errors.Add "Enabled execution requires execution.launcher."

        let contractPath =
            execution
            |> Option.bind _.ContractPath
            |> Option.filter (String.IsNullOrWhiteSpace >> not)

        if execution.IsSome && contractPath.IsNone then
            errors.Add "Enabled execution requires execution.contractPath."

        let hasPraxis =
            manifest.Components |> List.exists (fun request -> request.Id = "praxis" && request.Required)

        if not hasPraxis then
            errors.Add "Enabled execution requires the Praxis component."

        match LockFile.verifyManifest target manifestPath with
        | Ok() -> ()
        | Error lockErrors -> lockErrors |> List.iter errors.Add

        match Requirements.verify target manifest with
        | Ok() -> ()
        | Error requirementErrors -> requirementErrors |> List.iter errors.Add

        match contractPath with
        | Some path ->
            match safeExistingContract target path with
            | Ok() -> ()
            | Error contractErrors -> contractErrors |> List.iter errors.Add
        | None -> ()

        if errors.Count = 0 then
            match Planner.create target Verify manifest with
            | Error planErrors -> planErrors |> List.iter errors.Add
            | Ok plan ->
                match LockFile.verifyResolvedComponents target plan.Components with
                | Error identityErrors -> identityErrors |> List.iter errors.Add
                | Ok() ->
                    match Installer.execute target manifestPath plan with
                    | Ok _ -> ()
                    | Error verificationErrors -> verificationErrors |> List.iter errors.Add

        if errors.Count > 0 then
            Error(List.ofSeq errors)
        else
            let resolvedContract = contractPath |> Option.get
            let mission = missionFor manifest resolvedContract

            match Mission.launchState target mission with
            | Error missionErrors -> Error missionErrors
            | Ok missionState ->
                Ok
                    { Mission = mission
                      Launcher = launcher |> Option.get
                      ContractPath = resolvedContract
                      MissionState = missionState }
