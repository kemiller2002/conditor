# EV-CON-2026-WI0054: Indy preset bootstraps on echelon-current 1.28.0

**Work item:** WI-0054. **Date:** 2026-10-27.

## Observation

`conditor init --preset indy-init` with released Conditor 0.10.12 (linux-x64, sha256 `7cbdf498…a8cab`) on an empty repository locks Ordo 1.5.0, Forma 0.5.0 and Folio 0.3.0. echelon-current 1.28.0 (Registry `e6d8a734`, linux-x64 set sha256 `5aee6c3c…`) selects Ordo 1.17.0, Forma 0.9.0 and Folio 0.4.1. `conditor upgrade --current` on that bootstrap refuses the whole plan: Folio's foundations capability declares a `releaseArtifact` Conditor cannot prove for 0.4.1 (exit 9, nothing changed).

## Cause

`tests/Conditor.Tests/EchelonCurrent.fs` documented itself as the echelon-current selection but was the 1.17.0 selection with only Praxis moved (WI-0053 moved Praxis 3.21.0 only). The Indy preset is tested to follow that list, so it stayed behind.

## Change

The list is the 1.28.0 selection (Ordo 1.17.0, Forma 0.9.0, Folio 0.4.1) and the Indy preset pins the same versions. All three were already in the descriptors' `qualifiedVersions`; Folio 0.4.1 already has its release artifact.

## Verification

- Released Conditor 0.10.12, `conditor init --target .` on an empty repository whose `conditor.json` is the changed preset (anonymous source fetches): exit 0. Lock: Praxis 3.21.0, Ordo 1.17.0, Percepta 0.1.0, Aegis 1.0.0, Limen 0.9.0, Forma 0.9.0, Folio 0.4.1. `package.json`: design-system `0.9.0`, limen `0.9.0`, print-components v0.4.1 release asset.
- `conditor verify --target .` on that repository: exit 0.
- **Not run locally:** the Conditor test suite (restoring `JsonSchema.Net` from nuget.org is not possible in the authoring environment). CI on the pull request runs it, including "the Indy preset pins … at its echelon-current selection" and "Conditor qualifies the echelon-current selection …".

## Not changed

`upgrade --current` still refuses Folio 0.3.0 → 0.4.1 for a repository already bootstrapped from the old preset. That is a separate defect, recorded as a follow-up.
