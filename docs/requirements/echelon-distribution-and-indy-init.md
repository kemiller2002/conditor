# Echelon Distribution and Indy Init Requirements

Status: draft
Owner: Conditor
Cross-repository dependencies: Echelon Registry, Praxis, Ordo, Project Administration, every distributable Echelon system

## Objective

Provide one governed installation path that can establish a complete Echelon environment on a supported clean host and can reproduce the Indy Init competition environment without manual dependency installation, repository cloning, file copying, or hand editing.

The primary user-facing boundary is Conditor. Echelon Registry describes distributable systems and release sets. Praxis governs repository execution after handoff. Project Administration owns central installation inventory.

## Architectural invariants

- Conditor orchestrates installation; it does not absorb component-owned lifecycle logic.
- Registry describes systems, releases, compatibility, and profiles; it does not record where software is installed.
- Project Administration records installation instances and history.
- Praxis may request or validate an environment but must not become the package manager.
- Released artifacts are immutable.
- A profile is a versioned desired-state declaration, not a monolithic package.
- Consumer installation must not require cloning source repositories.
- Consumer installation of self-contained tools must not require a machine-wide .NET runtime, Node.js, or an F# toolchain.
- Planning is read-only. Mutation begins only from an explicitly authorized plan or an equivalent non-interactive policy boundary.
- Unknown, ambiguous, unverifiable, or incompatible required state fails closed.

## Pass 1: complete installation architecture

### Bootstrap and entry point

**CON-DIST-001** Conditor SHALL be the only Echelon component a new user must bootstrap manually.

**CON-DIST-002** The project SHALL provide supported bootstrap entry points for macOS/Linux shell and Windows PowerShell.

**CON-DIST-003** Bootstrap SHALL detect host OS and architecture and select an exact compatible Conditor artifact.

**CON-DIST-004** Bootstrap SHALL verify artifact integrity before execution.

**CON-DIST-005** Bootstrap SHALL install Conditor without requiring Git, Node.js, a machine-wide .NET runtime, or source checkout unless the selected platform cannot support a self-contained artifact and that limitation is explicitly reported.

**CON-DIST-006** Bootstrap SHALL never execute an unpinned moving branch or mutable artifact as part of a reproducible profile.

### Profiles and desired state

**CON-DIST-010** Conditor SHALL support versioned installation profiles that declare a complete desired Echelon environment.

**CON-DIST-011** Profiles SHALL be independently versioned from Conditor releases.

**CON-DIST-012** A profile SHALL be able to extend another profile without copying its complete contents.

**CON-DIST-013** A profile SHALL identify required components, optional components, version constraints or exact versions, supported platforms, and policy constraints.

**CON-DIST-014** Conditor SHALL provide at least these conceptual profiles:
- minimal host readiness;
- standard Echelon engineering;
- Indy Init competition;
- governance/tooling;
- web/application development.

The exact names may differ, but the roles SHALL remain distinct.

**CON-DIST-015** The Indy Init profile SHALL resolve to an immutable release set so the same named profile version can be reconstructed later.

**CON-DIST-016** A profile resolution SHALL produce a machine-readable lock describing the exact system versions, artifacts, digests, sources, and profile identity selected.

**CON-DIST-017** Conditor SHALL refuse a profile whose required component has no compatible artifact for the current platform.

### Registry-driven resolution

**CON-DIST-020** Conditor SHALL be able to resolve installable Echelon systems from Echelon Registry metadata rather than requiring system release facts to be compiled into Conditor source.

**CON-DIST-021** Registry metadata SHALL remain separable from Conditor's local compatibility policy so a release existing in Registry does not automatically mean the current Conditor build has qualified it.

**CON-DIST-022** Conditor SHALL cache resolved registry metadata only as non-authoritative local state.

**CON-DIST-023** Every resolved release SHALL identify system id, semantic version, canonical repository, immutable release/tag identity, executable name where applicable, distribution mechanism, platform artifact, and digest.

**CON-DIST-024** Release stage/channel such as stable, preview, or nightly SHALL be distinct from distribution mechanism such as GitHub Release, NuGet, or npm.

**CON-DIST-025** Conditor SHALL support an offline authoritative input consisting of a previously resolved signed or digest-verified catalog/profile bundle.

### Component lifecycle contract

**CON-DIST-030** Every independently installable Echelon executable SHALL expose a stable machine-readable version identity.

**CON-DIST-031** Installable lifecycle components SHALL expose the lifecycle operations Conditor needs to establish and verify them, including installation/initialization, status, verify, doctor, and upgrade where supported.

