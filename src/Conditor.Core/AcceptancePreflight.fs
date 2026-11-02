namespace Conditor.Core

open System
open System.IO
open System.Security.Cryptography
open System.Text.Json

/// Acceptance-trust preflight (CON-ACC-PREFLIGHT). Before a timed run starts,
/// prove that the inputs the acceptance verifier will trust at the end of the
/// run -- the pinned requirements baseline and the operator approvals -- are
/// structurally acceptable to that verifier.
///
/// Conditor holds no acceptance rules of its own. It runs the repository's
/// authoritative verifier in its pre-T0 mode (`--preflight`) and reports that
/// verifier's problems verbatim, so the two can never disagree. Conditor adds
/// only what a verifier cannot know: whether each pinned input is present and
/// matches the digest pinned in the contract.
///
/// The decision is pure (`evaluate`: contract + observations -> report). All
/// file and process I/O is in `observe`.
module AcceptancePreflight =
    [<Literal>]
    let ContractSchema = "conditor.acceptance-preflight/1"

    [<Literal>]
    let VerifierSchema = "causa.acceptance-preflight/1"

    [<Literal>]
    let ReportSchema = "conditor.acceptance-preflight-report/1"

    [<Literal>]
    let DefaultContractPath = "conditor.acceptance.json"

    type PinnedInput = { Id: string; Path: string; Sha256: string }

    type Contract =
        { Verifier: string list
          Baseline: PinnedInput
          BaselineSource: string
          Approvals: PinnedInput
          TrustedOperators: string list
          RequiredInputs: PinnedInput list }

    type Severity =
        | Passing
        | Blocking

    type Finding =
        { Code: string
          Severity: Severity
          Subject: string
          Detail: string
          OwnerAction: string option }

    type VerifierRun =
        { ExitCode: int
          StandardOutput: string
          StandardError: string }

    /// What `observe` saw: the digest of every pinned input (None when absent)
    /// and the verifier's run.
    type Observations =
        { Digests: Map<string, string option>
          Verifier: VerifierRun }

    type Verdict =
        | Ready
        | Invalid

    type Report =
        { Verdict: Verdict
          Findings: Finding list }

    // ------------------------------------------------------------------
    // Contract
    // ------------------------------------------------------------------

    let private isDigest (value: string) =
        value.Length = 64 && value |> Seq.forall (fun c -> Uri.IsHexDigit c && not (Char.IsUpper c))

    let private property (name: string) (element: JsonElement) =
        match element.ValueKind with
        | JsonValueKind.Object ->
            match element.TryGetProperty name with
            | true, value -> Some value
            | _ -> None
        | _ -> None

    let private text name element =
        property name element
        |> Option.filter (fun value -> value.ValueKind = JsonValueKind.String)
        |> Option.map (fun value -> value.GetString() |> nonNull)
        |> Option.filter (String.IsNullOrWhiteSpace >> not)

    let private strings name element =
        property name element
        |> Option.filter (fun value -> value.ValueKind = JsonValueKind.Array)
        |> Option.map (fun value ->
            value.EnumerateArray()
            |> Seq.choose (fun item -> if item.ValueKind = JsonValueKind.String then Option.ofObj (item.GetString()) else None)
            |> List.ofSeq)

    let private pinned (id: string) (element: JsonElement) : Result<PinnedInput, string list> =
        match text "path" element, text "sha256" element with
        | Some path, Some sha when isDigest sha -> Ok { Id = id; Path = path; Sha256 = sha }
        | Some _, Some _ -> Error [ $"{id}: sha256 must be 64 lowercase hex characters" ]
        | _ -> Error [ $"{id}: path and sha256 are required (the digest is pinned here, never computed from the file)" ]

    let private collect (results: Result<'a, string list> list) =
        let errors = results |> List.collect (function Error e -> e | Ok _ -> [])
        if errors.IsEmpty then Ok(results |> List.choose (function Ok v -> Some v | Error _ -> None)) else Error errors

    let parse (json: string) : Result<Contract, string list> =
        try
            use document = JsonDocument.Parse json
            let root = document.RootElement

            let schema =
                if text "schema" root = Some ContractSchema then Ok()
                else Error [ $"schema must be {ContractSchema}" ]

            let verifier =
                match strings "verifier" root with
                | Some(head :: rest) -> Ok(head :: rest)
                | _ -> Error [ "verifier must be a non-empty command array (the authoritative acceptance verifier)" ]

            let input name =
                property name root
                |> Option.map (pinned name)
                |> Option.defaultValue (Error [ $"{name} is required" ])

            let required =
                property "requiredInputs" root
                |> Option.filter (fun value -> value.ValueKind = JsonValueKind.Array)
                |> Option.map (fun value ->
                    value.EnumerateArray()
                    |> Seq.map (fun item ->
                        match text "id" item with
                        | Some id -> pinned id item
                        | None -> Error [ "requiredInputs entries need an id" ])
                    |> List.ofSeq
                    |> collect)
                |> Option.defaultValue (Ok [])

            let operators =
                match strings "trustedOperators" root with
                | Some(head :: rest) -> Ok(head :: rest)
                | _ -> Error [ "trustedOperators must name at least one operator" ]

            match schema, verifier, input "baseline", input "approvals", operators, required with
            | Ok(), Ok verifier, Ok baseline, Ok approvals, Ok operators, Ok required ->
                Ok
                    { Verifier = verifier
                      Baseline = baseline
                      BaselineSource = text "baselineSource" root |> Option.defaultValue "."
                      Approvals = approvals
                      TrustedOperators = operators
                      RequiredInputs = required }
            | schema, verifier, baseline, approvals, operators, required ->
                let errors result =
                    match result with
                    | Error e -> e
                    | Ok _ -> []

                Error(
                    errors schema @ errors verifier @ errors baseline @ errors approvals
                    @ errors operators @ errors required
                )
        with :? JsonException as ex ->
            Error [ $"contract is not valid JSON: {ex.Message}" ]

    let pinnedInputs (contract: Contract) =
        contract.Baseline :: contract.Approvals :: contract.RequiredInputs

    /// The verifier's argument list. The pinned digests are passed, never the
    /// digests of the files on disk, so a changed file is the verifier's finding.
    let verifierArguments (contract: Contract) =
        contract.Verifier.Tail
        @ [ "--preflight"
            "--baseline"; contract.Baseline.Path
            "--baseline-sha256"; contract.Baseline.Sha256
            "--baseline-source"; contract.BaselineSource
            "--approvals"; contract.Approvals.Path
            "--approvals-sha256"; contract.Approvals.Sha256 ]
        @ (contract.TrustedOperators |> List.collect (fun id -> [ "--trusted-operator-id"; id ]))

    // ------------------------------------------------------------------
    // Decision (pure)
    // ------------------------------------------------------------------

    let private inputFindings (contract: Contract) (digests: Map<string, string option>) =
        pinnedInputs contract
        |> List.choose (fun input ->
            match digests |> Map.tryFind input.Id |> Option.flatten with
            | None ->
                Some
                    { Code = "COND-ACC-INPUT-MISSING"
                      Severity = Blocking
                      Subject = input.Id
                      Detail = $"{input.Path} does not exist"
                      OwnerAction = Some $"restore {input.Path} at the pinned revision" }
            | Some actual when actual <> input.Sha256 ->
                Some
                    { Code = "COND-ACC-INPUT-DIGEST"
                      Severity = Blocking
                      Subject = input.Id
                      Detail = $"{input.Path} sha256 {actual} differs from the pinned {input.Sha256}"
                      OwnerAction = Some "re-pin only through the owner decision process; never re-pin to match an edited file" }
            | Some _ -> None)

    let private verifierFindings (run: VerifierRun) =
        let unavailable detail =
            [ { Code = "COND-ACC-VERIFIER"
                Severity = Blocking
                Subject = "verifier"
                Detail = detail
                OwnerAction = None } ]

        try
            use document = JsonDocument.Parse run.StandardOutput
            let root = document.RootElement

            match text "schema" root, text "verdict" root with
            | Some VerifierSchema, Some verdict ->
                let problems =
                    property "problems" root
                    |> Option.filter (fun value -> value.ValueKind = JsonValueKind.Array)
                    |> Option.map (fun value ->
                        value.EnumerateArray()
                        |> Seq.map (fun item ->
                            { Code = "COND-ACC-TRUST"
                              Severity = Blocking
                              Subject = "approvals"
                              Detail = text "problem" item |> Option.defaultValue "(unnamed problem)"
                              OwnerAction = text "owner_action" item })
                        |> List.ofSeq)
                    |> Option.defaultValue []

                match verdict, run.ExitCode, problems with
                | "READY", 0, [] ->
                    [ { Code = "COND-ACC-READY"
                        Severity = Passing
                        Subject = "verifier"
                        Detail = "the authoritative verifier accepts the pinned baseline and approvals"
                        OwnerAction = None } ]
                | "INVALID", 3, _ :: _ -> problems
                | _ ->
                    unavailable $"verifier verdict {verdict}, exit {run.ExitCode} and {problems.Length} problems are inconsistent"
            | _ -> unavailable $"verifier did not answer with {VerifierSchema} (exit {run.ExitCode}): {run.StandardError.Trim()}"
        with :? JsonException ->
            unavailable $"verifier output is not JSON (exit {run.ExitCode}): {run.StandardError.Trim()}"

    let evaluate (contract: Contract) (observations: Observations) : Report =
        let findings = inputFindings contract observations.Digests @ verifierFindings observations.Verifier

        { Verdict = if findings |> List.exists (fun f -> f.Severity = Blocking) then Invalid else Ready
          Findings = findings }

    // ------------------------------------------------------------------
    // Observation (I/O shell)
    // ------------------------------------------------------------------

    let private fileDigest (path: string) =
        if File.Exists path then
            Some(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes path)).ToLowerInvariant())
        else
            None

    let observe (target: string) (runVerifier: string -> string -> string list -> VerifierRun) (contract: Contract) =
        { Digests =
            pinnedInputs contract
            |> List.map (fun input -> input.Id, fileDigest (Path.Combine(target, input.Path)))
            |> Map.ofList
          Verifier = runVerifier target contract.Verifier.Head (verifierArguments contract) }

    let processVerifier target executable arguments =
        let result = ProcessRunner.runProcess target executable arguments

        { ExitCode = result.ExitCode
          StandardOutput = result.StandardOutput
          StandardError = result.StandardError }

    let preflight (target: string) (contractPath: string) runVerifier =
        if File.Exists contractPath then
            parse (File.ReadAllText contractPath)
            |> Result.map (fun contract -> evaluate contract (observe target runVerifier contract))
        else
            Error [ $"No acceptance-preflight contract at {contractPath}." ]

    // ------------------------------------------------------------------
    // Rendering
    // ------------------------------------------------------------------

    let verdictText =
        function
        | Ready -> "READY"
        | Invalid -> "INVALID"

    let toJson (report: Report) =
        let options = JsonSerializerOptions(WriteIndented = true)

        let value =
            {| schema = ReportSchema
               verdict = verdictText report.Verdict
               findings =
                report.Findings
                |> List.map (fun f ->
                    {| code = f.Code
                       severity = (match f.Severity with Passing -> "info" | Blocking -> "error")
                       subject = f.Subject
                       detail = f.Detail
                       ownerAction = f.OwnerAction |> Option.toObj |})
               ownerActions =
                report.Findings
                |> List.choose (fun f -> f.OwnerAction)
                |> List.countBy id
                |> List.map (fun (action, count) -> {| action = action; findings = count |}) |}

        JsonSerializer.Serialize(value, options) + "\n"

    let toText (report: Report) =
        let lines =
            report.Findings
            |> List.map (fun f ->
                let action = f.OwnerAction |> Option.map (sprintf "\n    owner action: %s") |> Option.defaultValue ""
                $"  {f.Code} [{f.Subject}] {f.Detail}{action}")

        String.concat "\n" ($"acceptance preflight: {verdictText report.Verdict}" :: lines) + "\n"
