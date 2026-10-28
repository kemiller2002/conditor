# Test-environment contract and preflight

A browser test run depends on a port, a base URL, fixtures, a build of a
particular configuration, the exact artifact that build produced, a browser
and its operating-system libraries, and a server that stays up. When any of
these is written in more than one place, or not checked at all, the suite fails
late and in a misleading way. Usually that is `ERR_CONNECTION_REFUSED` minutes
into a CI job, or a green run against the wrong bytes.

Conditor gives each consuming repository **one authoritative contract** for
these values. It also gives a **preflight** that proves the machine matches the
contract before any browser starts. Requirements: CON-368..CON-375. Work item:
WI-0058, from `.praxis/inbox/documents/2026-10-28-EII-E-test-environment-contract.md`
(Echelon Engineering Improvement Initiative, Workstream E).

- Implementation: `src/Conditor.Core/TestEnvironment.fs`. The pure core is
  `parse`, `effective` and `evaluate`, which turn the contract plus observed
  facts into findings. `observe` is the thin I/O shell.
- Specification by test: `tests/Conditor.Tests/TestEnvironmentTests.fs` and
  `WebScaffoldTests.runTestEnvironmentOptIn`.
- Schemas: `schemas/conditor-test-environment.schema.json` (the contract) and
  `schemas/conditor-test-environment-report.schema.json` (the report).

## The contract: `conditor.test-environment.json`

The contract lives at the repository root, next to `conditor.json`. Every
config, spec and script reads its values from this file. None of them writes
a port, URL or browser path of its own. Relative paths resolve against the
contract's directory.

```json
{
  "schema": "conditor.test-environment/v1",
  "application": { "endpoint": "http://127.0.0.1:4821/" },
  "test": {
    "baseUrl": "http://127.0.0.1:4822/",
    "baseUrlVariable": "APP_BASE_URL",
    "port": 4822,
    "portVariable": "APP_TEST_PORT",
    "readinessPath": "index.html"
  },
  "build": { "configuration": "test", "command": "npm run build:test" },
  "runtime": { "configuration": "fixture", "environment": { "APP_FIXTURE": "1" } },
  "artifact": {
    "path": "obj/site-test",
    "servedAt": "",
    "identityPath": "build-info.json",
    "buildInfo": { "path": "obj/site-test/build-info.json", "configurationField": "configuration", "digestField": "contentSha256" },
    "sha256": null,
    "sha256Variable": "CANDIDATE_SITE_SHA256"
  },
  "fixtures": [ { "path": ".percepta/fixtures/seeded.json", "sha256": "<64 hex>" } ],
  "browsers": [
    { "name": "chromium", "executable": "/opt/pw-browsers/chromium", "executableVariable": "APP_CHROMIUM_PATH", "revision": "1243" }
  ],
  "diagnostics": { "path": "TestResults/test-environment" }
}
```

| Field | Meaning |
|---|---|
| `application.endpoint` | The application (production-configuration) endpoint, or `null` if there is none. When it is local, it must not share the test port. |
| `test.baseUrl`, `test.port` | The test site's browser base URL and port. When the URL is local, its port must equal `port`. |
| `test.portVariable` | The one environment variable that overrides the port, for example for parallel worktrees. Every runner reads this same variable. |
| `test.baseUrlVariable` | The variable that replaces the base URL entirely, for example to test a deployed site. |
| `build.configuration`, `build.command` | The configuration the artifact under test must have, and the command that builds it. |
| `runtime.configuration`, `runtime.environment` | The runtime configuration's name, and the variables it needs with their values. A variable set to another value is a conflict. |
| `artifact.path` | The built artifact (a directory or a single file). |
| `artifact.buildInfo` | The JSON file the build writes, holding its configuration and its content digest. |
| `artifact.sha256`, `artifact.sha256Variable` | A pinned expected digest, or a variable carrying the exact candidate's digest, for example from the CI build job. |
| `artifact.identityPath`, `artifact.servedAt` | The artifact-relative file whose served bytes prove which artifact a running server serves, and where the artifact root is served under the base URL. |
| `fixtures[]` | Fixture files and their expected sha256. |
| `browsers[]` | Each browser the suite launches: its executable and/or override variable, and the expected Playwright revision (`null` means unchecked). |
| `diagnostics.path` | Where `preflight.json` and `preflight.txt` are written. |

