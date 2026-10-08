namespace Conditor.Core

open System
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open System.Text.RegularExpressions

type CutPolicy =
    | NeverCut
    | CutLast
    | Stretch
    | Deferred

type RequirementDocument =
    { Scheme: string
      Path: string
      /// A regular expression over one line; group 1 is the source ID,
      /// group 2 its heading text.
      Heading: string
      Expected: int }

type AddedSlice =
    { Id: string
      After: string
      CutPolicy: CutPolicy
      Goal: string
      Basis: string }

type SliceAssignment = { Slice: string; Match: string list }

type KickoffCounts =
    { PrioritySlices: int
      SuccessGates: int
      StopTheLine: int }

/// The `requirementsImport` section of a Conditor manifest.
type RequirementsImportSpec =
    { WorkItemPrefix: string
      Source: string
      Kickoff: string
      TraceAttachment: string
      Documents: RequirementDocument list
      KickoffExpected: KickoffCounts
      AdditionalSlices: AddedSlice list
      Assignments: SliceAssignment list }

/// One addressable source requirement, such as P3.4 or A12.
type SourceRequirement =
    { Id: string
      Scheme: string
      Document: string
      Line: int
      Heading: string }

type ImportSlice =
    { Id: string
      Goal: string
      CutPolicy: CutPolicy
      Basis: string
      SourceReference: string }

type PlannedWorkItem =
    { Id: string
      Slice: string
      Order: int
      Title: string
      Description: string
      Priority: string
      Tags: string list
      SourceReference: string
      DependsOn: string option
      CutPolicy: CutPolicy
      Requirements: string list }

type RequirementsImportPlan =
    { Items: PlannedWorkItem list
      Trace: string
      Digest: string
      RequirementCount: int }

/// A work item as the Praxis queue records it.
type ExistingWorkItem =
    { Id: string
      Title: string
      Description: string option
      Priority: string
      Status: string
      Source: string option
      SourceReference: string option
      Tags: string list }

type ImportAction =
    | CaptureItem of PlannedWorkItem
    | MarkReady of id: string
    | AttachTrace of workItem: string * name: string * content: string

