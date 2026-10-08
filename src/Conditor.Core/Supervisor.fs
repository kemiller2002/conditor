namespace Conditor.Core

open System
open System.IO
open System.Text.Json

/// How the supervisor keeps a headless agent working.
type SupervisorPolicy =
    { /// No launch starts at or after this moment, and no wait runs past it.
      Until: DateTimeOffset option
      MaxLaunches: int
      /// A run shorter than this counts as a fast exit (a crash, an auth or
      /// usage-limit refusal) and backs off before the next launch.
      FastExit: TimeSpan
      BaseBackoff: TimeSpan
      MaxBackoff: TimeSpan
      /// The pause after a run that worked for a while before it ended.
      Pause: TimeSpan }

type SupervisorState =
    { Launches: int
      ConsecutiveFastExits: int }

/// What the supervisor sees between runs.
type SupervisorObservation =
    { Now: DateTimeOffset
      /// The effective Praxis state of the umbrella mission.
      MissionState: string
      /// Imported slice items not yet complete or abandoned; None when the
      /// repository has no imported queue.
      OpenSlices: int option
      StopRequested: bool }

type SupervisorDecision =
    | Launch of kind: Launcher.LaunchKind * after: TimeSpan
    | Finish of reason: string

/// The launched process and its supervisor, as functions the run loop calls.
type SupervisorEffects =
    { Now: unit -> DateTimeOffset
      Sleep: TimeSpan -> unit
      Observe: unit -> Result<SupervisorObservation, string list>
      /// Activates a ready mission (the `start` transition).
      Activate: unit -> Result<unit, string list>
      /// Runs one agent session to its end and returns its exit code.
      Run: Launcher.LaunchKind -> int -> Result<int, string>
      Log: string -> unit }

