---
id: DF-CON-2026-A006
title: Workstation registration defaults to the ledger-owned, verified ~/.local/bin/praxis; explicit configuration wins; backfill is planned and authorized
status: accepted
version: 1.0.0
owners:
  - conditor
created: 2026-10-13
updated: 2026-10-13
research_area: workstation-bootstrap
decision_type: architecture
related_documents:
  - requirements/CONDITOR-REQUIREMENTS.md
  - docs/workstation.md
  - DF-CON-2026-A005
tags: [decision, workstation, registration, praxis, ledger]
confidence: high
provenance:
  contributions:
    EXE-20261013T220325306Z-15bc4dea:
      operations: [created]
      at: 2026-10-13T22:08:43.928Z
      actor:
        kind: agent
        id: claude-code
        provider: anthropic
        model: claude-opus-5-5
        runtime: unknown
      reason: "Decide the workstation registrar default (ledger-owned, verified ~/.local/bin/praxis; explicit wins) and planned backfill for conditor#95"
---

# Context

conditor#95: on the shared workstation, `conditor workstation apply` printed
`registration ordo: unavailable` and nothing else. The ledger showed the same
outcome for strata 0.1.1, praxis 3.11.0 and ordo 1.7.0, with the detail
"no Praxis with installation registration configured (--praxis)".
Registration needs a Praxis executable, and none had been given, so it never
ran.

There were two further problems:

- A configured Praxis that could not produce a result was also reported as
  `unavailable`, with an empty detail. The reproduction was
  `--praxis /bin/false`.
- The installs that had already happened had no way to be registered after
  the fact.

The Registry's integration standard
(echelon-registry `spec/echelon-integration-standard.md`) already separates
the two cases. A missing optional provider is `unavailable`; a declared
provider that cannot be used is `misconfigured`, with diagnostics. Neither
may stop core behaviour.

# Options for the default registrar

| Option | Behaviour | Assessment |
|---|---|---|
| A | Keep requiring `--praxis` / `CONDITOR_PRAXIS` | Rejected. It is correct but makes registration silently absent on every workstation where the operator forgets it, which is the defect |
| B | Use whatever `praxis` is first on `PATH` | Rejected. That is an unverified executable Conditor does not own: it may be a different version, a wrapper, or something the user installed. Registration evidence would then rest on an unknown program |
| C | **Use `~/.local/bin/praxis` only when the ledger proves Conditor installed it and it still verifies** | Accepted |

# Decision

1. **Explicit configuration always wins.** `--praxis PATH` or
   `CONDITOR_PRAXIS` is used as given.
2. **Otherwise, the ledger-owned shim** in the workstation home's
   `~/.local/bin/praxis` is the registrar, but only when every one of these
   holds at the moment of use:
   - The ledger's ownership record for it is `conditor-created`, for
     component `praxis`.
   - The `praxis-shim` step's last receipt is `match`.
   - The file is still exactly the shim Conditor writes: the
     `Managed by Conditor` marker, and a single `exec` of
     `<install root>/praxis/<version>/praxis`.
   - That install directory is itself `conditor-created` in the ledger, and
     the target exists.
   - `praxis --version` reports `<version>` now.

   Failing any condition gives `unavailable`, and the reason names the
   condition. Conditor never falls back to `PATH`.
3. **Outcomes keep their reason** and use the Registry's vocabulary:
   - Praxis's own outcomes pass through, with its code and message.
   - No registrar is `unavailable`.
   - A registrar that cannot run, or that returns no installation outcome, is
     `misconfigured`, with the executable, exit code and output.
4. **Backfill is a planned, authorized effect.** `conditor workstation
   register` plans by default and applies only with `--authorize DIGEST`.
   - **What it proposes:** only components the ledger shows Conditor
     installed, whose shim reports the selected version now, and whose
     registration of that version never succeeded.
   - **The digest** binds the registrar, the target, the selection's source
     identity and each registration.
   - **No registrar:** with work to do and no registrar, the plan is refused
     as `unavailable`.
   - **Evidence and ledger:** each registration cites the original install
     receipt, and is recorded with `trigger: backfill`.

# Consequences

- A workstation whose Praxis was installed by Conditor now registers its
  installs without extra flags. Within an apply, Praxis registers itself
  through its own new shim, and later components follow.
- A component installed earlier in the same apply than Praxis still records
  `unavailable`, with the reason. The backfill command repairs that.
- The registrar's identity is part of a backfill authorization. Changing
  `--praxis`, or upgrading the ledger-owned Praxis, needs a new plan.
- A hand-installed or replaced `~/.local/bin/praxis` is never trusted
  implicitly. The operator names it with `--praxis` if it is wanted.
- The shared workstation's existing registrations (strata, praxis, ordo) are
  repaired by `conditor workstation register`, reviewed as a plan first.
