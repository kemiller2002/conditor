# Tutela Installation and Upgrade

Conditor MUST support idempotent Tutela installation/update in target repositories.

Install/update SHOULD manage:
- .echelon/tutela.json;
- Tutela project security profile;
- versioned schemas or a pinned distribution reference;
- security assessment/gate workflow;
- agent security instructions;
- integration configuration for Aegis/Praxis/Forma/Folio when applicable.

Requirements:
- Never overwrite user-owned security policy silently.
- Detect and report drift.
- Upgrades MUST preserve project-specific assets, boundaries, invariants, exceptions and evidence.
- Gate weakening requires explicit human authorization.
- Installation must be inspectable/dry-runnable before mutation.
- Failure must leave the target repository in a known recoverable state.
- Doctor/status output SHOULD report Tutela version, gate presence, integration state and drift.
