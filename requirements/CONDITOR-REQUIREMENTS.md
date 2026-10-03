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

## Workstation bootstrap and machine boundaries

Tracked by GitHub issue #3.

- **CON-130** Conditor SHALL support a governed bootstrap path from a supported workstation plus an empty or uninitialized Git repository to an execution-ready project.
- **CON-131** Before host mutation begins, the plan SHALL declare every user-level or machine-level path, persistent startup mechanism, shell/profile entry, tool installation, and other durable host resource that the selected bootstrap profile may create or modify.
- **CON-132** Host prerequisites and provider launchers SHALL be installed only when explicitly declared by the selected bootstrap profile or requested operation.
- **CON-133** Reproducible host bootstrap SHALL use pinned or release-bound artifacts and SHALL verify downloaded artifact integrity where the distribution ecosystem provides a stable verification mechanism.
- **CON-134** Provider/runtime installation SHALL be represented as a distinct trust boundary from repository initialization, including the identity and source of externally executed installers or binaries.
- **CON-135** Repeated workstation bootstrap SHALL be idempotent with respect to Conditor-owned host state and SHALL NOT accumulate duplicate shell/profile configuration or duplicate startup registrations.

## Step receipts

- **CON-140** Every executable Conditor plan step SHALL declare an expected receipt or postcondition before execution.
- **CON-141** Conditor SHALL capture the observed receipt after each executed step independently from the expected receipt.
- **CON-142** Receipt comparison SHALL be explicit and machine-readable.
- **CON-143** A required receipt mismatch SHALL stop subsequent dependent steps and SHALL preserve the observed result and diagnostic context.
- **CON-144** Successful receipts SHALL be retained with sufficient operation, component, version/source, platform, and target identity to reconstruct what Conditor established and why.
- **CON-145** Receipt records SHALL distinguish verified postconditions from agent or child-process narrative claims.
- **CON-146** Readiness SHALL depend on required receipt satisfaction in addition to existing component verification and compatibility gates.

## Deterministic uninstall and rollback

- **CON-150** Conditor lock/install state SHALL retain enough ownership and provenance information to calculate a deterministic uninstall plan for Conditor-managed state.
- **CON-151** Uninstall planning SHALL be read-only and SHALL identify the exact resources Conditor proposes to remove, restore, detach, or leave unchanged.
- **CON-152** Uninstall SHALL respect tool-owned, shared, and user-owned boundaries and SHALL never delete user-owned work merely because it lies beneath a Conditor-managed repository or host path.
- **CON-153** Conditor SHALL distinguish resources it created from compatible resources that existed before bootstrap and were adopted or reused.
- **CON-154** Conditor SHALL NOT remove an adopted pre-existing tool or host resource unless explicit ownership evidence says Conditor created it or the user separately authorizes removal.
- **CON-155** Workstation bootstrap SHALL provide an explicit uninstall or undo path for Conditor-owned durable host state, including startup registrations and shell/profile edits that Conditor created.
- **CON-156** Uninstall SHALL preserve receipts or a final removal receipt sufficient to explain what was removed and what intentionally remained.
- **CON-157** Rollback after a partially failed bootstrap SHALL use recorded effects and receipts rather than assuming an attempted action completed.


## Bootstrap authorization and external execution

- **CON-136** Before executing an external installer, downloaded script,
  provider bootstrap, or persistent host-registration command, Conditor SHALL
  expose the exact planned command or artifact identity together with the
  expected durable host effects that operation is allowed to create.
- **CON-137** A host-bootstrap profile SHALL distinguish preauthorized
  operations from operations that require explicit user authorization and
  SHALL NOT turn repeated approval prompts into implicit blanket permission.
- **CON-138** When an external installer cannot be cryptographically pinned or
  independently integrity-verified, Conditor SHALL report that limitation as
  an unresolved trust property rather than representing the bootstrap as fully
  reproducible.
- **CON-139** Host bootstrap SHALL record whether each prerequisite was created
  by Conditor, already present and reused, or externally installed outside
  Conditor so later repair and uninstall do not infer ownership.


## Complete workstation profile

Tracked by GitHub issue #5.