**CON-DIST-032** Conditor SHALL invoke component-owned lifecycle behavior rather than reproducing another component's installation internals.

**CON-DIST-033** Component lifecycle commands used by Conditor SHALL support non-interactive execution and machine-readable results.

**CON-DIST-034** A component's version output SHALL distinguish canonical system id, version, build/release identity, and compatibility aliases where relevant.

**CON-DIST-035** Identity aliases and renames SHALL resolve to one canonical system id; ambiguous identities SHALL fail rather than create duplicate installations.

**CON-DIST-036** Conditor SHALL consume Echelon Registry's generic repository lifecycle contract `echelon.repository-lifecycle` (`spec/repository-lifecycle-contract.md` in `kemiller2002/echelon-registry`) only from a release that declares it, as carried into a verified resolved release set's `repositoryLifecycle` field. It SHALL refuse unsupported contract versions and unknown contracts, and SHALL refuse generic lifecycle planning for a `repository-lifecycle` component whose release declares no contract.

**CON-DIST-037** For a conforming component, Conditor SHALL derive every lifecycle fact — system id, exact version, repository, tag, source commit, executable, platform artifact and SHA-256, contract version — from the verified resolved release set, and SHALL plan only the contract invocations `<executable> status|init|verify|doctor|upgrade --root <repository>`. Conditor source SHALL contain no system-specific lifecycle, URL or version logic for such components.

**CON-DIST-038** Before any repository invocation, Conditor SHALL revalidate the cached artifact digest and the installed executable's full contract identity (`systemId`, `repository`, `executable`, `releaseVersion`, `sourceCommit`) against the resolved release set, and SHALL execute only the invocations disclosed in a plan whose digest binds the resolved-set identity, platform and repository root.

### Installation plan and execution

**CON-DIST-040** Conditor SHALL calculate the complete dependency and effect plan before mutation.

**CON-DIST-041** The plan SHALL disclose every download, file-system mutation, activation change, prerequisite action, external trust boundary, and registration operation that Conditor can predict.

**CON-DIST-042** Plan identity SHALL be digestible so authorization can apply to the exact disclosed plan.

**CON-DIST-043** Installation SHALL execute in dependency order.

**CON-DIST-044** A required component failure SHALL stop dependent installation steps.

**CON-DIST-045** Optional component failure SHALL follow explicit profile policy and SHALL NOT silently convert the component to required or ignored.

**CON-DIST-046** Conditor SHALL verify each component before considering it installed.

**CON-DIST-047** Desired-state lock/receipt state SHALL be written only after the corresponding operation has reached a verified postcondition.

### Receipts and inventory

**CON-DIST-050** Each installation step SHALL produce durable local evidence sufficient to determine whether its expected postcondition matches, mismatches, or is indeterminate.

**CON-DIST-051** Conditor SHALL maintain an ownership ledger distinguishing Conditor-created, adopted, shared, external, user-owned, and other-Echelon resources.

**CON-DIST-052** Re-running the same satisfied profile SHALL be idempotent and SHALL produce zero unnecessary mutation.

**CON-DIST-053** Conditor SHALL register verified installations through the Echelon installation protocol when that integration is available.

**CON-DIST-054** Central registration SHALL never replace local installation evidence.

**CON-DIST-055** Failure of optional central registration SHALL be diagnosable and SHALL NOT invalidate an otherwise successful installation unless the selected profile explicitly requires registration.

### User commands

**CON-DIST-060** Conditor SHALL provide commands or equivalent stable operations for plan, apply/install, status, doctor, repair, upgrade, uninstall, and verify.

**CON-DIST-061** Status and doctor SHALL be read-only.

**CON-DIST-062** Machine-readable output SHALL have versioned schemas and stable diagnostic identifiers.

**CON-DIST-063** Repair SHALL restore the already-declared desired state and SHALL NOT smuggle version/profile changes into a repair operation.

**CON-DIST-064** Upgrade SHALL be a distinct declared desired-state change with an inspectable plan.

**CON-DIST-065** Uninstall SHALL derive removal authority from recorded ownership and SHALL never remove adopted or user-owned resources merely because they resemble Echelon files.

## Initial acceptance criteria

A supported clean host can bootstrap Conditor, resolve a named profile, present a deterministic plan, install exact verified artifacts, verify the resulting environment, emit durable local receipts, and report the complete installed state without requiring source repository clones or manual dependency installation.
