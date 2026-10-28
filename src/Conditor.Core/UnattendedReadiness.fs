namespace Conditor.Core

open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.Json.Nodes

[<RequireQualifiedAccess>]
type ReadinessStatus =
    | Passed
    | Failed
    | NotRun
    | NotApplicable

type ReadinessCheck =
    { Id: string
      Name: string
      Status: ReadinessStatus
      Detail: string }

[<RequireQualifiedAccess>]
type UnattendedVerdict =
    | Ready
    | Blocked
    | NotRun

type UnattendedReadinessRequest =
    { Repository: string
      BaseBranch: string
      /// The Praxis executable (a `praxis` launcher or native binary).
      Praxis: string
      Authorization: string
      TrustStore: string
      Journal: string
      /// A local clone of `Repository` the probe branch is created in.
      Workdir: string
      RunId: string
      /// Requirement trace the probe commit carries (for the independent review).
      Trace: string
      ProbeBranch: string
      PollSeconds: int }

/// What the probe observed, for the readiness receipt.
type ReadinessEvidence =
    { PullRequest: int option
      CandidateSha: string option
      MergeCommitSha: string option
      PraxisReceiptDigest: string option
      PolicyDigest: string option
      AuthorizationDigest: string option }

type UnattendedReadinessReport =
    { Request: UnattendedReadinessRequest
      Verdict: UnattendedVerdict
      Checks: ReadinessCheck list
      Evidence: ReadinessEvidence
      StartedAt: DateTimeOffset
      FinishedAt: DateTimeOffset }