### Artifact content digest

The content digest is SHA-256 over one line per regular file in the artifact,
excluding the build-info file. Each line is `<lowercase sha256>␠␠<relative
path>\n`. The relative path uses `/` separators, and the lines are sorted by
ordinal relative path. This is exactly what `sha256sum` prints, so a build can
record the digest without Conditor:

```bash
cd obj/site-test
find . -type f ! -path ./build-info.json | sed 's|^\./||' | LC_ALL=C sort \
  | xargs -d '\n' sha256sum | sha256sum
```

`conditor test-env digest [--contract PATH]` prints the same value. A test
checks that the recipe and the implementation agree.

## The command

```text
conditor test-env preflight [--contract PATH] [--target PATH] [--json]
conditor test-env digest    [--contract PATH] [--target PATH]
```

- `--contract` defaults to `<target>/conditor.test-environment.json`.
- Exit codes:
  - `0`: the run can be trusted. Warnings may remain.
  - `3`: at least one finding refuses the run.
  - `2`: the contract is missing or invalid.
- Output: a text report by default, or the report JSON with `--json`. The
  preflight also always writes `preflight.json` and `preflight.txt` to the
  contract's diagnostics location, so CI can upload them with the test
  results.
- Each warning and error says **what** is wrong, **where** (file, contract
  location, variable or endpoint) and **how to fix it**.
- `conditor doctor` includes the same findings as `COND-DOC-TENV-*`, but only
  when the repository has a contract. Doctor stays read-only and writes no
  diagnostics. A repository without a contract sees no change.

## Failures it prevents

| # | Failure | Code | Rehearsal 5 evidence |
|---|---|---|---|
| 1 | Port unavailable, or occupied by a server that does not serve the expected artifact. Identity is checked by comparing the served bytes of the identity file with the local artifact. | `COND-TENV-PORT` | Locally, `reuseExistingServer` reused any server on the port and only checked that `index.html` answered. |
| 2 | Conflicting configuration: base URL port ≠ test port; application and test site on one port; port and base-URL overrides that disagree; a runtime variable set to another value. | `COND-TENV-CONFIG` | The test port was defined in 11 executable places. A config-only move (`a0e7d4a`) broke main run 40599949953 with `ERR_CONNECTION_REFUSED`, and a merge (`d23060f`) reintroduced the old port. The production port disagreed with itself (4821 vs 4321). |
| 3 | Missing fixture, or fixture digest mismatch. | `COND-TENV-FIXTURE` | Nothing checked fixture presence or digests before a run. |
| 4 | Wrong build configuration: build-info's configuration ≠ the contract's. | `COND-TENV-BUILD` | `979c465`: test and production became separate builds, and nothing declared which one a browser job tests. |
| 5 | Missing browser, missing shared libraries (`ldd`), or wrong revision (read from the Playwright install directory). | `COND-TENV-BROWSER` | The CI install proves libraries with `ldd`, but a local run did not. |
| 6 | Stale artifact: the content digest ≠ the pinned digest, the candidate variable, or build-info. The fix is to reuse the exact candidate or rebuild. Nothing else is accepted. | `COND-TENV-ARTIFACT` | `build-info.json` had no content digest, and only the deployed site was checked byte-for-byte. |
| 7 | A running server answers 5xx, or crashes, on an undecodable URL path (`/%E0%A4%A`, sent raw). It is probed only when a server is running. | `COND-TENV-SERVER` | `scripts/serve.mjs` crashed on `decodeURIComponent`, and every later test got "connection refused" (fixed in `2e7599e`). |

Values the preflight cannot observe are reported as warnings, never invented.
These are an undeterminable browser revision, a shared-library check on a host
without `ldd`, and an unpinned artifact identity.

