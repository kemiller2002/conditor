---
id: DF-CON-2026-A008
title: Conditor qualifies the Forma 1.x line and the optional Echelon theme, discloses the Forma 1.0 boundary, and leaves existing projects to migrate gradually
status: accepted
version: 1.0.0
owners:
  - conditor
created: 2026-11-09
updated: 2026-11-09
research_area: web-package-bindings
decision_type: architecture
related_documents:
  - .praxis/inbox/documents/2026-11-09-EDS-ADR-002-forma-1-echelon-theme-qualification.md
  - research/decisions/DF-CON-2026-A004--project-binding-qualification-and-declared-web-bindings.md
  - docs/web-package-upgrade-contract.md
  - docs/indy-day-of.md
tags: [decision, forma, echelon-theme, forma-workflow, indy-init, eds-adr-002]
confidence: high
provenance:
  contributions:
    EXE-20261109T203232178Z-feb464a9:
      operations: [created]
      at: 2026-11-09T21:12:03.000Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Record the Forma 1.x qualification, the Echelon theme as an optional component, the Forma 1.0 boundary disclosure and the gradual-migration constraint (EDS-ADR-002)"
---

# Context

The owner moved Echelon branding out of Forma (EDS-ADR-002) and chose the
namespace rename as one major break ("Do the major break", 2026-11-09). Forma
1.0.0 is brand-neutral and renames `ef-` to `forma-`; 1.0.1 fixes its codemod
(`tools/NamespaceMigrator`). The Echelon brand is now
`@echelon-foundry/echelon-theme` (kemiller2002/echelon-design-system), which
requires Forma 1.0.0 or later. The owner approved channel releases for Forma
1.0.0, the theme component and echelon-current 1.38.0 ("Yes, approved
(Recommended)", 2026-11-09, between 08:45 and 09:24 US Eastern), and chose a
gradual migration: Signal first; every other application, Indy/Causa
projects included, when it is next worked on ("On 5 let's do a gradual
upgrade. Do signal now and we'll do each other when we work on them",
09:37).

# Decisions

1. **Qualify the Forma 1.x line.** Forma 1.0.1 is an exact GitHub
   release-asset pin; 1.0.0 is not qualified. The descriptor's
   `qualifiedRange` becomes `>=0.2.0 <2.0.0` (CON-341: this build adds the
   1.x line). forma-workflow 2.0.0 (the Forma 1.x engine) is qualified;
   its range becomes `>=1.2.0 <3.0.0`.
2. **Never cross the Forma 1.0 boundary silently.** A web-package transition
   from below 1.0.0 to 1.0.0 or later names the namespace and brand break and
   the codemod in the plan and binds it into the authorization digest.
   Conditor does not run the codemod or judge the migration (component-owned
   tooling stays component-owned).
3. **The Echelon theme is an optional component.** It is installed only by
   declaring it in `conditor.json`, never added implicitly and never written
   to `.echelon/foundations.json`. The Registry selects it with
   `required: false`.
4. **Peer rules are keyed by release line.** Theme 1.x requires Forma
   `>=1.0.0`; forma-workflow 1.x requires Forma `>=0.4.0 <1.0.0` (it emits
   `ef-` names); forma-workflow 2.x requires Forma `>=1.0.0`. A version
   qualified later by attestation carries its line's rule.
5. **The indy-init preset follows echelon-current 1.38.0** (Forma 1.0.1, the
   theme 1.0.0) and the ODR-COMP-P14 owner exemption for forma-workflow moves
   from 1.2.0 to 2.0.0. This is the same exemption carried to the preset's new
   Forma major, a consequence of the approved major break, not a new decision:
   forma-workflow 2.0.0 exists only because of the rename, and 1.2.0 cannot
   work with Forma 1.x. The exempt version travels only with the preset's
   Forma major (owner, relayed 2026-11-09).
6. **Existing projects are not migrated.** A project's `conditor.json` is its
   own copy of the preset; `upgrade` never reads the preset, `start --preset`
   refuses a differing manifest, and `upgrade --current` moves only to a set
   passed explicitly by digest. A project that declares forma-workflow is
   refused by `--current` under any set. Forma 0.14.0 with forma-workflow
   1.2.0 stays qualified and valid, so projects pinned to 1.37.2 keep it.

# Consequences

- Locks written by Conditor 0.10.21-0.10.23 record the previous forma and
  forma-workflow descriptor digests. After installing 0.10.24, an existing
  project runs `conditor init` once to re-lock; its versions do not change
  (`docs/indy-day-of.md`; FormaWorkflowComponentTests).
- Adding a web package that no `package.json` binds yet through
  `upgrade --current` is unchanged (CON-332): the repository installs the
  exact pin first, then declares it.

# Evidence

`WebPackageUpgradeTests.echelonCurrent1380`, `breakingBoundaries` and
`formaWorkflowProjectIsRefusedByCurrent`; `EchelonThemeComponentTests`;
`FormaWorkflowComponentTests` (peer rules and the re-lock);
`WebScaffoldTests.runKickoffSet1380` over the vendored set of
echelon-registry#112; conditor#137 CI.
