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

    /// The permission mode a headless Claude Code run uses when the manifest
    /// declares none. `auto` lets the run edit, build, test and push without
    /// a person while the classifier still refuses destructive actions;
    /// repository settings (.claude/settings.json) widen it, for example to
    /// merge the agent's own pull requests once CI is green.
    [<Literal>]
    let DefaultPermissionMode = Readiness.DefaultPermissionMode

    type LaunchKind =
        | StartRun
        | ResumeRun

    /// What a launch knows beyond the readiness of the mission.
    type InstructionContext =
        { Kind: LaunchKind
          /// The work-item attachment holding the imported requirements trace.
          ImportedQueue: string option
          /// When work must be finished (feature freeze), if there is a deadline.
          Until: DateTimeOffset option
          Attempt: int }

    let private queueGuidance (ready: Readiness.ReadyExecution) (attachment: string) =
        $"""

The work is an ordered queue of Praxis slice work items (SLICE-*) imported from the execution contract; every source requirement is traced to its slice in the {attachment} attachment of {ready.Mission.Id}. Run `praxis work ready` and `praxis plan analyze`, then take the next ready slice in order: never-cut slices first, honoring each "Depends on". Start it with `praxis work start`, deliver it, checkpoint and complete it with evidence, and continue with the next slice. Never wait for a person: when a slice is blocked, record the block through Praxis and move to the next unblocked slice."""

    /// The instruction a launch gives the agent. Pure.
    let instructionFor (context: InstructionContext) (ready: Readiness.ReadyExecution) =
        let opening =
            match context.Kind with
            | StartRun -> $"Execute the active Praxis mission {ready.Mission.Id} in this repository."
            | ResumeRun ->
                $"Resume the active Praxis mission {ready.Mission.Id} in this repository (launch {context.Attempt}). A previous session ended; its work survives only as repository and Praxis state. Run `praxis status` and `praxis work context` first, and continue interrupted active work with `praxis work continue`."

        let queue =
            context.ImportedQueue |> Option.map (queueGuidance ready) |> Option.defaultValue ""

        let deadline =
            context.Until
            |> Option.map (fun until ->
                let iso = until.ToString("yyyy-MM-dd'T'HH:mm:sszzz")
                $"\n\nHard deadline {iso}. Start no new slice in its last 20 minutes; leave every change merged or checkpointed before it.")
            |> Option.defaultValue ""

        $"""{opening}

Read AGENTS.md first. Then read the canonical execution contract at {ready.ContractPath} and every normative document it references. Run the repository Praxis work-context command for {ready.Mission.Id} and obey the installed Ordo, Praxis, Aegis, Limen, Forma, Folio, security, visual, and communication constraints.{queue}

The main branch is protected. Publish every change, including bootstrap commits not yet pushed, through a pull request, and merge it once its required checks pass. Use repository-native lifecycle and verification commands as evidence. If blocked, record the block through Praxis. Do not treat your own statement that the work is finished as completion. Complete a Praxis work item only when its required implementation, tests, runtime/verification evidence, and governing requirements are satisfied.{deadline}"""

    let instruction (ready: Readiness.ReadyExecution) =
        instructionFor
            { Kind = StartRun
              ImportedQueue = None
              Until = None
              Attempt = 1 }
            ready

    /// The process a launch runs: the executable and its arguments. Pure.
    let command (ready: Readiness.ReadyExecution) (instruction: string) =
        match ready.Launcher.Trim().ToLowerInvariant() with
        | "codex" -> Ok("codex", [ "exec"; "--full-auto"; instruction ])
        | "claude" ->
            Ok("claude", [ "-p"; "--permission-mode"; ready.PermissionMode; "--output-format"; "text"; instruction ])
        | unsupported -> Error $"Unsupported execution launcher '{unsupported}'."

    let launch target (ready: Readiness.ReadyExecution) =
        match command ready (instruction ready) with
        | Ok(executable, arguments) -> ProcessRunner.runProcess target executable arguments
        | Error error ->
            { ExitCode = -1
              StandardOutput = String.Empty
              StandardError = error }
