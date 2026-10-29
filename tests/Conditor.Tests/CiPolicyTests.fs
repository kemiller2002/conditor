module CiPolicyTests

open System
open System.IO
open Conditor.Core

/// `conditor ci-policy` (WI-0063, Indy-init R7-REM-004). Each rule on a
/// minimal workflow, the shapes that pass, the reader failing closed, and
/// Rehearsal 6's actual workflows (tests/fixtures/ci-policy/rehearsal-006,
/// copied from indy-init-rehearsal-006 main 41fb6d6), whose per-commit group
/// on main starved CI for 124 minutes.

let private rules (text: string) =
    CiPolicy.evaluate [ ".github/workflows/w.yml", text ]
    |> _.Findings
    |> List.map _.Rule
    |> List.distinct
    |> List.sort

let private repositoryRoot () =
    let rec up (directory: DirectoryInfo | null) =
        match directory with
        | null -> failwith "Conditor.slnx not found above the test assembly"
        | d when File.Exists(Path.Combine(d.FullName, "Conditor.slnx")) -> d.FullName
        | d -> up d.Parent

    up (DirectoryInfo AppContext.BaseDirectory)

let private fixture name =
    let path = Path.Combine(repositoryRoot (), "tests", "fixtures", "ci-policy", "rehearsal-006", name)
    $".github/workflows/{name}", File.ReadAllText path

let private safe =
    """name: Build and test
on:
  pull_request:
  push:
    branches: [main]
permissions:
  contents: read
concurrency:
  group: ${{ github.workflow }}-${{ github.ref }}
  cancel-in-progress: true
jobs:
  build:
    runs-on: ubuntu-latest
    timeout-minutes: 20
    steps:
      - run: echo "if: always() inside a step is not a job condition"
      - name: Upload on failure
        if: always()
        run: echo ok
  aggregate:
    needs: build
    if: ${{ !cancelled() }}
    runs-on: ubuntu-latest
    timeout-minutes: 5
    steps:
      - run: echo done
  foundations:
    uses: owner/repo/.github/workflows/verify.yml@v1
"""

let private candidate =
    """name: Candidate acceptance
on:
  workflow_dispatch:
    inputs:
      candidate:
        required: true
        type: string
concurrency:
  group: candidate-acceptance-${{ inputs.candidate }}
  cancel-in-progress: false
jobs:
  verify:
    runs-on: ubuntu-latest
    timeout-minutes: 30
    steps:
      - run: echo verify
"""

