# EII-E: Standard test-environment contract and preflight (reproducible browser environments)

Priority: P1 (recurring; every browser-tested rehearsal repository)
Owner system: Conditor
Target repositories: conditor
Intake: Praxis document ingestion. Source material, not a canonical requirement.
Initiative: Echelon Engineering Improvement Initiative, Workstream E ("Reproducible browser environments").

## Evidence (kemiller2002/indy-init-rehearsal-005, read-only)

- The test-site port was defined in 11 executable places (`playwright.config.js:11`, nine specs,
  `tools/acceptance/ci/fixture-ui.sh:7`) plus stale comments and docs.
- A config-only port change (`a0e7d4a`) broke CI with `ERR_CONNECTION_REFUSED` (main run 40599949953).
- A merge (`d23060f`) reintroduced the old port.
- The static test server crashed on an undecodable URL path (fixed in `2e7599e`); every later test on that
  port then saw "connection refused".
- The production port disagreed with itself (config 4821, `scripts/serve.mjs` and `package.json` 4321).
- Locally, Playwright reuses whatever server is on the port (`reuseExistingServer: !process.env.CI`) and only
  checks that `index.html` answers: there is no artifact identity check.
- Browser jobs download a `site-test` artifact (`979c465`) but nothing declares or verifies which artifact a
  browser job must test.
- Conditor already scaffolds the origin of R5's `playwright.config.js` (`Scaffolding.fs` `webPlaywrightConfig`),
  a hang-proof browser install with an `ldd` check, and `Doctor.fs` aggregation. Scaffold files are written once
  and never upgraded.

## Requirements (proposed)

- **EII-E-R1. Contract schema.** `schemas/conditor-test-environment.schema.json` declares, in ONE authoritative
  file per consuming repository: application endpoint, browser base URL, test port (with its environment
  override variable), fixtures (path and expected sha256), build configuration, runtime configuration, artifact
  identity (path plus expected content digest, or the build-info file holding it), required browsers
  (executable or override variable, expected revision), and the diagnostics output location.
- **EII-E-R2. Preflight.** `conditor test-env preflight [--contract PATH] [--json]` fails fast, with what/where/how
  to fix diagnostics written to the contract's diagnostics location, for:
  1. port unavailable, or occupied by a server not serving the expected artifact;
  2. conflicting configuration (endpoint port differs from test port, duplicate ports, override disagreeing);
  3. missing fixture or fixture digest mismatch;
  4. wrong build configuration (artifact build-info configuration differs from the contract);
  5. missing browser, missing shared libraries, or wrong browser revision;
  6. stale artifact (digest mismatch: reuse the exact candidate artifact or refuse);
  7. a running server that crashes or answers 5xx on an undecodable URL path.
  The decision logic is a pure function (contract + observed facts -> findings) behind a thin I/O shell.
- **EII-E-R3. Tests.** One test per failure class plus a passing control, with measured preflight wall-clock time,
  compared with the time-to-failure it replaces only where evidence exists.
- **EII-E-R4. Integration.** Aggregate into `conditor doctor` when a contract exists; optionally let the
  `fsharp-limen-web` scaffold emit the contract and a `playwright.config.js` that reads its ports from it.
  Repositories that do not opt in see no behavior change.
- **EII-E-R5. Documentation.** Contract, command, prevented failures, and adoption steps for consumers and agents
  (adoption is NOT applied to Rehearsal 5, which is an active experiment).

## Acceptance criteria

- Each of the seven failure classes is detected by a test and produces an actionable finding; the control passes.
- `conditor doctor` in a repository without a contract is unchanged.
- A fresh `fsharp-limen-web` scaffold has exactly one place that defines the test port.
