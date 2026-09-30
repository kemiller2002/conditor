# Indy Init Distribution Requirements - Pass 3: Competition Proof and Ecosystem Rollout

Status: draft
Owner: Conditor
Purpose: turn the general Echelon distribution architecture into a falsifiable Indy Init demonstration and a reusable onboarding contract for every Echelon system.

## Competition outcome

The demonstration is successful only if a supported clean host and empty target directory can be transformed into a governed, verified, agent-ready Indy Init environment through the documented Conditor path, with no source checkout, manual file copy, hand-edited generated configuration, or hidden prerequisite installation.

## Clean-host definition

**CON-INDY-001** The Indy Init test plan SHALL define exactly what "clean host" means, including operating system/version, architecture, initial PATH expectations, network assumptions, and which external tools, if any, may already exist.

**CON-INDY-002** The competition path SHALL NOT depend on a developer's existing Echelon checkout, NuGet cache, npm cache, .NET SDK, Node.js installation, F# toolchain, or unpublished local artifact.

**CON-INDY-003** The primary competition host platform SHALL be explicitly named and tested from a fresh environment before the event.

**CON-INDY-004** CI SHALL exercise at least one independent clean platform in addition to the primary competition-host rehearsal where practical.

## One-path bootstrap

**CON-INDY-010** The public competition instructions SHALL expose one canonical bootstrap/install path.

**CON-INDY-011** The canonical path SHALL bootstrap Conditor, install or resolve the exact Indy Init profile, initialize the target repository, verify the environment, establish Ordo/Praxis state, and reach agent-launch readiness without requiring the operator to understand individual component installation commands.

**CON-INDY-012** The canonical path SHALL be non-interactive except for explicit trust/authorization or provider authentication boundaries that cannot safely be automated.

**CON-INDY-013** Any required external authentication SHALL be detected before the first irreversible competition-specific mutation where possible.

**CON-INDY-014** Human-readable output SHALL show major semantic stages rather than low-level package-manager noise.

**CON-INDY-015** The command SHALL have a machine-readable mode suitable for automated rehearsal and evidence capture.

## Immutable Indy Init release set

**CON-INDY-020** The Indy Init competition profile SHALL have its own immutable profile version.

**CON-INDY-021** The competition profile version SHALL resolve to exact immutable versions/artifacts of every required Echelon system and governing contract.

**CON-INDY-022** The resolved competition release set SHALL be captured before the event and SHALL NOT change when new stable Echelon releases are published.

**CON-INDY-023** Updating the competition release set SHALL create a new profile version or release-set identity rather than mutating the prior one.

**CON-INDY-024** The competition profile SHALL identify the exact canonical application/governing-contract sources needed for Indy Init.

**CON-INDY-025** The competition profile SHALL fail if a required system exists only as an unpublished local build or moving branch reference.

## Complete Echelon system onboarding contract

**CON-INDY-030** Every Echelon system intended to be installed by Conditor SHALL satisfy the standard Registry release-publication contract before being added to a stable profile.

**CON-INDY-031** Each system SHALL publish or expose:
- canonical system id and aliases;
- semantic version;
- canonical repository;
- immutable release/tag identity;
- source commit when available;
- supported platforms;
- exact artifact names and digests;
- canonical executable/version probe where applicable;
- provided capabilities;
- required compatibility facts;
- release stage;
- license identifier.

**CON-INDY-032** Self-contained CLI systems SHALL publish supported native artifacts without requiring the consumer to install a language runtime.

**CON-INDY-033** Libraries that are not workstation executables SHALL declare their installation/binding type so Conditor does not attempt to install them globally as commands.

**CON-INDY-034** Application-bound libraries such as UI/runtime packages SHALL require an explicit project/scaffold binding target and SHALL never be attached to an arbitrary project file by guessing.

**CON-INDY-035** A system that cannot yet satisfy the stable publication contract SHALL be represented as unavailable/not-ready for the stable all-Echelon profile rather than handled through permanent bespoke Conditor code.

**CON-INDY-036** The rollout SHALL maintain a release-readiness matrix for all Echelon systems showing canonical id, repo, distribution type, release manifest support, current stable version, supported platforms, Conditor compatibility status, and blocking gaps.

## All-Echelon and task-focused profiles

**CON-INDY-040** Conditor SHALL provide a versioned "all supported Echelon systems" profile once all included systems meet the publication contract.

**CON-INDY-041** The all-Echelon profile SHALL include only systems that are meaningful workstation installations; project-bound libraries SHALL instead be available through project profiles/scaffolds.

**CON-INDY-042** Smaller task-focused profiles SHALL remain available so users do not have to install the complete ecosystem for a narrow task.

**CON-INDY-043** Installing a larger profile over a smaller compatible profile SHALL reuse verified components rather than reinstall them.

**CON-INDY-044** Removing one profile SHALL NOT remove a shared component still required by another recorded profile or environment ownership relationship.

## Evidence receipt

**CON-INDY-050** A completed Indy Init bootstrap SHALL emit a durable environment receipt.

**CON-INDY-051** The receipt SHALL include profile id/version, resolved-release-set digest, Conditor version, host platform class, selected component versions, artifact digests, verification result, and final environment health.

**CON-INDY-052** The receipt SHALL omit credentials, usernames, hostnames, absolute home paths, hardware identifiers, and other unnecessary machine identity.

**CON-INDY-053** The receipt SHALL be sufficient to answer "what exact environment was demonstrated?" without consulting conversation history.

**CON-INDY-054** Conditor SHALL be able to verify a current environment against a prior receipt/release-set identity and explain drift.

## Offline competition path

**CON-INDY-060** Before the event, the exact Indy Init profile SHALL be exportable as an offline bundle for the primary competition platform.

