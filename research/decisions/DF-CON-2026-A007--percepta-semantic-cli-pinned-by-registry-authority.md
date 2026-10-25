---
id: DF-CON-2026-A007
title: The Percepta semantic CLI is pinned by the Registry authority and installed by the workstation, not declared as a conditor.json component
status: accepted
version: 1.0.0
owners:
  - conditor
created: 2026-10-25
updated: 2026-10-25
research_area: workstation-bootstrap
decision_type: architecture
related_documents:
  - research/decisions/DF-CON-2026-A005--workstation-host-tools-from-the-registry.md
  - docs/decisions/0001-registry-version-authority.md
  - docs/indy-day-of.md
tags: [decision, workstation, registry, percepta, dokimos, indy-init]
confidence: high
provenance:
  contributions:
    EXE-20261025T013547873Z-404de468:
      operations: [created]
      at: 2026-10-25T04:33:03.000Z
      actor:
        kind: unknown
        id: anthropic/claude-code
        provider: anthropic
        model: claude-opus-5-5
        runtime: claude-code
      reason: "Decide how the kickoff set pins the Percepta semantic CLI under Conditor's component model (Indy-init OD-INDY-2, OD-INDY-8)"
---

# Context

The owner decided on 2026-10-24 that Percepta semantic UI verification is released, not waived ("Release it";
Indy-init `docs/OWNER-DECISIONS-2026-10-24.md` OD-INDY-2). UI completion needs evidence from the released semantic
CLI, pinned by the kickoff release set. OD-INDY-8 makes that set the Registry's echelon-current, approved after
Conditor qualification.

The semantic CLI is released as `percepta-semantic-v<version>`. Its Registry system is `percepta-semantic`, and its
executable is `percepta`, with self-contained per-platform archives (Percepta `DF-PERCEPTA-2026-0003`). Its commands
are `version` (`--version` is accepted too), `doctor`, `install-browser` and `verify`. Its own guide says that it never
changes repository installation state. It is a different system from `percepta` (`percepta-repo`, the repository
lifecycle that the indy-init preset already declares).

Conditor has two ways to carry a component:

1. **A `conditor.json` component.** Conditor drives the component's repository installation: `init`, `verify`,
   `doctor` and `upgrade` in the target repository. That needs an embedded descriptor, or a Registry release that
   declares `echelon.repository-lifecycle`. Every embedded host-tool descriptor (`praxis`, `ordo`, `percepta`) is a
   repository-lifecycle tool.
2. **The Registry authority** (`registryAuthority`, a digest-verified resolved set). The set pins every selected
   release, with its version and artifact digests. `conditor workstation plan|apply --resolved-set` installs every
   self-contained native host tool in the set onto the host (DF-CON-2026-A005 decision 4). It checks each archive
   against the set's digest, and checks each shim's `--version` output against the selected version.

# Options

| Option | Assessment |
|---|---|
| A. Embedded descriptor `percepta-semantic` with `init`/`verify` arguments | Rejected. The CLI has no repository lifecycle. Its `init`, `verify` and `upgrade` would have to be faked, for example as `--version`, which would report repository installation that never happened. `doctor` needs a browser and a contract, so it fails during bootstrap by design |
| B. A new "host-only component" kind in `conditor.json` | Not needed. Pinning and host installation already exist through the Registry authority. A second pin in `conditor.json` would be a parallel version authority (decision 0001) |
| C. **Pin through the Registry authority; install with `conditor workstation`** | Accepted |

# Decision

1. `percepta-semantic` is **not** a `conditor.json` component and has no embedded descriptor. The indy-init preset
   and the kickoff `conditor.json` keep `percepta` (percepta-repo) for repository lifecycle.
2. The semantic CLI's exact version and digests are pinned by the Registry set that the consumer declares as
   `registryAuthority`. For the Indy Init kickoff, that is the first echelon-current version that selects
   `percepta-semantic`. On 2026-10-25, `percepta-semantic-v0.1.0` was still unpublished (kemiller2002/percepta#39),
   so echelon-current 1.27.0 does not include it, and the Registry records a VER-003 exception. The host installs it with
   `conditor workstation apply --resolved-set <set> --resolved-set-sha256 <sha>`. The shim is
   `~/.local/bin/percepta`, and its receipt is `percepta --version` reporting the selected version.
3. Dokimos follows the existing generic path. It is a Registry `repository-lifecycle` release declaring
   `echelon.repository-lifecycle` v1, so Conditor needs no descriptor and no per-version qualification. Dokimos 0.7.0
   (`completion.verify-attested`) is installed by the same workstation apply, and a repository that declares
   `dokimos` gets it through the Registry lifecycle path.
4. REG-RU-007 (echelon-registry `spec/rolling-upgrade-requirements.md`) therefore needs no new qualification for
   echelon-current 1.27.0: no consumer declares `percepta-semantic` or `dokimos` in `conditor.json`. Conditor still
   qualifies the set itself:
   - the sweep (CI job `echelon-current-sweep-qualification`): `upgrade --current` from 1.26.0 to 1.27.0 plans no
     version transition for the declared components;
   - CI job `echelon-current-1-27-0-kickoff-set-qualification`: `workstation apply` installs every native host tool
     of the set from the real release bytes and verifies their receipts, and Dokimos 0.7.0 reports its release
     identity;
   - `WebScaffoldTests.runKickoffSet127`: a Rehearsal-style manifest plans under the 1.27.0 authority, both with and
     without `dokimos` declared.

   When `percepta-semantic` is registered, the next set gets the same qualification. The workstation's existing
   host-tool path needs no code change for it: the archive has a single top-level directory, and the shim's
   `--version` reports `percepta <version>+<commit>`.

# Consequences

- A consumer that runs `conditor workstation` with only `--profile echelon-engineering` gets Praxis and Ordo only.
  To get the semantic CLI and Dokimos, run the workstation with `--resolved-set` alone (the set's whole native
  environment).
- If the semantic CLI later gains a repository lifecycle (`echelon.repository-lifecycle`), it becomes declarable
  through the generic Registry lifecycle path with no Conditor descriptor. This decision is then revisited.
- On Windows, the semantic CLI archive needs its `.playwright/` directory next to `percepta.exe`. The workstation
  extracts the whole archive, so the directory is kept. Browser provisioning (`percepta install-browser`) is an
  explicit operator step, and Conditor does not perform it.
