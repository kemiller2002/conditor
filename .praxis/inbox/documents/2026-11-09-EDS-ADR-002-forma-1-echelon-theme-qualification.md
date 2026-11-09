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
5. **Peer compatibility.** The compatibility graph refuses, before mutation:
   the theme 1.0.0 without Forma or with Forma below 1.0.0; forma-workflow
   1.2.0 with Forma 1.0.0 or later; forma-workflow 2.0.0 without Forma 1.0.0 or
   later.
6. **forma-workflow 2.0.0 qualified** (npm, integrity
   `sha512-2MFROdc7ox/4/RFCx99M8CM83px+IzNCUBdUzf85gkjbftVeYFlzCYgJz5tPxxtHbB1MrorrUMFzKQX4VvHuZw==`).
7. **echelon-current 1.38.0 qualified.** The proposed set is vendored byte for
   byte from echelon-registry, the kickoff manifest scaffolds under it with
   every web pin equal to the Registry's declared specifier, CI installs its
   native host tools from the published releases, and a Conditor release
   carries the qualification so the Registry's REG-RU-007 gate passes.

## Out of scope

Migrating any consumer (Signal is migrated separately). Installing a web
package that no `package.json` binds yet through `upgrade --current` (the
existing opt-in contract, CON-332, applies unchanged).
