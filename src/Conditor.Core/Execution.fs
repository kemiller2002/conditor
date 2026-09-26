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

    let private executeLauncher target (ready: Readiness.ReadyExecution) =
        let result = Launcher.launch target ready

        if result.ExitCode = 0 then
            Ok result
        else
            Error
                [ $"Launcher '{ready.Launcher}' exited with code {result.ExitCode}. The Praxis mission state is unchanged by Conditor; inspect it with './ros work context {ready.Mission.Id}'."
                  result.StandardOutput.Trim()
                  result.StandardError.Trim() ]
            |> Result.mapError (List.filter (String.IsNullOrWhiteSpace >> not))

    /// Launches the agent against a ready (or already active) mission. Conditor
    /// performs no Praxis state transition here: activation belongs to the
    /// agent's own `work start` in the agent's own execution (CON-066), so the
    /// agent's work is never keyed to an execution Conditor started.
    let start target manifestPath manifest =
        match check target manifestPath manifest with
        | Error errors -> Error errors
        | Ok ready -> executeLauncher target ready

    let resume target manifestPath manifest =
        match check target manifestPath manifest with
        | Error errors -> Error errors
        | Ok ready when ready.MissionState <> "active" ->
            Error
                [ $"Praxis mission '{ready.Mission.Id}' is '{ready.MissionState}', not active."
                  "Use 'conditor start' to launch an agent that begins its own Praxis execution; resume never performs a state transition." ]
        | Ok ready ->
            executeLauncher target ready

    let handoff resumeOnly target manifestPath manifest =
        match Readiness.check target manifestPath manifest with
        | Error errors -> Error errors
        | Ok ready when resumeOnly && ready.MissionState <> "active" ->
            Error
                [ $"Praxis mission '{ready.Mission.Id}' is '{ready.MissionState}', not active."
                  "External resume handoff never performs a state transition." ]
        | Ok ready ->
            // Like `start`, a handoff never activates the mission on the external
            // agent's behalf (CON-066); the prompt tells the agent to begin its own execution.
            Ok(ready, Launcher.instruction ready)
