namespace Conditor.Core

open System
open System.IO

module Installer =
    let private commandText action =
        String.Join(" ", action.Executable :: action.Arguments)

    let describe (plan: InstallationPlan) =
        plan.Actions
        |> List.map (fun action -> $"{action.Sequence,2}. {action.ComponentId}@{action.ComponentVersion}: {commandText action}")

    let execute target manifestPath (plan: InstallationPlan) =
        Directory.CreateDirectory target |> ignore

        let rec loop actions =
            match actions with
            | [] ->
                if plan.Operation = Init then
                    let lockPath = LockFile.write target manifestPath plan
                    Ok(Some lockPath)
                else
                    Ok None
            | action :: remaining ->
                let result = ProcessRunner.run target action

                if result.ExitCode = 0 then
                    loop remaining
                else
                    Error
                        [ $"Conditor stopped at action {action.Sequence} ({action.ComponentId})."
                          $"Command: {commandText action}"
                          $"Exit code: {result.ExitCode}"
                          result.StandardOutput.Trim()
                          result.StandardError.Trim() ]
                    |> Result.mapError (List.filter (String.IsNullOrWhiteSpace >> not))

        loop plan.Actions
