# Requirements import

`conditor requirements import` turns the planning documents a preset
materializes into an ordered queue of Praxis slice work items. It also
produces a trace that maps every source requirement to its item. Decision:
`DF-CON-2026-A001`.

```bash
conditor requirements import --target . --check                       # read-only: plan, digest, changes
conditor requirements import --target . --authorize sha256:<digest>    # apply exactly that plan
conditor requirements import --target .                                # apply the current plan
```

## What it does

1. Reads `requirementsImport` from `conditor.json`, the materialized documents
   it names, and the kickoff manifest. It never reads the planning repository
   itself.
2. Extracts every source ID from document headings. Each document declares the
   number of IDs it must yield. Kickoff priority slices, success gates and
   stop-the-line rules become `K-SLICE-<id>`, `K-GATE-<id>` and `K-STOP-NN`.
3. Orders the slices: the kickoff's `prioritySlices`, with each added slice
   directly after its anchor.
4. Assigns every ID to exactly one slice. A rule is one of:
   - an exact ID: `A12`;
   - a section: `P3.*`;
   - a digit suffix: `F*`;
   - a range: `A1..A35` or `D-001..D-015`.
5. Builds one work item per slice:
   - **ID:** `SLICE-<ID>`.
   - **Priority:** from the cut policy. never-cut is high, cut-last is medium,
     stretch and deferred are low.
   - **Dependency:** "Depends on" the nearest earlier never-cut slice, which
     `praxis plan` infers.
   - **Description:** lists the owned IDs as ranges, plus any acceptance
     criteria the optional `acceptance` list adds for that slice from factory
     decisions (for Indy, `DF-CON-2026-A002`).
6. Reconciles with the Praxis queue:
   - It captures and readies missing items.
   - It readies items that are captured but not ready.
   - It leaves items that are started, complete or abandoned alone.
   - It attaches the trace to `COND-MISSION-001` as `requirements-trace.json`.
7. Commits `.ros` alone as one commit.

The trace (`conditor.requirements-trace/v1`) records:
- for every source ID: its document, line, heading and work item;
- every slice item with its order, cut policy and dependency;
- the SHA-256 of every source document;
- the plan digest.

## Guarantees

- **Deterministic.** The same pinned inputs give the same items, trace and
  digest. The digest can therefore be computed before the event and passed to
  `--authorize`.
- **Idempotent.** A second run reports "Already imported" and changes nothing.
  A run interrupted by a failing Praxis call commits what it applied, and
  re-running resumes from there.
- **Fail closed.** Nothing changes if any of these holds:
  - a document yields a different number of IDs, or an ID appears twice;
  - an ID matches no rule or two rules, or a rule matches nothing;
  - a slice ends up empty, or an assignment or anchor names an unknown slice;
  - the specification or the kickoff is malformed;
  - an item exists with different content, or a different trace is attached;
  - the umbrella item is missing;
  - something is staged, or `.ros` has uncommitted changes;
  - the `--authorize` digest differs.

## Why the trace is an attachment

`praxis validate` requires every meaningful repository change to be attributed
to a work item, and the import runs before any work exists.
`.ros/work/attachments/` is Praxis-owned state, written only through
`praxis work attach`. The trace therefore needs neither fabricated attribution
nor a push to the protected `main`. Read it with:

```bash
cat .ros/work/attachments/COND-MISSION-001/*-requirements-trace.json
```
