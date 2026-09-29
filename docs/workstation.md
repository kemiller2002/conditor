# Workstation bootstrap, receipts, uninstall and registration

Conditor establishes an Echelon engineering host. It installs, upgrades and
uninstalls; Praxis stays the runtime authority for work after handoff, and
Project Administration records what is installed where.

```
conditor workstation plan      [--profile echelon-engineering|minimal|FILE] [--with OPTIONAL]* [--home DIR] [--json]
conditor workstation apply     --authorize PLAN-DIGEST [--profile ...] [--praxis PATH] [--target-id ID] [--no-rollback]
conditor workstation status    [--home DIR]
conditor workstation reconcile --step ID [--profile ...]
conditor uninstall --plan
conditor uninstall --authorize PLAN-DIGEST [--praxis PATH] [--target-id ID]
```

## Profiles (CON-160..166, CON-250..254)

Versioned, declarative, composable JSON (`profiles/*.profile.json`, schema
`conditor.workstation-profile/v1`), embedded in the binary:

- **`minimal`**: host readiness only (Git ≥ 2.30 required, GitHub CLI optional). Installs nothing.
- **`echelon-engineering`**: extends `minimal`. Installs Praxis 3.6.0 and Ordo 1.4.0
  from their GitHub native releases, pinned per runtime identifier by sha256.
  Optional capabilities are listed (Praxis control plane, Forma, Limen,
  Tutela). They are installed only when selected with `--with`, and this
  profile version refuses them explicitly rather than half-installing them.
  Agent providers are selectable, and Conditor never installs them.
- **Project composition**: any file with `"extends": "echelon-engineering"`
  that adds or overrides prerequisites and components by id.

## Plan before authorization (CON-180..186)

`plan` is a dry run that never mutates the host. It discovers
prerequisites and classifies each one: satisfied, installable, external-only,
unsupported or unknown. GitHub CLI authentication is checked separately and
never persisted. Every effect is disclosed as a step:

| Field | Values |
|---|---|
| operation | `CREATE`, `MODIFY`, `DOWNLOAD`, `ADOPT/REUSE`, `EXTERNAL TRUST` |
| resource | shown relative to the home (`~/...`), so no absolute path leaks |
| artifact | release URL + pinned `sha256:` digest; trust class `integrity-verified` |
| expected receipt | a typed postcondition (file digest, directory contents, `praxis --version` reports 3.6.0, one managed profile block) |
| ownership | `conditor-created`, `adopted`, `shared`, `external`, `other-echelon`, `user-owned` |

The plan has a digest. `apply --authorize <digest>` runs exactly those
effects. Any other digest is refused, because authorization is scoped to the
disclosed plan.

## Receipts, ledger and resume (CON-140..146, CON-190..198)

Each step writes append-only entries to `~/.conditor/workstation/ledger.jsonl`:
`started`, `observed` (with outcome `match`, `mismatch` or `indeterminate`),
`reconciled`, `ownership`, `rolled-back`, `removed` and `registration`.

- A step whose receipt already matches is **reused**. A second bootstrap
  changes nothing, and the PATH block is never duplicated.
- A mismatch stops the dependent steps.
- A step that was started but never observed has an **unknown effect**.
  `apply` refuses to retry it until `workstation reconcile --step ID`
  observes whether it occurred, did not occur, or is still unknown.
  Reconciliation never assumes. Digest-verified downloads are the one
  exception, since they are safe to retry.
- An existing resource that Conditor did not create is never overwritten.
  If it already satisfies the receipt it is **adopted**, and adoption carries
  no uninstall authority. If it does not, the step fails.

## Partial-failure rollback (CON-220..225)

On failure, Conditor reverses the Conditor-created effects of this run in
reverse order, using the ledger:

- Each reversal has its own receipt.
- An indeterminate effect is retained for reconciliation instead of being
  guessed away.
- User-owned and adopted resources are never touched.

`--no-rollback` keeps the partial state so it can be inspected.

## Uninstall (CON-210..217)

`uninstall --plan` is derived only from recorded ownership. Its actions are
`remove`, `restore` (the shell-profile managed block), `retain-shared`,
`retain-adopted`, `retain-user` and `unresolved`. A shim that something else
replaced is `unresolved`, and any unresolved action blocks all removal.
Source repositories and user work are never in scope. `uninstall --authorize
<digest>` removes resources surgically and records a removal receipt for each.

## Installation registration

After a component's install receipt matches, Conditor runs:

```
praxis installation register --system <id> --version <v> --target-kind environment --target-id <id> \
  --source-repository <repo> --distribution github-release --release <tag> \
  --artifact <asset> --digest sha256:<...> --evidence receipt=conditor-workstation-ledger#<step>
```

This uses `--praxis PATH` or `CONDITOR_PRAXIS`. Each component is
registered separately against the same target.

- A component whose receipt is a mismatch or indeterminate is never
  registered.
- After uninstall receipts match, Conditor runs `praxis installation remove`.
- For repository installs, a successful `conditor init` registers each
  resolved component against the repository (`CONDITOR_PRAXIS`).
- The Conditor lock stays local installation evidence. It is not the central
  inventory.
- Registration is optional. When it is unavailable, that outcome is recorded
  and the bootstrap still succeeds.

## Version reconciliation (evidence, 2026-09-29)

| System | npm (lifecycle channel) | GitHub native release | Used where |
|---|---|---|---|
| Praxis | `@echelon-foundry/repository-operating-system` latest **3.1.4** | **v3.6.0**, including `native-checksums.txt` | repository lifecycle components: 3.1.4 (npm); workstation profile: 3.6.0 (native) |
| Ordo | `@echelon-foundry/sde` latest **1.3.0** | **v1.4.0**, including `native-checksums.txt` | repository lifecycle components: 1.3.0 (npm); workstation profile: 1.4.0 (native) |

The repository component descriptors (`components/*.component.json`) use
the `lifecycle-npm` distribution. Their pins of 3.1.4 and 1.3.0 **are** the
newest versions npm can install (`npm view … dist-tags`), so they were not
stale for that channel and are kept. The newer releases are published only
as native GitHub releases. The workstation profile installs those, verifies
the published sha256, and probes the version:
- `praxis --version` → `ros-fs 3.6.0`
- `ordo --version` → `1.4.0`

Moving repository lifecycle pins beyond npm requires the npm publication
gap to be closed, or a native lifecycle distribution for repository
components. Both are recorded as obligations.
