# 0001: Registry is the single version authority when declared

- Status: accepted
- Date: 2026-10-05
- Finding: CON-F1 (Echelon quality remediation)

## Context

`conditor.json` may declare `registryAuthority`, a digest-pinned
`echelon.resolved-release-set/v1` produced by the Echelon Registry. Before this
decision, the planner looked up each requested component in Conditor's embedded
catalog first and consulted the authority only for components the catalog did
not know. A manifest whose authority selected Praxis 3.6.0 and Ordo 1.4.0 but
which requested Praxis 3.1.4 and Ordo 1.3.0 therefore planned 3.1.4 and 1.3.0
without error. Only Registry-only components (for example Dokimos) were held to
the authority.

## Decision

When `registryAuthority` is declared:

1. Every requested component is resolved against the authority first,
   whatever its role in the set (`host-tool`, `repository-lifecycle`,
   `project-binding`) and whether or not Conditor also embeds a descriptor.
2. A request with no version is bound to the version the authority selects. The
   embedded `defaultVersion` is not used.
3. A request that pins a different version is refused with a
   `VersionConflict` naming the component, the requested version, the authority
   version and the authority source (path, SHA-256, profile).
4. A requested component the authority does not select is refused with
   `AbsentFromAuthority`. This applies to components in the embedded catalog
   and to optional components. The current code has no documented escape hatch.
   Conditor does not fall back to its embedded catalog for these components,
   because that fallback is what let embedded components bypass the Registry.
   To use such a component, add it to the Registry profile or remove it from
   `conditor.json`.

Without `registryAuthority`, behaviour is unchanged and the embedded catalog
governs.

The rule is the pure function `RegistryAuthorityBinding.decide`. `Planner.create`
uses it before any catalog lookup. `Adoption` uses it so that adoption never
writes a manifest that the planner would refuse. `CurrentUpgrade` refuses an
embedded component that is absent from the target set before it mutates
anything. The embedded catalog still provides *how* a bound version is
installed (command, arguments, qualification). It no longer decides *which*
version is installed.

## Consequences

- Each refusal names the exact Registry decision, so operators replace the
  authority instead of letting Conditor substitute a version silently.
- `RegistryAuthorityBindingTests` iterates every embedded component id and
  checks both the pure rule and the real planner. A newly embedded component
  that bypasses the authority fails CI.
- Embedded qualification still applies to the bound version. An authority can
  select a version that Conditor has not qualified, and that plan is still
  refused by the compatibility graph.

## Open debt (not addressed here)

- **CON-F2: parallel embedded catalog.** `components/*.component.json` still
  duplicates release facts that the Registry owns, such as versions, packages
  and lifecycle sources. This decision limits the catalog to installation
  mechanics under an authority, but the catalog still exists. Retiring or
  deriving it is tracked with issue #12.
- **CON-F3: Forma transport.** Forma is described as an npm package in the
  embedded catalog. The Registry may distribute it as a `github-release`
  artifact. The two transports are not reconciled yet. Tracked with issue #12.
- Related: issue #31, and draft PR #18 (Registry project-binding overlay for
  presets). PR #18 rewrites preset versions from a resolved set but does not
  change planner enforcement for a declared `registryAuthority`.
