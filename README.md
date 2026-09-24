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

Supported lifecycle components in the first slice are Praxis/ROS, Ordo/SDE, Visual Engineering, Communication Engineering, and Limen.

Forma, Folio, and Aegis are represented in the architecture but package binding is deliberately deferred until Conditor owns project scaffolding. Conditor must not guess which `package.json` or .NET project should receive an application dependency.

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
