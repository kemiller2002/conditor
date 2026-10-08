# Conditor

Conditor establishes a governed engineering environment from an empty repository.

Its first target is the Indy Init demonstration: start with an empty Git repository, resolve a declarative project manifest, install the selected Echelon capabilities, verify the result, create the initial mission, and hand execution to an agent without manual file copying.

This repository contains the reusable Conditor CLI and installation contracts. It is not specific to the competition application.

## First vertical slice

The current implementation is intentionally small and executable:

1. read and validate `conditor.json`;
2. resolve pinned Echelon lifecycle components from the built-in registry;
3. produce a deterministic installation plan;
4. execute each component through its own supported lifecycle CLI;
5. verify each installed capability;
6. write `.conditor/lock.json` only after the full plan succeeds.

Supported lifecycle components in the proven clean-room slice are Praxis/ROS, Ordo/SDE, Visual Engineering, Communication Engineering, and Limen. Tutela is also registered in the lifecycle catalog. Communication Engineering demonstrates the immutable GitHub-commit transport when a capability is ahead of its package-registry publication.

Conditor now owns a deterministic F#/Limen scaffold, so Forma, Folio, and Aegis can be bound to explicit generated targets without guessing. The scaffold also emits foundation metadata and an Aegis boundary manifest; application dependencies still require an explicit scaffold/binding target and Conditor will stop rather than guess.

## Native installation

Tagged releases produce self-contained binaries for Linux, macOS, and Windows on x64 and ARM64. The installers verify the selected release artifact against its published SHA-256 before installation. See `docs/installation.md`.

## Build

```bash
dotnet build Conditor.slnx
dotnet run --project tests/Conditor.Tests
```

## Adopt an existing Echelon repository

Conditor can take governance of an existing repository without reinstalling healthy lifecycle components.

For components already known to this Conditor build, start with the read-only proposal:

```bash
cd existing-repository
conditor adopt --target .
```

For Registry-defined systems that are not embedded in Conditor, provide an integrity-bound resolved release set:

```bash
conditor adopt --target . \
  --resolved-set /path/to/resolved-release-set.json \
  --resolved-set-sha256 <sha256>
```

The resolved set is discovery authority, not an instruction to install everything it contains. Conditor considers only entries with the `repository-lifecycle` role and a supported `echelon.repository-lifecycle` contract. If the executable is present, Conditor requires its `version` operation to report the exact Registry-selected system id, repository, executable, release version, and source commit, then requires the component's existing repository `verify` operation to pass.

Embedded lifecycle components continue to use their declared read-only `versionArguments` and `verifyArguments`. Registry authority takes precedence for the same system id so Conditor never merges two competing identities.

The proposal prints every observation, the proposed `conditor.json`, any refusal, and a digest binding the observed state and exact authority. Nothing is written during planning. After review, rerun the printed authorization command.

Authorized adoption does not invoke component `init` or rewrite component-owned state. When Registry authority is used, Conditor copies the exact resolved set to `.conditor/authority/resolved-release-set.json`, records its SHA-256 in `conditor.json`, and writes the lock only after the authority and manifest are safely materialized. Later `plan`, `verify`, `status`, `doctor`, and `repair` resolve Registry-only lifecycle components from that repository-local authority.

Conditor refuses unknown or ambiguous versions, mismatched Registry identities, unhealthy components, changed authority bytes, stale authorization digests, and repositories that already contain Conditor governance.

Application-package bindings such as Forma, Folio, Limen, and Aegis are reported but not inferred automatically because Conditor will not guess their project/scaffold target.

Once declared, the npm-distributed web packages (Limen, Forma, Folio) follow the Registry current selection through `conditor upgrade --current`: Conditor proves the exact source and target releases and their digests, changes exact pins only through npm, and refuses unpinned, drifted or unlocked bindings. See `docs/web-package-upgrade-contract.md`.

## Empty-repository quick start

Built-in presets are embedded in the native Conditor executable, so a target repository does not need a Conditor source checkout or a manually copied manifest.

```bash
mkdir indy-demo
cd indy-demo
git init

conditor start --preset indy-init
```

That single `start` command establishes an uninitialized repository when necessary, writes the exact preset to `conditor.json`, installs and verifies the declared Echelon environment, materializes pinned governing artifacts, creates the Ordo baseline and Praxis mission, checks execution readiness, activates the mission, and invokes the configured provider.

The Indy Init preset defaults to Codex. The same initialized repository can be handed to Claude without editing its governing manifest:

```bash
conditor start --preset indy-init --launcher claude
```

Use `conditor start --check --preset indy-init` only after initialization when you want a read-only readiness check.

