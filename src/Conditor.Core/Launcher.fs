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

    /// How the agent takes ownership of the mission in its own Praxis execution.
    /// Conditor never starts the execution for the agent (CON-066).
    let private activationGuidance (ready: Readiness.ReadyExecution) =
        match ready.MissionState with
        | "active" ->
            $"""The mission is already active from an earlier run. Run `./ros work context {ready.Mission.Id}` before changing anything. Do not record your work into another run's execution: if Praxis refuses to attribute your contributions to the active execution, block the mission with `./ros work block --id {ready.Mission.Id} --occurred-at <current UTC time> --reason <why>` and report it instead of declaring yourself as another actor."""
        | _ ->
            $"""Conditor captured this mission as automation and did not start it for you. Before any meaningful change, begin your own Praxis execution: `./ros work start --id {ready.Mission.Id} --occurred-at <current UTC time> --type feature`."""

    /// Provenance obligations from the Praxis agent identity and provenance
    /// contract (CON-067). Conditor passes no identity of its own to the agent.
    let private provenanceGuidance =
        """Check who Praxis will record you as with `./ros provenance identity`; if it is wrong, declare yourself (ROS_ACTOR_KIND/ROS_ACTOR or the identity flags) truthfully. Never claim another actor's identity and never invent a provider, model, or runtime you do not know. Attribute every canonical requirement, decision, or evidence record you create or materially change with `./ros provenance record --path PATH --operation created|modified` inside your own execution. If the installed Praxis has no `provenance` command, state that in your report; do not hand-write provenance front matter."""

    let instruction (ready: Readiness.ReadyExecution) =
        $"""Execute the Praxis mission {ready.Mission.Id} in this repository.

Read AGENTS.md first. Then read the canonical execution contract at {ready.ContractPath} and every normative document it references. Run the repository Praxis work-context command for {ready.Mission.Id} and obey the installed Ordo, Praxis, Aegis, Limen, Forma, Folio, security, visual, and communication constraints.

{activationGuidance ready}

{provenanceGuidance}

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
