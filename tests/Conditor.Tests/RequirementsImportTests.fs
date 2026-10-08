module RequirementsImportTests

open System
open System.IO
open System.Text.Json
open Conditor.Core

// A small planning set with the same shape as Indy-init: two requirement
// documents, a kickoff with priority slices, gates and stop-the-line rules,
// one added slice, and assignment rules of every kind.

let private requirementsDoc =
    "# Requirements\n\n## R1. Core\n\n### R1.1 Observation\ntext\n\n### R1.2 Identity\ntext\n\n## R2. UI\n\n### R2.1 Workspace\ntext\n"

let private scenariosDoc =
    "# Scenarios\n\n## A1. Keep the source\ntext\n\n## A2. Extra behaviour\ntext\n"

let private kickoffDoc =
    """{"prioritySlices":[{"order":2,"id":"ui","goal":"one workspace","cutPolicy":"cut-last"},{"order":1,"id":"core","goal":"typed kernel","cutPolicy":"never-cut"}],"successGates":[{"id":"domain","evidence":"x"}],"stopTheLine":["UI reimplements legality"]}"""

let private specJson =
    """{
  "workItemPrefix": "SLICE",
  "source": "demo",
  "kickoff": "kickoff/demo.json",
  "traceAttachment": "requirements-trace.json",
  "documents": [
    {"scheme": "R", "path": "docs/REQ.md", "heading": "^### (R[0-9]+\\.[0-9]+) (.+)$", "expected": 3},
    {"scheme": "A", "path": "docs/SCEN.md", "heading": "^## (A[0-9]+)\\. (.+)$", "expected": 2}
  ],
  "kickoffExpected": {"prioritySlices": 2, "successGates": 1, "stopTheLine": 1},
  "additionalSlices": [
    {"id": "extra", "after": "core", "cutPolicy": "stretch", "goal": "extra behaviour", "basis": "plan section 9"}
  ],
  "assignments": [
    {"slice": "core", "match": ["R1.*", "A1", "K-SLICE-core", "K-GATE-domain", "K-STOP-01"]},
    {"slice": "extra", "match": ["A2..A2"]},
    {"slice": "ui", "match": ["R2.*", "K-SLICE-ui"]}
  ]
}"""

let private sources =
    Map.ofList [ "docs/REQ.md", requirementsDoc; "docs/SCEN.md", scenariosDoc; "kickoff/demo.json", kickoffDoc ]

let private parse (json: string) =
    use document = JsonDocument.Parse json
    RequirementsImport.parseSpec document.RootElement

let private spec =
    match parse specJson with
    | Ok spec -> spec
    | Error errors -> failwith (String.concat "; " errors)

let private joined (errors: string list) = String.concat "; " errors

let private failsWith (needle: string) result =
    match result with
    | Error errors -> errors |> List.exists (fun (error: string) -> error.Contains(needle, StringComparison.Ordinal))
    | Ok _ -> false

let private planWith (spec: RequirementsImportSpec) (sources: Map<string, string>) =
    RequirementsImport.plan spec "COND-MISSION-001" sources

let private withAssignments assignments = { spec with Assignments = assignments }

let private ok =
    { ExitCode = 0
      StandardOutput = String.Empty
      StandardError = String.Empty }

let private umbrella =
    { Id = "COND-MISSION-001"
      Title = "Build"
      Description = None
      Priority = "high"
      Status = "ready"
      Source = Some "conditor"
      SourceReference = None
      Tags = [] }

/// The queue as Praxis records it after the plan was applied.
let private recorded (plan: RequirementsImportPlan) status =
    plan.Items
    |> List.map (fun item ->
        item.Id,
        { Id = item.Id
          Title = item.Title
          Description = Some item.Description
          Priority = item.Priority
          Status = status
          Source = Some "demo"
          SourceReference = Some item.SourceReference
          Tags = item.Tags })
    |> Map.ofList
    |> Map.add umbrella.Id umbrella

