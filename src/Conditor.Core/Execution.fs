namespace Conditor.Core

open System

module Execution =
    let check target manifestPath manifest =
        match Readiness.check target manifestPath manifest with
        | Error errors -> Error errors
        | Ok ready ->
            match Launcher.probe target ready.Launcher with
            | Error errors -> Error errors
            | Ok() -> Ok ready

    let start target manifestPath manifest =
        match check target manifestPath manifest with
        | Error errors -> Error errors
        | Ok ready ->
            match Mission.activate target ready.Mission with
            | Error errors -> Error errors
            | Ok() ->
                let result = Launcher.launch target ready

                if result.ExitCode = 0 then
                    Ok result
                else
                    Error
                        [ $"Launcher '{ready.Launcher}' exited with code {result.ExitCode}. The Praxis mission remains active and may be resumed."
                          result.StandardOutput.Trim()
                          result.StandardError.Trim() ]
                    |> Result.mapError (List.filter (String.IsNullOrWhiteSpace >> not))
