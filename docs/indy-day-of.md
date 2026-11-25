# Indy Init day-of runbook

On 2026-11-07, 9:30 to 5:00 America/Indiana/Indianapolis, the person runs
three steps and does nothing else. Decisions: `DF-CON-2026-A001`
(requirements import) and `DF-CON-2026-A002` (day-of defaults).

## Before the event

- Install the Conditor release that contains `conditor requirements import`
  and `conditor supervise` (0.6.0 or later).
- Install Praxis 3.26.0, Ordo 1.20.0 and percepta-repo 0.1.0 on the day-of
  host. These are the versions the preset pins.
- Install the kickoff release set's native host tools from the Registry set
  (Indy-init OD-INDY-8, `DF-CON-2026-A007`). Use the approved kickoff version
  of echelon-current for your platform,
  `channels/echelon-current/<platform>.json`, with the sha256 from
  `channel.json`:
  `conditor workstation plan --resolved-set <set> --resolved-set-sha256 <sha>`,
  then `conditor workstation apply ... --authorize <digest>`.
  The kickoff set is echelon-current 1.43.0, once the Registry owner
  approves it after Conditor qualifies it (1.42.0 is the approved set until
  then). Against 1.42.0 it moves Praxis 3.25.0 -> 3.26.0 (the web and hub
  pages are presented with Forma 1.0.1 and the Echelon theme 1.1.0, and
  `praxis foundations verify` recognises `forma-` markup) and Arca 0.7.0 ->
  0.7.1 (rebuilt against Aegis 2.0.2, public API unchanged; Arca is not a
  Conditor component and installs from the release-asset NuGet feed). The
  preset declares Praxis 3.26.0 (Conditor 0.10.30). 1.42.0 moved only Aegis 2.0.1 -> 2.0.2: a detached
  capture no longer waits synchronously for the fault record, which froze
  single-threaded WebAssembly hosts. The preset declares Aegis 2.0.2
  (Conditor 0.10.29). 1.41.0 moved Aegis 2.0.0 -> 2.0.1, a patch with no
  source change: 2.0.0 declares FSharp.Core >= 11.0.101 and fails restore
  on the .NET 10 SDK (NU1605/NU1109), and 2.0.1 pins the floor at 10.1.400.
  The preset declares Aegis 2.0.1 (Conditor 0.10.28); a project on 1.0.0 or
  2.0.0 moves with `conditor upgrade`. 1.40.0 moved Aegis 1.0.0 -> 2.0.0, a major NuGet
  release (no public signature removed; license MIT -> PolyForm
  Noncommercial 1.0.0). The preset declares Aegis 2.0.0 and a scaffold's
  engine project references `EchelonFoundry.Aegis.Core` 2.0.0; Conditor
  0.10.26 qualifies it. From Conditor 0.10.27 an existing project that
  declares Aegis 1.0.0 moves with `conditor upgrade` (change the declared
  version first) or `conditor upgrade --current`: the declaration and every
  exact `EchelonFoundry.Aegis.*` pin move together
  (`docs/nuget-binding-upgrade.md`). Build and test the project afterwards.
  1.39.0 moved the optional Echelon theme 1.0.0 -> 1.1.0, an additive release: the Echelon application layer
  (`dist/echelon-app.css`) and self-hosted fonts (`dist/echelon-fonts.css`);
  `dist/echelon.css` is unchanged and it still requires Forma 1.0.0 or later.
  the preset declares the theme at 1.1.0, and a scaffold on it loads Forma's
  `all.css`, then `echelon.css`, `echelon-fonts.css` and `echelon-app.css`
  (Conditor 0.10.25). 1.38.0 moved Forma 0.14.0 -> 1.0.1, the
  major release that makes Forma brand-neutral and renames its `ef-`
  namespace to `forma-` (EDS-ADR-002, FORMA-NS-001; migrate markup with
  Forma's `tools/NamespaceMigrator`), and added the optional Echelon theme
  (`@echelon-foundry/echelon-theme`, the Echelon brand for Forma 1.x); the
  preset then moved forma-workflow to 2.0.0 (1.2.0 emits `ef-` names).
  Existing Indy projects are not migrated (gradual migration, owner
  2026-11-09): they keep their own `conditor.json` with Forma 0.14.0 and
  forma-workflow 1.2.0. **After installing a new Conditor release, run
  `conditor init` once in an existing project to re-lock it; versions do not
  change.** The lock records each component descriptor's digest, so a release
  that changes a descriptor the project declares makes `start`, `status` and
  `repair` refuse with "Resolved identity for component '<id>' differs from
  the Conditor lock" until then: 0.10.24 changed forma and forma-workflow,
  0.10.25 changed echelon-theme (projects that declare the theme),
  0.10.26, 0.10.28 and 0.10.29 change aegis (projects that declare Aegis), and
  0.10.30 changes praxis (every project). It installs Praxis 3.26.0, which
  presents its web and hub pages with Forma 1.0.1 and the Echelon theme 1.1.0,
  carries the requirements-preservation gate, admits compiled verification
  plans (`praxis inbox admit-plan`), records process evidence in the work
  event log (`praxis work evidence`), adds owner-authorized autonomous
  execution (`praxis autonomy`) and compiles a requirement catalog into a
  deterministic verification manifest (`praxis requirements manifest
  compile` and `check`); Ordo 1.20.0, which orders the admitted
  plan (`sde verification schedule`), binds evidence freshness per policy
  and per obligation acceptance contract, and records opt-in decision events
  (`ordo.decision-event/1`: `ordo ecir recognize --events FILE` or
  `ORDO_DECISION_EVENTS`, and `ordo events record-execution`,
  `record-verification` and `summarize`); and Dokimos 0.11.1, which attested
  verified completion with Praxis 3.17.0 and later needs, which adds evidence
  reuse (`dokimos evidence plan`), whose release checkpoint refuses reuse
  (`--checkpoint release`, mandatory final verification), and which turns
  the frozen verification plan into test-writing work and reports which
  obligations are verified for a candidate SHA (`dokimos verification
  compile`, `scaffold`, `coverage` and `probe`), and which judges every
  obligation of a verification manifest from browser observations and
  executed test results (`dokimos verification requirements`, which matches
  the url-input component to an `<input>` of type url, text or no type). It
  also
  installs the
  Percepta semantic CLI 0.4.0 (`percepta`), which produces the UI completion
  evidence, checks a served application (`percepta verify-app`) and finds
  contradictory and incomplete specifications before implementation
  (`percepta spec-preflight`, `percepta.specification-preflight/1`). Then run
  `percepta install-browser`.
  The semantic CLI is not a `conditor.json` component: the Registry set pins
  it.
- Authenticate `gh` with the rights to create repositories, and Claude Code
  (`claude auth status`).
- Set `CONDITOR_GITHUB_TOKEN` to a token that can read
  `kemiller2002/Indy-init`.
- Create an AI provider key with a spend cap and keep it ready to paste into
  the app (RQR-003). Neither exists yet.

## The three steps

For a Causa rehearsal, complete the workspace preflight before the timed start:

```bash
conditor rehearsal init --target . --manifest ./conditor.json --json
```

This is preparation evidence only. A READY receipt does not activate the Praxis mission, launch an agent, or start the competition clock. The person must make the event's explicit timed-execution transition after reviewing the receipt.

```bash
# 1. Create the repository: initialize, publish, protect, enable GitHub Pages
mkdir ~/indy-app && cd ~/indy-app && git init -b main \
  && conditor init --preset indy-init --target . \
  && conditor repo create --repository kemiller2002/<NAME> --target . --deploy github-pages

# 2. Import the requirements: 23 slice work items, 992 source requirements traced
conditor requirements import --target . --authorize sha256:5d65d10e494465dbc038fb991ff0dd5f82bca7cb92eabc7756815112748e8009

# 3. Start the agent and keep it working until 4:40 PM
conditor supervise --target . --until 2026-11-07T16:40:00-05:00
```

The import digest is fixed by the pinned inputs: Indy-init `2eee2d5` (which
adds the deep-linking requirements R9.6-R9.10, P34.15-P34.20 and OQ-048) and
the `requirementsImport` section of the preset. If the plan differs, step 2
refuses and changes nothing. To stop the agent after its current session,
touch the stop file the supervisor prints.

## What R0 and R2 must prove

- **Permission mode (R0).** `conditor supervise` launches Claude Code with
  `--permission-mode auto`, and the tooling has no other mode built in. R0
  must prove that `auto`, together with the scaffold's
  `.claude/settings.json`, lets the agent open, push and merge its own pull
  requests once their required checks are green. Changing the mode is the
  person's call at R0, made with `--permission-mode` on the supervise
  command.
- **GitHub Pages (R2).** Pages on a private repository needs a paid GitHub
  plan. R2 must prove that `--deploy github-pages` succeeds on this account.
  If it does not, decide between a public repository (`--public`) and
  enabling Pages later.
- **Persistence (R2 to R5).** The investigation data repository and its
  fine-grained token (RQR-004) are created by the person, not by Conditor.
  The app works local-only until they exist.