let run (check: string -> bool -> unit) =
    // The embedded Indy preset carries a valid specification.
    match Presets.resolve "indy-init" with
    | Error errors -> check $"Indy preset resolves: {joined errors}" false
    | Ok preset ->
        match RequirementsImport.readSpec preset.Content with
        | Error errors -> check $"the Indy preset's requirementsImport parses: {joined errors}" false
        | Ok indy ->
            let schemes = indy.Documents |> List.map (fun document -> document.Scheme, document.Expected)

            check "the Indy import reads the eight requirement schemes with their expected counts"
                (schemes = [ "R", 82; "P", 464; "A", 250; "D", 61; "OQ", 48; "I", 20; "S", 25; "F", 10 ])

            check "the Indy import adds eleven slices to the kickoff's twelve" (indy.AdditionalSlices.Length = 11)
            check "every Indy slice has an assignment" (indy.Assignments.Length = 23)

            check "every Indy source document is materialized by the preset"
                (match Manifest.parseText preset.Content with
                 | Ok manifest ->
                     let targets = manifest.Requirements |> List.map _.TargetPath |> Set.ofList
                     (indy.Kickoff :: (indy.Documents |> List.map _.Path)) |> List.forall targets.Contains
                 | Error _ -> false)

    // Plan.
    match planWith spec sources with
    | Error errors -> check $"the demo set plans: {joined errors}" false
    | Ok plan ->
        check "slices follow the kickoff order with the added slice after its anchor"
            (plan.Items |> List.map _.Id = [ "SLICE-CORE"; "SLICE-EXTRA"; "SLICE-UI" ])

        check "priorities follow the cut policy"
            (plan.Items |> List.map _.Priority = [ "high"; "low"; "medium" ])

        check "every slice depends on the nearest earlier never-cut slice"
            (plan.Items |> List.map _.DependsOn = [ None; Some "SLICE-CORE"; Some "SLICE-CORE" ])

        let owned = plan.Items |> List.map _.Requirements

        check "each source requirement is owned by exactly one item"
            (owned = [ [ "R1.1"; "R1.2"; "A1"; "K-SLICE-core"; "K-GATE-domain"; "K-STOP-01" ]; [ "A2" ]; [ "R2.1"; "K-SLICE-ui" ] ])

        check "all nine source requirements are counted" (plan.RequirementCount = 9)

        let core = plan.Items.Head

        check "an item says what it delivers, when it runs and which requirements it covers"
            (core.Title = "Slice 01 core: typed kernel"
             && core.Description.Contains("cut policy never-cut", StringComparison.Ordinal)
             && core.Description.Contains("- R (2): R1.1..R1.2", StringComparison.Ordinal)
             && core.Description.Contains("requirements-trace.json attachment of COND-MISSION-001", StringComparison.Ordinal))

        check "a dependent item names its dependency in the phrase Praxis infers"
            (plan.Items[1].Description.Contains("Depends on SLICE-CORE.", StringComparison.Ordinal))

        check "kickoff slices reference the kickoff; added slices reference the manifest"
            (core.SourceReference = "kickoff/demo.json#prioritySlices/core"
             && plan.Items[1].SourceReference = "conditor.json#requirementsImport/additionalSlices/extra")

        check "items are tagged with the source, the slice marker and the cut policy"
            (core.Tags = [ "demo"; "slice"; "cut:never-cut" ])

        use trace = JsonDocument.Parse plan.Trace
        let root = trace.RootElement
        let traced = root.GetProperty("requirements").EnumerateArray() |> List.ofSeq

        check "the trace maps every source requirement to its work item"
            (traced.Length = 9
             && traced
                |> List.forall (fun entry -> entry.GetProperty("workItem").GetString() |> Option.ofObj |> Option.exists (fun id -> id.StartsWith("SLICE-", StringComparison.Ordinal))))

        check "the trace records where each requirement is defined"
            (traced
             |> List.exists (fun entry ->
                 entry.GetProperty("id").GetString() = "R1.2"
                 && entry.GetProperty("document").GetString() = "docs/REQ.md"
                 && entry.GetProperty("line").GetInt32() = 8
                 && entry.GetProperty("heading").GetString() = "Identity"
                 && entry.GetProperty("workItem").GetString() = "SLICE-CORE"))

        check "the trace carries its schema, the plan digest and the hash of every source"
            (root.GetProperty("schema").GetString() = RequirementsImport.TraceSchema
             && root.GetProperty("planDigest").GetString() = $"sha256:{plan.Digest}"
             && root.GetProperty("documents").GetArrayLength() = 3)

        check "planning is deterministic" (planWith spec sources = Ok plan)

        check "a changed source changes the digest"
            (match planWith spec (sources |> Map.add "docs/SCEN.md" (scenariosDoc.Replace("Extra behaviour", "Changed"))) with
             | Ok changed -> changed.Digest <> plan.Digest
             | Error _ -> false)

        // Reconcile.
        let onlyUmbrella = Map.ofList [ umbrella.Id, umbrella ]

        match RequirementsImport.reconcile spec plan umbrella.Id onlyUmbrella [] with
        | Error errors -> check $"a fresh queue reconciles: {joined errors}" false
        | Ok actions ->
            let expected =
                [ for item in plan.Items do
                      yield CaptureItem item
                      yield MarkReady item.Id
                  yield AttachTrace(umbrella.Id, "requirements-trace.json", plan.Trace) ]

            check "a fresh queue captures and readies every item, then attaches the trace" (actions = expected)

        check "a re-run against the imported queue changes nothing"
            (RequirementsImport.reconcile spec plan umbrella.Id (recorded plan "ready") [ "requirements-trace.json", plan.Trace ] = Ok [])

        check "an item someone already started or completed is left alone"
            (RequirementsImport.reconcile spec plan umbrella.Id (recorded plan "complete") [ "requirements-trace.json", plan.Trace ] = Ok [])

        let readied = plan.Items |> List.map (fun item -> MarkReady item.Id)

        check "an item captured but not yet ready is only marked ready"
            (RequirementsImport.reconcile spec plan umbrella.Id (recorded plan "captured") [ "requirements-trace.json", plan.Trace ] = Ok readied)

        let edited =
            recorded plan "ready"
            |> Map.change "SLICE-UI" (Option.map (fun item -> { item with Description = Some "edited by hand" }))

        check "an item with different content is a conflict, and nothing is done"
            (RequirementsImport.reconcile spec plan umbrella.Id edited [] |> failsWith "SLICE-UI exists with a different description")

        check "a different trace already attached is a conflict"
            (RequirementsImport.reconcile spec plan umbrella.Id (recorded plan "ready") [ "requirements-trace.json", "{}" ]
             |> failsWith "different requirements-trace.json")

        check "without the umbrella work item nothing is imported"
            (RequirementsImport.reconcile spec plan umbrella.Id (recorded plan "ready" |> Map.remove umbrella.Id) []
             |> failsWith "umbrella work item COND-MISSION-001 is missing")

    // Acceptance criteria from factory decisions.
    let withCriteria = { spec with Acceptance = [ { Slice = "ui"; Criteria = [ "Every page ships a CSP."; "Input is validated." ] } ] }

    match planWith withCriteria sources with
    | Error errors -> check $"acceptance criteria plan: {joined errors}" false
    | Ok plan ->
        let ui = plan.Items |> List.find (fun item -> item.Id = "SLICE-UI")

        check "a slice lists the acceptance criteria its factory decisions add"
            (ui.Description.Contains("Acceptance criteria (factory decisions):\n- Every page ships a CSP.\n- Input is validated.", StringComparison.Ordinal))

        check "slices without criteria say nothing about them"
            (plan.Items |> List.filter (fun item -> item.Id <> "SLICE-UI") |> List.forall (fun item -> not (item.Description.Contains("Acceptance criteria", StringComparison.Ordinal))))

        check "acceptance criteria are part of the plan digest"
            (match planWith spec sources with
             | Ok plain -> plain.Digest <> plan.Digest
             | Error _ -> false)

    check "acceptance criteria for an unknown slice are refused"
        (planWith { spec with Acceptance = [ { Slice = "nowhere"; Criteria = [ "x" ] } ] } sources
         |> failsWith "Acceptance criteria name unknown slice 'nowhere'")

    check "two acceptance entries for one slice are refused"
        (planWith { spec with Acceptance = [ { Slice = "ui"; Criteria = [ "x" ] }; { Slice = "ui"; Criteria = [ "y" ] } ] } sources
         |> failsWith "Slice 'ui' has more than one acceptance entry")

    check "an acceptance entry without criteria is refused"
        (parse (specJson.Replace("\"assignments\": [", "\"acceptance\": [{\"slice\": \"ui\", \"criteria\": []}],\n  \"assignments\": ["))
         |> failsWith "'criteria' must be a non-empty array")

    // The Indy decisions (DF-CON-2026-A002) as the preset carries them.
    match Presets.resolve "indy-init" |> Result.bind (fun preset -> RequirementsImport.readSpec preset.Content) with
    | Error errors -> check $"the Indy specification reads: {joined errors}" false
    | Ok indy ->
        let criteriaFor slice =
            indy.Acceptance |> List.tryFind (fun entry -> entry.Slice = slice) |> Option.map (fun entry -> String.concat " " entry.Criteria) |> Option.defaultValue ""

        check "demo readiness is never-cut: a demo is required to compete"
            (indy.AdditionalSlices |> List.exists (fun slice -> slice.Id = "demo-readiness" && slice.CutPolicy = NeverCut))

        check "security hardening stays cut-last"
            (indy.AdditionalSlices |> List.exists (fun slice -> slice.Id = "security-hardening" && slice.CutPolicy = CutLast))

        check "multi-agent keeps the kickoff's stretch policy: the preset neither adds nor overrides it"
            (indy.AdditionalSlices |> List.forall (fun slice -> slice.Id <> "multi-agent")
             && indy.Acceptance |> List.forall (fun entry -> entry.Slice <> "multi-agent"))

        check "never-cut slices carry baseline security: no secrets in the client, a CSP, input validation"
            (criteriaFor "bootstrap" |> fun text -> text.Contains("no credential, token or provider key", StringComparison.Ordinal) && text.Contains("Content-Security-Policy", StringComparison.Ordinal)
             && (criteriaFor "workspace").Contains("validated at the domain boundary", StringComparison.Ordinal)
             && (criteriaFor "ai-proposals").Contains("held in memory for the session only", StringComparison.Ordinal))

        check "the factory view publishes a generated, read-only trace view"
            ((criteriaFor "factory-proof").Contains("read-only requirements trace view", StringComparison.Ordinal))

        check "the demo deploys to GitHub Pages with the DEMO-PLAN scenario"
            ((criteriaFor "demo-readiness").Contains("GitHub Pages", StringComparison.Ordinal)
             && (criteriaFor "demo-readiness").Contains("order-delivery", StringComparison.Ordinal))

    // Fail closed.
    check "a document that changed shape is refused"
        (planWith { spec with Documents = spec.Documents |> List.map (fun document -> { document with Expected = 4 }) } sources
         |> failsWith "expected 4 R requirements, found 3")

    let fourRequirements =
        { spec with Documents = spec.Documents |> List.map (fun document -> if document.Scheme = "R" then { document with Expected = 4 } else document) }

    check "a repeated source ID is refused"
        (planWith fourRequirements (sources |> Map.add "docs/REQ.md" (requirementsDoc + "\n### R2.1 Again\n"))
         |> failsWith "R2.1 appears 2 times")

    check "a source requirement no rule matches is refused by name"
        (planWith (withAssignments (spec.Assignments |> List.map (fun assignment -> if assignment.Slice = "extra" then { assignment with Match = [ "A1..A1" ] } else assignment))) sources
         |> failsWith "A2 (docs/SCEN.md) matches no assignment rule")

    check "a source requirement two rules match is refused"
        (planWith (withAssignments (spec.Assignments @ [ { Slice = "ui"; Match = [ "R1.1" ] } ])) sources
         |> failsWith "R1.1 matches more than one rule")

    check "a rule that matches nothing is refused"
        (planWith (withAssignments (spec.Assignments @ [ { Slice = "ui"; Match = [ "R9.*" ] } ])) sources
         |> failsWith "Rule 'R9.*' for slice 'ui' matches no source requirement")

    check "an assignment to an unknown slice is refused"
        (planWith (withAssignments (spec.Assignments @ [ { Slice = "nowhere"; Match = [ "R9.1" ] } ])) sources
         |> failsWith "unknown slice 'nowhere'")

    check "an added slice after an unknown slice is refused"
        (planWith { spec with AdditionalSlices = spec.AdditionalSlices |> List.map (fun slice -> { slice with After = "nowhere" }) } sources
         |> failsWith "follows unknown slice 'nowhere'")

    check "a slice without requirements is refused"
        (planWith (withAssignments (spec.Assignments |> List.filter (fun assignment -> assignment.Slice <> "extra") |> List.map (fun assignment -> if assignment.Slice = "core" then { assignment with Match = assignment.Match @ [ "A2" ] } else assignment))) sources
         |> failsWith "Slice 'extra' has no source requirements")

    check "an empty or unbounded rule is refused"
        (planWith (withAssignments (spec.Assignments @ [ { Slice = "ui"; Match = [ "A5..A2"; "*" ] } ])) sources
         |> fun result -> failsWith "is an empty range" result && failsWith "would match everything" result)

    check "a missing source document is refused"
        (planWith spec (sources |> Map.remove "docs/SCEN.md") |> failsWith "Source document is missing: docs/SCEN.md")

    check "a kickoff that is not JSON is refused" (planWith spec (sources |> Map.add "kickoff/demo.json" "{") |> failsWith "is not valid JSON")

    check "a kickoff with a different number of gates is refused"
        (planWith spec (sources |> Map.add "kickoff/demo.json" (kickoffDoc.Replace("""{"id":"domain","evidence":"x"}""", """{"id":"domain"},{"id":"ui"}""")))
         |> failsWith "expected 1 successGates, found 2")

    check "kickoff slice orders must be 1..n"
        (planWith spec (sources |> Map.add "kickoff/demo.json" (kickoffDoc.Replace("\"order\":2", "\"order\":3")))
         |> failsWith "orders must be 1..2")

    check "an unknown cut policy is refused"
        (planWith spec (sources |> Map.add "kickoff/demo.json" (kickoffDoc.Replace("cut-last", "maybe"))) |> failsWith "Unknown cut policy 'maybe'")

    check "an unknown specification field is refused" (parse (specJson.Replace("\"source\": \"demo\"", "\"source\": \"demo\", \"extra\": 1")) |> failsWith "unknown field 'extra'")
    check "a heading pattern without two groups is refused" (parse (specJson.Replace("^## (A[0-9]+)\\\\. (.+)$", "^## A")) |> failsWith "must capture the ID")
    check "an invalid heading pattern is refused" (parse (specJson.Replace("^## (A[0-9]+)\\\\. (.+)$", "^## (A")) |> failsWith "not a valid regular expression")
    check "a manifest without requirementsImport has nothing to import" (RequirementsImport.readSpec "{\"schemaVersion\":1}" |> failsWith "declares no 'requirementsImport'")

    let compressed =
        RequirementsImport.compress [ "A1"; "A2"; "A3"; "A5"; "P1.1"; "P1.2"; "P2.1"; "D-001"; "D-002"; "K-GATE-domain" ]

    check "IDs collapse into runs of one prefix"
        (compressed = [ "A1..A3"; "A5"; "P1.1..P1.2"; "P2.1"; "D-001..D-002"; "K-GATE-domain" ])

    // The effectful shell, against a real target directory and a fake runner.
    let target = Path.Combine(Path.GetTempPath(), $"conditor-import-{Guid.NewGuid():N}")

    let write (relative: string) (content: string) =
        let path = Path.Combine(target, relative)
        Directory.CreateDirectory(Path.GetDirectoryName(path) |> Option.ofObj |> Option.defaultValue target) |> ignore
        File.WriteAllText(path, content)

    try
        write "conditor.json" ("{\"schemaVersion\":1,\"name\":\"demo\",\"components\":[],\"requirementsImport\":" + specJson + "}")
        sources |> Map.iter write
        write ".ros/work/queue.json" """{"items":[{"id":"COND-MISSION-001","title":"Build","status":"ready","priority":"high","source":"conditor","tags":[],"attachments":[]}]}"""

        match RequirementsImportRun.observe target (Path.Combine(target, "conditor.json")) with
        | Error errors -> check $"the shell observes a prepared target: {joined errors}" false
        | Ok observation ->
            check "the shell plans the whole import against the queue on disk"
                (observation.Plan.Items.Length = 3 && observation.Actions.Length = 7)

            let calls = ResizeArray<string * string list>()
            let traces = ResizeArray<string>()

            let runner failOn executable (arguments: string list) =
                calls.Add((executable, arguments))

                if failOn executable arguments then
                    { ok with ExitCode = 1; StandardError = "refused" }
                else
                    { ok with StandardOutput = "abc123\n" }

            let withTrace (content: string) (action: string -> ProcessResult) =
                traces.Add content
                action "/tmp/trace.json"

            match RequirementsImportRun.apply (runner (fun _ _ -> false)) (fun () -> "2026-11-07T14:45:00.000Z") withTrace true observation with
            | Error(error, _) -> check $"the shell applies every action: {error}" false
            | Ok outcome ->
                let praxisCalls = calls |> Seq.filter (fun (executable, _) -> executable = "praxis") |> Seq.map snd |> List.ofSeq
                let gitCalls = calls |> Seq.filter (fun (executable, _) -> executable = "git") |> Seq.map snd |> List.ofSeq

                check "every action runs once, in order, through Praxis"
                    (outcome.Applied = observation.Actions && outcome.Failed.IsNone && praxisCalls.Length = 7)

                let expectedCapture =
                       [ "work"; "capture"; "--id"; "SLICE-CORE"; "--title"; "Slice 01 core: typed kernel"
                         "--description"; observation.Plan.Items.Head.Description; "--priority"; "high"
                         "--source"; "demo"; "--source-reference"; "kickoff/demo.json#prioritySlices/core"
                         "--actor"; "conditor"; "--occurred-at"; "2026-11-07T14:45:00.000Z"
                         "--tag"; "demo"; "--tag"; "slice"; "--tag"; "cut:never-cut" ]

                check "a capture passes the deterministic id, title, description, priority, source and tags"
                    (praxisCalls.Head = expectedCapture)

                let expectedAttach =
                    [ "work"; "attach"; "--id"; "COND-MISSION-001"; "--file"; "/tmp/trace.json=requirements-trace.json"; "--occurred-at"; "2026-11-07T14:45:00.000Z" ]

                check "the trace is attached to the umbrella from a file"
                    (List.last praxisCalls = expectedAttach && List.ofSeq traces = [ observation.Plan.Trace ])

                check "the result is committed as Praxis state only"
                    (gitCalls.Length = 3
                     && gitCalls[0] = [ "add"; "--all"; "--"; ".ros" ]
                     && gitCalls[1] |> List.take 2 = [ "commit"; "--quiet" ]
                     && gitCalls[1] |> List.rev |> List.take 2 = [ ".ros"; "--" ]
                     && outcome.Commit = Some "abc123")

            calls.Clear()

            match
                RequirementsImportRun.apply
                    (runner (fun executable arguments -> executable = "praxis" && arguments |> List.contains "SLICE-EXTRA"))
                    (fun () -> "2026-11-07T14:45:00.000Z")
                    withTrace
                    true
                    observation
            with
            | Error(error, _) -> check $"a partial run still commits what it applied: {error}" false
            | Ok outcome ->
                check "a failing step stops the run and reports what remains"
                    (outcome.Applied.Length = 2
                     && outcome.Failed |> Option.exists (fun (action, _) -> action = CaptureItem observation.Plan.Items[1])
                     && outcome.Remaining.Length = 4
                     && calls |> Seq.filter (fun (executable, _) -> executable = "praxis") |> Seq.length = 3)

                check "what was applied before the failure is still committed, so a re-run resumes"
                    (outcome.Commit = Some "abc123")

        let statusRunner (staged: string) (praxis: string) _ (arguments: string list) =
            match arguments with
            | [ "rev-parse"; "--is-inside-work-tree" ] -> ok
            | [ "diff"; "--cached"; "--name-only" ] -> { ok with StandardOutput = staged }
            | "status" :: _ -> { ok with StandardOutput = praxis }
            | _ -> ok

        check "a clean target may be imported" (RequirementsImportRun.commitPreconditions (statusRunner "" "") = [])
        check "staged changes block the import"
            (RequirementsImportRun.commitPreconditions (statusRunner "src/app.fs\n" "") |> List.exists (fun error -> error.Contains("already staged", StringComparison.Ordinal)))
        check "uncommitted Praxis state blocks the import"
            (RequirementsImportRun.commitPreconditions (statusRunner "" " M .ros/work/queue.json\n") |> List.exists (fun error -> error.Contains("uncommitted changes", StringComparison.Ordinal)))
        check "outside Git the import refuses"
            (RequirementsImportRun.commitPreconditions (fun _ _ -> { ok with ExitCode = 128 }) |> List.exists (fun error -> error.Contains("not a Git repository", StringComparison.Ordinal)))

        File.Delete(Path.Combine(target, "docs", "SCEN.md"))

        check "the shell refuses when a materialized document is missing"
            (RequirementsImportRun.observe target (Path.Combine(target, "conditor.json")) |> failsWith "Source document is missing: docs/SCEN.md")
    finally
        if Directory.Exists target then
            Directory.Delete(target, true)
