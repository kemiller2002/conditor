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

## Registry-declared lifecycle contract

Embedded descriptors describe legacy and bootstrap components whose lifecycle
arguments differ per system (for example `verify --strict`). A Registry release
that declares `echelon.repository-lifecycle` v1 needs no descriptor at all:
Conditor derives everything from the verified resolved release set and invokes
the standard contract (`docs/workstation.md`, CON-DIST-036..038). New
conforming systems should use that path rather than adding a descriptor.

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

### Package renames

A component's package name can change between versions. The descriptor's `package` is the current identity. Earlier identities go in `historicalPackages`, and each entry lists the exact qualified versions that were published under that name:

```json
"package": "@echelon-foundry/limen",
"historicalPackages": [
  { "package": "@echelon-foundry/typescript-wasm-kernel", "versions": ["0.6.1", "0.6.2"] }
],
"defaultVersion": "0.7.0",
"qualifiedVersions": ["0.6.1", "0.6.2", "0.7.0"]
```

Every package reference the plan produces comes from `ComponentDefinition.packageFor version`: the npm/NuGet binding, the lifecycle `npx --package` source, the lock's `package`, adoption sources, and the scaffold's Limen protocol import. A legacy pin therefore reproduces its original package, and a lock written for Limen 0.6.2 still records `@echelon-foundry/typescript-wasm-kernel`. Descriptor loading refuses an entry whose version is not qualified, an entry that repeats the current `package`, a version listed under more than one identity, the same identity declared twice, and a `defaultVersion` that is listed under a historical identity.

The v1 descriptor schema is published at `schemas/conditor-component.schema.json`. A descriptor carries component identity, qualified versions, distribution source, executable name, supported lifecycle arguments, and application binding. Built-in descriptors are embedded in the native binary through a wildcard resource rule and discovered dynamically, so adding a built-in descriptor does not require editing Registry code.

The `distribution` decides which optional fields a descriptor must carry. The descriptor loader (`ComponentDescriptors.parse`) enforces these rules and the schema states the same rules:

| `distribution` | Required in addition to the common fields | Refused |
| --- | --- | --- |
| `host-tool` | `command`, non-empty `versionArguments` | `lifecycleSource` |
| `lifecycle-npm` | `lifecycleSource`, `command`, non-empty `versionArguments` | |
| `npm` | `applicationBinding: "npm"` | |
| `nuget` | `applicationBinding: "nuget"` | |

The loader fails closed on the schema's structural rules as well. It refuses, with a typed `DescriptorViolation` and a specific message: properties the schema does not define (at the top level, in `lifecycleSource` and in `lifecycleSource.entrypoint`); an `id`, `lifecycleSource.repository` or `lifecycleSource.commit` that does not match its schema pattern; duplicate or empty `qualifiedVersions` entries; a `command` that is not a non-empty string; and an `applicationBinding` that is not a string.

`versionArguments` is an array of strings. A `host-tool` component is a native executable already installed on the host (for example Praxis, Ordo, Percepta); Conditor invokes its `command` directly and never resolves it through a package manager.

The test suite validates every descriptor under `components/` against `schemas/conditor-component.schema.json`, and the `conditor components --json` inventory against `schemas/conditor-components.schema.json`, so the published schemas cannot drift from what the loader accepts and the CLI emits. CI repeats the check with an independent validator.

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


## Adoption discovery contract

Lifecycle component descriptors used for existing-repository adoption declare `versionArguments`, a read-only command argument vector that reports the installed component version without changing repository state.

Conditor adoption executes the already-installed command directly, first with `versionArguments` and then with the component's existing `verifyArguments`. It does not invoke package-manager resolution, `init`, `upgrade`, or a remote source during discovery.

A component is eligible for adoption only when:

1. its command is already available on PATH or the target repository's `node_modules/.bin`;
2. the version probe identifies exactly one embedded qualified version;
3. the verification contract exits successfully; and
4. Conditor can preserve the immutable source identity required by its component descriptor.

This probe is evidence for adoption, not authentication, and does not transfer ownership of component-managed files to Conditor.


## Registry-authorized adoption

An existing repository MAY supply a Registry `echelon.resolved-release-set/v1` as additional adoption authority. The caller must provide the expected SHA-256 separately; Conditor rejects the set before discovery if the bytes, platform, profile/catalog identity, distribution facts, or lifecycle contract do not validate.

Only resolved entries with role `repository-lifecycle`, distribution class `self-contained-native-cli`, active lifecycle state, GitHub-release distribution, and a supported `echelon.repository-lifecycle` contract participate in repository adoption.

For each such entry Conditor runs only:

1. the contract-defined `version` identity probe; and
2. `verify --root <repository>`.

The identity probe must exactly report the Registry-selected `systemId`, `repository`, `executable`, `releaseVersion`, and immutable `sourceCommit`. Discovery never invokes `init` or `upgrade`.

After authorization, the exact resolved-set bytes are copied to `.conditor/authority/resolved-release-set.json` and their SHA-256 is recorded in `conditor.json`. Future repository lifecycle planning resolves non-embedded components from this authority. The resolved set therefore remains part of durable project governance rather than transient CLI input.

A Registry-resolved component identity and an embedded descriptor with the same system id are never blended. The explicit Registry authority owns discovery for that id.
