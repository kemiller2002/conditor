module SupervisorTests

open System
open System.IO
open Conditor.Core

let private start = DateTimeOffset(2026, 11, 7, 9, 45, 0, TimeSpan.FromHours -5.0)
let private until = DateTimeOffset(2026, 11, 7, 16, 40, 0, TimeSpan.FromHours -5.0)
let private policy = Supervisor.defaultPolicy (Some until)

let private observation state =
    { Now = start
      MissionState = state
      OpenSlices = Some 23
      StopRequested = false }

let private ready: Readiness.ReadyExecution =
    { Mission =
        { Id = "COND-MISSION-001"
          Title = "Build"
          Description = "Build"
          ContractPath = "kickoff/indy-init.kickoff.json" }
      Launcher = "claude"
      ContractPath = "kickoff/indy-init.kickoff.json"
      MissionState = "ready"
      PermissionMode = Launcher.DefaultPermissionMode }

let private context kind attempt queue deadline: Launcher.InstructionContext =
    { Kind = kind
      ImportedQueue = queue
      Until = deadline
      Attempt = attempt }

let private contains (needle: string) (text: string) = text.Contains(needle, StringComparison.Ordinal)

/// A scripted supervisor run: each launch takes the next duration, and the
/// mission state after launch n is `stateAfter n`.
let private simulate (policy: SupervisorPolicy) (durations: TimeSpan list) (stateAfter: int -> string) (initialState: string) =
    let mutable now = start
    let mutable launches = 0
    let mutable remaining = durations
    let sleeps = ResizeArray<TimeSpan>()
    let kinds = ResizeArray<Launcher.LaunchKind * int>()
    let mutable activations = 0

    let effects: SupervisorEffects =
        { Now = fun () -> now
          Sleep =
            fun span ->
                sleeps.Add span
                now <- now + span
          Observe =
            fun () ->
                Ok
                    { Now = now
                      MissionState = (if launches = 0 then initialState else stateAfter launches)
                      OpenSlices = Some 5
                      StopRequested = false }
          Activate =
            fun () ->
                activations <- activations + 1
                Ok()
          Run =
            fun kind attempt ->
                kinds.Add((kind, attempt))
                launches <- launches + 1

                let duration =
                    match remaining with
                    | next :: rest ->
                        remaining <- rest
                        next
                    | [] -> TimeSpan.FromMinutes 30.0

                now <- now + duration
                Ok 0
          Log = ignore }

    let result = Supervisor.run policy effects
    result, List.ofSeq kinds, List.ofSeq sleeps, activations, now

