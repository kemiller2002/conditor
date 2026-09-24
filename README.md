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

## CLI

```bash
conditor plan   --target . --manifest ./conditor.json
conditor init   --target . --manifest ./conditor.json
conditor verify --target . --manifest ./conditor.json
conditor doctor --target . --manifest ./conditor.json
```

The committed Indy Init example is at `examples/indy-init.conditor.json`.

See `docs/architecture.md`, `docs/component-contract.md`, and `docs/roadmap.md`.


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

The next validation layer is the fresh-agent rehearsal described by the Indy Init planning repository: a new agent receives only the initialized repository contract and must build the sacrificial app without architecture coaching.
