# Remote Codex execution

Conditor remote execution is separate from remote bootstrap.

The bootstrap workflow establishes a governed repository and pushes its first commit. The Codex execution workflow operates only on an already-established repository and requires the Praxis mission to be in the state appropriate for the selected mode.

Workflow:

```text
.github/workflows/remote-execute-codex.yml
```

## Authentication model

Remote Codex execution uses two independent workload identities:

1. A GitHub App installation token provides repository access and read access to private pinned governing sources.
2. GitHub Actions OIDC provides the upstream identity for OpenAI Codex workload identity federation.

The workflow does not require a long-lived OpenAI API key.

Codex workload identity federation currently requires a managed ChatGPT workspace with workload identity enabled. Configure the GitHub Actions OIDC provider and a federation rule in the OpenAI Admin Portal before running the workflow.

## Conditor repository configuration

Configure these repository variables:

- `CONDITOR_APP_CLIENT_ID`: GitHub App client ID used by bootstrap and execution.
- `CONDITOR_CODEX_VERSION`: exact Codex CLI version, for example a reviewed version number rather than `latest`.
- `OPENAI_WIF_AUDIENCE`: the exact audience configured for the OpenAI workload identity provider.
- `OPENAI_FEDERATION_RULE_ID`: the non-secret `idpm_...` federation rule ID downloaded/configured for Codex.

Configure this repository secret:

- `CONDITOR_APP_PRIVATE_KEY`: GitHub App private key.

The GitHub App must be installed on:

- the target repository; and
- every private repository referenced by the target's pinned requirements.

## OpenAI federation rule

Use GitHub Actions as the OIDC issuer:

```text
https://token.actions.githubusercontent.com
```

Restrict the rule to the Conditor repository and the exact remote execution workflow/ref you intend to trust. Prefer exact repository/ref/workflow claims rather than owner-wide trust.

The Codex process receives:

```text
OPENAI_FEDERATION_RULE_ID
OPENAI_IDENTITY_TOKEN_FILE
OPENAI_WORKLOAD_IDENTITY_CONTEXT
```

The identity-token file lives under `$RUNNER_TEMP`, outside the target repository.

Conditor intentionally does not call `codex login status` when workload-identity variables are present. With assertion replay protection enabled, an authentication check can consume the OIDC assertion before the actual `codex exec` process. The workflow validates the token file itself and lets the execution process perform the exchange.

The workflow refreshes the GitHub OIDC token file every three minutes so a long-running Codex process can obtain fresh OpenAI access tokens when necessary.

## Run from iPad/browser

In the Conditor repository:

1. Open **Actions**.
2. Select **remote-execute-codex**.
3. Choose **Run workflow**.
4. Enter the established target repository name.
5. Select the target branch.
6. Choose:
   - `start` for a ready Praxis mission; or
   - `resume` for an already-active Praxis mission.
7. Run the workflow.

The workflow clones the target, verifies Conditor status, launches through the Conditor command, validates Praxis afterward, commits any resulting repository state using the GitHub App bot identity, and pushes the branch.

## State semantics

`start` is allowed to perform the Praxis ready-to-active transition.

`resume` requires the mission to already be active and never performs that transition.

Provider process success is not project completion. The workflow records and pushes whatever Praxis state remains after the provider exits.

## Security properties

- No OpenAI credential is committed to the repository.
- The GitHub OIDC token is stored only in an ephemeral runner path with restrictive permissions.
- The token file is continuously refreshed and removed when the execution step exits.
- The Codex CLI version is explicit, not `latest`.
- The GitHub App token is process-scoped and never written into Conditor lock state.
- The OpenAI federation rule ID and WIF audience are identifiers, not bearer credentials.
- Repository writes use the GitHub App bot identity.

## Agent identity

The workflow needs no identity variables of its own. Conditor starts `codex` with `ROS_ACTOR_KIND=agent`, `ROS_TELEMETRY_PROVIDER=openai`, and `ROS_TELEMETRY_RUNTIME=codex`, plus `ROS_TELEMETRY_MODEL` only when `execution.model` is configured. First it removes every identity variable Praxis discovery reads: `ROS_*`, runtime session ids, `GITHUB_ACTIONS`/`GITHUB_RUN_ID` and `OLLAMA_HOST`, per the Praxis identity-environment list. The agent therefore begins its own Praxis execution and is never recorded as Conditor, as the operator's session, or as the GitHub Actions run. Conditor's own `ros` calls declare `ROS_ACTOR_KIND=automation` and `ROS_ACTOR=conditor` (CON-130, CON-131). None of these values are credentials.