- **CON-160** Conditor SHALL support a versioned workstation profile that can
  establish the declared Echelon engineering environment before repository
  initialization begins.
- **CON-161** A standard Echelon engineering workstation profile SHALL be able
  to declare Conditor, Praxis, Ordo, Git prerequisite checks, GitHub
  authentication/readiness checks, and explicitly selected agent/provider
  tooling without requiring unrelated Echelon products.
- **CON-162** Workstation profiles SHALL be declarative, versioned, and
  deterministic with respect to the profile, platform, resolved immutable
  component identities, and observed pre-existing host state.
- **CON-163** Conditor SHALL NOT install an agent provider, shell integration,
  local daemon/control plane, or other host tool merely because it is commonly
  used; it must be declared by the selected profile or explicit user request.
- **CON-164** A profile MAY request the Praxis local control plane and its
  supporting UI/runtime capabilities, but Conditor SHALL treat those as
  installable components and SHALL NOT own their workflow semantics.
- **CON-165** Workstation bootstrap SHALL distinguish host readiness from
  repository execution readiness so a healthy workstation cannot be mistaken
  for an initialized project.
- **CON-166** A workstation profile SHALL be composable enough to support a
  minimal profile, a standard Echelon profile, and project-specific additions
  without forking Conditor logic.

## Host prerequisite discovery

- **CON-170** Conditor SHALL discover prerequisite state before planning host
  mutation and SHALL classify each prerequisite as already satisfied,
  installable by Conditor, installable only through an external provider,
  unsupported, or unknown.
- **CON-171** Git readiness checks SHALL include executable availability and the
  minimum version/capabilities required by the selected profile.
- **CON-172** GitHub readiness checks SHALL distinguish Git transport
  authentication, GitHub API/CLI authentication where required, and repository
  authorization; one SHALL NOT be inferred from another.
- **CON-173** Authentication checks SHALL verify capability without persisting
  secrets into Conditor manifests, locks, receipts, logs, or generated files.
- **CON-174** A missing optional provider SHALL NOT block workstation
  bootstrap unless the selected profile or requested launcher makes it
  required.
- **CON-175** A missing required prerequisite SHALL produce a typed refusal or
  actionable plan item rather than causing Conditor to guess an installation
  path.

## Plan-before-authorization contract

- **CON-180** Before any host-level mutation, Conditor SHALL expose a complete
  planned effect set covering files/directories, shell/profile edits, PATH
  changes, startup registrations, downloads, external installers, binaries,
  provider tooling, and other persistent resources.
- **CON-181** For every executable plan step, the plan SHALL identify the
  operation, source/artifact identity, expected effect, expected receipt,
  ownership classification to be recorded on success, and whether explicit
  authorization is required.
- **CON-182** External installers or scripts SHALL be shown by exact artifact
  identity or exact command invocation when known before authorization is
  requested.
- **CON-183** Conditor SHALL clearly identify which external operations are
  independently integrity-verified, publisher/release-bound, transport-only
  trusted, or otherwise not fully reproducible.
- **CON-184** Unknown trust properties SHALL remain explicit unknowns and SHALL
  NOT be converted into a generic success/green status because the user
  authorized execution.
- **CON-185** Authorization SHALL apply only to the disclosed operation/effect
  set. A materially different external command, artifact identity, or durable
  effect SHALL require a new plan/authorization decision.
- **CON-186** Repeated interactive approval SHALL NOT be interpreted as blanket
  authority for undisclosed future host mutations.

## Bootstrap step ledger and resume

- **CON-190** Workstation and repository bootstrap SHALL persist a durable
  step ledger sufficient to resume after interruption without relying on the
  original terminal or agent conversation.
- **CON-191** Each step record SHALL retain the requested action, relevant
  component/source identity, expected receipt, observed receipt, comparison
  result, ownership effect, and time.
- **CON-192** Receipt comparison SHALL preserve at least match, mismatch, and
  indeterminate/unknown-effect outcomes.
- **CON-193** A matching receipt MAY permit a completed step to be skipped on
  resume when its inputs and dependencies remain valid.
- **CON-194** A mismatched required receipt SHALL block dependent steps until
  repaired or replanned.
