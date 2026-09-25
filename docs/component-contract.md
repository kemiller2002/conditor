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

The built-in registry is now projected from versioned component descriptor files under `components/*.component.json`. The F# registry contains no per-component package/version/source literals.

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

The v1 descriptor schema is published at `schemas/conditor-component.schema.json`. A descriptor carries component identity, qualified versions, distribution source, executable name, supported lifecycle arguments, and application binding. Built-in descriptors are embedded in the native binary through a wildcard resource rule and discovered dynamically, so adding a built-in descriptor does not require editing Registry code.

The remaining evolution is to make descriptors release-bound and externally publishable, with signed integrity metadata and compatibility constraints that can be verified independently of a Conditor source release.

## Application dependency

Application packages use a different contract because installation requires a binding target.

Examples:

- Forma: npm dependency
- Folio: npm dependency
- Aegis: NuGet dependency

Conditor must first know which generated application/project owns the dependency. It must not infer a random `package.json` or `.fsproj`.

## Compatibility

Qualified component versions now come from the descriptors. Conditor also maintains orchestration-level dependency rules that are not owned by any single component, such as `fsharp-limen-web -> limen` and `execution:enabled -> praxis`.

Planning rejects an unqualified version or missing required capability before mutation. Future descriptor revisions should carry component-owned compatibility constraints such as minimum runtime or peer-capability versions.
