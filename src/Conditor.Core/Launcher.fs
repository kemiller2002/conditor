namespace Conditor.Core

open System
open System.IO

module Launcher =
    let private successOrErrors (name: string) (result: ProcessResult) =
        if result.ExitCode = 0 then
            Ok()
        else
            Error
                [ $"{name} launcher check failed with exit code {result.ExitCode}."
                  result.StandardOutput.Trim()
                  result.StandardError.Trim() ]
            |> Result.mapError (List.filter (String.IsNullOrWhiteSpace >> not))

    let private probeCommand (target: string) (executable: string) (arguments: string list) (name: string) =
        ProcessRunner.runProcess target executable arguments
        |> successOrErrors name

    let private environmentValue name =
        Environment.GetEnvironmentVariable name
        |> Option.ofObj
        |> Option.filter (String.IsNullOrWhiteSpace >> not)

    let private validateCodexWorkloadIdentity () =
        let ruleId = environmentValue "OPENAI_FEDERATION_RULE_ID"
        let tokenPath = environmentValue "OPENAI_IDENTITY_TOKEN_FILE"

        match ruleId, tokenPath with
        | None, None -> Ok false
        | Some _, None ->
            Error
                [ "OPENAI_FEDERATION_RULE_ID is set but OPENAI_IDENTITY_TOKEN_FILE is missing."
                  "Codex workload identity requires both variables." ]
        | None, Some _ ->
            Error
                [ "OPENAI_IDENTITY_TOKEN_FILE is set but OPENAI_FEDERATION_RULE_ID is missing."
                  "Codex workload identity requires both variables." ]
        | Some _, Some path when not (Path.IsPathRooted path) ->
            Error [ "OPENAI_IDENTITY_TOKEN_FILE must be an absolute path." ]
        | Some _, Some path when not (File.Exists path) ->
            Error [ $"Codex workload identity token file is missing: {path}" ]
        | Some _, Some path ->
            let token = File.ReadAllText(path).Trim()

            if String.IsNullOrWhiteSpace token then
                Error [ $"Codex workload identity token file is empty: {path}" ]
            else
                Ok true

    let probe target (launcher: string) =
        match launcher.Trim().ToLowerInvariant() with
        | "codex" ->
            match probeCommand target "codex" [ "--version" ] "Codex" with
            | Error errors -> Error errors
            | Ok() ->
                match validateCodexWorkloadIdentity () with
                | Error errors -> Error errors
                | Ok true ->
                    // Avoid login-status under WIF. Replay protection can consume
                    // the GitHub OIDC assertion before the actual exec process.
                    Ok()
                | Ok false ->
                    probeCommand target "codex" [ "login"; "status" ] "Codex authentication"
        | "claude" ->
            match probeCommand target "claude" [ "--version" ] "Claude Code" with
            | Error errors -> Error errors
            | Ok() ->
                probeCommand target "claude" [ "auth"; "status" ] "Claude Code authentication"
        | unsupported ->
            Error [ $"Unsupported execution launcher '{unsupported}'. Supported launchers: codex, claude." ]

    let instruction (ready: Readiness.ReadyExecution) =
        $"""Execute the active Praxis mission {ready.Mission.Id} in this repository.

Read AGENTS.md first. Then read the canonical execution contract at {ready.ContractPath} and every normative document it references. Run the repository Praxis work-context command for {ready.Mission.Id} and obey the installed Ordo, Praxis, Aegis, Limen, Forma, Folio, security, visual, and communication constraints.

Implement the mission in the repository. Use repository-native lifecycle and verification commands as evidence. If blocked, record the block through Praxis. Do not treat your own statement that the work is finished as completion. Complete the Praxis work item only when its required implementation, tests, runtime/verification evidence, and governing requirements are satisfied."""

    let launch target (ready: Readiness.ReadyExecution) =
        let instruction = instruction ready

        match ready.Launcher.Trim().ToLowerInvariant() with
        | "codex" ->
            ProcessRunner.runProcess target "codex" [ "exec"; "--full-auto"; instruction ]
        | "claude" ->
            ProcessRunner.runProcess target "claude" [ "-p"; "--output-format"; "text"; instruction ]
        | unsupported ->
            { ExitCode = -1
              StandardOutput = String.Empty
              StandardError = $"Unsupported execution launcher '{unsupported}'." }
