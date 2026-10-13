# Web-package current-upgrade contract

`conditor upgrade --current` moves Registry `project-binding` selections whose
distribution class is `web-package` (Limen `@echelon-foundry/limen`, Forma
`@echelon-foundry/design-system`, Folio `@echelon-foundry/print-components`)
only when every step can be proven. Anything Conditor cannot prove is refused;
nothing is guessed. Implementation: `src/Conditor.Core/WebPackageBinding.fs`.
Specification by test: `tests/Conditor.Tests/WebPackageUpgradeTests.fs`.

## Release identity

The **target** is the single entry the integrity-bound target resolved set
selects for the component. It must have role `project-binding`, class
`web-package`, an exact version, an `owner/name` repository, a tag, a release
manifest SHA-256, a `distribution.package`, and exactly one artifact with
purpose `package` and a SHA-256 digest. The pin that names exactly that
release is:

| Mechanism | package.json specifier | Artifact fetched from |
|---|---|---|
| `npm` | the exact version, e.g. `0.7.1` | `distribution.url` (npmjs.org only) |
| `github-release` | `https://github.com/<repository>/releases/download/<tag>/<artifact>` | the same URL |

Conditor's embedded descriptor must declare an npm application binding,
qualify both the source and target versions, and bind the target version to the
same package name the Registry names (historical renames such as
`@echelon-foundry/typescript-wasm-kernel` → `@echelon-foundry/limen` are
resolved through the descriptor's `historicalPackages`).

The **source** is the version `conditor.json` declares, which the Conditor lock
already proves. When the repository records Registry authority, that authority
file must still match its recorded digest and must select the same source
version; its specifier is then the only acceptable source pin. Without recorded
authority, the source pin must be the exact version or a GitHub release-asset
tarball of exactly that version.

## Qualification (CON-340..346, DF-CON-2026-A004 decision 2)

A source or target version is **exactly qualified** when the embedded
descriptor lists it in `qualifiedVersions`. This is the only rule for
lifecycle tools.

A project binding's descriptor may also declare `qualifiedRange` (Forma
`>=0.2.0 <1.0.0`, Folio `>=0.3.0 <1.0.0`, Limen `>=0.6.1 <1.0.0`) and
`deniedVersions`.

A **target** release outside `qualifiedVersions` is qualified **by
attestation** only when every one of these holds:

* the version is in `qualifiedRange` and not in `deniedVersions`;
* the resolved-set entry carries a `releaseAttestation`;
* its ECDSA P-256 signature verifies over the statement
  `echelon.release-attestation/v1|<systemId>|<version>|<releaseManifest.sha256>|<sorted contracts>`;
* the key that signed it is pinned in this build (`trust/registry-release-keys.json`)
  and is not revoked;
* it claims `echelon.web-package-binding/v1`.

A **source** version outside `qualifiedVersions` is qualified only by the same
attestation, carried by the repository's **recorded Registry authority**: the
digest-verified set the repository was last applied from. That set must
select exactly the source version. A range alone never qualifies anything:
option D is gated on signatures in its entirety (CON-346). This holds for
the declared path and the opt-in path alike.

Anything else is refused as "not qualified by this Conditor build", with the
reason. That includes no attestation, an unknown or revoked key, a bad
signature, a missing contract, a version out of range and a denied version.
Conditor never relies on an unsigned contract claim.

Each transition discloses how it was qualified: `exact` or
`attested:<range>:<keyId>`. An attested qualification is bound into the plan
digest. The compatibility graph, used by the final verification, by
`verify` and by `doctor`, accepts the same attested selections of the
repository's digest-verified authority.

**This build pins no key.** Until the Registry signs release attestations
(REG-SIG-001..008) and a Conditor release pins its key, every project binding
stays under exact qualification.

## Repository observation

Every `package.json` below the repository root (excluding `node_modules`,
`.git`, `bin`, `obj`, `.conditor` and symlinked directories) is read. A
declaration of the source or target package in `dependencies`,
`devDependencies`, `optionalDependencies` or `peerDependencies` is a binding.

* **No binding** — the repository does not consume the package. The transition
  is `web-package-declaration`: only `conditor.json`, the authority and the lock
  change, after the usual full repository verification.
