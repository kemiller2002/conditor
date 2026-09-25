# Conditor architecture

## Purpose

Conditor is the establishment layer for an Echelon project. It begins before application code exists and carries the repository through a verified execution handoff.

The core invariant is:

> Conditor orchestrates capabilities; it does not absorb their implementation or ownership.

A component that owns repository lifecycle behavior remains responsible for its own `init`, `verify`, `doctor`, and `upgrade` semantics. Conditor's compatibility graph determines which exact component/version combinations this build is willing to orchestrate; existence of a newer upstream release is not itself compatibility evidence. Conditor resolves the desired system, invokes those contracts deterministically, binds application dependencies only to declared scaffold targets, and refuses to launch an agent until the governed repository is execution-ready.

## Implemented pipeline

```text
empty repository
      |
      v
built-in preset OR repository conditor.json
      |
      +--> built-in preset is materialized as target conditor.json
      |
      v
conditor.json
      |
      v
manifest + source validation
      |
      v
compatibility graph
      |
      +--> qualified exact versions
      +--> scaffold/runtime dependencies
      +--> execution/Praxis dependency
      |
      v
registry/distribution resolution
      |
      v
read-only deterministic plan
      |
      v
capability installation
      |
      v
capability verification
      |
      v
deterministic project scaffold
      |
      +--> explicit Forma / Folio / Aegis / Limen bindings
      +--> application foundation metadata
      +--> shared-file managed regions
      |
      v
immutable requirements materialization
      |
      v
Ordo greenfield baseline
      |
      +--> SDE-MAP.md
      +--> bounded context/CURRENT-STATE.md baseline
      +--> explicit unknowns and obligations
      |
      v
strict project readiness verification
      |
      v
Praxis mission creation
      |
      v
.conditor/lock.json
      |
      v
conditor start --check
      |
      +--> lock/manifest identity
      +--> requirement drift checks
      +--> canonical contract existence
      +--> component/project verification
      +--> mission launchability
      +--> provider executable/authentication
      |
      v
Praxis mission activation
      |
      v
Codex / Claude launcher adapter
      |
      v
agent execution
```

Agent process success does not complete the Praxis mission. Project completion remains evidence-driven through the repository's governing requirements and verification contracts.

## State and authority boundaries

Conditor owns orchestration, built-in preset selection, source/version resolution, planning, installation ordering, scaffold selection, immutable requirements materialization, Conditor lock state, readiness gating, initial mission handoff, and provider launch. Built-in presets are merely packaged project declarations: once selected, the exact preset content is written as the target repository's `conditor.json` and becomes ordinary repository state.

Praxis owns repository work state, attribution, execution telemetry, mission lifecycle, and completion evidence.

Ordo owns semantic engineering rules. Conditor initializes only a greenfield routing baseline from accepted governing inputs. It does not invent domain concepts, legal states, transitions, invariants, capabilities, or effect semantics.

Lifecycle capabilities own their installed tool files and lifecycle semantics. Application libraries such as Forma, Folio, Aegis, and Limen application bindings are installed only when an explicit scaffold identifies the correct package/project target.

Percepta contracts may be materialized as immutable governing artifacts without implying that the Percepta CLI itself has been installed.

## Shared integration files

Some repository files are integration surfaces rather than single-tool property. Conditor therefore uses bounded managed regions instead of claiming entire-file ownership.

Current examples:

- `AGENTS.md`: Conditor owns only the `conditor:agent-entry` region.
- `context/CURRENT-STATE.md`: Conditor owns only the `conditor:ordo-baseline` region.

Content outside those markers is preserved. A malformed partial region is a hard failure rather than an excuse to overwrite surrounding content.

## Distribution identity

Capability version and distribution identity are separate concepts.

A capability can currently resolve from:

- an exact package-registry version; or
- an exact GitHub commit plus declared executable entrypoint.

Moving branch names are not accepted as reproducible sources. The resolved source reference is written into Conditor lock state.

Private GitHub sources may use an existing Git credential helper or process-scoped token environment. Conditor converts the token to an in-memory Git HTTP header for the fetch operation only; credentials are not part of project manifests, locks, generated files, or diagnostic command text.

## Failure semantics

- Planning is read-only.
- Unknown required components fail before target mutation.
- Invalid or escaping requirement paths fail before target mutation.
- A fixed-source capability version without an immutable mapping fails closed.
- Application dependency binding without an explicit scaffold fails closed.
- Existing user-owned files are not silently overwritten.
- Shared files are modified only inside valid Conditor-managed regions.
- Lifecycle execution stops on the first required failure.
- Unqualified component versions and missing declared capability dependencies fail before target mutation.
- The Conditor lock is written only after initialization, requirements materialization, readiness verification, and initial mission establishment succeed.
- `conditor start` refuses execution when the manifest has drifted from the lock, governing requirements differ from their pinned sources, the contract is missing, verification fails, the Praxis mission cannot be launched, or provider authentication is unavailable.

## Reproducibility and clean-room proof

Competition and production presets pin component versions and immutable requirement sources. The generated lock records resolved component/distribution identity, the SHA-256 identity of each embedded component descriptor, the governing manifest snapshot, and a SHA-256 of the manifest.

CI exercises:

- build and dependency-free unit tests;
- deterministic planning;
- embedded preset resolution and preset-to-target manifest materialization;
- a self-contained native Conditor binary using embedded presets without a source checkout;
- empty-repository initialization;
- immutable requirement materialization;
- Ordo baseline generation;
- Praxis mission creation and activation;
- `start --check`;
- machine-readable compatibility and Doctor surfaces;
- fake Codex and Claude adapters so provider selection/readiness are tested without model credentials;
- a sacrificial factory rehearsal that rejects accidental application implementation; and
- repeated initialization with a repository snapshot comparison to prove zero unintended drift.