- **CON-195** An indeterminate step whose retry could duplicate a host,
  repository, network, or external-system effect SHALL require reconciliation
  before retry.
- **CON-196** Reconciliation SHALL determine whether the attempted effect
  occurred, did not occur, or remains unknown and SHALL append the result
  rather than rewriting the original observation.
- **CON-197** Bootstrap resume SHALL derive the next operation from durable
  plan/receipt state and current host observation rather than an agent's prose
  statement about prior progress.
- **CON-198** A resumed bootstrap SHALL preserve historical receipts and
  authorization records so the final installation can be audited across
  multiple sessions/providers.

## Resource ownership ledger

- **CON-200** Conditor SHALL maintain a durable resource ownership ledger for
  host and repository resources that participate in bootstrap, repair,
  upgrade, or uninstall.
- **CON-201** At minimum, each relevant resource SHALL be classifiable as
  created by Conditor, adopted/reused pre-existing state, created by another
  Echelon component, externally installed outside Conditor, shared, or
  user-owned.
- **CON-202** Ownership classification SHALL be based on observed evidence and
  installation history rather than inferred solely from the resource's current
  path or name.
- **CON-203** Conditor SHALL record the prior state needed to reverse its own
  shell/profile or startup-registration edits without deleting unrelated user
  content from the same file or mechanism.
- **CON-204** Conditor SHALL NOT claim ownership of an existing Git, GitHub CLI,
  provider CLI, runtime, or shell configuration merely because bootstrap
  validated and reused it.
- **CON-205** Repair SHALL preserve the distinction between adopted state and
  Conditor-created state so a repair does not silently convert a pre-existing
  resource into Conditor-owned state.
- **CON-206** Upgrade SHALL preserve or explicitly migrate ownership metadata
  before a new version may rely on it for destructive actions.

## Deterministic uninstall plan

- **CON-210** `conditor uninstall --plan` or an equivalent read-only operation
  SHALL calculate the exact proposed removal/restoration actions from durable
  lock, ownership, and receipt state.
- **CON-211** The uninstall plan SHALL categorize resources as remove, restore,
  detach/unregister, retain because shared, retain because adopted,
  retain because user-owned, or unresolved.
- **CON-212** Uninstall SHALL refuse destructive action for an unresolved
  ownership state until evidence or explicit user authorization resolves it.
- **CON-213** Removal of a Conditor-added line or block from a shared shell/
  profile/configuration file SHALL be surgical and SHALL preserve unrelated
  user content.
- **CON-214** Uninstall SHALL be idempotent: rerunning it after successful
  removal SHALL not create new mutations or fail merely because owned
  resources are already absent.
- **CON-215** Uninstall SHALL produce a final receipt set describing what was
  removed, restored, detached, retained, or left unresolved.
- **CON-216** Uninstall receipts SHALL retain enough source/version/ownership
  identity to explain why each resource was acted upon or retained.
- **CON-217** Project source and user-owned repository work SHALL remain outside
  automatic workstation uninstall even when the repository was originally
  created by a Conditor workflow, unless a separate explicit project-deletion
  operation is designed and authorized.

## Partial-failure rollback

- **CON-220** A failed bootstrap SHALL use the recorded step/effect ledger to
  determine which completed effects are eligible for rollback.
- **CON-221** Conditor SHALL NOT roll back an indeterminate external effect as
  though it were known to have completed; it SHALL reconcile first or leave an
  explicit unresolved obligation.
- **CON-222** Automatic rollback SHALL be limited to effects with sufficient
  ownership and reversal evidence to make rollback safe.
- **CON-223** When rollback cannot safely restore the prior state, Conditor
  SHALL preserve the partial state, receipts, and precise remediation needed
  rather than hiding the failure behind cleanup.
- **CON-224** A successful rollback SHALL itself produce receipts proving the
  resulting state.
- **CON-225** Failure of a rollback step SHALL be represented separately from
  the original bootstrap failure.

## Provider and external-tool trust

- **CON-230** Conditor SHALL model provider/runtime installation as an explicit
  trust boundary independent from Echelon component installation.
- **CON-231** For each external provider/tool bootstrap, Conditor SHALL record
  the publisher/source, requested version or release identity when available,
  integrity mechanism, authorization status, and ownership classification.
