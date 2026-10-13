# Drift advisory and stepwise current upgrade

Small, frequent upgrades are easier than one large jump. `conditor drift`
shows how far a repository has fallen behind its Registry channel, and
`upgrade --current --check --stepwise` turns a large move into a sequence of
Registry-produced steps, one release line at a time. Requirements:
CON-300..CON-320. Decision: `DF-CON-2026-A003`.

- Implementation: `src/Conditor.Core/Drift.fs` and
  `src/Conditor.Core/StepwiseUpgrade.fs`.
- Specification by test: `tests/Conditor.Tests/DriftTests.fs` (drift) and
  `tests/Conditor.Tests/DeclaredWebBindingTests.fs` (stepwise).
- Report schema: `schemas/conditor-drift-report.schema.json`.

## `conditor drift`

```text
conditor drift --registry DIR [--channel echelon-current] [--platform RID]
               [--target PATH] [--json] [--fail-on never|warn|fail]
```

The command reads its inputs as follows:

- `--registry` is an echelon-registry checkout, read only.
- `--channel` names a directory under `channels/`. The default is
  `echelon-current`.
- `--platform` selects that channel's platform set. The default is the
  current runtime identifier. Web and NuGet selections are
  platform-independent.

Nothing is downloaded, installed, or written.

### Registry input (digest chain)

1. `channels/<channel>/channel.json` (`echelon.current-channel/v1`). Its
   SHA-256 is reported.
2. The platform resolved set named there. Its SHA-256 must equal the index
   entry.
3. The catalog snapshot named there. Its SHA-256 must equal the index entry.
4. Every release and profile manifest the snapshot lists. Each SHA-256 must
   equal the snapshot entry.

A missing file or a mismatch stops the command with exit 2. No partial report
is produced.

**Registered releases** are the snapshot's releases that are `active` and
whose `releaseStage` is one of the profile's `allowedReleaseStages`. The
**supported range** of a component is its profile `version.range`.

### Pins discovered

| Source (`pins[].source`) | Pin | Attributed to a system by |
|---|---|---|
| `conditor.json` (`conditor.json`) | `components[].version` | the component id |
| `**/package.json` (`package.json`) | `dependencies`, `devDependencies`, `optionalDependencies`, `peerDependencies` | a registered release's `distribution.package` |
| `.echelon/foundations.json` (`foundations`) | `capabilities.<id>.version` | the capability key (only when it is a registered system) |
| `vendor/nuget/*.lock` (`nuget-feed-lock`) | `system` / `version` lines (`conditor.nuget-feed-lock/v1`) | the `system` line |
| `Directory.Packages.props`, `Directory.Build.props`, `**/*.{fs,cs}proj` (`msbuild`, specifier `<id> <version>`) | `PackageVersion`/`PackageReference` `Include`+`Version` | a registered release's NuGet `distribution.package` or `.nupkg` artifact id |

Directory walks skip `node_modules`, `.git`, `bin`, `obj`, `.conditor` and
symbolic links.

A `package.json` specifier resolves to a version in one of two ways:

- it is an exact `X.Y.Z` version;
- it equals `https://github.com/<repository>/releases/download/<tag>/<artifact>`
  for the package artifact of exactly one registered release.

Anything else is reported verbatim with `version: null` and level at least
`warn`.

### Per-component report

| Field | Meaning |
|---|---|
| `declared` | true when `conditor.json` declares the component |
| `pins[]` | `source`, `path`, `line`, `specifier`, `version`, `pinnedSince` (committer time of the Git commit that last changed that line; `null` when unavailable or uncommitted) |
| `pinnedVersion` | the `conditor.json` version for a declared component, else the oldest resolved pin (CON-305) |
| `consistent` | false when the resolved pins disagree |
| `channelSelected` | the version the channel's resolved set selects, or `null` |
| `latestRegistered` | the newest registered release |
| `releasesBehind` | the number of registered releases newer than the pin |
| `minorsBehind` | the number of registered `major.minor` lines newer than the pin's line |
| `majorsBehind` | the newest registered major minus the pinned major |
| `supportedRange`, `belowSupportedRange` | from the profile |
| `pinAgeDays` | days since the oldest `pinnedSince` |
| `behindSince` | the Registry registration time of the first registered release newer than the pin (Registry Git history; `null` when unavailable) |
| `level`, `reasons[]` | `ok`, `info`, `warn` or `fail`, with the reasons |
| `nextStep` | `{ version, crosses: [{ version, migrationNotes }], pinUpdates: [{ source, path, line, from, to }], command, declare }`, or `null` when there is no registered step |

`nextStep.version` is the newest registered patch of the next registered minor
line (DF-CON-2026-A003 decision 4). `crosses` lists every registered release
in `(pinned, next]`. `migrationNotes` is the release manifest's
`migrationNotes` when present, and otherwise
`https://github.com/<repository>/releases/tag/<tag>`.

`pinUpdates` gives the exact new specifier of every pin outside
`conditor.json`. For a tarball pin, that is the next release's asset URL.

- `command`: for a component declared in `conditor.json`, the stepwise
  current-upgrade command.
- `declare`: for an undeclared web package, the exact `conditor.json` entry
  that lets `upgrade --current` adopt it and then move every pin (CON-334,
  `docs/web-package-upgrade-contract.md`).

### Staleness policy

```json
"stalenessPolicy": {
  "warnAfterMinorsBehind": 1,
  "warnAfterMajorsBehind": 0,
  "warnAfterReleasesBehind": null,
  "warnAfterPinAgeDays": null,
  "failBelowSupportedRange": true
}
```

The values shown are the built-in defaults. The Registry resolved set may
declare `stalenessPolicy` at its top level, and `conditor.json` may declare it
too. For each field, `conditor.json` overrides the Registry set, which
overrides the default. The report's `policy.sources` names where each field
came from.

