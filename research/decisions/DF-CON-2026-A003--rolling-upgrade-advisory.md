---
id: DF-CON-2026-A003
title: Conditor reports version drift as a read-only advisory and plans stepwise current upgrades only through Registry-produced resolved sets
status: accepted
version: 1.0.0
owners:
  - conditor
created: 2026-10-13
updated: 2026-10-13
research_area: repository-lifecycle
decision_type: architecture
related_documents:
  - requirements/CONDITOR-REQUIREMENTS.md
  - docs/drift-advisory.md
  - docs/requirements/rolling-upgrade-notes.md
  - docs/decisions/0001-registry-version-authority.md
  - docs/web-package-upgrade-contract.md
tags: [decision, upgrade, drift, registry]
confidence: medium
provenance:
  contributions:
    EXE-20261013T150353451Z-40bfcd6e:
      operations: [created, modified]
      at: 2026-10-13T15:06:44.932Z
      last: 2026-10-13T15:09:21.712Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Record the rolling-upgrade advisory decision: read-only drift, Registry-produced stepwise sets, declared staleness policy"
---

# Context

The largest upgrade problems in the Echelon portfolio come from repositories
that stop tracking their versions. Several small jumps are easier than one
large jump.

The motivating case is Forma. Forma 0.6.1 was released on 2026-10-10 but was
not registered in echelon-registry. Chrona, Summa and Signal stayed on Forma
0.4.1 and now face a jump of two minor lines.

Their Forma pins are a GitHub release-asset tarball URL in `package.json` and
a capability version in `.echelon/foundations.json`. Neither pin is a
`conditor.json` component. Before this decision Conditor saw neither:

- `upgrade --current` compares only declared `conditor.json` components
  (CON-279);
- web-package bindings are observed only for declared components
  (`WebPackageBinding`);
- `.echelon/foundations.json` is written by the scaffold but never read back.

Chrona's persisted Registry authority (echelon-current 1.19.0) already selects
Forma 0.5.0. The repository is a registered release line behind, and nothing
reported it.

# Decisions

1. **Drift is a separate, read-only advisory command (`conditor drift`).** It
   is not part of `status` or `doctor`, because it needs a Registry checkout
   that those commands do not take, and because its outcome must stay
   advisory. It exits 0 by default. `--fail-on warn|fail` opts a caller (CI
   or work-start tooling) into a non-zero exit (3). Making it blocking by
   default would change the scope of Conditor's gates and is not done here.

2. **Discovery reads every pin kind Conditor writes or rewrites.** These are
   `conditor.json`, `package.json`, `.echelon/foundations.json`, NuGet feed
   locks and MSBuild package pins. A pin is attributed to a Registry system
   only through Registry facts:
   - the system id (for `conditor.json` and the foundations keys);
   - the release's `distribution.package` (npm, NuGet);
   - the exact release-asset URL of its package artifact (tarball pins);
   - its `.nupkg` artifact names (MSBuild pins).

   Nothing is attributed by name similarity.

3. **The Registry is read through its own digest chain.** The chain runs from
   the channel index to the platform resolved set and the catalog snapshot,
   and from the snapshot to the release and profile manifests. Any mismatch
   stops the advisory (exit 2). Conditor does not keep a second list of
   releases.

4. **A step is one registered minor line, landing on its newest registered
   patch.** This is the unit for both the recommended next step and stepwise
   planning. The lead's example "one registered release at a time (0.4.1 →
   0.5.0 → 0.6.1)" is satisfied: each step there is a different minor line.
   Patch releases inside a line are crossed together, because semantic
   versioning promises their compatibility. They are still listed, with
   their migration-notes links, so no release's notes are hidden. The choice
   is reversible: the step function is one pure function.

5. **Stepwise upgrade never composes a resolved set.** Each step targets one
   of the following Registry-produced sets, chosen as the newest set that
   advances no declared component by more than one line, never downgrades
   one, and never passes the final target:
   - the supplied target set;
   - the channel's current set;
   - a historical channel set recovered from the Registry's Git history. Its
     SHA-256 must equal the digest the channel index recorded at the same
     commit.

   Synthesising a set (copying a release entry into another set) would make
   Conditor a resolver and would persist a non-Registry authority into
   repositories. That would contradict decision 0001 and CON-277, so it is
   refused. When the Registry has no set for an intermediate line (for
   example, two releases recorded in one channel bump), the plan reports the
   gap and takes the smallest larger Registry step. The Registry-side remedy
   is a requirement note (REG-RU-003).

6. **Only the first step gets a plan digest.** A later step's digest binds
   the manifest and binding bytes that the earlier steps will produce. Those
   bytes do not exist yet, so Conditor gives each later step its exact Registry
   set (path and SHA-256) and the `--check` command to run once the step
   before it has been applied. The authorization model (`CurrentUpgrade`
   digest, CON-278) is unchanged.

7. **The staleness policy is declared data.** The defaults are:
   - warn at more than one minor line behind or any major behind;
   - fail only below the Registry profile's minimum supported range.

   A Registry resolved set and `conditor.json` may each declare
   `stalenessPolicy`. The repository's fields override the Registry's,
   which override the defaults, and the report names each field's source.

# Consequences

- Chrona-style pins outside `conditor.json` become visible, with an exact next
  specifier and migration-notes links. Moving them is still manual, or comes
  after they are declared to Conditor. Declaring and governing those pins, and
  removing the per-version qualification coupling that refused Forma 0.5.0 and
  0.6.1, are proposed separately in `DF-CON-2026-A004`.
- Release manifests carry no release date and no migration-notes field. Drift
  uses Registry Git history for registration time and the GitHub release page
  for notes. Requirement notes REG-RU-001 and REG-RU-002 ask the Registry to
  publish both.
- Praxis `work start` integration and the weekly cross-repository report are
  recorded as requirement notes for their owners
  (`docs/requirements/rolling-upgrade-notes.md`), not implemented here.
