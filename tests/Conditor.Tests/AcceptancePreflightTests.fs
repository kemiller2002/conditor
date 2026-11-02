module AcceptancePreflightTests

// CON-ACC-PREFLIGHT: the acceptance-trust preflight. Rehearsal 8 lost its
// verdict (INVALID) to an approvals file the verifier could not trust; the
// file existed before T0. These scenarios prove the preflight refuses that
// shape before a timed run, names the owner action, and adds no acceptance
// rule of its own (the verifier's problems are reported verbatim).

open System
open System.IO
open System.Security.Cryptography
open System.Text
open Conditor.Core

let private sha (text: string) =
    Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes text)).ToLowerInvariant()

let private baselineText = "{\"schema\":\"praxis.requirements-baseline/1\"}"
let private approvalsText = "{\"schema\":\"causa.requirements-approvals/1\"}"

let private contractJson (baselineSha: string) =
    $"""{{
  "schema": "conditor.acceptance-preflight/1",
  "verifier": ["python3", "tools/compute-acceptance-verdict.py"],
  "baseline": {{ "path": "requirements/baseline.json", "sha256": "{baselineSha}" }},
  "baselineSource": ".",
  "approvals": {{ "path": "kickoff/approvals.causa.json", "sha256": "{sha approvalsText}" }},
  "trustedOperators": ["repository-owner"]
}}"""

let private withRepository (action: string -> 'a) =
    let root = Path.Combine(Path.GetTempPath(), "conditor-acc-" + Guid.NewGuid().ToString("N"))
    let write (relative: string) (text: string) =
        let path = Path.Combine(root, relative)
        Directory.CreateDirectory(Path.GetDirectoryName path |> nonNull) |> ignore
        File.WriteAllText(path, text)

    write "requirements/baseline.json" baselineText
    write "kickoff/approvals.causa.json" approvalsText

    try
        action root
    finally
        Directory.Delete(root, true)

let private verifierAnswer verdict exitCode (problems: (string * string) list) =
    let items =
        problems
        |> List.map (fun (problem, action) -> $"{{\"problem\": \"{problem}\", \"owner_action\": \"{action}\"}}")
        |> String.concat ", "

    fun (_: string) (_: string) (_: string list) ->
        { AcceptancePreflight.ExitCode = exitCode
          AcceptancePreflight.StandardOutput =
            $"{{\"schema\": \"causa.acceptance-preflight/1\", \"verdict\": \"{verdict}\", \"problems\": [{items}]}}"
          AcceptancePreflight.StandardError = "" }

let private preflightWith root (contract: string) runVerifier =
    let path = Path.Combine(root, AcceptancePreflight.DefaultContractPath)
    File.WriteAllText(path, contract)
    AcceptancePreflight.preflight root path runVerifier

let run (check: string -> bool -> unit) =
    withRepository (fun root ->
        let ready = preflightWith root (contractJson (sha baselineText)) (verifierAnswer "READY" 0 [])

        check
            "acceptance preflight: a verifier READY with matching pinned inputs is READY"
            (match ready with
             | Ok report -> report.Verdict = AcceptancePreflight.Ready
             | Error _ -> false)

        let r8 =
            preflightWith root (contractJson (sha baselineText)) (
                verifierAnswer "INVALID" 3 [
                    "approval APR-V411-OWNER-1 lacks an immutable authority source",
                    "owner: re-record this approval with source.repository"
                    "approval APR-V4-OWNER-1 contains an invalid change identity",
                    "owner: every change must name requirement_id and action"
                ]
            )

        check
            "acceptance preflight: the Rehearsal 8 approvals shape is INVALID before T0"
            (match r8 with
             | Ok report -> report.Verdict = AcceptancePreflight.Invalid
             | Error _ -> false)

        check
            "acceptance preflight: verifier problems are reported verbatim with their owner actions"
            (match r8 with
             | Ok report ->
                 let trust = report.Findings |> List.filter (fun f -> f.Code = "COND-ACC-TRUST")

                 trust.Length = 2
                 && trust.[0].Detail = "approval APR-V411-OWNER-1 lacks an immutable authority source"
                 && trust |> List.forall (fun f -> f.OwnerAction |> Option.exists (fun a -> a.StartsWith "owner:"))
             | Error _ -> false)

        let drifted = preflightWith root (contractJson (String('0', 64))) (verifierAnswer "READY" 0 [])

        check
            "acceptance preflight: a pinned input whose file changed is INVALID even when the verifier answers READY"
            (match drifted with
             | Ok report ->
                 report.Verdict = AcceptancePreflight.Invalid
                 && report.Findings |> List.exists (fun f -> f.Code = "COND-ACC-INPUT-DIGEST" && f.Subject = "baseline")
             | Error _ -> false)

        let crashed =
            preflightWith root (contractJson (sha baselineText)) (fun _ _ _ ->
                { AcceptancePreflight.ExitCode = 2
                  AcceptancePreflight.StandardOutput = ""
                  AcceptancePreflight.StandardError = "Unreadable input" })

        check
            "acceptance preflight: an unavailable verifier is INVALID, never READY"
            (match crashed with
             | Ok report ->
                 report.Verdict = AcceptancePreflight.Invalid
                 && report.Findings |> List.exists (fun f -> f.Code = "COND-ACC-VERIFIER")
             | Error _ -> false)

        let inconsistent = preflightWith root (contractJson (sha baselineText)) (verifierAnswer "READY" 3 [])

        check
            "acceptance preflight: a verifier verdict that contradicts its exit code is INVALID"
            (match inconsistent with
             | Ok report -> report.Verdict = AcceptancePreflight.Invalid
             | Error _ -> false)

        let args =
            match AcceptancePreflight.parse (contractJson (sha baselineText)) with
            | Ok contract -> AcceptancePreflight.verifierArguments contract
            | Error _ -> []

        check
            "acceptance preflight: the verifier receives the pinned digests and --preflight"
            (args.Head = "tools/compute-acceptance-verdict.py"
             && List.contains "--preflight" args
             && List.contains (sha baselineText) args
             && List.contains "repository-owner" args)

        let unpinned =
            AcceptancePreflight.parse (
                (contractJson (sha baselineText)).Replace($"\"sha256\": \"{sha approvalsText}\"", "\"sha256\": \"\"")
            )

        check
            "acceptance preflight: an approvals file without a pinned digest is a contract error"
            (match unpinned with
             | Error errors -> errors |> List.exists (fun e -> e.StartsWith "approvals")
             | Ok _ -> false)

        let doctor = Doctor.acceptanceFindings root (verifierAnswer "INVALID" 3 [ "approval X lacks an immutable authority source", "owner: fix" ])

        check
            "acceptance preflight: doctor reports the trust problem as an error when the contract exists"
            (doctor |> List.exists (fun f -> f.Code = "COND-DOC-ACC-TRUST" && f.Severity = Doctor.Error)))

    let empty = Path.Combine(Path.GetTempPath(), "conditor-acc-none-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory empty |> ignore

    check
        "acceptance preflight: doctor is silent for a repository with no acceptance contract"
        (Doctor.acceptanceFindings empty (verifierAnswer "INVALID" 3 []) |> List.isEmpty)

    Directory.Delete(empty, true)