**CON-INDY-061** The offline bundle SHALL contain Conditor or a bootstrap path to the exact Conditor version, Registry/profile snapshot, all required distributable artifacts, checksums/provenance evidence, and immutable governing artifacts needed for initialization.

**CON-INDY-062** The offline installation path SHALL be tested with network access disabled.

**CON-INDY-063** The competition operator SHALL be able to select online or offline source without changing the desired profile identity.

**CON-INDY-064** Online and offline installation of the same release set SHALL produce semantically equivalent verified environments.

## Rehearsal suite

**CON-INDY-070** The repository SHALL contain an automated clean-room rehearsal that begins from an empty target and proves the canonical bootstrap path without competition application implementation leaking from Conditor itself.

**CON-INDY-071** The rehearsal SHALL prove a second run is zero-drift/idempotent.

**CON-INDY-072** The rehearsal SHALL prove provider handoff readiness separately from provider task success.

**CON-INDY-073** A competition rehearsal SHALL prove the exact pinned profile from a fresh host/container/VM rather than only unit-testing resolver functions.

**CON-INDY-074** An offline rehearsal SHALL prove the prepared competition bundle installs with external network access disabled.

**CON-INDY-075** An interrupted-install rehearsal SHALL terminate the process after selected mutating boundaries and prove that the next run reports indeterminate state or safely resumes/reconciles according to receipts.

**CON-INDY-076** A corrupted-artifact rehearsal SHALL prove the artifact is refused before activation.

**CON-INDY-077** A stale-PATH rehearsal SHALL prove Doctor detects when an older executable shadows the active version.

**CON-INDY-078** A rollback rehearsal SHALL prove a failed upgrade does not destroy the last verified usable environment for components that support side-by-side activation.

## Performance and operator experience

**CON-INDY-080** Rehearsals SHALL measure bootstrap elapsed time by semantic phase so competition delays can be diagnosed.

**CON-INDY-081** Performance telemetry SHALL distinguish network/download time, verification time, component lifecycle time, repository initialization time, and provider-launch readiness.

**CON-INDY-082** No competition acceptance criterion SHALL depend solely on a time target; correctness and reproducibility remain gating.

**CON-INDY-083** The operator-facing result SHALL end in one unambiguous healthy/blocked result with the next legal action.

**CON-INDY-084** Failure output SHALL name the failed system, requirement or artifact and provide a bounded remediation path without telling the operator to manually edit Conditor-owned state.

## Release freeze and event operations

**CON-INDY-090** The project SHALL define a competition release-freeze point after which the demonstrated Indy Init release set changes only for a documented blocking defect.

**CON-INDY-091** Any post-freeze change SHALL produce a new resolved-release-set digest and require the full clean-room and offline rehearsal suite before adoption.

**CON-INDY-092** The exact competition bundle, profile, release-set document, and verification receipt schema SHALL be retained as durable evidence after the event.

**CON-INDY-093** Competition-day success SHALL NOT depend on GitHub Actions, package registries, or remote source repositories being available if the offline bundle has been prepared.

## Final Indy Init acceptance test

Given:
1. a supported clean host;
2. an empty target directory;
3. either normal network access or the prepared offline competition bundle; and
4. required provider credentials only at the explicit provider boundary,

when the operator follows the canonical Conditor path, then Conditor SHALL:
1. establish itself;
2. resolve the immutable Indy Init release set;
3. verify every artifact before activation;
4. install/reuse the required environment;
5. initialize the governed repository;
6. materialize pinned governing contracts;
7. establish Ordo/Praxis state;
8. verify project and environment readiness;
9. emit a durable environment receipt; and
10. reach the selected agent-launch boundary

without manual dependency installation, source cloning, file copying, or hand editing.

The same release-set identity SHALL be reproducible later and SHALL produce an equivalent verified environment on the same supported platform class.


## Competition profile ownership and clean-host baseline

**CON-INDY-094** The Indy Init planning repository SHALL publish a machine-readable competition profile manifest that is distinct from Conditor's implementation and distinct from the application source generated at kickoff.

**CON-INDY-095** The competition profile manifest SHALL name the exact required Echelon capabilities by canonical Registry id, required distribution role, profile/release-set identity, governing contract-bundle identity, and any explicitly permitted optional capabilities.

**CON-INDY-096** The competition profile SHALL declare the exact clean-host baseline, including permitted preinstalled operating-system facilities, Git availability/version, shell/PowerShell assumptions, provider CLI/runtime assumptions, authentication boundaries, browser requirements, and whether each prerequisite is Conditor-managed or external.

**CON-INDY-097** A requirement described as "agent-ready" SHALL identify which provider execution boundary is being proven. An absent provider executable or missing provider authentication SHALL be reported as an external readiness block, not as successful agent readiness.

**CON-INDY-098** The competition profile SHALL declare the primary event platform and every additional supported platform. A successful rehearsal on one platform SHALL NOT imply another platform is supported.

**CON-INDY-099** The frozen event release set SHALL include the Conditor version/bootstrap identity used to establish the environment, not only downstream component versions.

**CON-INDY-100** The competition profile and contract bundle SHALL be content-addressed independently so a change to environment/tooling can be distinguished from a change to product requirements/contracts.

**CON-INDY-101** The event-day canonical bootstrap SHALL be executable from the declared clean-host baseline without a manual PATH edit or shell restart between Conditor bootstrap and profile execution.

**CON-INDY-102** The offline competition rehearsal SHALL begin from the same declared clean-host baseline as the online rehearsal, except for network availability. It SHALL NOT rely on warm caches, pre-existing Echelon binaries, or unpublished local packages.

**CON-INDY-103** The final pre-event acceptance record SHALL identify every external prerequisite that Conditor intentionally does not install. The demonstration SHALL NOT describe those prerequisites as part of Conditor's installed result.