- **CON-232** A live `curl | sh`-style installer whose exact executed bytes
  cannot be pinned or independently verified SHALL NOT be represented as
  equivalent to an immutable checksum-verified release artifact.
- **CON-233** When an external ecosystem offers signed or checksummed release
  artifacts, a reproducible profile SHOULD prefer those over moving installer
  endpoints.
- **CON-234** External provider credentials SHALL remain outside Conditor's
  durable state unless a future credential component explicitly owns secure
  storage; Conditor MAY record only non-secret capability/readiness facts.
- **CON-235** An externally managed provider/tool MAY be adopted as a
  prerequisite without Conditor assuming uninstall authority over it.

## Praxis and Ordo handoff

- **CON-240** Conditor SHALL use Ordo/Praxis contracts to establish the initial
  governed execution context when a selected project profile requests agent
  execution.
- **CON-241** Conditor SHALL NOT redefine execution roles, legal work
  transitions, evaluator independence, receipt semantics, or ongoing work
  state that belong to Ordo/Praxis.
- **CON-242** After successful handoff, ongoing work execution SHALL remain
  governed by Praxis/Ordo even if Conditor originally launched the provider.
- **CON-243** Conditor SHALL be able to include the initialized Praxis execution
  identity and initial readiness/activation receipt in its bootstrap evidence
  without duplicating Praxis's execution ledger.
- **CON-244** A Conditor resume operation that resumes installation/bootstrap
  state MUST remain distinguishable from a Praxis resume operation that
  resumes governed project work.
- **CON-245** Installing or starting the Praxis local control plane SHALL be an
  explicit workstation/profile step with its own receipt and ownership state;
  the control plane's runtime data remains Praxis-owned.


## Standard Echelon workstation composition

- **CON-250** The standard Echelon engineering workstation profile MUST be able
  to include the Praxis local control plane, Forma, Limen, and declared
  security/containment support when the selected profile requires those
  capabilities.
- **CON-251** Forma and Limen SHALL be installed only as required dependencies
  of declared interactive/browser capabilities; their presence SHALL NOT be
  assumed for CLI-only profiles.
- **CON-252** A security/containment profile SHALL describe the host-enforcement
  capabilities requested for agent execution separately from Ordo/Praxis
  semantic capabilities.
- **CON-253** Conditor SHALL verify that requested control-plane, Forma, Limen,
  and containment components are compatible with the selected Praxis/Ordo
  versions before host mutation begins.
- **CON-254** Workstation setup SHALL keep each installed Echelon component
  independently identifiable and lifecycle-managed so removal or upgrade of one
  component does not require deleting the entire Echelon environment.


## Existing repository adoption

- **CON-260** Conditor SHALL provide a read-only adoption plan for repositories that already contain Echelon lifecycle components and do not yet contain Conditor governance.
- **CON-261** Adoption discovery SHALL use component-declared read-only version probes and component-owned verification contracts; it SHALL NOT run initialization, upgrade, package installation, or other mutation during discovery.
- **CON-262** A lifecycle component SHALL be adoptable only when its installed command is observable, its version resolves unambiguously to exactly one version qualified by the current Conditor build, and its verification contract succeeds.
- **CON-263** Unknown, unsupported, ambiguous, unhealthy, or source-unmappable lifecycle state SHALL remain an explicit refusal and SHALL NOT be guessed into the generated declaration.
- **CON-264** The adoption plan SHALL disclose all observations, the exact proposed `conditor.json`, descriptor-bound component identities, refusals, and an authorization digest covering the proposal.
- **CON-265** Adoption mutation SHALL require an authorization digest that still matches a fresh observation of the repository; stale authorization SHALL fail before writing durable state.
- **CON-266** Successful adoption SHALL write Conditor governance and lock evidence without invoking component initialization or claiming ownership of component-owned repository state.
- **CON-267** Conditor SHALL refuse adoption when existing `conditor.json` or `.conditor/lock.json` governance is present; established repositories SHALL use status, repair, or upgrade instead.
- **CON-268** Application-package bindings whose target cannot be proven uniquely SHALL be reported but SHALL NOT be auto-adopted or assigned a guessed scaffold/package target.
