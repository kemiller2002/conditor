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

The initial registry adapts existing package-distributed lifecycle CLIs to this contract.

## Capability identity is not distribution identity

Conditor deliberately keeps these separate:

- **Capability version** describes the version of the Echelon capability the target repository is being established with.
- **Distribution source** identifies the immutable artifact Conditor can actually retrieve to establish that version.

For a stable npm release, the source is the exact package and version, for example:

```text
capability: praxis 3.1.4
source:     @echelon-foundry/repository-operating-system@3.1.4
```

When implementation is ahead of its registry publication, a temporary fixed source may be an exact GitHub commit:

```text
capability: communication-engineering 1.0.0
source:     github:kemiller2002/communication-engineering#<commit>
```

A branch name such as `main` is not an acceptable reproducible source. Conditor rejects requests for versions that do not have an immutable source mapping.

The resolved source is written to the Conditor lock so the installation can be reconstructed later.

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
