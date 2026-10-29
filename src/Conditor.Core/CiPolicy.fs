namespace Conditor.Core

open System
open System.IO
open System.Text.Json.Nodes

/// A concurrency setting, at workflow or job level.
type WorkflowConcurrency =
    { Group: string
      CancelInProgress: string option
      Line: int }

/// One job, read only as far as the CI policy needs.
type WorkflowJob =
    { Id: string
      Line: int
      If: string option
      TimeoutMinutes: string option
      Uses: string option
      Concurrency: WorkflowConcurrency option }

/// A GitHub Actions workflow, read only as far as the CI policy needs.
type Workflow =
    { Path: string
      Triggers: string list
      /// The `push` trigger's branch filter: None when `push` runs for every branch.
      PushFiltered: bool
      DispatchInputs: string list
      Concurrency: WorkflowConcurrency option
      Jobs: WorkflowJob list }

/// One breach of the CI policy.
type CiFinding =
    { Rule: string
      Path: string
      Line: int
      Message: string }

/// The CI policy's verdict over a repository's workflows.
type CiPolicyReport =
    { Workflows: string list
      Findings: CiFinding list }

    member this.Passed = List.isEmpty this.Findings

/// The CI policy (Indy-init R7-REM-004): the workflow shapes that starved
/// Rehearsal 6's CI for 124 minutes are findings, before they can run.
///
/// - CIP-000 a workflow this check cannot read (fails closed, never passes);
/// - CIP-001 a per-commit concurrency group in a push or pull-request
///   workflow, at workflow or job level (superseded runs pile up);
/// - CIP-002 a push or pull-request workflow with no workflow-level group;
/// - CIP-003 a pull-request workflow that does not cancel superseded runs;
/// - CIP-004 a job-level `always()` (it runs even when the run is cancelled;
///   use `!cancelled()`);
/// - CIP-005 a job that runs steps with no `timeout-minutes`;
/// - CIP-006 an unfiltered `push` together with `pull_request` (every
///   pull-request push runs twice);
/// - CIP-007 a candidate workflow that development pushes, or another
///   candidate, can cancel: it must be dispatch-only, keyed on its candidate
///   input, and never cancel in progress.
///
/// The reader understands the block and flow YAML that workflows use for
/// `on`, `concurrency` and job keys. Anchors, aliases, merge keys and lines
/// it cannot place in those sections are CIP-000.
[<RequireQualifiedAccess>]
module CiPolicy =

    type private Line = { Number: int; Indent: int; Text: string }

    type private Entry =
        | Pair of key: string * value: string option * line: int * children: Entry list
        | Item of text: string * line: int * children: Entry list
        | Unreadable of line: int * text: string

    /// The line without its comment: a `#` at the start or after whitespace,
    /// outside quotes.
    let private uncomment (text: string) =
        let rec scan index quote previous =
            if index >= text.Length then
                text
            else
                let c = text.[index]

                match quote with
                | Some q when c = q -> scan (index + 1) None c
                | Some _ -> scan (index + 1) quote c
                | None when c = '\'' || c = '"' -> scan (index + 1) (Some c) c
                | None when c = '#' && (index = 0 || Char.IsWhiteSpace previous) -> text.Substring(0, index)
                | None -> scan (index + 1) None c

        scan 0 None ' '

    let private lines (text: string) =
        text.Replace("\r\n", "\n").Split('\n')
        |> Array.mapi (fun index raw ->
            let content = (uncomment raw).TrimEnd()

            { Number = index + 1
              Indent = content.Length - content.TrimStart().Length
              Text = content.Trim() })
        |> Array.filter (fun line -> line.Text <> "" && line.Text <> "---")
        |> List.ofArray

    let private unquote (text: string) =
        let text = text.Trim()

        if text.Length >= 2 && ((text.StartsWith "'" && text.EndsWith "'") || (text.StartsWith "\"" && text.EndsWith "\"")) then
            text.Substring(1, text.Length - 2)
        else
            text

    /// `key: value` or `key:`, with an optionally quoted key.
    let private keyValue (text: string) =
        let key, rest =
            if text.StartsWith "\"" || text.StartsWith "'" then
                let close = text.IndexOf(text.[0], 1)
                if close < 0 then "", "" else text.Substring(1, close - 1), text.Substring(close + 1)
            else
                let colon = text.IndexOf ':'
                if colon <= 0 then "", "" else text.Substring(0, colon), text.Substring colon

        if key = "" || not (rest.StartsWith ":") || key.Contains ' ' && not (text.StartsWith "\"" || text.StartsWith "'") then
            None
        elif rest = ":" then
            Some(key, None)
        elif Char.IsWhiteSpace rest.[1] then
            let value = rest.Substring(1).Trim()
            Some(key, (if value = "" then None else Some value))
        else
            None

    let private isBlockScalar (value: string) =
        value.Length <= 3 && (value.StartsWith "|" || value.StartsWith ">")

    let private joined (deeper: Line list) =
        deeper |> List.map _.Text |> String.concat " "

    let rec private block (lines: Line list) : Entry list =
        match lines with
        | [] -> []
        | first :: _ ->
            let indent = first.Indent

            let rec go acc remaining =
                match remaining with
                | [] -> List.rev acc
                | (line: Line) :: rest when line.Indent = indent ->
                    let deeper = rest |> List.takeWhile (fun next -> next.Indent > indent)
                    go (entry line deeper :: acc) (List.skip deeper.Length rest)
                | line :: rest -> go (Unreadable(line.Number, line.Text) :: acc) rest

            go [] lines

    and private entry (line: Line) (deeper: Line list) =
        if line.Text = "-" || line.Text.StartsWith "- " then
            Item(line.Text.Substring(1).Trim(), line.Number, block deeper)
        else
            match keyValue line.Text with
            | Some(key, Some value) when isBlockScalar value -> Pair(key, Some(joined deeper), line.Number, [])
            // A plain or quoted scalar continued on deeper lines.
            | Some(key, Some value) when not deeper.IsEmpty -> Pair(key, Some(value + " " + joined deeper), line.Number, [])
            | Some(key, value) -> Pair(key, value, line.Number, block deeper)
            | None -> Unreadable(line.Number, line.Text)

    let private find (key: string) (entries: Entry list) =
        entries
        |> List.tryPick (function
            | Pair(k, value, line, children) when k = key -> Some(value, line, children)
            | _ -> None)

    let private value key entries =
        find key entries |> Option.bind (fun (value, _, _) -> value) |> Option.map unquote

    /// `[a, b]`, a single scalar, or a block of `- item` entries.
    let private sequence (value: string option) (children: Entry list) =
        match value with
        | Some text when text.StartsWith "[" && text.EndsWith "]" ->
            text.Substring(1, text.Length - 2).Split(',') |> Array.map unquote |> Array.filter ((<>) "") |> List.ofArray
        | Some text -> [ unquote text ]
        | None ->
            children
            |> List.choose (function
                | Item(text, _, _) -> Some(unquote text)
                | _ -> None)

    let private unreadable (entries: Entry list) =
        entries
        |> List.choose (function
            | Unreadable(line, text) -> Some(line, text)
            | Pair(key, value, line, _) when key.StartsWith "<<" || key.StartsWith "&" || key.StartsWith "*" -> Some(line, key)
            | Pair(key, Some value, line, _) when value.StartsWith "&" || value.StartsWith "*" -> Some(line, $"{key}: {value}")
            | _ -> None)

    let private concurrency (entries: Entry list) =
        find "concurrency" entries
        |> Option.map (fun (scalar, line, children) ->
            match scalar with
            | Some group ->
                { Group = unquote group
                  CancelInProgress = None
                  Line = line }
            | None ->
                { Group = value "group" children |> Option.defaultValue ""
                  CancelInProgress = value "cancel-in-progress" children
                  Line = find "group" children |> Option.map (fun (_, groupLine, _) -> groupLine) |> Option.defaultValue line })

    let private job (id: string) (line: int) (children: Entry list) =
        { Id = id
          Line = line
          If = value "if" children
          TimeoutMinutes = value "timeout-minutes" children
          Uses = value "uses" children
          Concurrency = concurrency children }

    /// The workflow, or the lines this check could not read (CIP-000).
    let parse (path: string) (text: string) : Result<Workflow, (int * string) list> =
        let top = block (lines text)
        let on = find "on" top |> Option.orElse (find "true" top)
        let jobs = find "jobs" top |> Option.map (fun (_, _, children) -> children) |> Option.defaultValue []

        let triggers, onChildren =
            match on with
            | Some(Some value, _, _) -> sequence (Some value) [], []
            | Some(None, _, children) ->
                children
                |> List.choose (function
                    | Pair(key, _, _, _) -> Some key
                    | Item(text, _, _) -> Some(unquote text)
                    | Unreadable _ -> None),
                children
            | None -> [], []

        let pushFiltered =
            match find "push" onChildren with
            | Some(_, _, children) ->
                [ "branches"; "branches-ignore"; "tags"; "tags-ignore" ]
                |> List.exists (fun key -> (find key children).IsSome)
            | None -> false

        let inputs =
            find "workflow_dispatch" onChildren
            |> Option.bind (fun (_, _, children) -> find "inputs" children)
            |> Option.map (fun (_, _, children) ->
                children
                |> List.choose (function
                    | Pair(key, _, _, _) -> Some key
                    | _ -> None))
            |> Option.defaultValue []

        let jobKeys =
            jobs
            |> List.collect (function
                | Pair(_, _, _, children) -> children
                | _ -> [])

        let concurrencyChildren =
            find "concurrency" top |> Option.map (fun (_, _, children) -> children) |> Option.defaultValue []

        let problems =
            [ yield! unreadable top
              yield! unreadable onChildren
              yield! unreadable concurrencyChildren
              yield! unreadable jobs
              yield! unreadable jobKeys
              if on.IsNone then
                  yield 1, "no `on:` trigger section"
              if triggers.IsEmpty then
                  yield (on |> Option.map (fun (_, line, _) -> line) |> Option.defaultValue 1), "no trigger could be read" ]

        if not problems.IsEmpty then
            Error problems
        else
            Ok
                { Path = path
                  Triggers = triggers
                  PushFiltered = pushFiltered
                  DispatchInputs = inputs
                  Concurrency = concurrency top
                  Jobs =
                    jobs
                    |> List.choose (function
                        | Pair(id, _, line, children) -> Some(job id line children)
                        | _ -> None) }

    let private developmentTriggers = [ "push"; "pull_request"; "pull_request_target" ]

    let private perCommitMarkers =
        [ "github.sha"
          "github.run_id"
          "github.run_number"
          "github.run_attempt"
          "github.event.after"
          "github.event.pull_request.head.sha"
          "github.event.head_commit.id" ]

    let private normalized (text: string) =
        text.ToLowerInvariant().Replace(" ", "")

    /// True when a group names one commit or one run: every push then gets a
    /// group of its own and nothing is ever superseded.
    let isPerCommit (group: string) =
        let group = normalized group
        perCommitMarkers |> List.exists group.Contains

    let private isExpression (text: string) = text.Contains "${{"

    let private finding (workflow: Workflow) rule line message =
        { Rule = rule
          Path = workflow.Path
          Line = line
          Message = message }

    let private candidateInputs (workflow: Workflow) =
        workflow.DispatchInputs |> List.filter (fun input -> input.ToLowerInvariant().Contains "candidate")

    /// Every finding for one workflow.
    let check (workflow: Workflow) : CiFinding list =
        let development = workflow.Triggers |> List.exists (fun t -> List.contains t developmentTriggers)
        let pullRequests = workflow.Triggers |> List.exists (fun t -> t = "pull_request" || t = "pull_request_target")
        let at = finding workflow
        let candidates = candidateInputs workflow

        [ // CIP-001 and CIP-002: one group per ref, never per commit.
          if development then
              match workflow.Concurrency with
              | None -> yield at "CIP-002" 1 "a push or pull-request workflow has no workflow-level concurrency group, so superseded runs accumulate"
              | Some c when isPerCommit c.Group ->
                  yield at "CIP-001" c.Line $"concurrency group `{c.Group}` is per commit: every push runs in parallel and none is superseded (Rehearsal 6 starvation)"
              | Some _ -> ()

              for job in workflow.Jobs do
                  match job.Concurrency with
                  | Some c when isPerCommit c.Group -> yield at "CIP-001" c.Line $"job `{job.Id}`: concurrency group `{c.Group}` is per commit"
                  | _ -> ()

          // CIP-003: superseded pull-request runs are cancelled.
          if pullRequests then
              match workflow.Concurrency with
              | Some { CancelInProgress = Some cancel } when cancel = "true" || isExpression cancel -> ()
              | Some c -> yield at "CIP-003" c.Line "a pull-request workflow does not cancel superseded runs (`cancel-in-progress: true`)"
              | None -> ()

          // CIP-004: cancelled runs must not start jobs.
          for job in workflow.Jobs do
              match job.If with
              | Some condition when (normalized condition).Contains "always()" ->
                  yield at "CIP-004" job.Line $"job `{job.Id}`: `if: {condition}` runs even when the run is cancelled; use `!cancelled()`"
              | _ -> ()

          // CIP-005: nothing holds a runner for the six-hour default.
          for job in workflow.Jobs do
              if job.Uses.IsNone && job.TimeoutMinutes.IsNone then
                  yield at "CIP-005" job.Line $"job `{job.Id}` has no `timeout-minutes`"

          // CIP-006: one run per pull-request push, not two.
          if List.contains "push" workflow.Triggers && pullRequests && not workflow.PushFiltered then
              yield at "CIP-006" 1 "`push` runs for every branch and `pull_request` too: every pull-request push runs twice; filter `push` to `branches: [main]`"

          // CIP-007: a candidate's verification is cancelled by nothing.
          if not candidates.IsEmpty then
              if development then
                  yield at "CIP-007" 1 "a candidate workflow must be dispatch-only: a development push must not start or cancel it"

              match workflow.Concurrency with
              | None -> yield at "CIP-007" 1 "a candidate workflow needs a concurrency group keyed on its candidate input"
              | Some c ->
                  let group = normalized c.Group

                  if not (candidates |> List.exists (fun input -> group.Contains $"inputs.{input.ToLowerInvariant()}")) then
                      yield at "CIP-007" c.Line $"candidate concurrency group `{c.Group}` is not keyed on the candidate input, so another candidate can cancel or queue behind it"

                  if c.CancelInProgress |> Option.exists (fun cancel -> cancel = "true" || isExpression cancel) then
                      yield at "CIP-007" c.Line "a candidate workflow must never cancel in progress" ]

    /// The verdict over `(path, text)` workflow files.
    let evaluate (files: (string * string) list) : CiPolicyReport =
        { Workflows = files |> List.map fst
          Findings =
            files
            |> List.collect (fun (path, text) ->
                match parse path text with
                | Ok workflow -> check workflow
                | Error problems ->
                    problems
                    |> List.map (fun (line, text) ->
                        { Rule = "CIP-000"
                          Path = path
                          Line = line
                          Message = $"cannot read `{text}`; the policy fails closed" })) }

    /// The repository's `.github/workflows/*.yml` and `*.yaml` files, as
    /// repository-relative paths and text, in name order.
    let workflowFiles (root: string) =
        let directory = Path.Combine(root, ".github", "workflows")

        if Directory.Exists directory then
            Directory.GetFiles directory
            |> Array.filter (fun file -> file.EndsWith ".yml" || file.EndsWith ".yaml")
            |> Array.sort
            |> Array.map (fun file -> Path.GetRelativePath(root, file).Replace('\\', '/'), File.ReadAllText file)
            |> List.ofArray
        else
            []

    /// The report as `conditor.ci-policy/1` JSON.
    let toJson (report: CiPolicyReport) =
        let findings = JsonArray()

        report.Findings
        |> List.iter (fun f ->
            let item = JsonObject()
            item["rule"] <- JsonValue.Create f.Rule
            item["path"] <- JsonValue.Create f.Path
            item["line"] <- JsonValue.Create f.Line
            item["message"] <- JsonValue.Create f.Message
            findings.Add item)

        let workflows = JsonArray()
        report.Workflows |> List.iter (fun path -> workflows.Add(JsonValue.Create path))
        let root = JsonObject()
        root["schema"] <- JsonValue.Create "conditor.ci-policy/1"
        root["verdict"] <- JsonValue.Create(if report.Passed then "PASS" else "FAIL")
        root["workflows"] <- workflows
        root["findings"] <- findings
        root.ToJsonString(System.Text.Json.JsonSerializerOptions(WriteIndented = true, IndentSize = 2)) + "\n"
