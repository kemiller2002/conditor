# NuGet release-asset feed contract

Some Echelon libraries are released as NuGet packages that are **not on
nuget.org**. Their `.nupkg` files are GitHub release assets with build-provenance
(Sigstore) attestations, and the Registry records every asset's SHA-256:

- Ordo's `ordo-core.nupkg`;
- Arca's `EchelonFoundry.Arca.Core` and `EchelonFoundry.Arca.GitHub` until
  Arca's nuget.org Trusted Publishing exists (arca DF-ARCA-2026-0006).

Conditor installs such a release into a consuming repository through a **local
NuGet feed**, and refuses every state it cannot prove. Implementation:
`src/Conditor.Core/NugetFeed.fs`. Specification by test:
`tests/Conditor.Tests/NugetFeedTests.fs`.

Before this contract, a consumer vendored such a package by hand. Praxis, for
example, keeps `vendor/nuget/echelonfoundry.ordo.core.1.5.0.nupkg`, an
`ordo-core.lock` and a `NuGet.config` mapping. Conditor now does the same thing
generally, from the Registry.

## Which releases

A component of the repository's Registry authority (its resolved release set)
qualifies when it is a `project-binding` with:

- distribution class `nuget-library`;
- distribution mechanism `github-release`;
- lifecycle state `active`;
- a release manifest SHA-256;
- one or more artifacts with purpose `package`, each named
  `<PackageId>.<version>.nupkg` with a 64-character SHA-256 and a distinct
  package id.

Each package is downloaded from
`https://github.com/<repository>/releases/download/<tag>/<artifact>`. A
component that is not a feed release is unaffected. A component that has an
embedded Conditor descriptor (Aegis, on nuget.org) keeps its descriptor's
binding.

## What a repository holds

| Path | Content |
|---|---|
| `vendor/nuget/<PackageId>.<version>.nupkg` | The unmodified release asset, whose SHA-256 equals the Registry's |
| `vendor/nuget/<system>.lock` | `conditor.nuget-feed-lock/v1`: system, version, repository, tag, release-manifest digest, and per package its id, version, asset, digest and URL |
| `NuGet.config` | Package source `echelon-vendor` = `vendor/nuget`, and `packageSourceMapping` that maps each package id to `echelon-vendor` and to no other source |

When `NuGet.config` had no source mapping, Conditor adds an explicit
`nuget.org` source mapped to `*`, so everything else restores as before.
Existing sources, mappings and comments are kept. Conditor refuses, rather than
overrides, two cases:

- an `echelon-vendor` key that points elsewhere;
- a package id that another source already maps.

The consumer references the packages by exact version, as with any NuGet
package.

## Operations

- **`conditor init`** (and `upgrade`) for a declared feed component plans
  `nuget feed <system>@<version>` and then verifies the feed.
- **`conditor verify`** (and `doctor`) proves the feed:
  - the lock is exactly this release's;
  - every package file holds the Registry's bytes;
  - `NuGet.config` maps every id to the feed only.
- **`conditor upgrade --current`**:
  - **Opt-in.** Declare the component in `conditor.json` at exactly the version
    the target set selects, then run `upgrade --current`. The plan lists it
    under `NuGet release-asset feeds` as `opt in at <version>`.
  - **Version change.** When the target set selects a newer version, the
    transition mode is `nuget-feed`. The previous version's package files are
    removed. Every exact pin of the package ids at the previous version moves
    to the new one: `PackageVersion` or `PackageReference` `Version` attributes
    in `*.props`, `*.targets`, `*.fsproj` and `*.csproj` outside `bin`, `obj`,
    `vendor`, `.git`, `.conditor` and `node_modules`. A pin at any other
    version or range is refused.
  - **Digest.** The feed changes are part of the plan digest.

In every operation, every package is fetched and proven against the Registry
digest **before anything is written**. A mismatch changes nothing. After
writing, the feed is verified. `upgrade --current` then verifies the whole
repository against the staged authority before it commits the authority, the
manifest and the lock.

## Fetching

Packages are fetched in this order:

1. `upgrade --current` uses its `--artifact-mirror` directory, and honours
   `--offline`.
2. `init`, `upgrade` and `repair` use the directory named by the environment
   variable `CONDITOR_ARTIFACT_MIRROR`, when it holds the asset.
3. Otherwise the release asset URL.

The Registry digest binds the bytes whatever their source. The Registry's own
release record is made only after the release's attestations have been
verified, so the digest also carries that provenance.
