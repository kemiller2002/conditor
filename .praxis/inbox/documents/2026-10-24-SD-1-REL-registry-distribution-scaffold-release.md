# SD-1-REL: Release the scaffold fix that installs web packages through their Registry distribution

Priority: P1 (recurring; Indy Init kickoff depends on it)
Owner system: Conditor
Target repositories: conditor
Intake: Praxis document ingestion. Source material, not a canonical requirement.
Fix in progress: kemiller2002/conditor#108 (branch `claude/sd1-registry-distribution`, WI-0050). It is green but not
released (kemiller2002/indy-init-rehearsal-003 `docs/REHEARSAL-003-REMEDIATION-REPORT.md` §2, `research/rehearsal-003-engineering-hardening.md` §5).

## Evidence

- **Defect SD-1.** The `fsharp-limen-web` scaffold in Conditor 0.10.8 writes an npm version range for every web
  package, whatever the package's Registry distribution class is. Folio 0.4.0 is `distribution.mechanism = github-release`,
  so `npm ci` fails with `404 '@echelon-foundry/print-components@0.4.0' is not in this registry`.
  - R3 `research/rehearsal-003-defect-analysis.md` SD-1; R3 runs 40074002224 and 40074314605.
- It **recurred from Rehearsal 2.** Each rehearsal fixed it locally; the class was never closed upstream.
- The latest release, v0.10.9 (`620f951`), predates the fix.

## Requirements (proposed)

- **SD-1-REL-R1.** Merge conditor#108 and release it. The release is qualified through the Registry (REG-RU-007)
  before the Indy Init kickoff release set is frozen.
- **SD-1-REL-R2.** A scaffold test covers every web component in the current Registry profile, checking that the
  emitted dependency declaration matches that component's distribution mechanism (npm range, or GitHub-release
  tarball with sha256).

## Acceptance criteria

- A fresh `fsharp-limen-web` scaffold on the released version runs `npm ci` green with Folio resolved from its GitHub
  release tarball, and the tarball's sha256 equals the Registry entry.
- R2's test fails when a `github-release` component is emitted as an npm range.
