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
conditor plan   --preset indy-init --target .
conditor init   --preset indy-init --target .
conditor start  --preset indy-init --target .

conditor plan   --target . --manifest ./conditor.json
conditor init   --target . --manifest ./conditor.json
conditor verify --target . --manifest ./conditor.json
conditor doctor --target . --manifest ./conditor.json
conditor start  --check --target . --manifest ./conditor.json
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
