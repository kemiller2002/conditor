# NuGet application-binding upgrade

A NuGet application binding is a component descriptor whose
`"applicationBinding"` is `"nuget"` and whose package comes from nuget.org, not
from a GitHub release-asset feed (`docs/nuget-feed-contract.md`). Aegis is one:
`components/aegis.component.json`, package `EchelonFoundry.Aegis.Core`.

Before this contract, a version change of such a binding had no upgrade path:

- `conditor upgrade` (version-only) refused any component with an application
  binding;
- `conditor upgrade --current` refused it as "no safe repository upgrade
  contract for that distribution".

A repository that declared Aegis 1.0.0 therefore could not move to the Aegis
2.0.0 that echelon-current 1.40.0 selects (WI-0077).

## What moves

A version change moves two things together, or neither:

1. the binding's declaration in `conditor.json`;
2. every exact `PackageReference` or `PackageVersion` pin of the binding's
   **package family** in the repository's MSBuild files (`*.fsproj`,
   `*.csproj`, `*.props`, `*.targets`, outside `bin`, `obj`, `.git`,
   `node_modules`, `.conditor` and `vendor`).

The package family is every package id that shares the descriptor package's
owner prefix. For `EchelonFoundry.Aegis.Core` that is `EchelonFoundry.Aegis.`,
so `EchelonFoundry.Aegis.Store.GitHub` and
`EchelonFoundry.Aegis.Integration.GitHub` move with it. They are released at
one version together and pin Core exactly.

## Refusals

Conditor moves only what it can prove the declaration set. A move is refused,
and nothing is written, when:

- **The target version isn't qualified:** this Conditor build does not qualify
  the target version (`qualifiedVersions` in the descriptor).
- **A pin doesn't match:** any pin of the family names a version other than
  exactly the declared old version or the target version, such as a range, an
  older pin or a hand edit.
- **A file changed after planning:** a file changed between plan and apply.
  Every file is re-proven before any is written.

## How to run it

**Version-only.** Change the declared version in `conditor.json`, then run:

```bash
conditor upgrade --check     # lists the binding and every file and pin that moves
conditor upgrade             # moves the pins, verifies and re-locks
```

This path moves only the components whose declared version changed. Nothing
else from the Registry channel is adopted.

**Current.**

```bash
conditor upgrade --current --check --resolved-set <set> --resolved-set-sha256 <sha>
conditor upgrade --current --resolved-set <set> --resolved-set-sha256 <sha> --authorize <digest>
```

Here the binding is a `nuget-binding` transition:

- Its line in the plan digest names the binding, both versions, the family,
  and every file with its moved ids and pin count.
- A plan with no binding transition keeps the same digest as before this
  contract, so existing authorizations still hold.
- After applying, the pin-consistency invariant (CON-351) proves every pin
  again.
- Any failure restores the repository (CON-356).

## What this does not do

- It does not restore, build or test the consuming project. Run the
  repository's own build and tests after the move.
- A major release may change behaviour; Aegis 2.0.0 removed no public
  signature.
- It does not move packages outside the family, and does not add pins the
  repository does not already have.
