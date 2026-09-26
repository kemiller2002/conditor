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

## Shared Echelon application foundations

- **CON-120** Conditor's .NET/F# execution path MUST use Aegis for unexpected operational failures at download, package resolution, filesystem, process launch, Git/repository mutation, integrity verification, network, registry/provider invocation, and other external boundaries.
- **CON-121** Expected Conditor outcomes such as unsupported manifest version, unresolved required component, compatibility refusal, missing prerequisite, failed verification result, or execution-readiness refusal MUST remain typed Conditor/Ordo outcomes and MUST NOT be converted into Aegis faults.
- **CON-122** Aegis recovery MUST respect idempotency and unknown-effect risk. Conditor MUST NOT retry a repository mutation or external action whose completion state is unknown unless reconciliation proves retry is safe.
- **CON-123** Aegis context and diagnostics MUST redact credentials, tokens, private package information, and other sensitive values.
- **CON-124** If Conditor gains an interactive browser UI, that UI MUST consume a pinned Forma release and use existing Forma components/patterns before local equivalents.
- **CON-125** If Conditor produces printable/PDF/paginated installation plans, audit reports, bootstrap evidence packets, or similar documents, those surfaces MUST consume a pinned Folio release and use existing Folio primitives.
- **CON-126** Forma and Folio are conditional until their corresponding UI/document surfaces exist; Aegis is applicable now because Conditor already owns operational boundaries.
- **CON-127** Shared dependencies MUST be pinned to released versions or immutable artifacts. Missing shared behavior MUST be raised as a gap in the owning shared repository rather than silently reimplemented.

## Agent identity and provenance

Praxis is authoritative for the identity/provenance model (Praxis `docs/agent-provenance.md`, decisions DF-ROS-2026-A036 and DF-ROS-2026-A037). These requirements only state what Conditor must do when it installs Praxis and starts processes that talk to it; they do not restate the Praxis contract.

- **CON-128** After installing or verifying Praxis, Conditor MUST check whether the target repository carries the Praxis provenance contract: a `ros.json` `provenance` policy with `enforce: true` and a `requiredFrom` date (Praxis RQ-ROS-2026-A007), the AGENTS.md "Agent Identity and Provenance" guidance (RQ-ROS-2026-A012), and `docs/agent-provenance.md`. The check MUST report one of `enabled`, `not-supported-by-installed-version`, or `missing-when-expected`. `missing-when-expected` MUST fail verification before lock state is written (CON-010). Conditor MUST NOT write or repair Praxis-owned provenance files itself (CON-020).
- **CON-129** Which Praxis versions provide provenance MUST be data-driven (`components/praxis.capabilities.json`, `capabilities.provenance.minimumVersion`), not hard-coded. While no qualified Praxis release provides provenance, the threshold is `null`, and a repository on such a version MUST receive a visible `not-supported-by-installed-version` diagnostic (doctor warning `COND-DOC-PROVENANCE`, verify/init output line), never a silent pass. Conditor MUST NOT pin an unpublished Praxis version to obtain provenance. When Praxis publishes a release containing provenance, the threshold is set to that version and the version is qualified; greenfield installs on it then report `enabled` without code changes.
- **CON-130** Conditor's own Praxis calls (mission capture, readiness transition, start) MUST identify Conditor as automation: `ROS_ACTOR_KIND=automation`, `ROS_ACTOR=conditor`, `ROS_TELEMETRY_RUNTIME=conditor`, while keeping `--actor conditor` for Praxis releases that predate the identity variables (RQ-ROS-2026-A006, RQ-ROS-2026-A016). Inherited identity variables of an outer process (`ROS_EXECUTION_ID`, session, run, model, provider) MUST NOT leak into Conditor's identity.
- **CON-131** A launched agent MUST receive only true, known identity: `ROS_ACTOR_KIND=agent` and the provider/runtime of the CLI Conditor launches (`codex` → `openai`/`codex`, `claude` → `anthropic`/`claude-code`), and `ROS_TELEMETRY_MODEL` only when `execution.model` is configured, in which case the same model is passed to the CLI. The agent MUST NOT receive Conditor's identity, an inherited `ROS_ACTOR`, or an inherited `ROS_EXECUTION_ID` (it begins its own Praxis execution). Conditor MUST NOT fabricate provider, model, or runtime values (RQ-ROS-2026-A012, RQ-ROS-2026-A016).
- **CON-132** Identity declarations Conditor adds MUST NOT contain credentials (RQ-ROS-2026-A017, CON-051). Launcher credentials continue to be inherited from the operator's environment unchanged; Conditor neither reads nor adds them.

### Traceability

| Requirement | Implementation | Verification |
|---|---|---|
| CON-128 | `src/Conditor.Core/PraxisProvenance.fs` (`assess`, `observe`, `inspect`); gate in `src/Conditor.Core/Installer.fs` (`execute`); `Doctor.provenanceFinding`; CLI output in `src/Conditor.Cli/Program.fs` (`run`) | `tests/Conditor.Tests/Program.fs` "Praxis provenance readiness" (fixture `tests/Conditor.Tests/fixtures/praxis-provenance/` copied from Praxis `a42c44e` with SHA-256 check) |
| CON-129 | `components/praxis.capabilities.json`, `schemas/conditor-capabilities.schema.json`, `PraxisProvenance.parseCapabilities` / `supports` | capability-table and version-threshold tests; not-supported diagnostic test for 3.1.4 |
| CON-130 | `src/Conditor.Core/AgentIdentity.fs` (`conditorRosEnvironment`); `Mission.runRos` via `ProcessRunner.runProcessWithEnvironment` | "Identity propagation" tests: automation, conditor actor, no inherited execution/model |
| CON-131 | `AgentIdentity.agentEnvironment`, `Launcher.commandLine`, `Launcher.launch`; `execution.model` in `Manifest.fs` and `schemas/conditor.schema.json` | per-launcher agent environment tests, model-only-when-configured tests, command-line tests, manifest model parse/refusal tests |
| CON-132 | `AgentIdentity` (fixed identity facts only) | "environment adds no secrets" and "keeps inherited launcher credentials untouched" tests |
