---
id: DF-CON-2026-A004
title: Declared web-package bindings and contract-and-line qualification of project bindings, gated on signed Registry release records
status: accepted
version: 1.0.0
owners:
  - conditor
created: 2026-10-13
updated: 2026-10-13
research_area: repository-lifecycle
decision_type: architecture
related_documents:
  - DF-CON-2026-A003
  - requirements/CONDITOR-REQUIREMENTS.md
  - docs/web-package-upgrade-contract.md
  - docs/decisions/0001-registry-version-authority.md
tags: [decision, upgrade, qualification, web-package, trust]
confidence: medium
provenance:
  contributions:
    EXE-20261013T150353451Z-40bfcd6e:
      operations: [created, modified]
      at: 2026-10-13T15:09:00.516Z
      last: 2026-10-13T15:50:32.907Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Propose declared web bindings and contract-and-line qualification after echelon-registry#65 evidence"
---

# Status

**Accepted on 2026-10-13 by the user, relayed by the program lead.**

- **Decision 1 (declared web bindings and the opt-in migration):** accepted
  as written. It ships with the exact-qualification patch, so that the apps
  can move stepwise without hand-editing pins.
- **Decision 2 (option D):** accepted, on the condition that Registry
  release records are signed first. Contract-and-line qualification
  activates only for a release record whose signature verifies. Until the
  Registry signs, Conditor keeps exact per-version qualification, and it
  never falls back to an unsigned contract claim (CON-346).

# Context