## CLI

```bash
conditor presets
conditor compatibility [--json]
conditor adopt --target .
conditor adopt --target . --resolved-set ./resolved.json --resolved-set-sha256 <sha256>
conditor adopt --target . --authorize <plan-digest>
conditor plan   --preset indy-init --target .
conditor init   --preset indy-init --target .
conditor start  --preset indy-init --target .

conditor plan   --target . --manifest ./conditor.json
conditor init   --target . --manifest ./conditor.json
conditor verify --target . --manifest ./conditor.json
conditor doctor --json --target . --manifest ./conditor.json
conditor status --json --target . --manifest ./conditor.json
conditor repair  --target . --manifest ./conditor.json
conditor upgrade --target . --manifest ./conditor.json
conditor start   --check --target . --manifest ./conditor.json
conditor resume  --launcher claude --target . --manifest ./conditor.json
```

The committed source form of the Indy Init preset remains at `examples/indy-init.conditor.json`; release binaries embed that exact content.

See `docs/architecture.md`, `docs/component-contract.md`, and `docs/roadmap.md`.



## Private pinned sources

A preset may reference an exact commit in a private GitHub repository. Conditor never writes source credentials into `conditor.json`, `.conditor/lock.json`, generated files, or command diagnostics.

Git fetches can use an existing Git credential helper. For non-interactive environments, Conditor also recognizes these environment variables, in priority order:

1. `CONDITOR_GITHUB_TOKEN`
2. `GH_TOKEN`
3. `GITHUB_TOKEN`

The token is passed to the child Git process only as an in-memory HTTP authorization header and is not placed in the Git command line.

For the private Indy Init governing repository, authenticate Git or set a token with read access before the first `init` or `start --preset indy-init`.



## Start versus resume

`conditor start` owns the ready-to-active transition. It verifies the environment, activates the deterministic Praxis mission when it is `ready`, and then launches the selected provider.

`conditor resume` never performs that transition. It requires the Praxis mission to already be `active`, re-runs the same readiness/provider checks, and invokes the selected provider against the existing work context. This makes provider handoff explicit:

```bash
conditor start
# Codex session exits while work remains active
conditor resume --launcher claude
```

A resume attempt against `ready`, `blocked`, `complete`, or `abandoned` work fails without changing Praxis state.


## Compatibility and Doctor

`conditor compatibility` exposes the exact component versions and dependency relationships this Conditor build has qualified. Planning fails closed when a manifest asks for an unqualified version or violates a declared capability dependency.

`conditor doctor` is read-only and aggregates Conditor-owned checks with each lifecycle component's own doctor command. Findings use stable `COND-DOC-...` codes and include remediation when action is required. Use `--json` for agents and automation.

The compatibility graph is intentionally conservative. A newer upstream release is not automatically considered compatible merely because it exists.

## Status and repair

`conditor status` is read-only. It reports:

- whether `conditor.json` still matches the Conditor lock;
- whether every pinned requirement artifact still matches its immutable source;
- whether declared lifecycle components verify;
- and, when execution is enabled, whether the Praxis mission and canonical contract are execution-ready.

`conditor repair` is intentionally narrower than upgrade. It reconciles only the exact manifest already recorded by the lock. Missing tool-owned/generated state may be restored through the component and scaffold contracts, but Conditor refuses repair when `conditor.json` has changed since the lock was written. A declaration/version change is governance work and must not be smuggled through a repair command.


## Upgrade

Conditor lock schema v4 stores the complete governing declaration and preserves either the embedded component-descriptor identity or the integrity-bound Registry authority that produced each resolved component.

### Bring an existing repository to a Registry current set

Use an exact Registry resolved release set as the definition of "current". Conditor does not scrape product repositories for moving tags or infer that the highest GitHub release is compatible.

First review the complete plan:

```bash
conditor upgrade --current --check --target . \
  --resolved-set /path/to/current.resolved.json \
  --resolved-set-sha256 <sha256>
```

The plan compares the versions already declared in `conditor.json` with the exact Registry selection, refuses downgrades, discloses host-level artifact changes and repository lifecycle operations, and emits one outer authorization digest. Planning is read-only.

To apply the exact reviewed plan:

```bash
conditor upgrade --current --target . \
  --resolved-set /path/to/current.resolved.json \
  --resolved-set-sha256 <sha256> \
  --authorize <plan-digest>
```

For each already-declared system, Conditor uses the strongest available generic contract:

