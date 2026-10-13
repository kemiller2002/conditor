# Workstation bootstrap, receipts, uninstall and registration

Conditor establishes an Echelon engineering host. It installs, upgrades and
uninstalls; Praxis stays the runtime authority for work after handoff, and
Project Administration records what is installed where.

```
conditor workstation plan      [--profile echelon-engineering|minimal|FILE] --resolved-set SET --resolved-set-sha256 SHA [--allow-downgrade] [--with OPTIONAL]* [--home DIR] [--json]
conditor workstation apply     --authorize PLAN-DIGEST [--profile ...] --resolved-set SET --resolved-set-sha256 SHA [--allow-downgrade] [--praxis PATH] [--target-id ID] [--no-rollback]
conditor workstation register  [--authorize DIGEST] [--profile ...] --resolved-set SET --resolved-set-sha256 SHA [--praxis PATH] [--target-id ID] [--home DIR]
conditor workstation status    [--home DIR]
conditor workstation reconcile --step ID [--profile ...]
conditor uninstall --plan
conditor uninstall --authorize PLAN-DIGEST [--praxis PATH] [--target-id ID]
```

## Host-tool versions come from the Registry (CON-360..363, DF-CON-2026-A005)

A profile supplies *which* host tools, prerequisites and optional
capabilities; a digest-verified Registry resolved set supplies *which
versions*. For the shared workstation, for example:

```
conditor workstation plan --profile echelon-engineering \
  --resolved-set echelon-registry/channels/echelon-current/linux-x64.json \
  --resolved-set-sha256 <that platform's sha256 from channel.json>
```

- **No set:** a profile that names host tools is refused without a set. A
  profile without host tools (`minimal`) still plans.
- **Tool missing from the set:** the plan is refused, naming the tool. The
  plan digest binds the set's identity.
- **Set alone:** `--resolved-set` alone, with no `--profile`, installs the
  set's whole native environment, as before.
- **Downgrades:** a plan that would install a host tool *below* the version
  already installed is refused. The refusal names the tool and both
  versions. `--allow-downgrade` lifts the refusal explicitly, and the plan
  then prints each `DOWNGRADE (explicitly allowed)`.
  - **Installed version (any home):** the highest of these, all under the
    workstation home:
    - the Conditor install directories (`~/.local/share/echelon/<tool>/<version>`);
    - the `~/.local/bin` shim;
    - the Echelon native installer's `~/.echelon/bin`.
  - **Real home only:** the executable that `PATH` resolves is included too.
    A sacrificial `--home` is never influenced by the host's `PATH`.
  - **Reading a version:** a JSON identity (the repository-lifecycle
    `version` contract) is read through `ReleaseVersion` only. Its contract
    and schema versions never count. A tool already at the target version is
    a zero-effect no-op.

## Profiles (CON-160..166, CON-250..254)

Versioned, declarative, composable JSON (`profiles/*.profile.json`, schema
`conditor.workstation-profile/v1`), embedded in the binary:

- **`minimal`**: host readiness only (Git ≥ 2.30 required, GitHub CLI optional). Installs nothing.
- **`echelon-engineering`**: extends `minimal`. Its host tools are Praxis
  and Ordo. **Their versions come from the Registry set given with
  `--resolved-set`/`--resolved-set-sha256`, never from the profile.** The
  file still carries the Indy Init freeze pins (Praxis 3.6.0, Ordo 1.4.0),
  but Conditor does not use them to select anything (DF-CON-2026-A005).
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

Each component is registered separately against the same target.

### Which Praxis registers (CON-365, DF-CON-2026-A006)

1. `--praxis PATH` or `CONDITOR_PRAXIS`, when given, always wins.
2. Otherwise Conditor uses the workstation's own `~/.local/bin/praxis`, but
   only when all of these hold:
   - the ledger records that Conditor created it, for component `praxis`,
     and its install receipt matched;
   - it is still exactly the shim Conditor writes, pointing at a
     Conditor-created `praxis/<version>` install directory;
   - it reports that version now.
