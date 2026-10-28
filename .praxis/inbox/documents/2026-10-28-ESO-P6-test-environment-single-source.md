# ESO-P6: Standardized browser test environments: gap closure over EII-E

Priority: P1 (every browser-tested rehearsal repository)
Owner system: Conditor
Target repositories: conditor
Intake: Praxis document ingestion. Source material, not a canonical requirement.
Initiative: Echelon development speed optimization, Priority Six ("Standardized browser test environments").
Builds on: WI-0058 / `.praxis/inbox/documents/2026-10-28-EII-E-test-environment-contract.md` (merged as PR #117, main `83287e9`).

## Evidence

- Rehearsal 5 (kemiller2002/indy-init-rehearsal-005, read-only): the test-site port was written in 11 executable
  places; a config-only move (`a0e7d4a`) broke main run 40599949953; a merge (`d23060f`) reintroduced the old
  port; Playwright reused any server on the port without an artifact identity check (`b6238ad`, `2e7599e`).
- Evaluation of the merged contract against the Priority Six field list (application endpoint, port allocation,
  browser configuration, test configuration, fixture locations, artifact identity, startup diagnostics) and
  negative-test list (port collision, incorrect endpoint, missing fixture, stale build, incorrect runtime
  configuration, missing browser dependencies): every field and five of six negative classes are covered
  end to end. Gaps found:
  1. **Incorrect endpoint** is exercised only through override disagreement and a pure-core unit test; no
     end-to-end preflight scenario covers a contract whose base URL disagrees with its own port (the exact
     `a0e7d4a` shape) or a malformed endpoint.
  2. **Single source for consumers.** The opt-in scaffold's `playwright.config.js` reads the contract JSON
     statically, but the override rules (`test.portVariable` rewrites a local base URL; `test.baseUrlVariable`
     replaces it; `browsers[].executableVariable` wins over `executable`) live only in F# `effective`. A consumer
     that honours overrides must re-implement them in JavaScript or shell, which is how duplicate port values
     returned in Rehearsal 5. Shell scripts (R5 `tools/acceptance/ci/fixture-ui.sh`) cannot read the JSON at all.

## Requirements (proposed)

- **ESO-P6-R1. Resolved-settings emitter.** `conditor test-env resolve [--contract PATH] [--target PATH]
  [--json | --shell]` prints the effective port, base URL, readiness URL, application endpoint, browser
  executables and runtime environment, computed by the same pure `effective` function the preflight uses.
  `--shell` prints POSIX `export` lines (single-quoted) so scripts can `eval` them; `--json` is for configs.
  An invalid override exits 3 with the same message the preflight gives; a missing/invalid contract exits 2.
- **ESO-P6-R2. Incorrect-endpoint negative tests.** End-to-end preflight scenarios for a self-disagreeing
  contract (base URL port differs from `test.port`) and a malformed base URL override, timed like the others.
- **ESO-P6-R3. Documentation.** `docs/test-environment.md` documents `resolve` as the single-source path for
  existing repositories and the measured timings.

## Acceptance criteria

- `resolve` output follows the port override exactly as the preflight does (tested), and shell output quotes values.
- Both incorrect-endpoint scenarios fail the preflight with an actionable `COND-TENV-CONFIG` finding.
- Repositories that do not call `resolve` see no behaviour change; no new package dependencies.
