# Conditor SD-1 clean bootstrap verification

- Date: 2026-10-25
- Work item: `WI-0053`
- Verdict: **PASS**
- Scope: verify the released Conditor scaffold/bootstrap path only; no Causa requirements or other Echelon components were changed.

## Baseline

| Field | Value |
|---|---|
| Conditor version | 0.10.11 (latest stable release at verification time) |
| Conditor release SHA | `b37c7d35d1a1c2dbfcc94e58d38fb08f1e1f5680` (`v0.10.11`) |
| Conditor artifact | `conditor-osx-arm64` |
| Conditor artifact SHA-256 | `b3f6cb56335f0fce60c35beec5f3e8c9147fb833d0b89d3c226db91462f81504` (matches the release `checksums.txt`) |
| Indy-init requirements SHA inspected | `598a759b597e05d59e60bfbeb608c66d3945118b` (`kemiller2002/Indy-init`, `main`) |
| Causa rehearsal SHA retained as prior runtime evidence | `28183a78808c7b020baa7d533a257b602934208f` (`kemiller2002/indy-init-rehearsal-003`, `main`) |
| Scaffold template | `fsharp-limen-web`, application name `causa` |
| Registry authority | `echelon-current` 1.27.0, `osx-arm64`, SHA-256 `24b97a0e96bfc99c8e4a33e963f88111d0c4208f434a76dc454daf0cd9bd3b51` |
| SDK/runtime | .NET SDK 10.0.402; Node 24.22.1; npm 11.8.0 |
| Bootstrap command | `conditor init --manifest causa-current.conditor.json --target <new-directory>` |
| SD-1 status before this run | Fixed by `54254aa3e87e22960aa0ede79824f3a18c95d968`, merged in PR #108, released in 0.10.10, included in 0.10.11 |

The repositories discovered through the GitHub account are `kemiller2002/conditor`, `kemiller2002/Indy-init`, and `kemiller2002/indy-init-rehearsal-003`; all three default to `main`.

The current Indy-init kickoff contract requires Ordo, Praxis/ROS, Aegis, Limen, Forma, Folio, and Percepta. The verification manifest used the exact current component selection Conditor's `runKickoffSet127` qualification covers: Praxis 3.20.0, Ordo 1.17.0, Percepta 0.1.0, Aegis 1.0.0, Limen 0.9.0, Forma 0.9.0, Folio 0.4.0, and Limen F# 0.9.0. The task did not test requirements materialization or application implementation, so the temporary manifest kept `requirements` empty, matching that focused Conditor qualification boundary. No Indy-init content was modified.

## Existing SD-1 evidence

Rehearsal 3 recorded the original failure under Conditor 0.10.8: the scaffold rendered Folio 0.4.0 as the npm version `0.4.0`, but Folio 0.4.0 is distributed only as the Registry-declared GitHub release tarball, causing npm 404 during restore. Conditor work item `WI-0050` changed scaffold dependency rendering to follow each component's declared distribution. Its focused tests cover the Folio release asset, npm-distributed Limen, release-distributed Forma, exchanged distribution mechanisms, unsupported mechanisms, missing npm locations, and package-identity disagreement.

The generated 0.10.11 `package.json` used:

- Forma 0.9.0: `https://github.com/kemiller2002/forma/releases/download/v0.9.0/echelon-foundry-design-system-0.9.0.tgz`
- Limen 0.9.0: exact npm version `0.9.0`
- Folio 0.4.0: `https://github.com/kemiller2002/folio/releases/download/v0.4.0/echelon-foundry-print-components-0.4.0.tgz`

This is the corrected SD-1 behavior.

## Commands and acceptance results

Each acceptance run used a new Git repository and copied in only the same immutable Registry authority input. Package caches and downloaded tool artifacts were shared; generated files, `node_modules`, and build outputs were not.

The command sequence was identical:

1. `git init --initial-branch=main`
2. Copy the verified `echelon-current` 1.27.0 `osx-arm64` resolved release set to `.conditor/authority/resolved-release-set.json`.
3. `conditor init --manifest causa-current.conditor.json --target <run>`
4. `npm install --no-audit --no-fund`
5. `dotnet build App.slnx --configuration Release`
6. `dotnet test App.slnx --configuration Release --no-build -m:1`
7. `npm run check`
8. `conditor verify --manifest causa-current.conditor.json --target <run>`
9. `/usr/bin/python3 -u -m http.server <port> --bind 127.0.0.1`
10. `curl --fail http://127.0.0.1:<port>/src/kernel/index.html`
11. Send interrupt to the server and observe exit 0.