* **Bindings** — the transition is `web-package-binding`, and every binding must
  satisfy all of the following or the whole plan is refused:
  * the specifier is the exact source pin (ranges, tags, git refs, other
    versions and `file:` specs are refused as unpinned or modified);
  * it does not already pin the target (a change made outside Conditor is not
    recorded as a Conditor transition);
  * the nearest lockfile at or above the project is exactly one npm lockfile
    (`package-lock.json` or `npm-shrinkwrap.json`); no lockfile, an ambiguous
    set, or a pnpm, Yarn or Bun lockfile is refused, as is a `packageManager`
    field naming anything but npm;
  * the lockfile locks the source package at the source version (and, for a
    tarball pin, resolves to that tarball).
* A component the manifest marks `required` without a scaffold is refused
  when no `package.json` binds it (CON-333).

The SHA-256 of every observed `package.json` and lockfile is part of the plan
digest, so editing a binding after `--check` invalidates the authorization.

## Declaring a consumed web package (opt-in, CON-330..333)

A repository that already consumes a registered web package but has not
declared it is not governed by `upgrade --current` (CON-279). Chrona, Summa
and Signal pin Forma and Folio by tarball in `package.json` and
`.echelon/foundations.json`. Pins are never hand-edited. Instead the
repository declares the package once, and Conditor owns its pins from then
on:

1. Add `{ "id": "forma", "version": "<the version the pins name>", "required": true }`
   to `conditor.json`. Adding the declaration is the only change allowed
   since the lock.
2. Run `conditor upgrade --current --check --resolved-set ... --resolved-set-sha256 ... --home <isolated dir>`.
   The plan lists the declaration under "declarations added to conditor.json".
3. Authorize the printed digest.

Proof of the opt-in source:

* **Pins, not authority.** The source is never governed by a recorded Registry
  authority, so the authority is not consulted. Every `package.json` binding
  must be the exact source pin: the exact version, or the release-asset
  tarball of exactly that version. The npm lockfile must lock it at that
  version, as for any binding.
* **Package.json required unless declaring at target.** At least one
  `package.json` must bind the package. A declaration that no pin proves is
  refused unless it is at exactly the target version.
* **Declaration at the target version.** The pins must already name that
  release exactly, locked, and the foundations capability (if any) must agree.
  Nothing else changes.

The declaration and the move to the target selection are one plan with one
authorization digest. An `opt-in` line and the foundations line are added to
the digest material, so the digest of a plan that has neither is unchanged.

## Foundations capability (CON-331)

When `.echelon/foundations.json` declares the component's capability:

* At the source version, the transition also moves it to the target version.
* At the target version, the capability is left alone.
* At any other version, the transition is refused.

The edit is surgical: exactly the capability's plain `version` value changes,
and the plan proves that the edited document differs only there. A capability
carrying a release-specific key other than `required`, `version` and
`boundaryManifest` (for example `sourceCommit`) is refused, because Conditor
cannot prove its new value. The file's SHA-256 is bound into the digest. It is
backed up with the `package.json` and lockfiles, restored with them, and its
post-condition is checked after npm runs.

## Required web packages without a scaffold (CON-333)

A `required` web package in a manifest without a scaffold is planned when an
existing `package.json` binds it, because that file is an explicit target. It
is still refused when nothing binds it (CON-023).

## Execution

1. Every target package artifact is taken from the artifact mirror or
   downloaded (never when `--offline`), and its SHA-256 must equal the Registry
   digest before anything changes.
2. All affected `package.json` and lockfiles are backed up.
3. For each binding npm itself changes the pin (lockfiles are never edited by
   Conditor):
   * if the package was renamed, `npm uninstall --ignore-scripts --no-audit
     --no-fund <source-package>`;
   * `npm install --save-exact <--save-prod|--save-dev|--save-optional|--save-peer>
     --ignore-scripts --no-audit --no-fund <package>@<target-specifier>`, keeping
     the original dependency section.
4. Post-conditions: `package.json` pins exactly the target specifier, the
   retired package name is gone, and the lockfile locks the target version with
   `integrity` equal to the SHA-512 of the digest-verified Registry artifact
   (and, for GitHub release assets, `resolved` equal to the asset URL).
5. Each planned foundations capability version moves, and its post-condition
   is checked.
6. Any npm failure or failed post-condition restores every backed-up file
   (`package.json`, lockfiles, `foundations.json`) byte-for-byte and stops the
   upgrade before Conditor governance changes.
   `node_modules` is not restored; run `npm ci`.

The rest of the current upgrade is unchanged: full repository verification
against the pending authority, then the authority, `conditor.json` and lock are
committed together, and a second plan must show no remaining version drift.
