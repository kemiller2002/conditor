# Qualify the Echelon theme 1.1.0 for echelon-current 1.39.0 (Conditor)

Source: owner approval, 2026-11-09 (multiple-choice prompt, "Yes, approved
(Recommended)"): "To ship the new look I'd release Echelon theme 1.1.0
(fonts plus the approved panels, app bar, chips, emphasis buttons and
loading styles) and move echelon-current to 1.39.0, with only the theme
version changed. Approve that channel release?"

## Context

`@echelon-foundry/echelon-theme` 1.1.0 (kemiller2002/echelon-design-system
tag `v1.1.0`, `a03c5ac0`) adds, without changing anything 1.0.0 shipped:

- `dist/echelon-app.css`: the Echelon application layer (`@layer echelon.app`,
  every rule scoped to `[data-forma-brand="echelon"]`);
- `dist/echelon-fonts.css` with self-hosted latin WOFF2 subsets of Newsreader,
  Manrope and IBM Plex Mono in `dist/fonts/`, and their SIL OFL 1.1 texts.

`dist/echelon.css` is byte-identical to 1.0.0's and the Forma pin stays
v1.0.0, so it still requires Forma 1.0.0 or later. The documented load order
is Forma `all.css`, `echelon.css`, `echelon-fonts.css`, `echelon-app.css`.

## Requirements

1. **Theme 1.1.0 qualified** as an exact GitHub release-asset pin (sha256
   `e9360d743ad9e9a62abb3cf22d29bc8a26a4f9fd2b4980dd1d050c27937ec6bd`);
   1.0.0 stays qualified as its upgrade source. The theme 1.x peer rule
   (Forma >= 1.0.0) covers it; a test proves that.
2. **Scaffold.** A scaffold that declares the theme at 1.1.0 or later loads
   `echelon-fonts.css` and `echelon-app.css` after `echelon.css`, in the
   documented order. A scaffold on theme 1.0.0 (which ships neither file) is
   unchanged byte for byte.
3. **Upgrade.** A repository that declares the theme at 1.0.0 moves to 1.1.0
   through the existing web-package binding transition (an exact
   release-asset pin), crossing no breaking boundary.
4. **Re-lock.** The theme descriptor changes, so a lock written by Conditor
   0.10.24 for a project that declares the theme records the previous
   descriptor digest. Such a project runs `conditor init` once after
   installing 0.10.25; its versions do not change. Documented and tested.
5. **echelon-current 1.39.0 qualified.** The proposed set is vendored byte
   for byte from echelon-registry; the kickoff manifest scaffolds under it;
   a Conditor release (0.10.25) carries the qualification for REG-RU-007.
   The indy-init preset follows the channel (theme 1.1.0).
