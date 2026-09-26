namespace Conditor.Core

open System

/// Explicit identity declarations for processes Conditor starts (CON-130, CON-131).
/// Praxis owns identity discovery (RQ-ROS-2026-A006, A016); Conditor only states
/// what it knows to be true and removes inherited identity that belongs to
/// someone else. It never adds credentials: every value here is a fixed
/// identity fact or an operator-configured model name.
module AgentIdentity =
    /// A change to an inherited child-process environment: `Some value` sets
    /// the variable, `None` removes it. Everything else is inherited unchanged
    /// (launchers need their provider credentials, which Conditor never reads).
    type EnvironmentChange = string * string option

    [<Literal>]
    let ConditorActorId = "conditor"

    /// Every variable Praxis identity discovery reads (Praxis contract revision
    /// 1.1, `identity-environment.json`; pinned by a test against the vendored
    /// fixture). A child acting as a different actor must not inherit any of
    /// them: runtime session ids, CI run markers, and local-model hosts would
    /// put the operator's session or run on the child's execution, and Praxis
    /// could merge different runs.
    let inheritedIdentityVariables =
        [ "ROS_ACTOR"
          "ROS_ACTOR_KIND"
          "ROS_EXECUTION_ID"
          "ROS_TELEMETRY_PROVIDER"
          "ROS_TELEMETRY_MODEL"
          "ROS_TELEMETRY_MODEL_VERSION"
          "ROS_TELEMETRY_RUNTIME"
          "ROS_TELEMETRY_RUNTIME_VERSION"
          "ROS_TELEMETRY_SESSION_ID"
          "ROS_TELEMETRY_CONVERSATION_ID"
          "ROS_TELEMETRY_RUN_ID"
          "CLAUDE_CODE_SESSION_ID"
          "CODEX_SESSION_ID"
          "CODEX_THREAD_ID"
          "GEMINI_SESSION_ID"
          "COPILOT_SESSION_ID"
          "GITHUB_ACTIONS"
          "GITHUB_RUN_ID"
          "OLLAMA_HOST" ]

    let private clearInherited: EnvironmentChange list =
        inheritedIdentityVariables |> List.map (fun name -> name, None)

    /// Later entries win; the result names each variable once, in first-seen order.
    let private merge (changes: EnvironmentChange list) : EnvironmentChange list =
        let names = changes |> List.map fst |> List.distinct
        let last = changes |> List.fold (fun (state: Map<string, string option>) (name, value) -> state.Add(name, value)) Map.empty
        names |> List.map (fun name -> name, last[name])

    /// Environment for Conditor's own `ros` invocations: Conditor is
    /// deterministic automation with the stable id `conditor` and runtime
    /// `conditor`. Provider and model are not declared: Conditor has no model,
    /// and because CI and local-model signals (`GITHUB_ACTIONS`, `OLLAMA_HOST`)
    /// are cleared first, Praxis records both as `unknown` instead of inferring them.
    let conditorRosEnvironment: EnvironmentChange list =
        merge (
            clearInherited
            @ [ "ROS_ACTOR_KIND", Some "automation"
                "ROS_ACTOR", Some ConditorActorId
                "ROS_TELEMETRY_RUNTIME", Some "conditor" ]
        )

    /// Provider and runtime facts Conditor knows for a launcher because it
    /// chose the CLI it starts. Unsupported launchers have no known identity.
    let launcherIdentity (launcher: string) =
        match launcher.Trim().ToLowerInvariant() with
        | "codex" -> Some("openai", "codex")
        | "claude" -> Some("anthropic", "claude-code")
        | _ -> None

    let private configured (value: string option) =
        value
        |> Option.map _.Trim()
        |> Option.filter (String.IsNullOrWhiteSpace >> not)

    /// Environment for a launched agent. It carries only true, known identity:
    /// kind `agent`, the launched CLI's provider/runtime, and the model only when
    /// the operator configured one (and Conditor passes it to the CLI). It never
    /// carries Conditor's identity or an inherited actor/execution; the agent's
    /// stable id is left to Praxis (`provider/runtime`) and it begins its own execution.
    let agentEnvironment (launcher: string) (model: string option) : EnvironmentChange list =
        let identity =
            match launcherIdentity launcher with
            | Some(provider, runtime) ->
                [ "ROS_ACTOR_KIND", Some "agent"
                  "ROS_TELEMETRY_PROVIDER", Some provider
                  "ROS_TELEMETRY_RUNTIME", Some runtime ]
            | None -> [ "ROS_ACTOR_KIND", Some "agent" ]

        let modelChange =
            match configured model with
            | Some value -> [ "ROS_TELEMETRY_MODEL", Some value ]
            | None -> []

        merge (clearInherited @ identity @ modelChange)

    /// Applies changes to a snapshot of an environment (pure; used by tests and diagnostics).
    let apply (changes: EnvironmentChange list) (environment: Map<string, string>) =
        changes
        |> List.fold
            (fun (state: Map<string, string>) (name, value) ->
                match value with
                | Some text -> state.Add(name, text)
                | None -> state.Remove name)
            environment
