# Fresh-agent rehearsal

The clean-room CI rehearsal proves Conditor can establish the environment deterministically without model credentials. The fresh-agent rehearsal tests the next boundary: whether a newly launched agent can consume only the initialized repository contract and complete the sacrificial application without architecture coaching.

## Run

From a Conditor source checkout:

```bash
bash scripts/rehearse-fresh-agent.sh /tmp/conditor-agent codex
```

or:

```bash
bash scripts/rehearse-fresh-agent.sh /tmp/conditor-agent claude
```

If an installed `conditor` executable is on `PATH`, the script uses it. Otherwise it runs the local F# CLI.

The target must be empty except for an optional `.git` directory.

## What the script proves

The script deliberately invokes the single bootstrap-and-launch path:

```text
conditor start --preset evidence-triage-rehearsal --launcher <provider>
```

Conditor then:

1. materializes the embedded preset as the target repository's `conditor.json`;
2. installs and verifies the required Echelon lifecycle capabilities;
3. creates the neutral F#/Limen scaffold and explicit application bindings;
4. materializes the immutable rehearsal contract;
5. establishes the Ordo greenfield routing baseline;
6. reconciles shared Praxis metadata and proves strict readiness;
7. creates the deterministic Praxis mission;
8. writes the Conditor lock;
9. re-verifies lock, requirements, contract, components, mission, and provider authentication;
10. activates the Praxis mission; and
11. launches the selected provider.

After the provider returns, the rehearsal requires the live Praxis item to be `complete` and runs repository validation. Provider process exit alone is not success.

## Why this is not ordinary CI

Public CI uses fake Codex and Claude executables to exercise the launch boundary without credentials or model cost. This script is intentionally separate because it performs a real authenticated model run.

A failed rehearsal leaves the target repository intact for inspection.
