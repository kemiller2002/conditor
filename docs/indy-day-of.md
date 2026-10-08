# Indy Init day-of runbook

On 2026-11-07, 9:30 to 5:00 America/Indiana/Indianapolis, the person runs
three steps and does nothing else. Decisions: `DF-CON-2026-A001`
(requirements import) and `DF-CON-2026-A002` (day-of defaults).

## Before the event

- Install the Conditor release that contains `conditor requirements import`
  and `conditor supervise` (0.6.0 or later).
- Install Praxis 3.7.2, Ordo 1.5.0 and percepta-repo 0.1.0, the
  echelon-current selection the preset pins, on the day-of host.
- Authenticate `gh` with the rights to create repositories, and Claude Code
  (`claude auth status`).
- Set `CONDITOR_GITHUB_TOKEN` to a token that can read
  `kemiller2002/Indy-init`.
- Create an AI provider key with a spend cap and keep it ready to paste into
  the app (RQR-003). Neither exists yet.

## The three steps

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
