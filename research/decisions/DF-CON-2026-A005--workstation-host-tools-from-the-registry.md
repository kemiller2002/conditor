---
id: DF-CON-2026-A005
title: Workstation host-tool versions come from a digest-verified Registry set; profiles without a set are refused
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
  - DF-CON-2026-A003
tags: [decision, workstation, registry, downgrade]
confidence: high
provenance:
  contributions:
    EXE-20261013T174953100Z-477629ba:
      operations: [created]
      at: 2026-10-13T17:50:14.071Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Decide that workstation host-tool versions come from a digest-verified Registry set and that a profile without one is refused"
---

# Context

The embedded workstation profile `echelon-engineering@1` pins Praxis 3.6.0
and Ordo 1.4.0, together with their asset digests. That was the Indy Init
freeze baseline. Conditor 0.10.1's `workstation plan --profile
echelon-engineering` therefore planned those versions. The shared
workstation was running Praxis 3.11.0 and Ordo 1.5.0, and every application
was on echelon-current 1.22.0 (Ordo 1.7.0, Praxis 3.11.0). Applying the plan
would have downgraded the host tools. The Registry is the version authority
for repositories (decision 0001), but the workstation path ignored it.

# Options for a profile without `--resolved-set`

| Option | Behaviour | Assessment |
|---|---|---|
| A | Keep the embedded pins | Rejected: this is the defect. The pins go stale with every release |
| B | Use the newest version each embedded descriptor qualifies | Rejected: descriptors carry no release asset digests. Conditor would need a second, hand-maintained digest table, which is the parallel catalog that decision 0001 calls debt. "Newest qualified" is also not "the version the portfolio runs" |
| C | **Refuse, naming `--resolved-set`** | Accepted |

# Decision

1. Under C, a profile that names host tools is refused unless
   `--resolved-set PATH --resolved-set-sha256 SHA` is supplied. A profile
   with no host tools (`minimal`) still plans.
2. With a set, the profile keeps only what a profile owns:
   - the host-tool list;
   - prerequisites;
   - optional capabilities.

   Each tool's version, repository, tag, executable and asset digests come
   from the set entry with the same id. A profile tool that the set does not
   select is refused. The plan's source identity is the set's identity, so an
   authorization binds the exact Registry decision.
3. A plan that would lower an installed host tool's version is refused,
   naming the tool and both versions, unless `--allow-downgrade` is passed.
   The plan then discloses each downgrade.
   - **What "installed" means:** the highest version among the
     Conditor-managed install directories, the `~/.local/bin` shim and
     `~/.echelon/bin`, all under the workstation home.
   - **Real home only:** for the real home, the executable that `PATH`
     resolves is included too.
   - **Isolated homes:** they are not influenced by the host's `PATH`.
4. `--resolved-set` alone keeps its meaning, which is the set's whole native
   environment. CI and the Registry lifecycle proof use it, and it is
   already Registry-bound.

# Consequences

- The embedded profiles' `version`, `source` and `assets` fields are no
  longer used to select an installation. They remain readable by older
  tooling, and the profile is now a tool list.
- Operators must name the channel set, for example
  `--profile echelon-engineering --resolved-set channels/echelon-current/<platform>.json
  --resolved-set-sha256 <sha from channel.json>`.
- The CI and release-workflow smokes that ran
  `workstation plan --profile echelon-engineering` without a set now assert
  the refusal.