- exact native releases are installed or reused through the integrity-verified workstation engine;
- releases declaring `echelon.repository-lifecycle` use their standard `upgrade --root` and `verify --root` operations;
- legacy native lifecycle systems are upgraded only when this Conditor build has explicitly qualified the exact target version and lifecycle command contract;
- package-distributed repository lifecycle tools (Registry distribution class `repository-lifecycle`, such as the Visual Engineering npm CLI) are upgraded only when this Conditor build qualifies the exact target version, the Registry release names the descriptor's package and exactly one package artifact digest, and the descriptor maps that version to an immutable distribution. Conditor runs the package's own `upgrade` at exactly the target version, then its verify contract (`verify --strict` for Visual Engineering); for example Visual Engineering 1.0.0 -> 1.0.1, whose upgrade rewrites its managed `.gitignore` region to `!.visual-engineering/`.

Conditor never silently adds a new component to a repository during `--current`, never downgrades a declared component, and never guesses a project-binding migration. A changed Forma, Folio, Limen, Aegis, or other application binding is refused until its distribution exposes a safe generic migration contract.

#### Opting in to an optional native tool

A resolved set can select optional components, such as `strata` in echelon-current 1.2.0, that a project has not declared. To adopt one:
1. Declare it in `conditor.json` at exactly the version the set selects.
2. Run `upgrade --current`.

The plan names it under `repository lifecycle opt-ins`. When applied, it:
1. installs the tool through the workstation engine;
2. runs its `init --root` and `verify --root` with the installed copy;
3. verifies the whole repository;
4. commits the authority and a lock that establishes it.

This is the only change to `conditor.json` that `--current` accepts since the lock was written. A different version, a tool the set does not select, or a tool without an `echelon.repository-lifecycle` contract is refused, and so is any other edit.

After component upgrades, Conditor verifies the complete target repository against a staged copy of the new Registry authority. It runs that verification with the tools the upgrade installed (the workstation bin directory first), not whatever else is on `PATH`. Only after that succeeds does it atomically replace the persisted authority and `conditor.json`, write a fresh lock, and calculate the same current plan again. A successful operation requires that second plan to contain zero remaining version transitions.

### Explicit declaration upgrade

The original `conditor upgrade` command remains available for a deliberately edited `conditor.json`. It compares the prior lock declaration with the current manifest and supports the existing conservative version-only lifecycle upgrade boundary. It rejects membership, scaffold, requirements, execution-policy, unsafe application-binding, and unmapped source changes before mutation.

For locks older than schema v4, run `conditor repair` against the unchanged manifest first. Repair proves the current declaration still matches the recorded manifest identity and migrates the lock to the current schema before upgrade.

## Canonical execution contract

A manifest may declare `execution.contractPath`. Conditor accepts it only when that file already exists in the target repository or is one of the immutable requirement artifacts that the same initialization will materialize.

When a contract path is present, the supported scaffold emits `AGENTS.md` as a generic handoff. The handoff tells any implementation agent to read the canonical contract first, follow its referenced normative documents, preserve locked/experimental/deferred decision boundaries, and prove completion through repository evidence.

The Indy Init preset materializes:

- the pinned kickoff contract and its schema;
- the normative planning documents required before coding;
- the 25-screen Percepta application contract package;
- rehearsal and kickoff execution guidance.

The Percepta screen package is pinned to commit `9e6307734f5ae03f98155bdd0588fee13d701b50`, whose full 25-screen compilation passed Percepta Verification.

## Clean-room rehearsal

`examples/rehearsal-evidence-triage.conditor.json` describes a sacrificial Incident Evidence Triage Board rather than the competition application.

Run:

```bash
bash scripts/rehearse-clean-room.sh
```

The rehearsal begins from an empty temporary Git repository, plans and initializes the governed scaffold, materializes the canonical rehearsal contract, verifies the agent handoff and application bindings, and fails if bootstrap output contains competition-style application implementation. CI runs the same rehearsal after the core build/tests pass.

The clean-room CI path now proves initialization, a second zero-drift initialization, Ordo baseline routing, Praxis mission creation, `start --check`, guarded provider handoff through a fake Codex adapter, and Praxis-owned active execution state. The next validation layer is an authenticated fresh-agent rehearsal in a sacrificial repository, kept separate from public CI so model credentials are never required by ordinary builds.

## Workstation bootstrap

`conditor workstation plan|apply|status|reconcile` and `conditor uninstall`
bootstrap an Echelon engineering host from versioned profiles, with
plan-before-authorization, per-step receipts, an ownership ledger,
partial-failure rollback and installation registration through Praxis. See
[`docs/workstation.md`](docs/workstation.md). Release an operator build with
`gh workflow run release-orchestrator.yml -f bump=patch`.
