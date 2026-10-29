# CI policy

`conditor ci-policy check` answers one question: **can this repository's workflows starve its own CI?** It reads `.github/workflows/*.yml` and reports every workflow shape that let Rehearsal 6's CI go 124 minutes without a result on `main` (Indy-init R7-REM-004, WI-0063).

```bash
conditor ci-policy check [--target PATH] [--json]
```

- **Exit codes:** 0 when every workflow passes, 3 on any finding, 2 for a bad invocation.
- **Output:** `--json` writes a `conditor.ci-policy/1` report: verdict, workflows and findings with rule, path, line and message.

## Rules

| Rule | Finding | Why |
|---|---|---|
| CIP-000 | A workflow, or a part of `on`, `concurrency` or a job's keys, that the check cannot read (anchors, aliases, merge keys, misplaced lines) | The check fails closed and never passes what it did not read |
| CIP-001 | A per-commit or per-run concurrency group (`github.sha`, `github.run_id`, …) in a push or pull-request workflow, at workflow or job level | Every push runs in parallel and none is superseded. This was Rehearsal 6's root cause |
| CIP-002 | A push or pull-request workflow without a workflow-level concurrency group | Superseded runs accumulate |
| CIP-003 | A pull-request workflow whose `cancel-in-progress` is not `true` (an expression is accepted) | Superseded pull-request runs keep running |
| CIP-004 | A job-level `if:` containing `always()` | The job starts even after the run is cancelled; use `!cancelled()` |
| CIP-005 | A job that runs steps with no `timeout-minutes` (reusable-workflow calls are exempt) | A stalled job holds a runner for six hours |
| CIP-006 | An unfiltered `push` together with `pull_request` | Every pull-request push runs twice |
| CIP-007 | A workflow with a `candidate…` dispatch input that a push can start, whose group is not keyed on that input, or that cancels in progress | Development commits, or another candidate, must never cancel a candidate's verification |

## What Rehearsal 6 measured

These figures come from indy-init-rehearsal-006, 2026-10-28 20:00 to 2026-10-29 03:00 UTC, through the GitHub API:

- **Runs:** 427 workflow runs. Of the 123 main Build-and-test runs, 111 were cancelled and only 1 succeeded.
- **Queue:** up to about 40 main runs were open at once, while about 104–126 jobs waited for a runner.
- **Time without a result:** 124 minutes with no result on main.
- **Waste:** cancelled jobs used about 1,297 runner-minutes.

Against that repository the check reports, among others:

- **CIP-001** at `build-and-test.yml:39`, the per-commit group on main;
- **CIP-004** at `echelon-foundations.yml:60`;
- **CIP-006** for the foundations and Praxis validation workflows;
- **CIP-007** for `acceptance-final.yml`, whose group is not keyed on the candidate.

Copies of those workflows are test fixtures (`tests/fixtures/ci-policy/rehearsal-006/`).

## The tiers Conditor scaffolds

| Tier | Workflow | Runs on | Concurrency |
|---|---|---|---|
| 1, development | `build-and-test.yml` | Every pull request and push to `main` | One group per ref; superseded runs cancelled |
| 3, candidate | `candidate-acceptance.yml` | `workflow_dispatch` with the candidate's full SHA | One group per candidate; never cancelled |

The candidate workflow:

- refuses anything but a 40-character SHA on `main`;
- proves that the checkout is that SHA;
- repeats the build, the tests and the real-browser suite;
- uploads a `conditor.candidate-verification/1` record named after the SHA.

Integration checks that are too slow for tier 1 belong in the candidate workflow, or in a further workflow keyed the same way, never in a per-commit group.

Every workflow that Conditor scaffolds passes the policy, and a test enforces this.