The Registry work (echelon-registry#65) produced two findings that, together,
explain the drift `DF-CON-2026-A003` reports.

1. **Undeclared pins.** Chrona, Summa and Signal declare Forma and Folio only
   in `package.json` (as a GitHub release-asset tarball URL) and in
   `.echelon/foundations.json`. `upgrade --current --check` (Conditor 0.8.2)
   against echelon-current 1.20.0 therefore plans "no transitions".
   Requirement CON-279 makes it compare only declared components.
2. **Qualification refusal.** A fixture that declares them gets these
   refusals:
   - Forma 0.4.1 → 0.6.1: "target version … is not qualified by this
     Conditor build";
   - Folio 0.3.0 → 0.4.0: the same refusal.

   The same refusal occurred when Forma 0.5.0 entered channel 1.8.0. It
   persisted until Conditor 0.8.1 added `0.5.0` to
   `components/forma.component.json`, and the apps never moved.

The code shows what the per-version gate proves for a web package.
`WebPackageBinding.plan` checks three things:

- `qualifiedVersions.Contains source`;
- `qualifiedVersions.Contains target`;
- the package name at the target version.

The Forma and Folio descriptors have empty `init`, `verify`, `doctor` and
`upgrade` arguments. So a qualification commit records no functional
evidence about the release. The evidence comes from the steps that follow:

- the digest-verified artifact;
- npm's exact install;
- the lockfile integrity post-condition;
- full repository verification.

Those steps already run for every transition. The per-version list is
therefore a pure release-train coupling. Every library release needs a
Conditor release before any consumer can move, which guarantees drift.

A third gap: `WebPackageBinding` refuses a `required` web component when the
manifest declares no scaffold. The adopted apps have no scaffold.

# Decision 1 (no trust change): declared web bindings, with an opt-in migration

Web packages are declared in `conditor.json`, and Conditor owns all of their
pins (CON-330..334):

- `package.json` and the npm lockfile, through npm, as today;
- the `.echelon/foundations.json` capability version, through a surgical,
  backed-up, post-condition-checked edit in the same transaction.

Existing apps migrate through a **binding opt-in** on `upgrade --current`:

1. The user adds `{ "id": "forma", "version": "0.4.1" }` to `conditor.json`.
2. Conditor proves that every discovered pin names exactly that registered
   release and that the lockfile locks it.
3. The plan declares the binding and moves it to the target selection under
   one authorization digest.

This extends the existing opt-in (CON-279: only explicit declarations are
governed). It changes no authority. It is blocked in practice by Decision 2,
because the move it enables would be refused for any version released after
the running Conditor build.

# Decision 2 (trust change; recommendation): qualify contracts and lines, not versions

## Options

| | Option | Drift | Trust and authority effect | Assessment |
|---|---|---|---|---|
| A | Status quo: exact versions in the embedded descriptor | Guaranteed: one Conditor release per library release | Conditor build is a second, per-version gate | Rejected: it is the cause |
| B | Embedded `qualifiedRange` per component (for example `>=0.2.0 <1.0.0`) | A new build only per major line | The gate is still Conditor-owned, but coarser | Simple, but it trusts every future minor release blindly, with no Registry statement about the release |
| C | Qualification carried only by the Registry (release or channel record says "Conditor-qualified") | None | The Registry becomes sole qualifier. Release records are digest-chained but **not signed** today, so "signed" would be a new Registry capability | Moves all authority to the Registry. It is weakest when Registry review is thin |
| D | **B + C, scoped to project bindings** (recommended) | A new build only per major line, contract version or package identity | Two keys for each release: Conditor qualifies the contract and the line, and the Registry asserts per release that the release honours the contract. Both live in bytes Conditor already verifies | Recommended |
| E | Separate qualification overlay artifact (released independently of Conditor) | None | A third authority with its own distribution and integrity story | More moving parts than D, for the same effect |

## Recommendation: D

The recommendation has three parts:

- **Conditor qualifies contracts and lines.** It qualifies the
  `echelon.web-package-binding/v1` contract (and the NuGet feed contract)
  and a per-component `qualifiedRange`. It keeps `deniedVersions` as an
  escape hatch.
- **The Registry asserts compliance per release.** A Registry release record
  declares, in the release manifest that the snapshot digest-binds, that it
  honours the contract. Example: `consumerContracts: ["echelon.web-package-binding/v1"]`.
- **Lifecycle tools are unchanged.** Repository-lifecycle and host-tool
  releases keep exact embedded qualification (CON-343), because they execute
  with repository or host authority.

## Accepted condition: signed release records

The user accepted D only behind release-record signatures. Conditor's side
(CON-340..346) is built gated:

- A contract claim qualifies a release only when the release record's
  signature verifies against a Registry signing key that Conditor trusts,
  and the key is not revoked.
- An unsigned claim, an unverifiable claim, or a claim under a revoked key
  leaves the release under exact qualification.
- Conditor never falls back to an unsigned contract claim.

The Registry-side requirements (key, signing workflow, signed content,
verification, rotation and revocation) are REG-SIG-001..008 in
`docs/requirements/rolling-upgrade-notes.md`.

## Trust-model change

This changes what a Conditor build vouches for. Today, a project-binding
transition needs two keys: the Registry selects the release, and the
Conditor build names the exact version. Under D, the second key becomes the
Conditor build's contract and line qualification plus the Registry's
per-release contract claim.

Accepting it means accepting these terms:

- the Registry review is where a project-binding release is checked against
  its contract;
- for a project binding, the consumer's repository verification is the
  functional gate (as it already is in fact).

Without release signing, the claim is integrity-bound (digest chain, Git
history, PR review), not authenticated. The user therefore made signing a
precondition of D (see "Accepted condition" above).

# Consequences if accepted

- Forma 0.6.1 and Folio 0.4.0 become upgradable on the day they are
  registered, once the Registry release records carry the contract claim.
  No Conditor 0.9.x qualification release is needed.
- The Registry needs a release-manifest field and must backfill it
  (REG-RU-006, added to `docs/requirements/rolling-upgrade-notes.md`).
- `components/forma.component.json` and `components/folio.component.json` gain
  `qualifiedRange` and an optional `deniedVersions`. `qualifiedVersions`
  remains for lifecycle tools.

# Sequencing (as directed by the lead)

1. Ship an exact-qualification patch with Decision 1 and a release:
   - Forma 0.5.0 and 0.6.1, Folio 0.4.0, and Ordo 1.6.0 and 1.7.0;
   - the declared-binding opt-in;
   - foundations.json ownership;
   - the CON-333 scaffold fix.
2. Build `conditor drift` and `--stepwise` (CON-300..320).
3. Build option D's Conditor side, gated on signature verification.
4. The Registry implements REG-SIG-001..008. Until it does, step 3 stays
   inert, and exact qualification remains the only path.

# Implementation status

- Decision 1 shipped in Conditor 0.9.0 (#85).
- Decision 2's Conditor side is implemented, but inert:
  - `qualifiedRange` and `deniedVersions` on project-binding descriptors;
  - ECDSA P-256 verification of `releaseAttestation` against
    `trust/registry-release-keys.json`, which ships empty;
  - the same rule in planning, final verification and the compatibility
    graph.

  It activates only when the Registry signs (REG-SIG-001..008, whose
  statement-based payload replaced the earlier manifest-bytes wording so
  that Conditor can verify offline from the resolved set) and a Conditor
  release pins the key.
