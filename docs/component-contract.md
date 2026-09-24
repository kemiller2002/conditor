# Conditor component contract

Conditor needs a stable contract between orchestration and independently owned Echelon capabilities.

## Lifecycle capability

A lifecycle capability exposes these logical operations:

| Operation | Required | Meaning |
| --- | --- | --- |
| init | yes | Reach the declared installed state idempotently. |
| status | planned | Inspect without mutation. |
| verify | yes | Prove installed state is valid. |
| doctor | yes | Diagnose actionable problems. |
| upgrade | planned | Move an older supported state forward safely. |

The initial registry adapts existing npm-distributed lifecycle CLIs to this contract.

A future externally published descriptor should carry the component identity, immutable version, distribution source, executable name, supported operations, integrity metadata, and compatibility constraints. Conditor should eventually validate release-bound descriptors rather than maintaining package knowledge forever.

## Application dependency

Application packages use a different contract because installation requires a binding target.

Examples:

- Forma: npm dependency
- Folio: npm dependency
- Aegis: NuGet dependency

Conditor must first know which generated application/project owns the dependency. It must not infer a random `package.json` or `.fsproj`.

## Compatibility

The next registry revision will add compatibility constraints so a preset can be rejected before any mutation when one capability requires a newer Ordo, Praxis, Limen, runtime, or platform baseline.
