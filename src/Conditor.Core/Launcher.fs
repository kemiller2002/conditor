namespace Conditor.Core

open System

module Launcher =
    let private successOrErrors name (result: ProcessResult) =
        if result.ExitCode = 0 then
            Ok()
        else
            Error
                [ $"{name} launcher check failed with exit code {result.ExitCode}."
                  result.StandardOutput.Trim()
                  result.StandardError.Trim() ]
            |> Result.mapError (List.filter (String.IsNullOrWhiteSpace >> not))

    let private probeCommand target executable arguments name =
        ProcessRunner.runProcess target executable arguments
        |> successOrErrors name

    let probe target launcher =
        match launcher.Trim().ToLowerInvariant() with
        | "codex" ->
            match probeCommand target "codex" [ "--version" ] "Codex" with
            | Error errors -> Error errors
            | Ok() ->
                probeCommand target "codex" [ "login"; "status" ] "Codex authentication"
        | "claude" ->
            match probeCommand target "claude" [ "--version" ] "Claude Code" with
            | Error errors -> Error errors
            | Ok() ->
                probeCommand target "claude" [ "auth"; "status" ] "Claude Code authentication"
        | unsupported ->
            Error [ $"Unsupported execution launcher '{unsupported}'. Supported launchers: codex, claude." ]

    let private prompt (ready: Readiness.ReadyExecution) =
        $"""Execute the active Praxis mission {ready.Mission.Id} in this repository.

Read AGENTS.md first. Then read the canonical execution contract at {ready.ContractPath} and every normative document it references. Run the repository Praxis work-context command for {ready.Mission.Id} and obey the installed Ordo, Praxis, Aegis, Limen, Forma, Folio, security, visual, and communication constraints.

Implement the mission in the repository. Use repository-native lifecycle and verification commands as evidence. If blocked, record the block through Praxis. Do not treat your own statement that the work is finished as completion. Complete the Praxis work item only when its required implementation, tests, runtime/verification evidence, and governing requirements are satisfied."""

    let launch target (ready: Readiness.ReadyExecution) =
        let instruction = prompt ready

        match ready.Launcher.Trim().ToLowerInvariant() with
        | "codex" ->
            ProcessRunner.runProcess target "codex" [ "exec"; "--full-auto"; instruction ]
        | "claude" ->
            ProcessRunner.runProcess target "claude" [ "-p"; "--output-format"; "text"; instruction ]
        | unsupported ->
            { ExitCode = -1
              StandardOutput = String.Empty
              StandardError = $"Unsupported execution launcher '{unsupported}'." }