3. Otherwise registration is `unavailable`, and the reason names what was
   missing. A Praxis found only on `PATH` is never used.

### Outcomes and reasons (CON-364)

Every outcome is printed and recorded in the ledger with its reason:
`registration <id>: <outcome> (<reason>)`.

- **Praxis's own outcomes** pass through with its code and message:
  `recorded`, `replayed`, `unchanged`, `submitted`, `refused`, `invalid`,
  `unavailable` and `misconfigured`.
- **Conditor's own outcomes** follow the Registry's integration vocabulary:
  - `unavailable`: no Praxis is configured;
  - `misconfigured`: a configured Praxis could not be run, or returned no
    installation outcome. The reason gives the executable, its exit code and
    what it printed.

### Backfill (CON-366)

Installs whose registration never succeeded are repaired with a planned,
authorized command:

```
conditor workstation register --profile echelon-engineering --resolved-set SET --resolved-set-sha256 SHA
conditor workstation register --authorize <digest> --profile echelon-engineering --resolved-set SET --resolved-set-sha256 SHA
```

- **The plan** lists the registrar and each proposed registration, with its
  evidence receipt and last outcome. Everything else is skipped, with the
  reason. It writes nothing.
- **What it proposes:** only components the ledger shows Conditor installed,
  whose shim reports the selected version now, and whose registration of
  that version never succeeded.
- **The digest** binds the registrar, the target, the Registry selection and
  each registration.
- **Refusals:** a plan with work to do but no registrar is refused as
  `unavailable`.

- A component whose receipt is a mismatch or indeterminate is never
  registered.
- After uninstall receipts match, Conditor runs `praxis installation remove`.
- For repository installs, a successful `conditor init` registers each
  resolved component against the repository (`CONDITOR_PRAXIS`).
- The Conditor lock stays local installation evidence. It is not the central
  inventory.
- Registration is optional. When it is `unavailable` or `misconfigured`,
  that outcome and its reason are recorded and the bootstrap still succeeds.

## Generic repository lifecycle (CON-DIST-036..038)

Registry decides **what** is installed; the component decides **how** it
changes a repository. A release that declares Echelon Registry's
`echelon.repository-lifecycle` v1 capability resolves with a
`repositoryLifecycle` field, and Conditor then needs no code for that system:

```
conditor workstation plan  --resolved-set SET --resolved-set-sha256 SHA [--home DIR]
conditor workstation apply --authorize PLAN-DIGEST --resolved-set SET --resolved-set-sha256 SHA
conditor lifecycle plan  --operation init|status|verify|doctor|upgrade --root REPO --resolved-set SET --resolved-set-sha256 SHA [--json]
conditor lifecycle apply --authorize LIFECYCLE-DIGEST --operation ... --root REPO --resolved-set SET --resolved-set-sha256 SHA [--json]
```

- Installation verifies the selected artifact SHA-256 and the installed
  executable's full contract identity (`<executable> version` reports the
  Registry `systemId`, `repository`, `executable`, `releaseVersion` and
  `sourceCommit`).
- The lifecycle plan lists, for every `repository-lifecycle` component, only
  the contract invocations `<executable> <operation> --root <repo>`; `init`
  and `upgrade` are followed by `verify`. The executable name, version and
  path come from the resolved set.
- The lifecycle digest binds the resolved-set identity, platform, repository
  root, preconditions and invocations. `apply` revalidates the cached artifact
  digest and executable identity, runs exactly the disclosed invocations,
  stops at the first non-zero exit, and appends `lifecycle` ledger entries
  (exit code and output digest).
- A `repository-lifecycle` component whose release declares no contract, or
  declares an unsupported version, is refused rather than guessed.
- Conditor never reads or writes the component's repository state.

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
