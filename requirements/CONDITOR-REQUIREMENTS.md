# Conditor requirements

## Goal

Conditor must transform an uninitialized Git repository into a reproducible, governed, execution-ready project without requiring the user to manually copy framework files, prompts, requirements, workflows, or package configuration.

## Foundation

- **CON-001** Conditor shall accept a machine-readable project manifest.
- **CON-002** The manifest shall be schema-versioned and reject unsupported schema versions.
- **CON-003** Required components shall be resolved before repository mutation begins.
- **CON-004** Unknown required components shall stop execution before mutation.
- **CON-005** Component versions used by reproducible presets shall be immutable/pinned.
- **CON-006** Conditor shall produce a deterministic ordered plan for the same manifest, registry, platform, and target state.
- **CON-007** Planning shall not mutate the target repository.
- **CON-008** Conditor shall stop on the first failed required action.
- **CON-009** Conditor shall verify every lifecycle capability after installation.
- **CON-010** Conditor shall write lock state only after all requested initialization and verification actions succeed.
- **CON-011** Lock state shall record resolved component identities and versions plus a digest of the source manifest.
- **CON-012** Conditor shall be idempotent once component lifecycle contracts support idempotence.
- **CON-013** Conditor shall never silently overwrite user-owned work to repair a component installation.
- **CON-014** Conditor shall expose diagnostics that identify the failed component, operation, and repair path without exposing credentials.

## Component boundaries

- **CON-020** Conditor shall orchestrate component lifecycle contracts rather than copy component implementation into the target.
- **CON-021** Lifecycle components shall expose init and verify operations; doctor and upgrade are required for full support.
- **CON-022** Application dependencies shall declare their package ecosystem and exact binding target.
- **CON-023** Conditor shall not guess which npm project, .NET project, or other package target receives a dependency when more than one target is plausible.
- **CON-024** Compatibility constraints shall be evaluated before mutation.
- **CON-025** Component descriptors shall eventually be release-bound and integrity-verifiable rather than relying permanently on a hard-coded central registry.
- **CON-026** Capability version and distribution identity shall be separate concepts.
- **CON-027** A lifecycle capability may resolve through an exact package release or an immutable GitHub commit plus declared entrypoint.
- **CON-028** GitHub source acquisition shall use an exact full commit SHA and shall never use a moving branch for a reproducible installation.
- **CON-029** Source cache contents shall be non-authoritative tooling state and shall not substitute for repository lock or installation evidence.

## Empty-repository bootstrap

- **CON-030** A supported Conditor release shall be runnable on a new workstation without a preinstalled .NET SDK.
- **CON-031** Native release artifacts shall be available for supported Windows, macOS, and Linux architectures.
- **CON-032** Downloaded release artifacts shall be verified against published integrity metadata before execution.
- **CON-033** Conditor shall support a single entry point that can initialize an empty Git repository.
- **CON-034** Conditor shall create the selected project scaffold before binding application dependencies.
- **CON-035** Project scaffolding shall be deterministic and versioned.
- **CON-036** Conditor shall be able to prove that a second initialization produces no unintended drift.
- **CON-037** Capability installation verification and project execution-readiness verification shall be distinct gates when a capability supports adopting an empty repository before application code exists.
- **CON-038** Strict boundary verification shall run before agent execution once the project scaffold and configured source paths exist.

## Requirements and mission

- **CON-040** A project manifest shall be able to reference one or more requirements sources.
- **CON-041** Requirements ingestion shall preserve source identity and traceability.
- **CON-042** Requirements shall be validated before code-generation execution begins.
- **CON-043** Conditor shall initialize Ordo state from the accepted requirements set.
- **CON-044** Conditor shall create the initial Praxis work/mission record before implementation begins.
- **CON-045** Required evidence and verification obligations shall be established before implementation begins.
- **CON-046** A project shall not be declared execution-ready while required requirements, compatibility, or verification gates are unresolved.

## Agent launch

- **CON-050** Agent execution shall be behind an explicit launcher-adapter contract.
- **CON-051** Credentials shall remain external to manifests, generated files, logs, and lock state.
- **CON-052** Conditor shall check launcher capability and required credentials before starting execution.
- **CON-053** Agent launch shall occur only after the repository passes the execution-readiness gate.
- **CON-054** Completion shall be determined by project requirements and deterministic verification, not solely by an agent completion statement.
- **CON-055** Execution shall be resumable from durable repository state.
- **CON-056** Multiple supported agents shall be able to consume the same initialized repository contract.
- **CON-057** A manifest may designate one canonical execution contract path for agent handoff.
- **CON-058** A designated execution contract shall already exist or be materialized from an immutable requirement source in the same initialization plan; Conditor shall not generate a handoff to a missing contract.
- **CON-059** When a canonical execution contract is configured, supported scaffolds shall generate a generic agent entry file that points to that contract without embedding application implementation.
- **CON-060** Agent handoff instructions shall distinguish normative contracts from implementation freedom and shall prohibit treating an agent completion statement as project completion.
- **CON-061** Conditor shall support a clean-room rehearsal that starts from an empty Git repository and proves deterministic bootstrap without application-specific source implementation.
- **CON-062** The clean-room rehearsal shall fail when expected contract, lock, scaffold, or framework-binding artifacts are missing.
- **CON-063** A competition/project preset may materialize application UI contracts as pinned requirement artifacts without Conditor understanding their domain semantics.
- **CON-064** Preset requirement artifacts from Percepta or another contract system shall be pinned to an immutable commit that has passed that contract system's validation before being promoted into the competition preset.

## Indy Init acceptance

- **CON-100** The competition demonstration shall begin with a newly created empty Git repository.
- **CON-101** The demonstration shall not require manual copying or hand editing of generated setup files.
- **CON-102** A Conditor entry point shall install the selected engineering environment, validate it, ingest the predefined requirements, create the initial mission, and start the selected agent.
- **CON-103** The resulting repository shall retain enough lock, requirement, mission, and verification evidence to reconstruct what Conditor established and why.
