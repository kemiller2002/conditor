# EDS-ADR-002: qualify Forma 1.0.1 and the Echelon theme for echelon-current 1.38.0 (Conditor)

Source: owner decision EDS-ADR-002 (Echelon branding moves out of Forma into
`kemiller2002/echelon-design-system`; Forma becomes brand-neutral) and the
Forma namespace decision (2026-11-09, "Do the major break": `ef-` -> `forma-`).
Owner approval, 2026-11-09 ("Yes, approved"): channel releases for Forma 1.0.0,
the theme component and echelon-current 1.38.0.

## Context

- Forma 1.0.0 (forma#182) is a major release. Every `.ef-*` class, `--ef-*`
  property, `data-ef-*` attribute and `<ef-*>` tag is renamed to `forma-`;
  `dist/brands/echelon.css`, `forma-echelon-marketing.css` and
  `forma-marketing-theme-echelon.css` are removed. Consumers migrate with the
  shipped F# codemod `tools/NamespaceMigrator`. Forma 1.0.1 (forma#183) fixes
  false negatives in that codemod; no CSS output changed.
- `@echelon-foundry/echelon-theme` 1.0.0 (echelon-design-system release
  `v1.0.0`) carries the Echelon brand CSS, marketing theme and approved marks.
  It requires Forma 1.0.0 or later, is loaded after Forma's CSS, and applies
  only under `data-forma-brand="echelon"` on `<html>`.
- `@echelon-foundry/forma-workflow` 2.0.0 is the Forma 1.x workflow engine
  (peer `@echelon-foundry/design-system >=1.0.0`); 1.2.0 emits `ef-` classes.

## Requirements

1. **Forma 1.0.1 qualified.** `components/forma.component.json` qualifies
   1.0.1 as an exact GitHub release-asset pin, with an upgrade test
   0.14.0 -> 1.0.1.
2. **The Forma 1.0 boundary is disclosed, never silent.** A web-package
   transition that moves Forma from below 1.0.0 to 1.0.0 or later names the
   namespace and brand break and the codemod in the plan, and the disclosure
   is bound into the authorization digest. Other transitions' digests are
   unchanged.
3. **The scaffold follows the selected Forma.** Under Forma 1.0.0 or later
   the scaffolded page uses `<forma-button>`; with the Echelon theme declared
   it sets `data-forma-brand="echelon"` on `<html>` and loads the theme's
   `dist/echelon.css` after Forma's `dist/all.css`. Below 1.0.0 the scaffold is
   unchanged byte for byte.
4. **Echelon theme component.** A new web-package component `echelon-theme`
   (`@echelon-foundry/echelon-theme`) qualifies exactly 1.0.0 as its GitHub
   release asset. It is optional: a repository installs it only by declaring
   it in `conditor.json` (Echelon applications do; others need not), and it
   never enters `.echelon/foundations.json`.
5. **Peer compatibility, by release line.** The compatibility graph refuses,
   before mutation: the theme 1.x without Forma or with Forma below 1.0.0;
   forma-workflow 1.x with Forma 1.0.0 or later; forma-workflow 2.x without
   Forma 1.0.0 or later. The rules are keyed by release line, so a version
   qualified later by a Registry attestation carries its line's rule, and the
   descriptors' `qualifiedRange` covers the versions they qualify.
6. **forma-workflow 2.0.0 qualified** (npm, integrity
   `sha512-2MFROdc7ox/4/RFCx99M8CM83px+IzNCUBdUzf85gkjbftVeYFlzCYgJz5tPxxtHbB1MrorrUMFzKQX4VvHuZw==`).
7. **echelon-current 1.38.0 qualified.** The proposed set is vendored byte for
   byte from echelon-registry, the kickoff manifest scaffolds under it with
   every web pin equal to the Registry's declared specifier, CI installs its
   native host tools from the published releases, and a Conditor release
   carries the qualification so the Registry's REG-RU-007 gate passes.

8. **The indy-init preset follows 1.38.0.** It declares Forma 1.0.1 and the
   Echelon theme 1.0.0, and the ODR-COMP-P14 owner exemption for
   forma-workflow moves from 1.2.0 to 2.0.0. Owner rationale (relayed
   2026-11-09): this is the same exemption carried to the preset's new Forma
   major, a consequence of the approved major break, not a new decision;
   forma-workflow 2.0.0 exists only because of the approved `ef-` -> `forma-`
   rename, 1.2.0 with Forma 1.x would be broken, and the exempt version
   travels only with the preset's Forma major.
9. **Gradual migration: existing projects are untouched.** The owner chose to
   migrate consumers gradually ("On 5 let's do a gradual upgrade. Do signal
   now and we'll do each other when we work on them", 2026-11-09 09:37 US
   Eastern). Projects pinned to echelon-current 1.37.2 keep Forma 0.14.0 and
   forma-workflow 1.2.0, and nothing in this change upgrades an existing
   project implicitly: `upgrade` never reads the preset, and `--current`
   moves only to a set passed explicitly by digest (and refuses a project
   that declares forma-workflow under any set). One mechanical effect is
   allowed and documented: a lock written by Conditor 0.10.21-0.10.23 records
   the previous forma and forma-workflow descriptor digests, so after
   installing 0.10.24 the project runs `conditor init` once to re-lock, with
   no version change (`docs/indy-day-of.md`).

Decision record: `research/decisions/DF-CON-2026-A008--forma-1-line-echelon-theme-and-gradual-migration.md`.

## Out of scope

Migrating any consumer (Signal is migrated separately). Installing a web
package that no `package.json` binds yet through `upgrade --current` (the
existing opt-in contract, CON-332, applies unchanged).
