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
* A component the manifest marks `required` without a scaffold is refused, as
  the planner would refuse to verify it.

The SHA-256 of every observed `package.json` and lockfile is part of the plan
digest, so editing a binding after `--check` invalidates the authorization.

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
5. Any npm failure or failed post-condition restores every backed-up file
   byte-for-byte and stops the upgrade before Conditor governance changes.
   `node_modules` is not restored; run `npm ci`.

The rest of the current upgrade is unchanged: full repository verification
against the pending authority, then the authority, `conditor.json` and lock are
committed together, and a second plan must show no remaining version drift.
