module UnattendedReadinessTests

open System
open System.IO
open System.Text
open Conditor.Core

/// `conditor unattended-readiness` against a scripted runner: the verdict
/// rules, the order of the probe, and that nothing merges when an earlier
/// check fails. The real-GitHub proof is the integration run recorded in
/// docs/autonomous-execution/INTEGRATION-TEST-RESULTS.md.
let private policy =
    """{"schema":"praxis.autonomous-execution-policy/1","mergeRequirements":{"requiredChecks":["build-and-test"],"independentReviewCheck":"independent-review"}}"""

let private candidate = String('a', 40)
let private merged = String('c', 40)

type private Script =
    { Diagnosis: string
      PraxisAvailable: bool
      AuthorizationValid: bool
      MergeOutcome: string
      mutable MergeCalls: int }

let private result code (out: string) = { ExitCode = code; StandardOutput = out; StandardError = (if code = 0 then "" else out) }

let private runner (script: Script) : UnattendedReadiness.Runner =
    fun _ executable arguments ->
        let has (value: string) = List.contains value arguments
        let joined = String.Join(" ", arguments)
        match executable, arguments with
        | "praxis", "version" :: _ -> if script.PraxisAvailable then result 0 "praxis 3.23.0" else result 127 "not found"
        | "praxis", "autonomy" :: "diagnose" :: _ -> result 0 script.Diagnosis
        | "praxis", "autonomy" :: "policy" :: _ -> result 0 """{"valid":true,"policyVersion":1,"policyDigest":"sha256:feed"}"""
        | "praxis", "autonomy" :: "authorize" :: "verify" :: _ when has "--revocations" -> result 3 """{"valid":false,"findings":["run R is revoked"]}"""
        | "praxis", "autonomy" :: "authorize" :: "verify" :: _ ->
            if script.AuthorizationValid then result 0 """{"valid":true,"runId":"R","authorizationDigest":"sha256:a","expiresAt":"2030-01-01T00:00:00Z"}"""
            else result 3 """{"valid":false,"findings":["the owner signature does not verify"]}"""
        | "praxis", "autonomy" :: "revoke" :: _ -> result 0 "revocation"
        | "praxis", "autonomy" :: "merge" :: _ ->
            script.MergeCalls <- script.MergeCalls + 1
            if script.MergeCalls = 1 then result (if script.MergeOutcome.Contains "MERGED" then 0 else 3) script.MergeOutcome
            else result 0 $"""{{"outcome":"ALREADY_MERGED","mergeCommitSha":"{merged}","receiptDigest":"sha256:abc"}}"""
        | "praxis", "autonomy" :: "receipts" :: _ -> result 0 "receipt chain: 1 receipt(s), intact"
        | "gh", [ "api"; repo ] when repo.StartsWith "repos/" && not (repo.Contains "/pulls") -> result 0 """{"permissions":{"push":true}}"""
        | "gh", _ when joined.Contains "policy.json" -> result 0 (Convert.ToBase64String(Encoding.UTF8.GetBytes policy))
        | "gh", _ when joined.Contains ".github/workflows" -> result 0 "1"
        | "gh", _ when joined.Contains "/pulls -X POST" -> result 0 "5"
        | "gh", _ when joined.Contains "/compare/" -> result 0 "identical"
        | "gh", _ when joined.Contains "/pulls/5" -> result 0 $"""{{"merged":true,"merge_commit_sha":"{merged}"}}"""
        | "git", "rev-parse" :: _ -> result 0 candidate
        | "git", _ -> result 0 ""
        | _ -> result 1 $"unscripted: {executable} {joined}"

let private journal () =
    let root = Path.Combine(Path.GetTempPath(), $"conditor-ur-{Guid.NewGuid():N}")
    Directory.CreateDirectory(Path.Combine(root, "receipts")) |> ignore
    File.WriteAllText(
        Path.Combine(root, "receipts", "abc.json"),
        $"""{{"candidateSha":"{candidate}","checks":[{{"name":"independent-review","conclusion":"success","app":"github-actions","headSha":"{candidate}"}},{{"name":"build-and-test","conclusion":"success","app":"github-actions","headSha":"{candidate}"}}]}}""")
    root