let run (check: string -> bool -> unit) =
    // decide
    match Supervisor.decide policy Supervisor.initial None (observation "ready") with
    | Launch(Launcher.StartRun, after), state ->
        check "a ready mission is started at once" (after = TimeSpan.Zero && state.Launches = 1)
    | _ -> check "a ready mission is started at once" false

    check "an active mission is resumed, never restarted"
        (match Supervisor.decide policy Supervisor.initial None (observation "active") with
         | Launch(Launcher.ResumeRun, _), _ -> true
         | _ -> false)

    let finishes state = match Supervisor.decide policy { Launches = 3; ConsecutiveFastExits = 0 } (Some(TimeSpan.FromMinutes 40.0)) state with | Finish _, _ -> true | _ -> false

    check "a complete mission stops the supervisor" (finishes (observation "complete"))
    check "a blocked mission stops the supervisor for a person" (finishes (observation "blocked"))
    check "an abandoned mission stops the supervisor" (finishes (observation "abandoned"))
    check "a drained slice queue stops the supervisor" (finishes { observation "active" with OpenSlices = Some 0 })
    check "a stop request stops the supervisor" (finishes { observation "active" with StopRequested = true })
    check "the deadline stops the supervisor" (finishes { observation "active" with Now = until })
    check "a repository without an imported queue keeps working" (not (finishes { observation "active" with OpenSlices = None }))

    check "the launch budget stops the supervisor"
        (match Supervisor.decide { policy with MaxLaunches = 3 } { Launches = 3; ConsecutiveFastExits = 0 } (Some(TimeSpan.FromMinutes 40.0)) (observation "active") with
         | Finish reason, _ -> contains "launch budget (3)" reason
         | _ -> false)

    check "a wait that would cross the deadline is not started"
        (match Supervisor.decide policy { Launches = 3; ConsecutiveFastExits = 4 } (Some(TimeSpan.FromSeconds 5.0)) { observation "active" with Now = until - TimeSpan.FromMinutes 3.0 } with
         | Finish reason, _ -> contains "deadline" reason
         | _ -> false)

    let waits =
        [ 1..8 ]
        |> List.scan
            (fun (state, _) _ ->
                match Supervisor.decide policy state (Some(TimeSpan.FromSeconds 10.0)) (observation "active") with
                | Launch(_, after), next -> next, after
                | Finish _, next -> next, TimeSpan.MinValue)
            (Supervisor.initial, TimeSpan.Zero)
        |> List.tail
        |> List.map (snd >> _.TotalSeconds)

    check "fast exits back off from 30 s, doubling to a 15-minute ceiling"
        (waits = [ 30.0; 60.0; 120.0; 240.0; 480.0; 900.0; 900.0; 900.0 ])

    check "a session that worked for a while relaunches after a short pause and resets the backoff"
        (match Supervisor.decide policy { Launches = 5; ConsecutiveFastExits = 6 } (Some(TimeSpan.FromMinutes 25.0)) (observation "active") with
         | Launch(Launcher.ResumeRun, after), state -> after = policy.Pause && state.ConsecutiveFastExits = 0
         | _ -> false)

    // run loop with fake effects
    let result, kinds, sleeps, activations, _ =
        simulate policy [ TimeSpan.FromMinutes 50.0; TimeSpan.FromMinutes 70.0; TimeSpan.FromMinutes 20.0 ] (fun launches -> if launches >= 3 then "complete" else "active") "ready"

    check "the loop starts once, resumes until the mission completes, then stops"
        (result = Ok "The mission is complete."
         && kinds = [ Launcher.StartRun, 1; Launcher.ResumeRun, 2; Launcher.ResumeRun, 3 ]
         && activations = 1
         && sleeps = [ policy.Pause; policy.Pause ])

    let result, kinds, sleeps, _, finishedAt =
        simulate policy (List.replicate 500 (TimeSpan.FromSeconds 5.0)) (fun _ -> "active") "active"

    check "a crashing agent is relaunched with backoff until the deadline and never waited past it"
        ((match result with
          | Ok reason -> contains "deadline" reason
          | Error _ -> false)
         && kinds.Length > 20
         && finishedAt <= until
         && sleeps |> List.forall (fun span -> span <= policy.MaxBackoff))

    let failingActivation =
        Supervisor.run
            policy
            { Now = fun () -> start
              Sleep = ignore
              Observe = fun () -> Ok(observation "ready")
              Activate = fun () -> Error [ "praxis refused" ]
              Run = fun _ _ -> Ok 0
              Log = ignore }

    check "a mission that cannot be activated stops with the reason" (failingActivation = Error [ "praxis refused" ])

    let missingAgent =
        Supervisor.run
            policy
            { Now = fun () -> start
              Sleep = ignore
              Observe = fun () -> Ok(observation "active")
              Activate = fun () -> Ok()
              Run = fun _ _ -> Error "Unable to execute 'claude': not found on PATH."
              Log = ignore }

    check "a missing agent executable stops instead of spinning" (missingAgent = Error [ "Unable to execute 'claude': not found on PATH." ])

    // launcher command and instruction
    check "Claude runs headless with an explicit permission mode"
        (match Launcher.command ready "go" with
         | Ok(executable, arguments) -> executable = "claude" && arguments = [ "-p"; "--permission-mode"; "auto"; "--output-format"; "text"; "go" ]
         | Error _ -> false)

    check "the permission mode comes from the readiness"
        (match Launcher.command { ready with PermissionMode = "bypassPermissions" } "go" with
         | Ok(_, arguments) -> arguments |> List.pairwise |> List.contains ("--permission-mode", "bypassPermissions")
         | Error _ -> false)

    check "Codex stays available but is no longer the default"
        (Launcher.command { ready with Launcher = "codex" } "go" = Ok("codex", [ "exec"; "--full-auto"; "go" ]))

    check "an unknown launcher is refused" (match Launcher.command { ready with Launcher = "gemini" } "go" with Error _ -> true | Ok _ -> false)

    let first = Launcher.instructionFor (context Launcher.StartRun 1 (Some "requirements-trace.json") (Some until)) ready
    let later = Launcher.instructionFor (context Launcher.ResumeRun 3 (Some "requirements-trace.json") (Some until)) ready
    let plain = Launcher.instruction ready

    check "the first launch executes the mission from the kickoff contract"
        (first.StartsWith("Execute the active Praxis mission COND-MISSION-001", StringComparison.Ordinal)
         && contains "kickoff/indy-init.kickoff.json" first)

    check "a later launch resumes from Praxis state"
        (later.StartsWith("Resume the active Praxis mission COND-MISSION-001 in this repository (launch 3)", StringComparison.Ordinal)
         && contains "praxis work continue" later)

    check "with an imported queue the agent works the slices in order and never waits for a person"
        (contains "SLICE-*" first && contains "requirements-trace.json attachment of COND-MISSION-001" first
         && contains "never-cut slices first" first && contains "Never wait for a person" first)

    check "the deadline is stated with its offset" (contains "Hard deadline 2026-11-07T16:40:00-05:00" first)
    check "every launch publishes through pull requests to the protected main" (contains "The main branch is protected" plain)
    check "without a queue or deadline nothing about them is said" (not (contains "SLICE-" plain) && not (contains "deadline" plain))

    // manifest
    check "the Indy preset launches Claude in auto permission mode"
        (match Presets.resolve "indy-init" |> Result.bind (fun preset -> Manifest.load preset.ManifestPath) with
         | Ok manifest ->
             manifest.Execution
             |> Option.exists (fun execution -> execution.Launcher = Some "claude" && execution.PermissionMode = Some "auto")
         | Error _ -> false)

    let manifestWith (mode: string) =
        Manifest.parseText
            $"""{{"schemaVersion":1,"name":"m","components":[],"execution":{{"enabled":true,"launcher":"claude","contractPath":"c.json","permissionMode":"{mode}"}}}}"""

    check "a headless permission mode is accepted" (match manifestWith "bypassPermissions" with Ok _ -> true | Error _ -> false)

    check "a mode that waits for a person is refused"
        (match manifestWith "plan" with
         | Error errors -> errors |> List.exists (contains "Unsupported execution permissionMode 'plan'")
         | Ok _ -> false)

    check "an undeclared mode defaults to auto" (Readiness.DefaultPermissionMode = "auto" && Launcher.DefaultPermissionMode = "auto")

    // observation of Praxis state on disk
    let target = Path.Combine(Path.GetTempPath(), $"conditor-supervisor-{Guid.NewGuid():N}")
    Directory.CreateDirectory(Path.Combine(target, ".ros", "work")) |> ignore
    Directory.CreateDirectory(Path.Combine(target, ".ros", "context")) |> ignore

    try
        File.WriteAllText(
            Path.Combine(target, ".ros", "work", "queue.json"),
            """{"items":[
{"id":"COND-MISSION-001","status":"ready","tags":["conditor"],"attachments":[{"name":"requirements-trace.json","file":"1-requirements-trace.json"}]},
{"id":"SLICE-A","status":"complete","tags":["indy-init","slice"]},
{"id":"SLICE-B","status":"ready","tags":["indy-init","slice"]},
{"id":"SLICE-C","status":"ready","tags":["indy-init","slice"]},
{"id":"WI-1","status":"ready","tags":[]}]}"""
        )

        File.WriteAllText(
            Path.Combine(target, ".ros", "context", "current.json"),
            """{"workItems":[{"id":"COND-MISSION-001","semanticState":"active"},{"id":"SLICE-C","semanticState":"complete"}]}"""
        )

        check "the live execution state wins and only open slices count"
            (Supervisor.observeState target "COND-MISSION-001" = Ok("active", Some 1))

        check "the imported trace is found on the mission" (Supervisor.importedTrace target "COND-MISSION-001" = Some "requirements-trace.json")

        check "a missing mission is an error"
            (match Supervisor.observeState target "COND-MISSION-404" with
             | Error _ -> true
             | Ok _ -> false)

        File.WriteAllText(
            Path.Combine(target, ".ros", "work", "queue.json"),
            """{"items":[{"id":"COND-MISSION-001","status":"ready","tags":[],"attachments":[]}]}"""
        )

        check "without imported slices there is no queue to drain"
            (Supervisor.observeState target "COND-MISSION-001" = Ok("active", None)
             && Supervisor.importedTrace target "COND-MISSION-001" = None)
    finally
        Directory.Delete(target, true)
