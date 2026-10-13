# Rolling-upgrade requirement notes for other owners

Status: proposed (for routing by the program lead)
Owner of this note: Conditor
Decision: `DF-CON-2026-A003`
Conditor requirements: CON-300..CON-320 (`requirements/CONDITOR-REQUIREMENTS.md`)

Conditor reports version drift (`conditor drift`) and plans stepwise current
upgrades. Several needs fall outside Conditor's authority. This note records
them as requirements for their owners to accept, reword or reject. Conditor
does not implement them, and does not edit those repositories.

## Praxis (route to kemiller2002/praxis)

- **PRX-RU-001** `praxis work start` SHOULD run the drift advisory for the
  repository when the repository declares one in `ros.json` (for example
  `workProtocol.driftAdvisory.command`). An example value is:

  ```text
  conditor drift --target . --registry <path> --json
  ```

  It SHOULD show the human summary to the agent before work begins.
- **PRX-RU-002** The advisory SHALL NOT block `work start` by default. It
  SHOULD treat Conditor's exit code 3 as blocking only when the repository
  opts in with `--fail-on`, set in the same configuration. Exit code 2 (the
  advisory could not run) SHALL be recorded as "drift advisory unavailable",
  not as "no drift".
- **PRX-RU-003** Praxis SHOULD attach the JSON report
  (`conditor.drift-report/v1`) to the execution as evidence, so that a
  reviewer can see which upgrades were recommended when the work started and
  whether the work took one.
- **PRX-RU-004** When an application repository is being worked on and the
  advisory recommends a next step, Praxis SHOULD offer to capture that step as
  a backlog item, for example "Move forma 0.4.1 → 0.5.0". It SHOULD NOT
  silently add it to the active work item.

## Echelon Registry (route to kemiller2002/echelon-registry)

- **REG-RU-001** Each library's release workflow SHALL open a Registry
  registration pull request automatically when it publishes a stable release.
  The pull request records the release manifest, adds the release to the
  catalog snapshot, regenerates the channel sets and index, and bumps the
  channel's profile version. The release-to-registration gap is what left
  Forma 0.6.1 (released 2026-10-10) invisible to Chrona, Summa and Signal.
- **REG-RU-002** `echelon.release/v2` (or a successor) SHOULD carry:
  - `releasedAt`, the publication time;
  - `migrationNotes`, a URL to the notes for consumers moving to that
    release.

  Conditor already prefers `migrationNotes` when present. Without it,
  Conditor links the GitHub release page. Without `releasedAt`, Conditor
  derives registration time from Registry Git history, which is unavailable
  in a shallow clone.
- **REG-RU-003** The Registry SHOULD keep one channel bump per registered
  release line (no two minor lines of one system in a single bump). It MAY
  instead publish a "stepping-stone" resolved set per registered release
  line. Either way, every intermediate step that stepwise upgrade needs then
  exists as a Registry-produced set, and Conditor never has to report a gap
  or compose a set (`DF-CON-2026-A003` decision 5).
- **REG-RU-004** The Registry MAY declare a `stalenessPolicy` in each channel's
  resolved sets, using the shape Conditor reads (`docs/drift-advisory.md`),
  so that the portfolio's default thresholds are Registry data and not
  Conditor's built-in default.
- **REG-RU-005** A profile's `version.range` lower bound is Conditor's
  "minimum supported" line (CON-318). The Registry SHOULD raise those lower
  bounds deliberately, as releases leave support, and record each change in
  the profile's version history.

## Weekly cross-repository drift report (owner to be decided by the lead)

- **ORG-RU-001** A scheduled job, weekly by default, SHALL check out each
  consumer repository of the portfolio read-only and run:

  ```text
  conditor drift --target <repo> --registry <echelon-registry checkout> --json
  ```

  It SHALL aggregate the reports.
- **ORG-RU-002** The aggregate SHALL list, per component, every consumer and
  its effective pin, the next recommended step, and its level. Repositories
  at `fail` sort first, then `warn`.
- **ORG-RU-003** A repository the job cannot check out or advise (exit 2)
  SHALL appear as "unavailable" with the reason, never be omitted.
- **ORG-RU-004** The job SHALL be read-only. It MAY open or update one issue
  per consumer repository that is at `warn` or above. It SHALL NOT open
  upgrade pull requests on its own.
- **ORG-RU-005** The job SHOULD also report released-but-unregistered library
  versions: the newest GitHub release of each registered system that is
  newer than the Registry's newest registered release. Those versions are
  the REG-RU-001 gap, made visible.