let private request journalRoot =
    { Repository = "kemiller2002/example"
      BaseBranch = "main"
      Praxis = "praxis"
      Authorization = "/outside/authorization.json"
      TrustStore = "/outside/trust.json"
      Journal = journalRoot
      Workdir = Path.GetTempPath()
      RunId = "R"
      Trace = "WI-1"
      ProbeBranch = $"unattended-readiness/test-{Guid.NewGuid():N}"
      PollSeconds = 1 }

let private openRules = """{"requiredApprovingReviews":0,"requiresCodeOwnerReview":false,"rulesVisible":true}"""

let private script () =
    { Diagnosis = openRules
      PraxisAvailable = true
      AuthorizationValid = true
      MergeOutcome = $"""{{"outcome":"MERGED","mergeCommitSha":"{merged}","receiptDigest":"sha256:abc"}}"""
      MergeCalls = 0 }

let private status (report: UnattendedReadinessReport) id =
    report.Checks |> List.find (fun c -> c.Id = id) |> _.Status

let private go (s: Script) = UnattendedReadiness.run (runner s) (fun () -> DateTimeOffset.UnixEpoch) (request (journal ()))

let run (check: string -> bool -> unit) =
    let ready = script ()
    let report = go ready
    check "unattended readiness: a probe that merges, verifies and reconciles is READY" (report.Verdict = UnattendedVerdict.Ready)
    check "unattended readiness: the repeated merge request is checked to be idempotent" (ready.MergeCalls = 2 && status report "UR-13" = ReadinessStatus.Passed)
    check "unattended readiness: the receipt carries the evidence" (report.Evidence.MergeCommitSha = Some merged && report.Evidence.PullRequest = Some 5)

    let review = { script () with Diagnosis = """{"requiredApprovingReviews":1,"requiresCodeOwnerReview":false,"rulesVisible":true}""" }
    let blocked = go review
    check "unattended readiness: GitHub requiring human review is BLOCKED" (blocked.Verdict = UnattendedVerdict.Blocked && status blocked "UR-03" = ReadinessStatus.Failed)
    check "unattended readiness: nothing is merged after an earlier failure" (review.MergeCalls = 0 && status blocked "UR-09" = ReadinessStatus.NotRun)

    let hidden = go { script () with Diagnosis = """{"requiredApprovingReviews":null,"requiresCodeOwnerReview":null,"rulesVisible":false}""" }
    check "unattended readiness: unreadable branch rules are not assumed compatible" (hidden.Verdict = UnattendedVerdict.Blocked)

    let noPraxis = go { script () with PraxisAvailable = false }
    check "unattended readiness: without Praxis the probe is NOT_RUN" (noPraxis.Verdict = UnattendedVerdict.NotRun)

    let noAuthorization = go { script () with AuthorizationValid = false }
    check "unattended readiness: without a valid run authorization the probe is NOT_RUN" (noAuthorization.Verdict = UnattendedVerdict.NotRun)

    let denied = go { script () with MergeOutcome = """{"outcome":"DENY","decision":{"result":"DENY"}}""" }
    check "unattended readiness: a Praxis denial is BLOCKED, never READY" (denied.Verdict = UnattendedVerdict.Blocked && status denied "UR-09" = ReadinessStatus.Failed)

    let configurationOnly =
        UnattendedReadiness.verdict
            [ { Id = "UR-01"; Name = ""; Status = ReadinessStatus.Passed; Detail = "" }
              { Id = "UR-06"; Name = ""; Status = ReadinessStatus.Passed; Detail = "" }
              { Id = "UR-09"; Name = ""; Status = ReadinessStatus.NotRun; Detail = "" } ]
    check "unattended readiness: configuration alone is never READY" (configurationOnly = UnattendedVerdict.Blocked)

    let json = UnattendedReadiness.toJson report
    check "unattended readiness: the receipt is versioned and self-digested"
        (json.Contains UnattendedReadiness.Schema && json.Contains "\"receiptDigest\": \"sha256:" && json.Contains "\"verdict\": \"READY\"")