/// Pure core of `conditor requirements import`: it turns the materialized
/// planning documents into a deterministic queue of slice work items plus a
/// trace of every source ID, and decides what must change in Praxis. It
/// reads no files and runs nothing.
module RequirementsImport =
    [<Literal>]
    let TraceSchema = "conditor.requirements-trace/v1"

    let private workItemPattern = Regex("^[A-Z][A-Z0-9_-]*-[A-Z0-9][A-Z0-9_-]*$")
    let private sliceIdPattern = Regex("^[a-z0-9]+(-[a-z0-9]+)*$")

    let cutPolicyName policy =
        match policy with
        | NeverCut -> "never-cut"
        | CutLast -> "cut-last"
        | Stretch -> "stretch"
        | Deferred -> "deferred"

    let parseCutPolicy (value: string) =
        match value with
        | "never-cut" -> Ok NeverCut
        | "cut-last" -> Ok CutLast
        | "stretch" -> Ok Stretch
        | "deferred" -> Ok Deferred
        | other -> Error [ $"Unknown cut policy '{other}'. Expected never-cut, cut-last, stretch or deferred." ]

    let priorityFor policy =
        match policy with
        | NeverCut -> "high"
        | CutLast -> "medium"
        | Stretch
        | Deferred -> "low"

    let private errorsOf result =
        match result with
        | Ok _ -> []
        | Error errors -> errors

    let private sequence (results: Result<'a, string list> list) =
        let errors = results |> List.collect errorsOf

        if errors.IsEmpty then
            Ok(results |> List.choose (fun result -> match result with Ok value -> Some value | Error _ -> None))
        else
            Error errors

    // ------------------------------------------------------------------
    // Specification
    // ------------------------------------------------------------------

    let private property (element: JsonElement) (name: string) =
        let mutable value = Unchecked.defaultof<JsonElement>

        if element.ValueKind = JsonValueKind.Object && element.TryGetProperty(name, &value) then
            Some value
        else
            None

    let private text (context: string) (element: JsonElement) (name: string) =
        match property element name with
        | Some value when value.ValueKind = JsonValueKind.String ->
            match value.GetString() |> Option.ofObj |> Option.filter (String.IsNullOrWhiteSpace >> not) with
            | Some value -> Ok value
            | None -> Error [ $"{context}: '{name}' must be a non-empty string." ]
        | _ -> Error [ $"{context}: '{name}' must be a non-empty string." ]

    let private count (context: string) (element: JsonElement) (name: string) =
        match property element name with
        | Some value when value.ValueKind = JsonValueKind.Number ->
            match value.TryGetInt32() with
            | true, number when number > 0 -> Ok number
            | _ -> Error [ $"{context}: '{name}' must be a positive integer." ]
        | _ -> Error [ $"{context}: '{name}' must be a positive integer." ]

    let private array (context: string) (element: JsonElement) (name: string) =
        match property element name with
        | Some value when value.ValueKind = JsonValueKind.Array && value.GetArrayLength() > 0 ->
            Ok(value.EnumerateArray() |> List.ofSeq)
        | _ -> Error [ $"{context}: '{name}' must be a non-empty array." ]

    let private onlyFields (context: string) (allowed: string list) (element: JsonElement) =
        if element.ValueKind <> JsonValueKind.Object then
            Error [ $"{context} must be an object." ]
        else
            let unknown =
                element.EnumerateObject()
                |> Seq.map _.Name
                |> Seq.filter (fun name -> not (List.contains name allowed))
                |> Seq.map (fun name -> $"{context}: unknown field '{name}'.")
                |> List.ofSeq

            if unknown.IsEmpty then Ok() else Error unknown

    let private parseDocument index (element: JsonElement) =
        let context = $"requirementsImport.documents[{index}]"

        match onlyFields context [ "scheme"; "path"; "heading"; "expected" ] element with
        | Error errors -> Error errors
        | Ok() ->
            match text context element "scheme", text context element "path", text context element "heading", count context element "expected" with
            | Ok scheme, Ok path, Ok heading, Ok expected ->
                let regex =
                    try
                        let compiled = Regex(heading)

                        if compiled.GetGroupNumbers().Length < 3 then
                            Error [ $"{context}: 'heading' must capture the ID (group 1) and the heading text (group 2)." ]
                        else
                            Ok()
                    with :? ArgumentException as ex ->
                        Error [ $"{context}: 'heading' is not a valid regular expression: {ex.Message}" ]

                regex
                |> Result.map (fun () ->
                    { Scheme = scheme
                      Path = path
                      Heading = heading
                      Expected = expected })
            | scheme, path, heading, expected ->
                Error(errorsOf scheme @ errorsOf path @ errorsOf heading @ errorsOf expected)

    let private parseAdded index (element: JsonElement) =
        let context = $"requirementsImport.additionalSlices[{index}]"

        match onlyFields context [ "id"; "after"; "cutPolicy"; "goal"; "basis" ] element with
        | Error errors -> Error errors
        | Ok() ->
            let policy = text context element "cutPolicy" |> Result.bind parseCutPolicy

            match text context element "id", text context element "after", policy, text context element "goal", text context element "basis" with
            | Ok id, Ok after, Ok policy, Ok goal, Ok basis ->
                if sliceIdPattern.IsMatch id then
                    Ok
                        { Id = id
                          After = after
                          CutPolicy = policy
                          Goal = goal
                          Basis = basis }
                else
                    Error [ $"{context}: slice id '{id}' must be lower-case words joined by '-'." ]
            | id, after, policy, goal, basis ->
                Error(errorsOf id @ errorsOf after @ errorsOf policy @ errorsOf goal @ errorsOf basis)

    let private parseAssignment index (element: JsonElement) =
        let context = $"requirementsImport.assignments[{index}]"

        match onlyFields context [ "slice"; "match" ] element with
        | Error errors -> Error errors
        | Ok() ->
            let rules =
                array context element "match"
                |> Result.bind (fun items ->
                    items
                    |> List.map (fun item ->
                        if item.ValueKind = JsonValueKind.String then
                            match item.GetString() |> Option.ofObj |> Option.filter (String.IsNullOrWhiteSpace >> not) with
                            | Some rule -> Ok rule
                            | None -> Error [ $"{context}: every match rule must be a non-empty string." ]
                        else
                            Error [ $"{context}: every match rule must be a non-empty string." ])
                    |> sequence)

            match text context element "slice", rules with
            | Ok slice, Ok rules -> Ok { Slice = slice; Match = rules }
            | slice, rules -> Error(errorsOf slice @ errorsOf rules)

    let private parseKickoffCounts (element: JsonElement) =
        let context = "requirementsImport.kickoffExpected"

        match onlyFields context [ "prioritySlices"; "successGates"; "stopTheLine" ] element with
        | Error errors -> Error errors
        | Ok() ->
            match count context element "prioritySlices", count context element "successGates", count context element "stopTheLine" with
            | Ok slices, Ok gates, Ok stops ->
                Ok
                    { PrioritySlices = slices
                      SuccessGates = gates
                      StopTheLine = stops }
            | slices, gates, stops -> Error(errorsOf slices @ errorsOf gates @ errorsOf stops)

    let private fields =
        [ "workItemPrefix"; "source"; "kickoff"; "traceAttachment"; "documents"; "kickoffExpected"; "additionalSlices"; "assignments" ]

    /// Parses the `requirementsImport` object. Every field is required and
    /// unknown fields are refused.
    let parseSpec (element: JsonElement) : Result<RequirementsImportSpec, string list> =
        let context = "requirementsImport"

        match onlyFields context fields element with
        | Error errors -> Error errors
        | Ok() ->
            let prefix =
                text context element "workItemPrefix"
                |> Result.bind (fun prefix ->
                    if Regex.IsMatch(prefix, "^[A-Z][A-Z0-9]*$") then
                        Ok prefix
                    else
                        Error [ $"{context}: 'workItemPrefix' must be upper-case letters and digits." ])

            let documents =
                array context element "documents"
                |> Result.bind (List.mapi parseDocument >> sequence)

            let kickoffExpected =
                match property element "kickoffExpected" with
                | Some value -> parseKickoffCounts value
                | None -> Error [ $"{context}: 'kickoffExpected' is required." ]

            let added =
                match property element "additionalSlices" with
                | Some value when value.ValueKind = JsonValueKind.Array ->
                    value.EnumerateArray() |> List.ofSeq |> List.mapi parseAdded |> sequence
                | _ -> Error [ $"{context}: 'additionalSlices' must be an array." ]

            let assignments =
                array context element "assignments"
                |> Result.bind (List.mapi parseAssignment >> sequence)

            match
                prefix,
                text context element "source",
                text context element "kickoff",
                text context element "traceAttachment",
                documents,
                kickoffExpected,
                added,
                assignments
            with
            | Ok prefix, Ok source, Ok kickoff, Ok attachment, Ok documents, Ok expected, Ok added, Ok assignments ->
                Ok
                    { WorkItemPrefix = prefix
                      Source = source
                      Kickoff = kickoff
                      TraceAttachment = attachment
                      Documents = documents
                      KickoffExpected = expected
                      AdditionalSlices = added
                      Assignments = assignments }
            | prefix, source, kickoff, attachment, documents, expected, added, assignments ->
                Error(
                    errorsOf prefix
                    @ errorsOf source
                    @ errorsOf kickoff
                    @ errorsOf attachment
                    @ errorsOf documents
                    @ errorsOf expected
                    @ errorsOf added
                    @ errorsOf assignments
                )

    /// Reads the `requirementsImport` section of a manifest text.
    let readSpec (manifestText: string) =
        try
            use document = JsonDocument.Parse manifestText

            match property document.RootElement "requirementsImport" with
            | None -> Error [ "The Conditor manifest declares no 'requirementsImport'; there is nothing to import." ]
            | Some element -> parseSpec element
        with :? JsonException as ex ->
            Error [ $"The Conditor manifest is not valid JSON: {ex.Message}" ]

    // ------------------------------------------------------------------
    // Source requirements
    // ------------------------------------------------------------------

    let private lines (content: string) =
        content.Replace("\r\n", "\n").Split('\n')

    /// Every heading the document's pattern matches, in document order. The
    /// count must equal the declared expectation, so a document that changed
    /// shape fails instead of importing a partial set.
    let extract (document: RequirementDocument) (content: string) =
        let pattern = Regex(document.Heading)

        let found =
            lines content
            |> Array.mapi (fun index line ->
                let matched = pattern.Match line

                if matched.Success then
                    Some
                        { Id = matched.Groups[1].Value
                          Scheme = document.Scheme
                          Document = document.Path
                          Line = index + 1
                          Heading = matched.Groups[2].Value.Trim() }
                else
                    None)
            |> Array.choose id
            |> List.ofArray

        if found.Length <> document.Expected then
            Error [ $"{document.Path}: expected {document.Expected} {document.Scheme} requirements, found {found.Length}. The pinned document changed shape; refusing a partial import." ]
        else
            Ok found

    let private kickoffSliceId (id: string) = $"K-SLICE-{id}"

    /// The kickoff's priority slices (the canonical order) and its slices,
    /// success gates and stop-the-line rules as traceable source IDs.
    let readKickoff (spec: RequirementsImportSpec) (content: string) =
        try
            use document = JsonDocument.Parse content
            let root = document.RootElement
            let context = spec.Kickoff

            let slices =
                array context root "prioritySlices"
                |> Result.bind (fun items ->
                    items
                    |> List.mapi (fun index item ->
                        let itemContext = $"{context} prioritySlices[{index}]"
                        let policy = text itemContext item "cutPolicy" |> Result.bind parseCutPolicy

                        match text itemContext item "id", text itemContext item "goal", policy, property item "order" with
                        | Ok id, Ok goal, Ok policy, Some order when order.ValueKind = JsonValueKind.Number ->
                            Ok(order.GetInt32(), ({ Id = id
                                                    Goal = goal
                                                    CutPolicy = policy
                                                    Basis = $"kickoff prioritySlices order {order.GetInt32()}"
                                                    SourceReference = $"{spec.Kickoff}#prioritySlices/{id}" }: ImportSlice))
                        | id, goal, policy, _ ->
                            Error(errorsOf id @ errorsOf goal @ errorsOf policy @ [ $"{itemContext}: 'order' must be a number." ] |> List.distinct))
                    |> sequence)
                |> Result.bind (fun ordered ->
                    let orders = ordered |> List.map fst

                    if List.sort orders <> [ 1 .. ordered.Length ] then
                        Error [ $"{context}: prioritySlices orders must be 1..{ordered.Length} without gaps or repeats." ]
                    elif ordered.Length <> spec.KickoffExpected.PrioritySlices then
                        Error [ $"{context}: expected {spec.KickoffExpected.PrioritySlices} prioritySlices, found {ordered.Length}." ]
                    else
                        Ok(ordered |> List.sortBy fst |> List.map snd))

            let gates =
                array context root "successGates"
                |> Result.bind (fun items ->
                    items
                    |> List.mapi (fun index item -> text $"{context} successGates[{index}]" item "id")
                    |> sequence)
                |> Result.bind (fun ids ->
                    if ids.Length <> spec.KickoffExpected.SuccessGates then
                        Error [ $"{context}: expected {spec.KickoffExpected.SuccessGates} successGates, found {ids.Length}." ]
                    else
                        Ok ids)

            let stops =
                array context root "stopTheLine"
                |> Result.bind (fun items ->
                    items
                    |> List.map (fun item ->
                        if item.ValueKind = JsonValueKind.String then
                            match item.GetString() |> Option.ofObj |> Option.filter (String.IsNullOrWhiteSpace >> not) with
                            | Some rule -> Ok rule
                            | None -> Error [ $"{context}: every stopTheLine rule must be a non-empty string." ]
                        else
                            Error [ $"{context}: every stopTheLine rule must be a non-empty string." ])
                    |> sequence)
                |> Result.bind (fun rules ->
                    if rules.Length <> spec.KickoffExpected.StopTheLine then
                        Error [ $"{context}: expected {spec.KickoffExpected.StopTheLine} stopTheLine rules, found {rules.Length}." ]
                    else
                        Ok rules)

            match slices, gates, stops with
            | Ok slices, Ok gates, Ok stops ->
                let requirement id heading =
                    { Id = id
                      Scheme = "K"
                      Document = spec.Kickoff
                      Line = 0
                      Heading = heading }

                let requirements =
                    (slices |> List.map (fun slice -> requirement (kickoffSliceId slice.Id) slice.Goal))
                    @ (gates |> List.map (fun gate -> requirement $"K-GATE-{gate}" $"success gate {gate}"))
                    @ (stops |> List.mapi (fun index rule -> requirement $"K-STOP-{index + 1:D2}" rule))

                Ok(slices, requirements)
            | slices, gates, stops -> Error(errorsOf slices @ errorsOf gates @ errorsOf stops)
        with :? JsonException as ex ->
            Error [ $"{spec.Kickoff} is not valid JSON: {ex.Message}" ]

    // ------------------------------------------------------------------
    // Slices and assignment
    // ------------------------------------------------------------------

    /// The kickoff order with each added slice inserted directly after its
    /// anchor, in specification order. An unknown anchor or a repeated id is
    /// an error.
    let orderSlices (kickoff: ImportSlice list) (added: AddedSlice list) =
        let folder (state: Result<ImportSlice list, string list>) (slice: AddedSlice) =
            state
            |> Result.bind (fun ordered ->
                if ordered |> List.exists (fun existing -> existing.Id = slice.Id) then
                    Error [ $"Slice '{slice.Id}' is declared more than once." ]
                else
                    match ordered |> List.tryFindIndex (fun existing -> existing.Id = slice.After) with
                    | None -> Error [ $"Added slice '{slice.Id}' follows unknown slice '{slice.After}'." ]
                    | Some index ->
                        let inserted =
                            { Id = slice.Id
                              Goal = slice.Goal
                              CutPolicy = slice.CutPolicy
                              Basis = slice.Basis
                              SourceReference = $"conditor.json#requirementsImport/additionalSlices/{slice.Id}" }

                        Ok(List.insertAt (index + 1) inserted ordered))

        added |> List.fold folder (Ok kickoff)

    type private Rule =
        | Exact of string
        | Prefix of string
        | PrefixDigits of string
        | Range of prefix: string * low: int * high: int

    let private trailingNumber = Regex("^(.*?)([0-9]+)$")

    let private parseRule (rule: string) =
        if rule.Contains("..", StringComparison.Ordinal) then
            match rule.Split("..") with
            | [| low; high |] ->
                let lowMatch = trailingNumber.Match low
                let highMatch = trailingNumber.Match high

                if lowMatch.Success && highMatch.Success && lowMatch.Groups[1].Value = highMatch.Groups[1].Value then
                    let lowNumber = int lowMatch.Groups[2].Value
                    let highNumber = int highMatch.Groups[2].Value

                    if lowNumber <= highNumber then
                        Ok(Range(lowMatch.Groups[1].Value, lowNumber, highNumber))
                    else
                        Error [ $"Match rule '{rule}' is an empty range." ]
                else
                    Error [ $"Match rule '{rule}' must be a range of one prefix, such as A1..A35." ]
            | _ -> Error [ $"Match rule '{rule}' must be a range of one prefix, such as A1..A35." ]
        elif rule.EndsWith("*", StringComparison.Ordinal) then
            let prefix = rule.Substring(0, rule.Length - 1)

            if prefix.Length = 0 then
                Error [ "Match rule '*' would match everything." ]
            elif prefix.EndsWith('.') || prefix.EndsWith('-') then
                Ok(Prefix prefix)
            else
                Ok(PrefixDigits prefix)
        else
            Ok(Exact rule)

    let private ruleMatches rule (id: string) =
        match rule with
        | Exact value -> id = value
        | Prefix prefix -> id.StartsWith(prefix, StringComparison.Ordinal)
        | PrefixDigits prefix ->
            id.StartsWith(prefix, StringComparison.Ordinal)
            && id.Length > prefix.Length
            && id.Substring(prefix.Length) |> Seq.forall Char.IsDigit
        | Range(prefix, low, high) ->
            if id.StartsWith(prefix, StringComparison.Ordinal) && id.Length > prefix.Length then
                let rest = id.Substring(prefix.Length)

                rest |> Seq.forall Char.IsDigit
                && (let number = int rest in number >= low && number <= high)
            else
                false

    /// Maps every source ID to exactly one slice. Fails closed on an ID no
    /// rule matches, an ID two rules match, a rule that matches nothing, an
    /// assignment to an unknown slice and a slice left without requirements.
    let assign (slices: ImportSlice list) (assignments: SliceAssignment list) (requirements: SourceRequirement list) =
        let sliceIds = slices |> List.map _.Id |> Set.ofList

        let unknownSlices =
            assignments
            |> List.filter (fun assignment -> not (sliceIds.Contains assignment.Slice))
            |> List.map (fun assignment -> $"Assignment names unknown slice '{assignment.Slice}'.")

        let parsed =
            assignments
            |> List.collect (fun assignment ->
                assignment.Match |> List.map (fun rule -> assignment.Slice, rule, parseRule rule))

        let ruleErrors = parsed |> List.collect (fun (_, _, rule) -> errorsOf rule)

        if not unknownSlices.IsEmpty || not ruleErrors.IsEmpty then
            Error(unknownSlices @ ruleErrors)
        else
            let rules =
                parsed
                |> List.choose (fun (slice, text, rule) ->
                    match rule with
                    | Ok rule -> Some(slice, text, rule)
                    | Error _ -> None)

            let matchesFor (requirement: SourceRequirement) =
                rules |> List.filter (fun (_, _, rule) -> ruleMatches rule requirement.Id)

            let unmatched =
                requirements
                |> List.filter (fun requirement -> (matchesFor requirement).IsEmpty)
                |> List.map (fun requirement -> $"Source requirement {requirement.Id} ({requirement.Document}) matches no assignment rule.")

            let ambiguous =
                requirements
                |> List.choose (fun requirement ->
                    match matchesFor requirement with
                    | first :: second :: _ ->
                        let describe (slice, text, _) = $"{text} -> {slice}"
                        Some $"Source requirement {requirement.Id} matches more than one rule: {describe first}; {describe second}."
                    | _ -> None)

            let unused =
                rules
                |> List.filter (fun (_, _, rule) -> requirements |> List.exists (fun requirement -> ruleMatches rule requirement.Id) |> not)
                |> List.map (fun (slice, text, _) -> $"Rule '{text}' for slice '{slice}' matches no source requirement.")

            if not unmatched.IsEmpty || not ambiguous.IsEmpty || not unused.IsEmpty then
                Error(unmatched @ ambiguous @ unused)
            else
                let assigned =
                    requirements
                    |> List.map (fun requirement ->
                        let slice, _, _ = (matchesFor requirement).Head
                        requirement, slice)

                let empty =
                    slices
                    |> List.filter (fun slice -> assigned |> List.exists (fun (_, owner) -> owner = slice.Id) |> not)
                    |> List.map (fun slice -> $"Slice '{slice.Id}' has no source requirements.")

                if empty.IsEmpty then Ok assigned else Error empty

    // ------------------------------------------------------------------
    // Plan
    // ------------------------------------------------------------------

    let workItemId (spec: RequirementsImportSpec) (slice: string) =
        $"{spec.WorkItemPrefix}-{slice.ToUpperInvariant()}"

    /// IDs in source order, consecutive runs of one prefix collapsed into
    /// `first..last` (for example P1.1..P1.6).
    let compress (ids: string list) =
        let parts (id: string) =
            let matched = trailingNumber.Match id

            if matched.Success then
                Some(matched.Groups[1].Value, int matched.Groups[2].Value)
            else
                None

        let flush (runs: string list) (first: string, last: string) =
            (if first = last then first else $"{first}..{last}") :: runs

        let folder (runs: string list, current: (string * string) option, previous: (string * int) option) (id: string) =
            match current, previous, parts id with
            | Some(first, last), Some(prefix, number), Some(nextPrefix, nextNumber) when prefix = nextPrefix && nextNumber = number + 1 ->
                runs, Some(first, id), Some(nextPrefix, nextNumber)
            | Some run, _, next -> flush runs run, Some(id, id), next
            | None, _, next -> runs, Some(id, id), next

        let runs, current, _ = ids |> List.fold folder ([], None, None)

        let all =
            match current with
            | Some run -> flush runs run
            | None -> runs

        List.rev all

    let private describe
        (spec: RequirementsImportSpec)
        (umbrella: string)
        (total: int)
        (order: int)
        (slice: ImportSlice)
        (dependsOn: string option)
        (requirements: SourceRequirement list)
        =
        let dependency =
            match dependsOn with
            | Some id -> $" Depends on {id}."
            | None -> ""

        let schemes =
            requirements
            |> List.groupBy _.Scheme
            |> List.map (fun (scheme, items) ->
                let ids = items |> List.map _.Id |> compress |> String.concat ", "
                $"- {scheme} ({items.Length}): {ids}")
            |> String.concat "\n"

        $"{slice.Goal}\n\nSlice {order} of {total} in the {spec.Source} requirements queue; cut policy {cutPolicyName slice.CutPolicy}.{dependency}\nBasis: {slice.Basis}.\n\nSource requirements ({requirements.Length}), each traced to this item in the {spec.TraceAttachment} attachment of {umbrella}:\n{schemes}\n\nComplete only when the kickoff completionRule holds for every source requirement above: requirement, implementation, tests, runtime or verification evidence, and Praxis and Aegis obligations."

    let private sha256 (value: string) =
        Encoding.UTF8.GetBytes value
        |> SHA256.HashData
        |> Convert.ToHexString
        |> fun hex -> hex.ToLowerInvariant()

    let private serialize (node: JsonNode) =
        node.ToJsonString(JsonSerializerOptions(WriteIndented = true, IndentSize = 2)) + "\n"

    let private renderTrace
        (spec: RequirementsImportSpec)
        (umbrella: string)
        (documents: (string * string) list)
        (items: PlannedWorkItem list)
        (assigned: (SourceRequirement * string) list)
        (digest: string option)
        =
        let root = JsonObject()
        root["schema"] <- JsonValue.Create TraceSchema
        root["source"] <- JsonValue.Create spec.Source
        root["umbrella"] <- JsonValue.Create umbrella

        digest |> Option.iter (fun value -> root["planDigest"] <- JsonValue.Create $"sha256:{value}")

        let sources = JsonArray()

        for path, sha in documents do
            let entry = JsonObject()
            entry["path"] <- JsonValue.Create path
            entry["sha256"] <- JsonValue.Create sha
            sources.Add entry

        root["documents"] <- sources

        let workItems = JsonArray()

        for item in items do
            let entry = JsonObject()
            entry["id"] <- JsonValue.Create item.Id
            entry["slice"] <- JsonValue.Create item.Slice
            entry["order"] <- JsonValue.Create item.Order
            entry["cutPolicy"] <- JsonValue.Create(cutPolicyName item.CutPolicy)
            entry["priority"] <- JsonValue.Create item.Priority
            let dependsOn: JsonNode | null =
                match item.DependsOn with
                | Some id -> JsonValue.Create id
                | None -> null

            entry["dependsOn"] <- dependsOn
            entry["sourceReference"] <- JsonValue.Create item.SourceReference
            entry["requirementCount"] <- JsonValue.Create item.Requirements.Length
            workItems.Add entry

        root["workItems"] <- workItems

        let requirements = JsonArray()

        for requirement, workItem in assigned do
            let entry = JsonObject()
            entry["id"] <- JsonValue.Create requirement.Id
            entry["scheme"] <- JsonValue.Create requirement.Scheme
            entry["document"] <- JsonValue.Create requirement.Document
            entry["line"] <- JsonValue.Create requirement.Line
            entry["heading"] <- JsonValue.Create requirement.Heading
            entry["workItem"] <- JsonValue.Create workItem
            requirements.Add entry

        root["requirements"] <- requirements
        serialize root

    /// Builds the whole import from the specification and the text of every
    /// source it names (`sources` maps a repository-relative path to its
    /// content). Deterministic: the same inputs give byte-identical items,
    /// trace and digest.
    let plan (spec: RequirementsImportSpec) (umbrella: string) (sources: Map<string, string>) =
        let source path =
            match Map.tryFind path sources with
            | Some content -> Ok content
            | None -> Error [ $"Source document is missing: {path}. Run 'conditor init' (and 'conditor status') first." ]

        let documents =
            spec.Documents
            |> List.map (fun document -> source document.Path |> Result.bind (extract document))
            |> sequence
            |> Result.map List.concat

        let kickoff = source spec.Kickoff |> Result.bind (readKickoff spec)

        match documents, kickoff with
        | Ok documentRequirements, Ok(kickoffSlices, kickoffRequirements) ->
            let requirements = documentRequirements @ kickoffRequirements

            let duplicates =
                requirements
                |> List.countBy _.Id
                |> List.filter (fun (_, count) -> count > 1)
                |> List.map (fun (id, count) -> $"Source requirement {id} appears {count} times.")

            let invalidIds =
                [ for slice in kickoffSlices do
                      if not (sliceIdPattern.IsMatch slice.Id) then
                          yield $"Kickoff slice id '{slice.Id}' must be lower-case words joined by '-'." ]

            if not duplicates.IsEmpty || not invalidIds.IsEmpty then
                Error(duplicates @ invalidIds)
            else
                orderSlices kickoffSlices spec.AdditionalSlices
                |> Result.bind (fun slices ->
                    assign slices spec.Assignments requirements
                    |> Result.bind (fun assigned ->
                        let total = slices.Length

                        let items =
                            slices
                            |> List.mapi (fun index slice ->
                                let order = index + 1

                                let dependsOn =
                                    slices
                                    |> List.take index
                                    |> List.filter (fun earlier -> earlier.CutPolicy = NeverCut)
                                    |> List.tryLast
                                    |> Option.map (fun earlier -> workItemId spec earlier.Id)

                                let owned =
                                    assigned |> List.filter (fun (_, owner) -> owner = slice.Id) |> List.map fst

                                { Id = workItemId spec slice.Id
                                  Slice = slice.Id
                                  Order = order
                                  Title = $"Slice {order:D2} {slice.Id}: {slice.Goal}"
                                  Description = describe spec umbrella total order slice dependsOn owned
                                  Priority = priorityFor slice.CutPolicy
                                  Tags = [ spec.Source; "slice"; $"cut:{cutPolicyName slice.CutPolicy}" ]
                                  SourceReference = slice.SourceReference
                                  DependsOn = dependsOn
                                  CutPolicy = slice.CutPolicy
                                  Requirements = owned |> List.map _.Id })

                        let invalidItems =
                            items
                            |> List.filter (fun item -> not (workItemPattern.IsMatch item.Id))
                            |> List.map (fun item -> $"Work item id '{item.Id}' is not a valid Praxis work-item id.")

                        if not invalidItems.IsEmpty then
                            Error invalidItems
                        else
                            let traced =
                                assigned |> List.map (fun (requirement, slice) -> requirement, workItemId spec slice)

                            let hashed =
                                (spec.Documents |> List.map _.Path) @ [ spec.Kickoff ]
                                |> List.map (fun path -> path, sha256 sources[path])

                            // The digest covers everything the import writes:
                            // the trace and every item field Praxis records.
                            let unsigned = renderTrace spec umbrella hashed items traced None

                            let itemText =
                                items
                                |> List.map (fun item ->
                                    String.Join("\u001f", [ item.Id; item.Title; item.Description; item.Priority; item.SourceReference ] @ item.Tags))
                                |> String.concat "\u001e"

                            let digest = sha256 (unsigned + "\u001d" + itemText)

                            Ok
                                { Items = items
                                  Trace = renderTrace spec umbrella hashed items traced (Some digest)
                                  Digest = digest
                                  RequirementCount = requirements.Length }))
        | documents, kickoff -> Error(errorsOf documents @ errorsOf kickoff)

    // ------------------------------------------------------------------
    // Reconciliation with the Praxis queue
    // ------------------------------------------------------------------

    let private conflicts (planned: PlannedWorkItem) (existing: ExistingWorkItem) (source: string) =
        [ if existing.Title <> planned.Title then
              yield $"{planned.Id} exists with a different title."
          if existing.Description <> Some planned.Description then
              yield $"{planned.Id} exists with a different description."
          if existing.Priority <> planned.Priority then
              yield $"{planned.Id} exists with priority '{existing.Priority}', not '{planned.Priority}'."
          if existing.Source <> Some source then
              yield $"{planned.Id} exists but was not captured from {source}."
          if existing.SourceReference <> Some planned.SourceReference then
              yield $"{planned.Id} exists with a different source reference."
          if Set.ofList existing.Tags <> Set.ofList planned.Tags then
              yield $"{planned.Id} exists with different tags." ]

    /// What must change so the queue holds exactly the plan. A second run
    /// against the result of the first yields no actions. An item that
    /// exists with different content is a conflict: nothing is overwritten,
    /// and no action is returned while any conflict remains.
    /// `attachments` are the umbrella's attachments as (name, content), in
    /// upload order.
    let reconcile
        (spec: RequirementsImportSpec)
        (plan: RequirementsImportPlan)
        (umbrella: string)
        (existing: Map<string, ExistingWorkItem>)
        (attachments: (string * string) list)
        =
        let umbrellaErrors =
            match Map.tryFind umbrella existing with
            | None -> [ $"The umbrella work item {umbrella} is missing. Run 'conditor init' first." ]
            | Some item when item.Status = "abandoned" -> [ $"The umbrella work item {umbrella} was abandoned; the trace has nowhere to live." ]
            | Some _ -> []

        let itemResults =
            plan.Items
            |> List.map (fun planned ->
                match Map.tryFind planned.Id existing with
                | None -> Ok [ CaptureItem planned; MarkReady planned.Id ]
                | Some current ->
                    match conflicts planned current spec.Source with
                    | [] when current.Status = "captured" -> Ok [ MarkReady planned.Id ]
                    | [] -> Ok []
                    | errors -> Error errors)

        let traceResult =
            match attachments |> List.filter (fun (name, _) -> name = spec.TraceAttachment) |> List.tryLast with
            | None -> Ok [ AttachTrace(umbrella, spec.TraceAttachment, plan.Trace) ]
            | Some(_, content) when content = plan.Trace -> Ok []
            | Some _ ->
                Error [ $"{umbrella} already carries a different {spec.TraceAttachment}; the pinned requirements or the import specification changed. Nothing was changed." ]

        let errors = umbrellaErrors @ (itemResults |> List.collect errorsOf) @ errorsOf traceResult

        if errors.IsEmpty then
            Ok((itemResults |> List.collect (fun result -> match result with Ok actions -> actions | Error _ -> []))
               @ (match traceResult with Ok actions -> actions | Error _ -> []))
        else
            Error errors
