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

## Agent identity and provenance

Praxis owns the agent identity and provenance contract (Praxis `DF-ROS-2026-A036`, `DF-ROS-2026-A037`; `RQ-ROS-2026-A006`, `RQ-ROS-2026-A012`, `RQ-ROS-2026-A014`). Conditor consumes it; these requirements state only Conditor's obligations and do not restate the contract.

- **CON-065** Every Praxis transition Conditor performs itself shall declare Conditor's identity explicitly as `--actor-kind automation --actor conditor`; Conditor shall never present itself as an agent or a human. Implementation: `Mission.conditorIdentityArguments`, `Mission.captureArguments`. Tests: "mission capture declares automation actor kind", "recorded capture carries automation identity".
- **CON-066** Conditor shall not start a Praxis execution on an agent's behalf. It captures the mission (as automation) and marks it ready; the launched or handed-off agent activates the mission with its own `work start` in its own execution. `start`, `handoff`, and `resume` perform no Praxis state transition. Implementation: `Execution.start`, `Execution.handoff`, `Mission.ensure`. Tests: "mission establishment never starts or begins an execution"; CI empty-repository smoke (handoff leaves the mission `ready`; the fake agent activates it).
- **CON-067** The launch and handoff instruction shall tell the agent to begin its own execution (or, for an already active mission, not to record into another run's execution), to check `./ros provenance identity`, and to attribute the canonical records it creates or changes with `./ros provenance record`. Conditor shall inject no identity into the agent process: it sets no `ROS_ACTOR`, `ROS_ACTOR_KIND`, or `ROS_TELEMETRY_*` value, because Praxis detects supported agent runtimes itself and Conditor does not know the agent's model. Implementation: `Launcher.instruction`. Tests: "ready instruction tells agent to begin its own execution", "instruction asks agent to check provenance identity", "instruction asks agent to record provenance", "active instruction forbids recording into another run's execution".
- **CON-068** A component descriptor may declare named capabilities with the first version that provides each (`capabilities.<name>.since`) and whether that version is `released` or `unreleased`; an `unreleased` capability shall not be satisfied by any qualified version. The Praxis descriptor shall declare the `provenance` capability. Planning shall report a warning, not a failure, when the selected Praxis version predates it, so a greenfield installation is never silently un-governed. Implementation: `ComponentDescriptors.parse`, `SemanticVersion`, `PraxisProvenance.gate`, `PraxisProvenance.planDiagnostics`, `schemas/conditor-component.schema.json`. Tests: "descriptor parses provenance capability", "descriptor refuses an unreleased capability satisfied by a qualified version", "Praxis 3.1.4 plan warns once about provenance", "capable version produces no diagnostic".
- **CON-069** When the selected Praxis version is provenance-capable, `init`, `verify`, and `upgrade` shall verify that the target's `ros.json` has a `provenance` policy with `enforce: true` and that `AGENTS.md` contains the Praxis "Agent Identity and Provenance" section, and shall stop when either is missing. Conditor shall not write either file itself (CON-020). Implementation: `Planner.create` (`ProvenanceVerify` action), `PraxisProvenance.verifyTarget`, `Installer.execute`. Tests: "installed provenance policy and guidance verify", "unenforced provenance policy fails verification", "installer stops on failed provenance verification"; CI guarded provenance policy step.
- **CON-070** Conditor shall materialize pinned requirement artifacts verbatim and shall not add, rewrite, or remove provenance contributions in them: transport is not a contribution, and source identity remains recorded in lock state (CON-041). Implementation: `Installer.materializeSourceFile` (byte-for-byte copy with conflict refusal).

## Indy Init acceptance

- **CON-100** The competition demonstration shall begin with a newly created empty Git repository.
- **CON-101** The demonstration shall not require manual copying or hand editing of generated setup files.
- **CON-102** A Conditor entry point shall install the selected engineering environment, validate it, ingest the predefined requirements, create the initial mission, and start the selected agent.
- **CON-103** The resulting repository shall retain enough lock, requirement, mission, and verification evidence to reconstruct what Conditor established and why.

## Shared Echelon application foundations

- **CON-120** Conditor's .NET/F# execution path MUST use Aegis for unexpected operational failures at download, package resolution, filesystem, process launch, Git/repository mutation, integrity verification, network, registry/provider invocation, and other external boundaries.
- **CON-121** Expected Conditor outcomes such as unsupported manifest version, unresolved required component, compatibility refusal, missing prerequisite, failed verification result, or execution-readiness refusal MUST remain typed Conditor/Ordo outcomes and MUST NOT be converted into Aegis faults.
- **CON-122** Aegis recovery MUST respect idempotency and unknown-effect risk. Conditor MUST NOT retry a repository mutation or external action whose completion state is unknown unless reconciliation proves retry is safe.
- **CON-123** Aegis context and diagnostics MUST redact credentials, tokens, private package information, and other sensitive values.
- **CON-124** If Conditor gains an interactive browser UI, that UI MUST consume a pinned Forma release and use existing Forma components/patterns before local equivalents.
- **CON-125** If Conditor produces printable/PDF/paginated installation plans, audit reports, bootstrap evidence packets, or similar documents, those surfaces MUST consume a pinned Folio release and use existing Folio primitives.
- **CON-126** Forma and Folio are conditional until their corresponding UI/document surfaces exist; Aegis is applicable now because Conditor already owns operational boundaries.
- **CON-127** Shared dependencies MUST be pinned to released versions or immutable artifacts. Missing shared behavior MUST be raised as a gap in the owning shared repository rather than silently reimplemented.