| Verification | Run 1 | Run 2 |
|---|---:|---:|
| Scaffold generation | PASS, exit 0, 40 planned actions | PASS, exit 0, 40 planned actions |
| Dependency restore | PASS, exit 0, 7 packages | PASS, exit 0, 7 packages |
| Compilation | PASS, exit 0, 0 warnings / 0 errors | PASS, exit 0, 0 warnings / 0 errors |
| Generated engine tests | PASS, 10/10 | PASS, 10/10 |
| TypeScript check | PASS, exit 0 | PASS, exit 0 |
| Conditor verification | PASS, exit 0 | PASS, exit 0 |
| Application startup | PASS | PASS |
| Readiness check | PASS, HTTP 200 | PASS, HTTP 200 |
| Clean shutdown | PASS, exit 0 | PASS, exit 0 |
| Manual intervention required | NO | NO |

The first exploratory run initially used the host's .NET SDK 10.0.100 and correctly failed restore because Aegis 1.0.0 requires FSharp.Core 10.1.400. Re-running without editing generated files under the already-verified SDK 10.0.402 passed. This was an environment/toolchain mismatch, not a scaffold repair and not a Limen failure. The two acceptance runs both began with the compatible toolchain already selected.

## Reproducibility

- Project structure: identical, 169 files per generated repository after excluding `.git`, dependency trees, and build outputs.
- Meaningful normalized output: 163 files, identical manifest SHA-256 `58d351f6f3004af5a57484d1e086d7038ca7379e00be73dc9f2a92110f8bde94`.
- Conditor lock: byte-identical, SHA-256 `ae0dfa0610d496dd26eaf10504346c7e626e835b0376e12ae6007ea1cadea7f4`.
- npm lock: byte-identical, SHA-256 `37f7ed79800cd6e48a23f71b5a3f0b862ae6b17fab68ddb02003234186c86935`.
- Registry authority: byte-identical, SHA-256 `24b97a0e96bfc99c8e4a33e963f88111d0c4208f434a76dc454daf0cd9bd3b51`.
- Build configuration and startup files (`Directory.Build.props`, `App.slnx`, `App.Engine.fsproj`, `package.json`, `index.html`, and `playwright.config.js`): byte-identical.
- Runtime behavior: both served the same application path, returned HTTP 200, and stopped cleanly.

Normalization ignored only the run-directory-derived Praxis repository name, Praxis instance UUIDs, timestamps, Git metadata, installed dependency trees, and build/cache output. These are the nondeterministic classes allowed by the verification contract.

## Regression evidence

- Conditor source build at `b37c7d35d1a1c2dbfcc94e58d38fb08f1e1f5680`: PASS, 0 warnings / 0 errors.
- The local all-platform test executable reported 11 unrelated failures on macOS in workstation/rollback harnesses that assume GNU/Linux shell behavior. The portable SD-1 assertion, `without an authority, Folio 0.4.0 is pinned to the release asset its descriptor declares`, passed. Registry-authority fixtures are intentionally skipped outside `linux-x64`.
- PR #108 independently passed `build-test`, `empty-repository-smoke`, `factory-rehearsal-smoke`, `echelon-current-sweep-qualification`, and all other required checks on Linux: https://github.com/kemiller2002/conditor/pull/108
- The exact 0.10.11 release commit's validation and artifact publication jobs passed: https://github.com/kemiller2002/conditor/actions/runs/40162970376
- Most importantly, both released-artifact clean bootstraps exercised the current Registry-authority SD-1 path on `osx-arm64` and passed restore, build, launch, and readiness.

## Changes and disposition

No Conditor product code, scaffold template, Causa requirement, or other Echelon component was changed. No new repair or release was required. This report and the owning-repository Praxis work record are the only repository changes.

There are no verified blockers to Conditor 0.10.11 bootstrapping the focused Causa scaffold with the identified compatible set.
