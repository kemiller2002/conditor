# Conditor roadmap

## Slice 1: deterministic foundation

Status: implemented.

- manifest parser and schema validation
- built-in component registry
- deterministic read-only planning
- exact package and immutable GitHub-commit lifecycle sources
- non-authoritative source cache
- component lifecycle execution
- post-install verification
- deterministic Conditor lock
- dependency-free F# test harness
- CI

## Slice 2: true empty-repository bootstrap

Status: substantially implemented and exercised by clean-room CI.

Implemented:

- six self-contained release targets for Linux, macOS, and Windows on x64 and ARM64
- SHA-256 release checksum generation
- checksum-verifying Unix and PowerShell installers
- native Linux CLI smoke execution
- embedded named presets available directly from the self-contained binary
- `plan --preset`, `init --preset`, and one-command `start --preset` bootstrap
- external private-source Git authentication without persisting credentials
- `fsharp-limen-web` scaffold
- explicit package binding for Forma, Folio, Aegis, and Limen
- foundation and Aegis boundary manifests
- bounded shared-file ownership for integration surfaces
- canonical agent entry region
- strict Limen project-readiness verification
- sacrificial clean-room/factory rehearsal
- second-initialization no-drift assertion
- read-only `status` across lock, requirements, lifecycle verification, and execution readiness
- locked-state `repair` that refuses declaration drift
- Conditor lock schema v2 with the prior governing declaration
- initial version-only lifecycle `upgrade` path with hard rejection of application-bound/governance changes
- real Praxis lifecycle upgrade rehearsal in CI

Remaining:

- signature/attestation policy beyond published SHA-256 checksums
- first-class compatibility graph instead of only registry-level source/version constraints
- broader scaffold catalog
- generated-state reset/reconciliation commands

## Slice 3: requirements to mission

Status: implemented for the initial greenfield flow.

Implemented:

- immutable requirements source contract
- exact-commit materialization with safe repository-relative targets
- requirement drift verification before execution
- canonical execution-contract handoff
- Ordo greenfield routing baseline in `SDE-MAP.md`
- explicit facts, unknowns, and obligations in the Conditor-managed current-state region
- deterministic Praxis mission `COND-MISSION-001`
- mission idempotence/conflict checks
- execution-readiness gate

Remaining:

- richer requirement-to-obligation derivation where an authoritative component contract supports it
- additional Ordo/Praxis structured handoff evidence once repository revision semantics are appropriate
- multi-mission decomposition beyond the initial bounded mission

## Slice 4: agent launch

Status: initial guarded launch implemented.

Implemented:

- `conditor start --check`
- `conditor start`
- Codex adapter
- Claude Code adapter
- executable/authentication probing
- launch only after lock, requirement, contract, verification, and mission gates pass
- Praxis activation before provider invocation
- provider exit does not imply project completion
- safe `--launcher codex|claude` override that cannot enable disabled execution
- explicit `resume` that requires an already-active Praxis mission and never activates ready work
- fake Codex and fake Claude readiness coverage in public CI
- authenticated fresh-agent rehearsal script kept outside public CI

Remaining:

- resumable provider execution metadata beyond the repository/Praxis state already used by explicit `resume`
- richer provider telemetry handoff into Praxis
- actual authenticated fresh-agent trial evidence for both providers

## Indy Init competition preset

Status: governed preset assembled, embedded in release binaries, and execution-enabled after the reusable clean-room gate passed.

The target command is `conditor start --preset indy-init`. The preset defaults to Codex and can be started with Claude through the safe launcher override. It currently pins:

- Praxis, Ordo, Visual Engineering, Communication Engineering, Limen, Forma, Folio, Aegis, and Tutela;
- the Indy Init kickoff contract and normative requirements to an exact Indy Init commit; and
- the validated Percepta application and 25-screen contract package to an exact Percepta commit.

The target acceptance test is:

> Given a newly created empty Git repository and the pinned Indy Init preset, Conditor establishes the engineering environment, materializes all governing contracts, records the initial Ordo/Praxis state, verifies readiness, creates the mission, and starts the selected agent without manual file copying or hand editing.

## Later platform work

- remote GitHub bootstrap for iPad/browser use
- GitHub App or equivalent remote authorization boundary
- reusable component descriptor publication so registry knowledge can move out of Conditor core
- first-class Percepta lifecycle distribution rather than contract-only materialization
- signed component/source integrity metadata
- cross-repository upgrade and doctor orchestration