let run (check: string -> bool -> unit) =
    // Shapes that pass.
    check "ci-policy: a per-ref, cancelling, filtered, time-limited workflow passes" (rules safe = [])
    check "ci-policy: a step-level always() is not a job condition" (rules safe = [])
    check "ci-policy: a dispatch-only candidate workflow keyed on its input passes" (rules candidate = [])

    check "ci-policy: a dispatch-only operational workflow needs no group"
        (rules "on: workflow_dispatch\njobs:\n  x:\n    runs-on: ubuntu-latest\n    timeout-minutes: 5\n    steps:\n      - run: echo\n" = [])

    // CIP-001: per-commit groups, at workflow and job level.
    check "ci-policy: CIP-001 a per-commit workflow group"
        (rules (safe.Replace("${{ github.workflow }}-${{ github.ref }}", "${{ github.workflow }}-${{ github.sha }}")) = [ "CIP-001" ])

    check "ci-policy: CIP-001 a per-run job group"
        (rules (safe.Replace("    timeout-minutes: 20\n", "    timeout-minutes: 20\n    concurrency:\n      group: build-${{ github.run_id }}\n")) = [ "CIP-001" ])

    check "ci-policy: a per-commit marker is found whatever its spacing" (CiPolicy.isPerCommit "x-${{github.SHA}}")
    check "ci-policy: a per-ref group is not per commit" (not (CiPolicy.isPerCommit "${{ github.workflow }}-${{ github.ref }}"))

    // CIP-002 and CIP-003.
    check "ci-policy: CIP-002 a push workflow without a group"
        (rules (safe.Replace("concurrency:\n  group: ${{ github.workflow }}-${{ github.ref }}\n  cancel-in-progress: true\n", "")) = [ "CIP-002" ])

    check "ci-policy: CIP-003 a pull-request workflow that keeps superseded runs"
        (rules (safe.Replace("cancel-in-progress: true", "cancel-in-progress: false")) = [ "CIP-003" ])

    check "ci-policy: an expression for cancel-in-progress is accepted"
        (rules (safe.Replace("cancel-in-progress: true", "cancel-in-progress: ${{ github.ref != 'refs/heads/main' }}")) = [])

    // CIP-004: job-level always(), in any form.
    check "ci-policy: CIP-004 a job-level always()"
        (rules (safe.Replace("if: ${{ !cancelled() }}", "if: always()")) = [ "CIP-004" ])

    check "ci-policy: CIP-004 always() inside an expression and a folded block"
        (rules (safe.Replace("if: ${{ !cancelled() }}", "if: >-\n      ${{ always() &&\n      needs.build.result == 'success' }}")) = [ "CIP-004" ])

    // CIP-005: a timeout on every job that runs steps; not on reusable calls.
    check "ci-policy: CIP-005 a job without timeout-minutes"
        (rules (safe.Replace("    timeout-minutes: 5\n", "")) = [ "CIP-005" ])

    // CIP-006: unfiltered push with pull_request, in block and flow form.
    check "ci-policy: CIP-006 unfiltered push with pull_request"
        (rules (safe.Replace("  push:\n    branches: [main]\n", "  push:\n")) = [ "CIP-006" ])

    check "ci-policy: CIP-006 in flow form"
        (rules (safe.Replace("on:\n  pull_request:\n  push:\n    branches: [main]\n", "on: [push, pull_request]\n")) = [ "CIP-006" ])

    check "ci-policy: a tag-filtered push is filtered"
        (rules (safe.Replace("    branches: [main]\n", "    tags: ['v*']\n")) = [])

    // CIP-007: a candidate workflow nothing can cancel.
    check "ci-policy: CIP-007 a candidate workflow a push can start"
        (rules (candidate.Replace("on:\n  workflow_dispatch:", "on:\n  push:\n    branches: [main]\n  workflow_dispatch:")) |> List.contains "CIP-007")

    check "ci-policy: CIP-007 a candidate group not keyed on the candidate"
        (rules (candidate.Replace("candidate-acceptance-${{ inputs.candidate }}", "candidate-acceptance")) = [ "CIP-007" ])

    check "ci-policy: CIP-007 a candidate workflow that cancels in progress"
        (rules (candidate.Replace("cancel-in-progress: false", "cancel-in-progress: true")) = [ "CIP-007" ])

    // CIP-000: the reader fails closed.
    check "ci-policy: CIP-000 a workflow with no trigger" (rules "name: x\njobs: {}\n" |> List.contains "CIP-000")

    check "ci-policy: CIP-000 an alias in the concurrency section"
        (rules (safe.Replace("  cancel-in-progress: true", "  cancel-in-progress: *cancel")) = [ "CIP-000" ])

    check "ci-policy: comments and quoted keys are read"
        (rules (safe.Replace("on:\n", "\"on\": # triggers\n")) = [])

    // Rehearsal 6's actual workflows.
    let r6 =
        [ "build-and-test.yml"
          "echelon-foundations.yml"
          "praxis-validation.yml"
          "acceptance-final.yml"
          "cancel-superseded.yml" ]
        |> List.map fixture
        |> CiPolicy.evaluate

    let ruleSet path =
        r6.Findings |> List.filter (fun f -> f.Path = $".github/workflows/{path}") |> List.map _.Rule |> Set.ofList

    let described = r6.Findings |> List.map (fun f -> $"{f.Rule} {f.Path}:{f.Line}") |> String.concat "; "
    check $"ci-policy: Rehearsal 6's workflows fail the policy [{described}]" (not r6.Passed)

    check "ci-policy: Rehearsal 6 build-and-test is caught by its per-commit main group (CIP-001 at line 39)"
        (r6.Findings |> List.exists (fun f -> f.Rule = "CIP-001" && f.Path = ".github/workflows/build-and-test.yml" && f.Line = 39))

    check "ci-policy: Rehearsal 6 foundations: no group, job-level always(), every push runs twice"
        (ruleSet "echelon-foundations.yml" = Set.ofList [ "CIP-002"; "CIP-004"; "CIP-006" ])

    check "ci-policy: Rehearsal 6 praxis-validation: no group, every push runs twice"
        (Set.isSubset (Set.ofList [ "CIP-002"; "CIP-006" ]) (ruleSet "praxis-validation.yml"))

    check "ci-policy: no Rehearsal 6 workflow is unreadable" (r6.Findings |> List.forall (fun f -> f.Rule <> "CIP-000"))

    // The JSON report.
    let json = CiPolicy.toJson r6
    check "ci-policy: the report is conditor.ci-policy/1 with a FAIL verdict" (json.Contains "\"conditor.ci-policy/1\"" && json.Contains "\"FAIL\"")