### Measured cost

Each scenario's preflight wall-clock time is printed as a `TIMING` line in the
test run. These numbers come from the WI-0058 run, on a Linux container with
fixture repositories in the temp directory.

| Scenario | Preflight wall-clock |
|---|---|
| control: no server running | 77.2 ms |
| control: faithful server reused (identity proved, probe survived) | 257.2 ms |
| 1 port occupied by a foreign HTTP server | 173.4 ms |
| 1b port held by a non-HTTP listener | 2011.8 ms (bounded by the 2 s HTTP timeout) |
| 2 / 2b / 2c conflicting configuration | 14.6 / 10.9 / 15.3 ms |
| 3 / 3b missing fixture / digest mismatch | 9.5 / 7.4 ms |
| 4 wrong build configuration | 10.3 ms |
| 5 / 5b / 5c missing browser / missing libraries / wrong revision | 10.8 / 13.5 / 15.5 ms |
| 6 / 6b stale artifact (changed bytes / not the candidate) | 10.4 / 12.4 ms |
| 7 / 7b server 5xx / crash on an undecodable path | 166.3 / 164.2 ms |
| control: the real Playwright Chromium 1243 | 12.6 ms |

What the preflight replaces in Rehearsal 5: the CI job durations of main run
40599949953 are **unavailable**, because the run's API was not reachable from
the session that built this. So no CI time-to-failure is claimed. The only
timing evidence is in Git. The breaking commit `a0e7d4a` (2026-10-27
23:00:10Z) and its fix `2e7599e` (23:28:05Z) are 27 min 55 s apart. That is
commit-to-fix latency, not the duration of a CI job.

## Adopting it

### New repositories (scaffold opt-in)

Set `"scaffold": { "kind": "fsharp-limen-web", "testEnvironment": true }` in
`conditor.json`. The scaffold then writes `conditor.test-environment.json` and
a `playwright.config.js` that reads the test port, base URL, readiness path and
Chromium path from it. Reading the JSON is the only JavaScript added. The
contract is the only scaffold file that holds the test port. Without the
opt-in, the scaffold output does not change. As with every scaffold file, the
contract is a seed: Conditor writes it once and never upgrades it.

### Existing repositories and agents

1. Write `conditor.test-environment.json` with the values the repository uses
   today, and validate it against the schema.
2. Delete every other definition of the port, base URL and browser path.
   Configs, specs, CI scripts and fixture scripts read the contract, or the
   contract's one override variable. A spec takes the origin from Playwright's
   `baseURL`, not from a literal.
3. Make the build write `build-info.json` with `configuration` and
   `contentSha256`, using the digest recipe above.
4. In CI, after the build job, pass the candidate's digest
   (`artifact.sha256Variable`) to every job that downloads the artifact. Run
   `conditor test-env preflight` before Playwright or Percepta starts. Upload
   the diagnostics directory with the test results.
5. Locally and in agent sessions, run the preflight before the browser suite.
   Treat exit 3 as a refusal. Follow the finding's fix instead of retrying.

### Steps a future rehearsal would take

**Not applied to Rehearsal 5**, which is an active experiment. These are the
steps a later rehearsal would take from the start:

1. Bootstrap with `scaffold.testEnvironment: true`, or add the contract in the
   first commit that adds browser tests.
2. Declare `application.endpoint` (production preview) and `test.port` as
   distinct ports, with one override variable for parallel worktrees.
3. Have the static server read its port from the contract, and give it a
   400-on-undecodable-path rule. The preflight's probe proves that rule.
4. Have the test build write `configuration` and `contentSha256` to
   build-info. Pass the site job's digest to the browser, live and Percepta
   jobs through `artifact.sha256Variable`.
5. Declare the Percepta and browser fixtures with their digests, and the
   Chromium revision the Playwright pin ships.
6. Add a `conditor test-env preflight` step before each browser job, and
   upload `TestResults/test-environment/`.
