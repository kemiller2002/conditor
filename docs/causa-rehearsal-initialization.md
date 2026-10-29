# Causa rehearsal workspace initialization

## Contract

Conditor extends its existing schema-version-1 `conditor.json` with an optional `rehearsal` declaration. There is no second configuration system. A complete example is `examples/causa-rehearsal.conditor.json`:

```json
{
  "schemaVersion": 1,
  "name": "indy-init-rehearsal-006",
  "components": [],
  "rehearsal": {
    "id": "006",
    "application": "causa",
    "receipt": ".conditor/evidence/rehearsal-006-readiness.json",
    "workspace": {
      "repository": "kemiller2002/causa-data",
      "branch": "main",
      "template": "historical-v1",
      "name": "rehearsal-006",
      "checkout": "../causa-data",
      "toolProject": "tools/src/Causa.Tool/Causa.Tool.fsproj",
      "dotnetExecutable": "dotnet",
      "create": true,
      "publish": true,
      "verify": true
    }
  }
}
```

The workspace name must equal the normalized rehearsal identifier (`006` becomes `rehearsal-006`). Creation, publication, and verification are mandatory; setting any to false is invalid. `checkout` identifies the Causa Data working copy containing the selected template and tool. Git authentication remains in the user's standard credential helper. Tokens are not configuration values and never appear in a receipt.

`template` may select `historical-v1` (Apollo 1, Apollo 11, and Challenger) or `demonstration-v1` (the same three immutable histories plus the everyday burned-out-lightbulb investigation). Conditor does not encode either count; it binds the non-empty, unique identifier set returned by Causa template verification and requires every later stage to return that same set and digest. `everyday-v1` remains available to direct Causa tooling as a one-case development template.

`toolProject` and `receipt` must be canonical relative paths: no rooted paths, empty segments, `.` or `..`, or backslashes. Their resolved files remain inside the selected checkout and target respectively.

## Command and ordered gates

```bash
conditor rehearsal init --target . --manifest ./conditor.json --json
```

Conditor calls the component-owned Causa F# tool and performs these gates in order:

1. Resolve the single rehearsal declaration and prove the checkout, tool project, and .NET runtime exist.
2. Run `template-verify` and bind its identifier, version, digest, source repository/commit, and exact non-empty investigation identifier set.
3. Run `workspace-local-check` and `workspace-remote-check` for the exact selected repository, branch, and name.
4. Run `workspace-create`.
5. Run initialized `workspace-verify` and require its identifiers and template digest to match the receipt under construction.
6. Run `workspace-publish`, which makes one non-forced commit containing only the workspace prefix.
7. Run `workspace-remote-verify` against the reported commit and intended branch, reading every path and byte back and matching the template digest and identifiers.
8. Run `workspace-application-verify`, which uses the Causa provider resolver and loads/replays every investigation selected by the template.
9. Atomically write the readiness receipt.
10. Return READY only after every preceding evidence document passes its identity checks.

Conditor deliberately does not copy, hash, replay, commit, or resolve investigation files itself. Those rules remain in Causa Data. The process runner passes arguments as an argument list rather than a shell string, and provider output is not persisted on failure.

## Receipt and states

The default receipt is `.conditor/evidence/rehearsal-<id>-readiness.json`, validated by `schemas/conditor-causa-rehearsal-readiness.schema.json`. It records the rehearsal, template version/digest, source identity, destination repository/branch, workspace, published commit, investigation identifiers/count, all validation results, timestamp, evidence for each gate, and overall verdict.

The evidence-backed transitions are:

```text
LOCAL_CREATED -> LOCAL_VERIFIED -> PUBLISHED -> REMOTE_VERIFIED
              -> APPLICATION_VERIFIED -> READY
```

Any failing gate records `BLOCKED` with the failing stage and its permitted recovery action. A receipt never infers a later state from an earlier one. In particular, local creation is not publication, and publication is not remote or application verification.

## Failure behavior

The command exits 3 and refuses readiness when configuration or evidence is missing, a template is missing/invalid, either destination exists, creation or validation fails, publication fails, remote bytes or commit do not match, or the application resolver cannot load the selected investigations. It does not choose another name, reuse a previous rehearsal, create an empty replacement, overwrite a destination, or report partial work as success.

Recovery is stage-specific and printed in the receipt. Diagnose the selected workspace and fix the underlying Causa/Git condition. When a local or remote destination already exists, select a new explicit rehearsal identifier; do not delete or overwrite it merely to make the preflight pass.

## Timing boundary

Workspace initialization is preparation. `conditor rehearsal init` never invokes `conditor start`, `conditor supervise`, Praxis activation, or any application-building agent. The event's timed execution begins only through its separate, explicit transition after the readiness receipt has been reviewed.