`null` disables a threshold. Every threshold is "more than": a component one
minor line behind is `info` under the default, and two lines behind is `warn`.

A component's level is the highest that applies:

| Level | When |
|---|---|
| `fail` | below the supported range (when `failBelowSupportedRange`) |
| `warn` | any of the following: more minors, majors or releases behind than the thresholds allow; the pin is older than `warnAfterPinAgeDays` while behind; the pin is unregistered; the pins disagree |
| `info` | behind, but within every threshold; or pinned ahead of the channel |
| `ok` | pinned at the newest registered release |

### Exit codes

| Code | Meaning |
|---|---|
| 0 | the report was produced (advisory; the default `--fail-on never`) |
| 2 | invalid arguments, or Registry input that is missing or fails verification |
| 3 | `--fail-on warn` and the overall level is `warn` or `fail`, or `--fail-on fail` and it is `fail` |

## `upgrade --current --check --stepwise`

```text
conditor upgrade --current --check --stepwise --registry DIR [--channel NAME]
                 --resolved-set PATH --resolved-set-sha256 SHA256 [--target PATH] [--home DIR]
```

The supplied resolved set is the final target, exactly as in direct mode.
Without `--stepwise` nothing changes.

Candidate step sets are all Registry-produced (CON-311):

- the supplied target set;
- the channel's working-tree set;
- every historical version of the channel's platform set, taken from
  `git log` and `git show` in the Registry checkout. A historical version
  counts only when its SHA-256 equals the digest that `channel.json` recorded
  at the same commit.

The planner starts from the versions `conditor.json` declares. It then
repeatedly picks the newest candidate that meets all of these conditions:

- it selects every declared component that the target set selects;
- it does not downgrade any of those components;
- it does not pass the final target;
- it advances each component by at most one registered minor line;
- it advances at least one component.

If none qualifies, the plan reports a **gap**. The gap names each component
and the lines it would skip. The planner then takes the candidate with the
fewest skipped lines.

The result has the following parts:

- **Step 1** is checked in full against the repository with the ordinary
  current-upgrade preview. It discloses that preview's plan digest, refusals
  and the exact `--authorize` command.
- **Later steps** each disclose their Registry set (path and SHA-256), the
  versions they move, and the `--check` command to run after the previous
  step has been applied (DF-CON-2026-A003 decision 6).
- **Every step** lists the registered releases it crosses for each moving
  component, with migration-notes links.

A historical set is materialised at
`<home>/.conditor/cache/registry-steps/<sha256>.json`, which is
content-addressed, non-authoritative tooling state, so that it can be passed
to `--resolved-set`. The repository and the Registry checkout are not
written.

Exit codes: 0 when the plan was produced and step 1 has no refusals; 9 when
step 1 is refused; 2 for invalid arguments or unverifiable Registry input.

## Integration point (Praxis `work start`, CI)

Work-start tooling and CI call the advisory as a plain command:

```text
conditor drift --target . --registry "$ECHELON_REGISTRY" --json > drift.json
```

The exit-code contract above is stable:

- **Advisory use**: ignore exit 0, and record exit 2 as "advisory
  unavailable".
- **Gating use**: add `--fail-on warn` or `--fail-on fail` and treat exit 3 as
  the gate.

The Praxis-side requirements are in
`docs/requirements/rolling-upgrade-notes.md` (PRX-RU-001..004). A CI job
needs a checkout of `kemiller2002/echelon-registry`, fetched with
`fetch-depth: 0` so that registration times and historical sets are
available. With a shallow checkout, those fields are reported as unavailable.

## Pin consistency (CON-350..354)

Drift and `upgrade --current` read the same pins (CON-301). They must never
disagree about whether a component is current.

- **Moves:** every version transition also moves the component's
  `.echelon/foundations.json` capability, when one exists. This covers Ordo
  and Praxis (embedded-qualified), repository-lifecycle tools, package
  lifecycle tools, NuGet feeds and web packages. The edit is surgical,
  digest-bound, backed up and post-checked. A missing capability is never
  created.
- **Invariant:** before governance is committed, every pin of every declared
  component must name the version the upgrade leaves declared. Otherwise
  the upgrade fails. Conditor restores the files it edited
  (`foundations.json`, `package.json`, lockfiles) and leaves `conditor.json`,
  the authority and the lock unchanged.
- **Repairs:** `upgrade --current --check` plans a *pin repair* for a declared
  component whose `foundations.json` capability disagrees with
  `conditor.json`. The repair aligns the capability to the declared version
  with no version change. Any other disagreeing pin kind is refused by name.
  When a moving component has a stale capability, the plan refuses it and
  names the repair, which is a check against the recorded authority.
- **Stepwise:** `--stepwise` puts a pending repair first, as step 1 against
  the recorded authority.
- **Drift:** for a declared component, `drift` treats the `conditor.json`
  version as effective, and points a stale pin at the repair.

To repair a half-applied repository, check against its recorded authority:

```text
conditor upgrade --current --check --resolved-set .conditor/authority/resolved-release-set.json \
  --resolved-set-sha256 <registryAuthority.sha256 from conditor.json>
```

Then authorize the printed digest.

## Locks record only tracked files (CON-355)

Ordo's structural review can report gitignored build output, such as a
`bin/` `dotnet.js` (kemiller2002/ordo#75). In a Git work tree, Conditor
records review signals in `.conditor/lock.json` only for paths Git tracks.
After every authorized current upgrade it checks the written lock, so a lock
that names an untracked path fails the upgrade and the previous lock is
restored. To drop a stale entry from an existing lock, re-run the current
upgrade on a clean tree against the recorded authority.