/// `conditor unattended-readiness`: can this repository complete the
/// authorized development lifecycle without the owner? READY is reported only
/// after a real pull request went branch -> commit -> pull request -> CI ->
/// independent review -> Praxis authorization -> merge -> verified receipt,
/// on GitHub, with the repository's actual rules and this credential.
/// Configuration inspection alone never yields READY. Conditor decides
/// nothing about authorization: every allow, deny and merge is Praxis's
/// (`praxis autonomy`), and Conditor only drives the probe and checks what
/// GitHub and Praxis report.
module UnattendedReadiness =
    [<Literal>]
    let Schema = "conditor.unattended-readiness/1"

    /// (working directory, executable, arguments) -> result.
    type Runner = string -> string -> string list -> ProcessResult

    let private check id name status detail = { Id = id; Name = name; Status = status; Detail = detail }

    let private parse (text: string) : JsonNode option =
        try
            match JsonNode.Parse text with
            | null -> None
            | node -> Some node
        with _ -> None

    let private child (node: JsonNode) (name: string) : JsonNode option =
        match node[name] with
        | null -> None
        | value -> Some value

    let private field (node: JsonNode) (name: string) = child node name |> Option.map (fun v -> v.ToString())

    let private items (node: JsonNode) : JsonNode list =
        node.AsArray() |> Seq.choose (fun item -> match item with null -> None | n -> Some n) |> Seq.toList

    /// A path through nested objects, e.g. ["mergeRequirements"; "requiredChecks"].
    let private path (node: JsonNode option) (names: string list) =
        names |> List.fold (fun acc name -> acc |> Option.bind (fun n -> child n name)) node

    let private ok (result: ProcessResult) = result.ExitCode = 0

    let private brief (result: ProcessResult) =
        let text = if String.IsNullOrWhiteSpace result.StandardError then result.StandardOutput else result.StandardError
        let trimmed = text.Trim()
        if trimmed.Length > 400 then trimmed.Substring(0, 400) + "…" else trimmed

    /// The verdict: NOT_RUN when a prerequisite stopped the probe before
    /// GitHub was exercised, BLOCKED when anything failed, READY only when
    /// every check passed (or does not apply).
    let verdict (checks: ReadinessCheck list) =
        let prerequisites = set [ "UR-01"; "UR-06" ]
        if checks |> List.exists (fun c -> prerequisites.Contains c.Id && c.Status = ReadinessStatus.Failed) then UnattendedVerdict.NotRun
        elif checks |> List.exists (fun c -> c.Status = ReadinessStatus.Failed) then UnattendedVerdict.Blocked
        elif checks |> List.exists (fun c -> c.Status = ReadinessStatus.NotRun) then UnattendedVerdict.Blocked
        else UnattendedVerdict.Ready

    let verdictCode verdict =
        match verdict with
        | UnattendedVerdict.Ready -> "READY"
        | UnattendedVerdict.Blocked -> "BLOCKED"
        | UnattendedVerdict.NotRun -> "NOT_RUN"

    /// Whether the target branch's own rules are compatible with an
    /// unattended merge, from `praxis autonomy diagnose`.
    let branchCompatibility (diagnosis: JsonNode) =
        match field diagnosis "requiredApprovingReviews", field diagnosis "requiresCodeOwnerReview", field diagnosis "rulesVisible" with
        | Some count, _, _ when count <> "0" ->
            ReadinessStatus.Failed, $"GitHub requires {count} approving review(s) on the target branch: BLOCKED until the owner changes the rule or approves"
        | _, Some "true", _ -> ReadinessStatus.Failed, "GitHub requires code-owner review on the target branch: BLOCKED"
        | Some "0", Some "false", Some "true" -> ReadinessStatus.Passed, "no required human review, no code-owner review (rules readable)"
        | _ -> ReadinessStatus.Failed, "the branch rules are not readable with this credential; compatibility is unknown, not assumed"

    type private State =
        { Checks: ReadinessCheck list
          Evidence: ReadinessEvidence
          Policy: JsonNode option
          Stop: bool }

    let private add (state: State) (c: ReadinessCheck) =
        { state with Checks = state.Checks @ [ c ]; Stop = state.Stop || c.Status = ReadinessStatus.Failed }

    let private step (state: State) id name (body: State -> State) =
        if state.Stop then add state (check id name ReadinessStatus.NotRun "not run: an earlier check failed") |> fun s -> { s with Stop = true }
        else body state

    let run (runner: Runner) (now: unit -> DateTimeOffset) (request: UnattendedReadinessRequest) : UnattendedReadinessReport =
        let started = now ()
        let temp = Path.Combine(Path.GetTempPath(), $"conditor-unattended-{Guid.NewGuid():N}")
        Directory.CreateDirectory temp |> ignore
        let praxis args = runner request.Workdir request.Praxis args
        let gh args = runner request.Workdir "gh" args
        let git args = runner request.Workdir "git" args
        let policyPath = Path.Combine(temp, "policy.json")

        let initial =
            { Checks = []
              Evidence = { PullRequest = None; CandidateSha = None; MergeCommitSha = None; PraxisReceiptDigest = None; PolicyDigest = None; AuthorizationDigest = None }
              Policy = None
              Stop = false }

        let final =
            initial
            |> fun s ->
                step s "UR-01" "Praxis available" (fun s ->
                    let result = praxis [ "version" ]
                    add s (check "UR-01" "Praxis available" (if ok result then ReadinessStatus.Passed else ReadinessStatus.Failed) (brief result)))
            |> fun s ->
                step s "UR-02" "GitHub authentication and repository permissions" (fun s ->
                    let result = gh [ "api"; $"repos/{request.Repository}" ]
                    match ok result, parse result.StandardOutput with
                    | true, Some repo ->
                        let push = child repo "permissions" |> Option.bind (fun p -> field p "push")
                        if push = Some "true" then add s (check "UR-02" "GitHub authentication and repository permissions" ReadinessStatus.Passed "authenticated; push permission")
                        else add s (check "UR-02" "GitHub authentication and repository permissions" ReadinessStatus.Failed "the credential cannot push to the repository")
                    | _ -> add s (check "UR-02" "GitHub authentication and repository permissions" ReadinessStatus.Failed (brief result)))
            |> fun s ->
                step s "UR-03" "Branch protection compatibility" (fun s ->
                    let result = praxis [ "autonomy"; "diagnose"; "--repository"; request.Repository; "--branch"; request.BaseBranch ]
                    match ok result, parse result.StandardOutput with
                    | true, Some diagnosis ->
                        let status, detail = branchCompatibility diagnosis
                        add s (check "UR-03" "Branch protection compatibility" status detail)
                    | _ -> add s (check "UR-03" "Branch protection compatibility" ReadinessStatus.Failed (brief result)))
            |> fun s ->
                step s "UR-04" "Praxis policy available on the target branch" (fun s ->
                    let result = gh [ "api"; $"repos/{request.Repository}/contents/.praxis/autonomy/policy.json?ref={request.BaseBranch}"; "--jq"; ".content" ]
                    if not (ok result) then add s (check "UR-04" "Praxis policy available on the target branch" ReadinessStatus.Failed (brief result))
                    else
                        File.WriteAllBytes(policyPath, Convert.FromBase64String(result.StandardOutput.Replace("\n", "").Trim()))
                        let checked' = praxis [ "autonomy"; "policy"; "check"; "--policy"; policyPath; "--json" ]
                        match ok checked', parse checked'.StandardOutput with
                        | true, Some node ->
                            let digest = field node "policyDigest"
                            { add s (check "UR-04" "Praxis policy available on the target branch" ReadinessStatus.Passed $"""valid; {defaultArg digest ""}""") with
                                Evidence = { s.Evidence with PolicyDigest = digest }
                                Policy = parse (File.ReadAllText policyPath) }
                        | _ -> add s (check "UR-04" "Praxis policy available on the target branch" ReadinessStatus.Failed (brief checked')))
            |> fun s ->
                step s "UR-05" "Required CI checks declared and present" (fun s ->
                    let required =
                        path s.Policy [ "mergeRequirements"; "requiredChecks" ]
                        |> Option.map (items >> List.map (fun n -> n.ToString()))
                        |> Option.defaultValue []
                    let workflows = gh [ "api"; $"repos/{request.Repository}/contents/.github/workflows?ref={request.BaseBranch}"; "--jq"; "length" ]
                    if required.IsEmpty then add s (check "UR-05" "Required CI checks declared and present" ReadinessStatus.Failed "the policy requires no CI check")
                    elif not (ok workflows) || workflows.StandardOutput.Trim() = "0" then
                        add s (check "UR-05" "Required CI checks declared and present" ReadinessStatus.Failed "no workflow on the target branch can produce the required checks")
                    else add s (check "UR-05" "Required CI checks declared and present" ReadinessStatus.Passed $"""required: {String.Join(", ", required)}; proven by UR-09"""))
            |> fun s ->
                step s "UR-06" "Valid run authorization" (fun s ->
                    let result = praxis [ "autonomy"; "authorize"; "verify"; "--authorization"; request.Authorization; "--trust-store"; request.TrustStore; "--policy"; policyPath; "--json" ]
                    match ok result, parse result.StandardOutput with
                    | true, Some node ->
                        { add s (check "UR-06" "Valid run authorization" ReadinessStatus.Passed $"""run {defaultArg (field node "runId") ""}, expires {defaultArg (field node "expiresAt") ""}""") with
                            Evidence = { s.Evidence with AuthorizationDigest = field node "authorizationDigest" } }
                    | _ -> add s (check "UR-06" "Valid run authorization" ReadinessStatus.Failed (brief result)))
            |> fun s ->
                step s "UR-07" "Revocation is effective" (fun s ->
                    let revocations = Path.Combine(temp, "revocations")
                    let wrote = praxis [ "autonomy"; "revoke"; "--run-id"; request.RunId; "--reason"; "readiness check: revocation must be honoured"; "--out"; Path.Combine(revocations, "probe.json") ]
                    let result = praxis [ "autonomy"; "authorize"; "verify"; "--authorization"; request.Authorization; "--trust-store"; request.TrustStore; "--revocations"; revocations; "--json" ]
                    let refused = ok wrote && result.ExitCode = 3 && result.StandardOutput.Contains "revoked"
                    add s (check "UR-07" "Revocation is effective" (if refused then ReadinessStatus.Passed else ReadinessStatus.Failed)
                               (if refused then "a revoked run's authorization is refused" else $"a revoked authorization was not refused: {brief result}")))
            |> fun s ->
                step s "UR-08" "Probe pull request opened (branch, commit, push)" (fun s ->
                    let file = Path.Combine("readiness", request.ProbeBranch.Replace('/', '-') + ".md")
                    let steps =
                        [ fun () -> git [ "fetch"; "origin"; $"+refs/heads/{request.BaseBranch}:refs/remotes/origin/{request.BaseBranch}" ]
                          fun () -> git [ "checkout"; "-B"; request.ProbeBranch; $"origin/{request.BaseBranch}" ]
                          fun () ->
                              Directory.CreateDirectory(Path.Combine(request.Workdir, "readiness")) |> ignore
                              File.WriteAllText(Path.Combine(request.Workdir, file), $"Unattended-readiness probe for run {request.RunId}.\n")
                              git [ "add"; file ]
                          fun () -> git [ "-c"; "user.name=Conditor readiness probe"; "-c"; "user.email=noreply@anthropic.com"; "commit"; "-m"; $"{request.Trace}: unattended-readiness probe (harmless change)\n\nWork-Item: {request.RunId}" ]
                          fun () -> git [ "push"; "-u"; "origin"; request.ProbeBranch ] ]

                    match steps |> List.fold (fun acc f -> match acc with Error e -> Error e | Ok () -> let r = f () in if ok r then Ok() else Error(brief r)) (Ok()) with
                    | Error reason -> add s (check "UR-08" "Probe pull request opened (branch, commit, push)" ReadinessStatus.Failed reason)
                    | Ok() ->
                        let head = git [ "rev-parse"; "HEAD" ]
                        let created =
                            gh [ "api"; $"repos/{request.Repository}/pulls"; "-X"; "POST"
                                 "-f"; $"title={request.Trace}: unattended-readiness probe ({request.RunId})"
                                 "-f"; $"head={request.ProbeBranch}"; "-f"; $"base={request.BaseBranch}"
                                 "-f"; $"body=Unattended-readiness probe (Conditor). A harmless change merged under run {request.RunId} through Praxis's authorization gate. Work-Item: {request.RunId}"
                                 "--jq"; ".number" ]
                        match ok created && ok head, Int32.TryParse(created.StandardOutput.Trim()) with
                        | true, (true, number) ->
                            let sha = head.StandardOutput.Trim()
                            { add s (check "UR-08" "Probe pull request opened (branch, commit, push)" ReadinessStatus.Passed $"pull request #{number} at {sha}") with
                                Evidence = { s.Evidence with PullRequest = Some number; CandidateSha = Some sha } }
                        | _ -> add s (check "UR-08" "Probe pull request opened (branch, commit, push)" ReadinessStatus.Failed (brief created)))
            |> fun s ->
                step s "UR-09" "CI, independent review, Praxis authorization and merge" (fun s ->
                    let merge () =
                        praxis [ "autonomy"; "merge"; "--repository"; request.Repository; "--pr"; string s.Evidence.PullRequest.Value
                                 "--candidate"; s.Evidence.CandidateSha.Value; "--authorization"; request.Authorization
                                 "--trust-store"; request.TrustStore; "--journal"; request.Journal; "--poll-seconds"; string request.PollSeconds; "--json" ]
                    let result = merge ()
                    match parse result.StandardOutput with
                    | Some node when field node "outcome" = Some "MERGED" ->
                        { add s (check "UR-09" "CI, independent review, Praxis authorization and merge" ReadinessStatus.Passed $"""merged as {defaultArg (field node "mergeCommitSha") ""}""") with
                            Evidence = { s.Evidence with MergeCommitSha = field node "mergeCommitSha"; PraxisReceiptDigest = field node "receiptDigest" } }
                    | Some node ->
                        add s (check "UR-09" "CI, independent review, Praxis authorization and merge" ReadinessStatus.Failed $"""Praxis outcome {defaultArg (field node "outcome") "?"}: {node.ToJsonString()}""")
                    | None -> add s (check "UR-09" "CI, independent review, Praxis authorization and merge" ReadinessStatus.Failed (brief result)))
            |> fun s ->
                step s "UR-10" "Merge verified on GitHub" (fun s ->
                    let pull = gh [ "api"; $"repos/{request.Repository}/pulls/{s.Evidence.PullRequest.Value}" ]
                    let commit = s.Evidence.MergeCommitSha.Value
                    let compare = gh [ "api"; $"repos/{request.Repository}/compare/{commit}...{request.BaseBranch}"; "--jq"; ".status" ]
                    match parse pull.StandardOutput with
                    | Some node when field node "merged" = Some "true" && field node "merge_commit_sha" = Some commit
                                     && (compare.StandardOutput.Trim() = "identical" || compare.StandardOutput.Trim() = "ahead") ->
                        add s (check "UR-10" "Merge verified on GitHub" ReadinessStatus.Passed $"pull request merged as {commit}; {request.BaseBranch} contains it")
                    | _ -> add s (check "UR-10" "Merge verified on GitHub" ReadinessStatus.Failed $"GitHub does not show {commit} merged into {request.BaseBranch}: {brief compare}"))
            |> fun s ->
                step s "UR-11" "Independent review and CI evidence bound to the candidate" (fun s ->
                    let digest = s.Evidence.PraxisReceiptDigest |> Option.defaultValue ""
                    let receiptPath = Path.Combine(request.Journal, "receipts", digest.Replace("sha256:", "") + ".json")
                    match (if File.Exists receiptPath then parse (File.ReadAllText receiptPath) else None) with
                    | Some receipt ->
                        let review = path s.Policy [ "mergeRequirements" ] |> Option.bind (fun m -> field m "independentReviewCheck")
                        let checks = child receipt "checks" |> Option.map items |> Option.defaultValue []
                        let reviewed =
                            review.IsSome
                            && checks |> List.exists (fun c -> field c "name" = review && field c "conclusion" = Some "success"
                                                              && field c "app" = Some "github-actions" && field c "headSha" = s.Evidence.CandidateSha)
                        if reviewed && field receipt "candidateSha" = s.Evidence.CandidateSha then
                            add s (check "UR-11" "Independent review and CI evidence bound to the candidate" ReadinessStatus.Passed $"{checks.Length} check(s) from github-actions at the candidate, including {review.Value}")
                        else add s (check "UR-11" "Independent review and CI evidence bound to the candidate" ReadinessStatus.Failed "the receipt does not bind a successful independent review to the candidate")
                    | None -> add s (check "UR-11" "Independent review and CI evidence bound to the candidate" ReadinessStatus.Failed $"no receipt {digest} in {request.Journal}"))
            |> fun s ->
                step s "UR-12" "Receipt persisted and chain intact" (fun s ->
                    let result = praxis [ "autonomy"; "receipts"; "verify"; "--journal"; request.Journal ]
                    add s (check "UR-12" "Receipt persisted and chain intact" (if ok result then ReadinessStatus.Passed else ReadinessStatus.Failed) (brief result)))
            |> fun s ->
                step s "UR-13" "Recovery: a repeated merge request is reconciled, not repeated" (fun s ->
                    let again =
                        praxis [ "autonomy"; "merge"; "--repository"; request.Repository; "--pr"; string s.Evidence.PullRequest.Value
                                 "--candidate"; s.Evidence.CandidateSha.Value; "--authorization"; request.Authorization
                                 "--trust-store"; request.TrustStore; "--journal"; request.Journal; "--json" ]
                    match parse again.StandardOutput with
                    | Some node when field node "outcome" = Some "ALREADY_MERGED" && field node "mergeCommitSha" = s.Evidence.MergeCommitSha ->
                        add s (check "UR-13" "Recovery: a repeated merge request is reconciled, not repeated" ReadinessStatus.Passed "ALREADY_MERGED with the same merge commit")
                    | _ -> add s (check "UR-13" "Recovery: a repeated merge request is reconciled, not repeated" ReadinessStatus.Failed (brief again)))
            |> fun s -> add s (check "UR-14" "Deployment permissions" ReadinessStatus.NotApplicable "the authorized operations include no deployment")

        { Request = request
          Verdict = verdict final.Checks
          Checks = final.Checks
          Evidence = final.Evidence
          StartedAt = started
          FinishedAt = now () }

    let private statusCode status =
        match status with
        | ReadinessStatus.Passed -> "passed"
        | ReadinessStatus.Failed -> "failed"
        | ReadinessStatus.NotRun -> "not-run"
        | ReadinessStatus.NotApplicable -> "not-applicable"

    let private utc (value: DateTimeOffset) = value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'")

    let private opt (value: 'a option) (create: 'a -> JsonNode | null) : JsonNode | null =
        match value with
        | Some v -> create v
        | None -> null

    /// The readiness receipt, with a digest over its own content.
    let toJson (report: UnattendedReadinessReport) =
        let r = report.Request
        let checks = JsonArray()
        report.Checks
        |> List.iter (fun c ->
            let node = JsonObject()
            node["id"] <- JsonValue.Create c.Id
            node["name"] <- JsonValue.Create c.Name
            node["status"] <- JsonValue.Create(statusCode c.Status)
            node["detail"] <- JsonValue.Create c.Detail
            checks.Add node)

        let evidence = JsonObject()
        evidence["pullRequest"] <- opt report.Evidence.PullRequest (fun v -> JsonValue.Create v)
        evidence["candidateSha"] <- opt report.Evidence.CandidateSha (fun v -> JsonValue.Create v)
        evidence["mergeCommitSha"] <- opt report.Evidence.MergeCommitSha (fun v -> JsonValue.Create v)
        evidence["praxisReceiptDigest"] <- opt report.Evidence.PraxisReceiptDigest (fun v -> JsonValue.Create v)
        evidence["policyDigest"] <- opt report.Evidence.PolicyDigest (fun v -> JsonValue.Create v)
        evidence["authorizationDigest"] <- opt report.Evidence.AuthorizationDigest (fun v -> JsonValue.Create v)

        let node = JsonObject()
        node["schema"] <- JsonValue.Create Schema
        node["repository"] <- JsonValue.Create r.Repository
        node["baseBranch"] <- JsonValue.Create r.BaseBranch
        node["runId"] <- JsonValue.Create r.RunId
        node["probeBranch"] <- JsonValue.Create r.ProbeBranch
        node["verdict"] <- JsonValue.Create(verdictCode report.Verdict)
        node["checks"] <- checks
        node["evidence"] <- evidence
        node["startedAt"] <- JsonValue.Create(utc report.StartedAt)
        node["finishedAt"] <- JsonValue.Create(utc report.FinishedAt)
        let body = node.ToJsonString()
        node["receiptDigest"] <- JsonValue.Create("sha256:" + (SHA256.HashData(Encoding.UTF8.GetBytes body) |> Convert.ToHexString).ToLowerInvariant())
        node.ToJsonString(JsonSerializerOptions(WriteIndented = true)) + "\n"

    let toText (report: UnattendedReadinessReport) =
        let lines =
            [ $"unattended readiness: {verdictCode report.Verdict} ({report.Request.Repository} -> {report.Request.BaseBranch}, run {report.Request.RunId})" ]
            @ (report.Checks |> List.map (fun c -> $"  {c.Id} {statusCode c.Status,-14} {c.Name}: {c.Detail}"))
        String.Join(Environment.NewLine, lines) + Environment.NewLine
