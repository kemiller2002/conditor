namespace Conditor.Core

open System
open System.IO
open System.Text.Json

type RequirementsImportObservation =
    { Spec: RequirementsImportSpec
      Plan: RequirementsImportPlan
      Actions: ImportAction list }

type RequirementsImportOutcome =
    { Applied: ImportAction list
      Failed: (ImportAction * ProcessResult) option
      Remaining: ImportAction list
      Commit: string option }

/// The effectful shell around RequirementsImport: it reads the manifest, the
/// materialized documents and the Praxis queue, and applies the decided
/// actions through the Praxis CLI and Git. Processes run through an injected
/// runner (executable, arguments) so tests never need Praxis or Git.
module RequirementsImportRun =
    /// The Praxis work item Conditor's execution handoff creates; the trace
    /// lives as its attachment.
    [<Literal>]
    let Umbrella = "COND-MISSION-001"

    let private safePath (target: string) (relative: string) =
        let root = Path.GetFullPath target
        let full = Path.GetFullPath(Path.Combine(root, relative))
        let prefix = root.TrimEnd(Path.DirectorySeparatorChar) + string Path.DirectorySeparatorChar

        if full.StartsWith(prefix, StringComparison.Ordinal) then
            Ok full
        else
            Error [ $"Path escapes the target repository: {relative}" ]

    let private readText target relative =
        safePath target relative
        |> Result.bind (fun path ->
            if File.Exists path then
                Ok(File.ReadAllText path)
            else
                Error [ $"Source document is missing: {relative}. Run 'conditor init' (and 'conditor status') first." ])

    let private property (element: JsonElement) (name: string) =
        let mutable value = Unchecked.defaultof<JsonElement>

        if element.ValueKind = JsonValueKind.Object && element.TryGetProperty(name, &value) then
            Some value
        else
            None

    let private stringOf (element: JsonElement) name =
        match property element name with
        | Some value when value.ValueKind = JsonValueKind.String -> value.GetString() |> Option.ofObj
        | _ -> None

    /// The queue's items and the umbrella's attachments as (name, content).
    let readQueue (target: string) =
        let queuePath = Path.Combine(target, ".ros", "work", "queue.json")

        if not (File.Exists queuePath) then
            Error [ $"Praxis work queue is missing: {queuePath}. Run 'conditor init' first." ]
        else
            try
                use document = JsonDocument.Parse(File.ReadAllText queuePath)

                match property document.RootElement "items" with
                | Some items when items.ValueKind = JsonValueKind.Array ->
                    let parsed =
                        [ for item in items.EnumerateArray() do
                              match stringOf item "id" with
                              | Some id ->
                                  let tags =
                                      match property item "tags" with
                                      | Some values when values.ValueKind = JsonValueKind.Array ->
                                          values.EnumerateArray() |> Seq.choose (fun tag -> tag.GetString() |> Option.ofObj) |> List.ofSeq
                                      | _ -> []

                                  let attachments =
                                      match property item "attachments" with
                                      | Some values when values.ValueKind = JsonValueKind.Array ->
                                          values.EnumerateArray()
                                          |> Seq.choose (fun attachment ->
                                              match stringOf attachment "name", stringOf attachment "file" with
                                              | Some name, Some file -> Some(name, file)
                                              | _ -> None)
                                          |> List.ofSeq
                                      | _ -> []

                                  yield
                                      ({ Id = id
                                         Title = stringOf item "title" |> Option.defaultValue ""
                                         Description = stringOf item "description"
                                         Priority = stringOf item "priority" |> Option.defaultValue ""
                                         Status = stringOf item "status" |> Option.defaultValue ""
                                         Source = stringOf item "source"
                                         SourceReference = stringOf item "sourceReference"
                                         Tags = tags }: ExistingWorkItem),
                                      attachments
                              | None -> () ]

                    let items = parsed |> List.map (fun (item, _) -> item.Id, item) |> Map.ofList

                    let umbrellaAttachments =
                        parsed
                        |> List.tryFind (fun (item, _) -> item.Id = Umbrella)
                        |> Option.map snd
                        |> Option.defaultValue []
                        |> List.map (fun (name, file) ->
                            let path = Path.Combine(target, ".ros", "work", "attachments", Umbrella, file)
                            name, (if File.Exists path then File.ReadAllText path else String.Empty))

                    Ok(items, umbrellaAttachments)
                | _ -> Error [ "Praxis work queue does not contain an 'items' array." ]
            with :? JsonException as ex ->
                Error [ $"Praxis work queue is not valid JSON: {ex.Message}" ]

    /// Reads everything the import depends on and decides the actions.
    /// Nothing is written.
    let observe (target: string) (manifestPath: string) =
        let manifestText =
            if File.Exists manifestPath then
                Ok(File.ReadAllText manifestPath)
            else
                Error [ $"Manifest not found: {manifestPath}" ]

        manifestText
        |> Result.bind RequirementsImport.readSpec
        |> Result.bind (fun spec ->
            let paths = (spec.Documents |> List.map _.Path) @ [ spec.Kickoff ]

            let sources =
                paths
                |> List.map (fun path -> readText target path |> Result.map (fun content -> path, content))

            let errors = sources |> List.collect (fun result -> match result with Error errors -> errors | Ok _ -> [])

            if not errors.IsEmpty then
                Error errors
            else
                let sourceMap =
                    sources |> List.choose (fun result -> match result with Ok pair -> Some pair | Error _ -> None) |> Map.ofList

                RequirementsImport.plan spec Umbrella sourceMap
                |> Result.bind (fun plan ->
                    readQueue target
                    |> Result.bind (fun (items, attachments) ->
                        RequirementsImport.reconcile spec plan Umbrella items attachments
                        |> Result.map (fun actions ->
                            { Spec = spec
                              Plan = plan
                              Actions = actions }))))

    /// The Praxis command line for one action. `tracePath` is where the
    /// trace content was written for `work attach`.
    let commandFor (spec: RequirementsImportSpec) (occurredAt: string) (tracePath: string) (action: ImportAction) =
        match action with
        | CaptureItem item ->
            [ "work"; "capture"
              "--id"; item.Id
              "--title"; item.Title
              "--description"; item.Description
              "--priority"; item.Priority
              "--source"; spec.Source
              "--source-reference"; item.SourceReference
              "--actor"; "conditor"
              "--occurred-at"; occurredAt ]
            @ (item.Tags |> List.collect (fun tag -> [ "--tag"; tag ]))
        | MarkReady id ->
            [ "work"; "backlog-transition"; "--id"; id; "--action"; "ready"; "--occurred-at"; occurredAt ]
        | AttachTrace(workItem, name, _) ->
            [ "work"; "attach"; "--id"; workItem; "--file"; $"{tracePath}={name}"; "--occurred-at"; occurredAt ]

    let describe action =
        match action with
        | CaptureItem item -> $"capture {item.Id} ({item.Priority}, {item.Requirements.Length} source requirements)"
        | MarkReady id -> $"mark {id} ready"
        | AttachTrace(workItem, name, _) -> $"attach {name} to {workItem}"

    /// Applies the actions in order and stops at the first failure. Then, if
    /// anything changed, records the Praxis state as one commit of `.ros`
    /// only. `run` executes a process in the target; `now` gives the
    /// occurred-at timestamp; `withTrace` writes the trace to a file, calls
    /// the continuation with its path and removes it.
    let apply
        (run: string -> string list -> ProcessResult)
        (now: unit -> string)
        (withTrace: string -> (string -> ProcessResult) -> ProcessResult)
        (commit: bool)
        (observation: RequirementsImportObservation)
        =
        let runAction action =
            match action with
            | AttachTrace(_, _, content) ->
                withTrace content (fun path -> run "praxis" (commandFor observation.Spec (now ()) path action))
            | _ -> run "praxis" (commandFor observation.Spec (now ()) String.Empty action)

        let rec loop applied remaining =
            match remaining with
            | [] -> List.rev applied, None, []
            | action :: rest ->
                let result = runAction action

                if result.ExitCode = 0 then
                    loop (action :: applied) rest
                else
                    List.rev applied, Some(action, result), rest

        let applied, failed, remaining = loop [] observation.Actions

        let committed =
            if commit && not applied.IsEmpty then
                let items = observation.Plan.Items.Length
                let traced = observation.Plan.RequirementCount

                let message =
                    $"Import {observation.Spec.Source} requirements: {items} slice work items, {traced} source requirements traced\n\nconditor requirements import, plan sha256:{observation.Plan.Digest}"

                let added = run "git" [ "add"; "--all"; "--"; ".ros" ]

                if added.ExitCode <> 0 then
                    Error $"git add failed: {added.StandardError.Trim()}"
                else
                    let result = run "git" [ "commit"; "--quiet"; "-m"; message; "--"; ".ros" ]

                    if result.ExitCode <> 0 then
                        Error $"git commit failed: {result.StandardError.Trim()} {result.StandardOutput.Trim()}"
                    else
                        let head = run "git" [ "rev-parse"; "HEAD" ]
                        Ok(Some(head.StandardOutput.Trim()))
            else
                Ok None

        match committed with
        | Error error ->
            Error(
                error,
                { Applied = applied
                  Failed = failed
                  Remaining = remaining
                  Commit = None }
            )
        | Ok commitId ->
            Ok
                { Applied = applied
                  Failed = failed
                  Remaining = remaining
                  Commit = commitId }

    /// The import commits only Praxis state. It refuses to start when `.ros`
    /// already has uncommitted changes, or anything is staged, so the commit
    /// never sweeps in someone else's work.
    let commitPreconditions (run: string -> string list -> ProcessResult) =
        let inside = run "git" [ "rev-parse"; "--is-inside-work-tree" ]

        if inside.ExitCode <> 0 then
            [ "The target is not a Git repository; the import records its result as a commit." ]
        else
            let staged = run "git" [ "diff"; "--cached"; "--name-only" ]
            let praxis = run "git" [ "status"; "--porcelain"; "--"; ".ros" ]

            [ if not (String.IsNullOrWhiteSpace staged.StandardOutput) then
                  yield "Changes are already staged. Commit or unstage them first; the import commits only its own Praxis state."
              if not (String.IsNullOrWhiteSpace praxis.StandardOutput) then
                  yield "The Praxis state (.ros) has uncommitted changes. Commit them first; the import commits only its own Praxis state." ]