/// Keeps the agent working until the mission is done, the deadline passes,
/// a person asks it to stop, or the launch budget is spent. Every session
/// is a fresh process: Praxis and the repository, not a conversation, carry
/// continuity (AGENTS.md "Durable checkpoints and continuity").
module Supervisor =
    let defaultPolicy until =
        { Until = until
          MaxLaunches = 200
          FastExit = TimeSpan.FromMinutes 2.0
          BaseBackoff = TimeSpan.FromSeconds 30.0
          MaxBackoff = TimeSpan.FromMinutes 15.0
          Pause = TimeSpan.FromSeconds 10.0 }

    let initial = { Launches = 0; ConsecutiveFastExits = 0 }

    let private backoff (policy: SupervisorPolicy) (fastExits: int) =
        let factor = Math.Pow(2.0, float (max 0 (fastExits - 1)))
        let seconds = min policy.MaxBackoff.TotalSeconds (policy.BaseBackoff.TotalSeconds * factor)
        TimeSpan.FromSeconds seconds

    /// The next step, given the last run's duration (None before the first
    /// launch) and what is observed now. Pure.
    let decide (policy: SupervisorPolicy) (state: SupervisorState) (lastRun: TimeSpan option) (observation: SupervisorObservation) =
        let fastExits =
            match lastRun with
            | Some duration when duration < policy.FastExit -> state.ConsecutiveFastExits + 1
            | Some _ -> 0
            | None -> state.ConsecutiveFastExits

        let state = { state with ConsecutiveFastExits = fastExits }

        let wait =
            match lastRun with
            | None -> TimeSpan.Zero
            | Some _ when fastExits > 0 -> backoff policy fastExits
            | Some _ -> policy.Pause

        let finish reason = Finish reason, state

        match observation.MissionState with
        | "complete" -> finish "The mission is complete."
        | "abandoned" -> finish "The mission was abandoned."
        | "blocked" -> finish "The mission is blocked and needs a person: see praxis status."
        | _ when observation.OpenSlices = Some 0 -> finish "Every imported slice is complete or abandoned."
        | _ when observation.StopRequested -> finish "Stop requested."
        | _ when state.Launches >= policy.MaxLaunches -> finish $"The launch budget ({policy.MaxLaunches}) is spent."
        | missionState ->
            match policy.Until with
            | Some until when observation.Now + wait >= until ->
                finish $"The deadline {until:o} is reached."
            | _ ->
                let kind =
                    if state.Launches = 0 && missionState = "ready" then
                        Launcher.StartRun
                    else
                        Launcher.ResumeRun

                Launch(kind, wait), { state with Launches = state.Launches + 1 }

    /// Runs the loop with the given effects and returns why it stopped.
    let run (policy: SupervisorPolicy) (effects: SupervisorEffects) =
        let rec loop state lastRun =
            match effects.Observe() with
            | Error errors -> Error errors
            | Ok observation ->
                match decide policy state lastRun observation with
                | Finish reason, _ -> Ok reason
                | Launch(kind, after), next ->
                    if after > TimeSpan.Zero then
                        effects.Log $"Waiting {after.TotalSeconds:F0}s before launch {next.Launches}."
                        effects.Sleep after

                    let activation =
                        match kind with
                        | Launcher.StartRun -> effects.Activate()
                        | Launcher.ResumeRun -> Ok()

                    match activation with
                    | Error errors -> Error errors
                    | Ok() ->
                        effects.Log $"Launch {next.Launches} ({kind})."
                        let started = effects.Now()

                        match effects.Run kind next.Launches with
                        | Error error -> Error [ error ]
                        | Ok exitCode ->
                            let duration = effects.Now() - started
                            effects.Log $"Launch {next.Launches} ended with exit code {exitCode} after {duration.TotalMinutes:F1} min."
                            loop next (Some duration)

        loop initial None

    // ------------------------------------------------------------------
    // Observation of the Praxis state on disk
    // ------------------------------------------------------------------

    let private property (element: JsonElement) (name: string) =
        let mutable value = Unchecked.defaultof<JsonElement>

        if element.ValueKind = JsonValueKind.Object && element.TryGetProperty(name, &value) then
            Some value
        else
            None

    let private text element name =
        match property element name with
        | Some value when value.ValueKind = JsonValueKind.String -> value.GetString() |> Option.ofObj
        | _ -> None

    let private items (path: string) (name: string) =
        if not (File.Exists path) then
            []
        else
            use document = JsonDocument.Parse(File.ReadAllText path)

            match property document.RootElement name with
            | Some values when values.ValueKind = JsonValueKind.Array -> values.EnumerateArray() |> Seq.map _.Clone() |> List.ofSeq
            | _ -> []

    /// The umbrella's effective state and the open imported slices. A live
    /// state in the execution context wins over the backlog status, as in
    /// `praxis work list`.
    let observeState (target: string) (missionId: string) =
        try
            let queue = items (Path.Combine(target, ".ros", "work", "queue.json")) "items"
            let context = items (Path.Combine(target, ".ros", "context", "current.json")) "workItems"

            let live =
                context
                |> List.choose (fun item ->
                    match text item "id", (text item "semanticState" |> Option.orElse (text item "state")) with
                    | Some id, Some state -> Some(id, state)
                    | _ -> None)
                |> Map.ofList

            let effective (item: JsonElement) =
                let id = text item "id" |> Option.defaultValue ""
                id, (Map.tryFind id live |> Option.orElse (text item "status") |> Option.defaultValue "")

            let states = queue |> List.map effective

            let isSlice (item: JsonElement) =
                match property item "tags" with
                | Some tags when tags.ValueKind = JsonValueKind.Array ->
                    tags.EnumerateArray() |> Seq.exists (fun tag -> tag.GetString() = "slice")
                | _ -> false

            let slices = queue |> List.filter isSlice |> List.map effective

            match states |> List.tryFind (fun (id, _) -> id = missionId) with
            | None -> Error [ $"Praxis mission {missionId} is missing from the work queue." ]
            | Some(_, missionState) ->
                let openSlices =
                    if slices.IsEmpty then
                        None
                    else
                        slices |> List.filter (fun (_, state) -> state <> "complete" && state <> "abandoned") |> List.length |> Some

                Ok(missionState, openSlices)
        with
        | :? JsonException as ex -> Error [ $"Praxis state is not valid JSON: {ex.Message}" ]
        | :? IOException as ex -> Error [ $"Praxis state cannot be read: {ex.Message}" ]

    /// The name of the requirements trace attached to the mission by
    /// `conditor requirements import`, when the queue holds imported slices.
    let importedTrace (target: string) (missionId: string) =
        try
            let queue = items (Path.Combine(target, ".ros", "work", "queue.json")) "items"

            let hasSlices =
                queue
                |> List.exists (fun item ->
                    match property item "tags" with
                    | Some tags when tags.ValueKind = JsonValueKind.Array ->
                        tags.EnumerateArray() |> Seq.exists (fun tag -> tag.GetString() = "slice")
                    | _ -> false)

            let attachment =
                queue
                |> List.tryFind (fun item -> text item "id" = Some missionId)
                |> Option.bind (fun mission -> property mission "attachments")
                |> Option.bind (fun attachments ->
                    if attachments.ValueKind = JsonValueKind.Array then
                        attachments.EnumerateArray()
                        |> Seq.choose (fun attachment -> text attachment "name")
                        |> Seq.tryFind (fun name -> name.EndsWith("requirements-trace.json", StringComparison.Ordinal))
                    else
                        None)

            if hasSlices then attachment else None
        with
        | :? JsonException
        | :? IOException -> None
