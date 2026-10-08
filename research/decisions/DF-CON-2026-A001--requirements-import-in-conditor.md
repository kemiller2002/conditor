---
id: DF-CON-2026-A001
title: Conditor owns the one-command requirements import; Praxis keeps the queue and holds the trace as a work-item attachment
status: accepted
version: 1.0.0
owners:
  - conditor
created: 2026-10-08
updated: 2026-10-08
research_area: factory-bootstrap
decision_type: architecture
related_documents:
  - docs/requirements-import.md
  - examples/indy-init.conditor.json
tags: [decision, requirements, praxis, indy-init]
confidence: high
provenance:
  contributions:
    EXE-20261008T113449054Z-2ff9c0f5:
      operations: [created]
      at: 2026-10-08T11:34:49.360Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Record where the one-command requirements import lives and why"
---

# Context

On the Indy Init build day (2026-11-07) the user runs Conditor to set up the
repository, runs **one** command that turns the pinned Indy-init planning
documents into Praxis work items, and starts an agent. Before this change no
such command existed: an agent received a single mission (`COND-MISSION-001`)
for the whole application and had to decompose about 950 requirements itself.

Constraints:

- The source IDs (`P3.4`, `R2.5`, `A12`, `D-001`, `OQ-001`, `I-01`, `S01`,
  `F3`) must be preserved, but Praxis work-item IDs must match
  `^[A-Z][A-Z0-9_-]*-[A-Z0-9][A-Z0-9_-]*$`, which rejects most of them.
  950 work items would also be unusable as a queue.
- Deterministic and idempotent: a re-run creates nothing. Malformed input fails
  closed.
- The preset pins Praxis 3.7.2 (echelon-current). Its CLI has `work capture`,
  `backlog-transition`, `attach` and inferred dependencies ("Depends on ID"),
  but no batch or import command.
- `praxis validate` treats every repository path outside `.ros/work/**` (and
  the other ignored Praxis paths) as a meaningful change that must be
  attributed to a work item. The import runs before any agent work exists.
- The repository's `main` is protected (pull requests and required checks,
  administrators included) by the time the import runs.

# Decision

1. **The command lives in Conditor:** `conditor requirements import`.
   Conditor already pins and materializes the planning documents (verified by
   `conditor status`) and already creates the umbrella mission through the
   Praxis CLI, with the same plan, authorize and idempotence pattern. The
   import is a pure function of those pinned inputs.
2. **The mapping is declarative data in the preset.** The `requirementsImport`
   section of `conditor.json` names, for each source document, the heading
   pattern and the expected count. It also lists the slices added to the
   kickoff's own priority slices, each with its anchor, cut policy, goal and
   basis, and the assignment rules (exact IDs, `P3.*` sections, `F*` digit
   suffixes, `A1..A35` ranges). The kickoff's `prioritySlices` remain the
   canonical order.
3. **Slices, not requirements, become work items.** Indy gets 23 slice items,
   `SLICE-<ID>`, from the kickoff's 12 plus 11 added from IMPLEMENTATION-SLICES
   and the execution packet. Priority follows the cut policy (never-cut high,
   cut-last medium, stretch and deferred low). Each item depends on the nearest
   earlier never-cut slice through the phrase Praxis infers ("Depends on ID").
   Its description lists every source ID it owns as compressed ranges.
4. **The trace is a Praxis attachment.** The full trace (all 980 IDs, with
   document, line, heading and work item, the SHA-256 of every source and the
   plan digest) is attached with `praxis work attach` to `COND-MISSION-001` as
   `requirements-trace.json`. It lives in `.ros/work/attachments/`, which
   Praxis owns and does not count as meaningful change. The import therefore
   needs no fabricated attribution and no push to protected `main`. It commits
   only `.ros` locally, as one commit, and the agent publishes it through its
   first pull request.
5. **Pure core, thin shell.** `RequirementsImport` parses the specification,
   extracts IDs, orders slices, assigns, renders the trace, computes the digest
   and reconciles against the observed queue. It reads no files and runs
   nothing. `RequirementsImportRun` reads the files, runs Praxis and Git
   through an injected runner, stops at the first failure, and leaves a
   re-runnable state.
6. **Fail closed** on:
   - a count mismatch, a repeated ID, an ID no rule matches, an ID two rules
     match, a rule matching nothing, an unknown slice or anchor, an empty
     slice, an invalid pattern or field, or an invalid kickoff;
   - an existing item with different content, or a different trace already
     attached;
   - a missing umbrella, staged changes, or uncommitted `.ros` state;
   - an `--authorize` digest that differs from the plan.

# Alternatives considered

- **`praxis work import --plan FILE` in Praxis.** It is the cleanest home for
  a transactional batch capture. It would need a Praxis release, a Registry
  echelon-current update and Conditor requalification before the event, and
  Praxis would still need someone to derive the plan. It is worth revisiting
  as the batch primitive under this command.
- **Trace as `requirements/trace.json` in the repository.** It is more
  visible, but it is a meaningful change. Attributing it would need a work
  item begun and completed, with a durable checkpoint, during the import, or
  attribution to the agent's bootstrap slice for work the agent did not do.
  Either one fabricates or forces a push to protected `main`. The agent can
  publish the attachment content into the Factory View, where judges see it.
- **One work item per requirement.** 980 items are not a queue, and most IDs
  are not valid work-item IDs.
- **Mapping in Indy-init.** The planning repository is read-only input. The
  mapping is a factory decision, not a planning one.

# Consequences

- The day-of command is `conditor requirements import --target .
  --authorize sha256:<digest>`. The digest is known in advance because the
  inputs are pinned, so the run refuses any drift.
- Ordering decisions the planning set left open (RQR-007) are now explicit in
  the preset. The added slices are not never-cut. The kickoff's never-cut set
  stays authoritative. Moving `security-hardening` or `demo-readiness` to
  never-cut, or making multi-agent the second signature feature, is a one-line
  preset change that also changes the digest.
- Any Indy-init change after the pin changes the document hashes and therefore
  the digest. The expected counts make a structural change fail loudly instead
  of importing a partial set.
