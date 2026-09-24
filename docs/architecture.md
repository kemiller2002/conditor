# Conditor architecture

## Purpose

Conditor is the establishment layer for an Echelon project. It begins before application code exists.

The core invariant is:

> Conditor orchestrates capabilities; it does not absorb their implementation or ownership.

A component that owns repository lifecycle behavior remains responsible for its own `init`, `verify`, `doctor`, and `upgrade` semantics. Conditor resolves the desired system and invokes those contracts in a deterministic order.

## Pipeline

```text
empty repository
      |
      v
conditor.json
      |
      v
manifest validation
      |
      v
registry resolution
      |
      v
deterministic plan
      |
      v
capability init
      |
      v
capability verification
      |
      v
.conditor/lock.json
      |
      v
project scaffold / dependency binding   [next slice]
      |
      v
requirements ingestion                  [next slice]
      |
      v
mission creation + agent launcher       [next slice]
```

## Boundaries

Conditor owns orchestration, version resolution, planning, installation ordering, lock state, compatibility checks, and launch handoff.

Praxis, Ordo, Visual Engineering, Communication Engineering, and Limen own their installed files and lifecycle semantics.

Forma, Folio, and Aegis are application dependencies, not repository lifecycle systems. Conditor will bind them only after it knows the application scaffold and the exact package target.

## Failure semantics

- Planning is read-only.
- Unknown required components fail before mutation.
- Unsupported application dependency binding fails before mutation.
- Execution stops on the first failed lifecycle action.
- The Conditor lock is written only after every requested action succeeds.
- Component lifecycle tools remain responsible for their own atomicity and conflict detection.

## Reproducibility

Competition presets and committed production manifests should pin every component explicitly. The generated lock records every resolved version and a SHA-256 of the manifest.
