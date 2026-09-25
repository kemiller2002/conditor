# Remote / iPad bootstrap

Conditor can establish a new repository from GitHub Actions so the operator does not need a local .NET SDK, Node installation, shell, or laptop.

The first remote implementation is intentionally narrow:

- the target repository must be under the same GitHub owner as the Conditor repository running the workflow;
- the target repository must exist and have no Git refs yet;
- authentication is a GitHub App installation token, not a personal access token;
- the workflow initializes, verifies, commits, and pushes the repository;
- model execution is a separate boundary and is not performed by this workflow.

## GitHub App

Create a GitHub App for Conditor remote bootstrap.

Required repository permission:

- **Contents: Read and write**

Install the app on the repositories that the workflow must access. For the Indy Init preset this means at least:

- the new empty target repository; and
- the private `Indy-init` repository that supplies pinned governing artifacts.

Public pinned source repositories do not need App access.

In the `conditor` repository configure:

- repository variable `CONDITOR_APP_CLIENT_ID`: the App client ID;
- repository secret `CONDITOR_APP_PRIVATE_KEY`: the App private key.

Do not put the private key or an installation token in `conditor.json`, a preset, the target repository, or a committed workflow input.

## iPad flow

From GitHub in a browser:

1. Create the target repository and leave it empty.
2. Open the `conditor` repository.
3. Open **Actions**.
4. Select **remote-bootstrap**.
5. Choose **Run workflow**.
6. Enter the empty target repository name.
7. Choose a built-in preset, such as `indy-init`.
8. Leave the initial branch as `main` unless the repository needs another branch.
9. Run the workflow.

The workflow:

```text
GitHub App installation token
        |
        v
prove target has no refs
        |
        v
build self-contained Conditor on GitHub runner
        |
        v
git init temporary target
        |
        v
conditor init --preset <preset>
        |
        v
conditor verify
        |
        v
conditor status --json  -> healthy required
        |
        v
commit as GitHub App bot
        |
        v
push first governed branch to target repository
```

## Private pinned sources

The App installation token is passed to Conditor as `CONDITOR_GITHUB_TOKEN` only for the bootstrap process. Conditor converts it to an in-memory Git HTTP authorization header when fetching a pinned private source.

The token is not written to:

- `conditor.json`;
- `.conditor/lock.json`;
- generated project files; or
- Conditor command diagnostics.

## Fail-closed behavior

Remote bootstrap stops without pushing when:

- the target repository name or branch is invalid;
- the target repository does not exist;
- the target repository already contains Git refs;
- the GitHub App cannot access required private sources;
- Conditor initialization fails;
- component verification fails;
- requirements do not match their immutable sources; or
- `conditor status --json` is not healthy.

The workflow does not attempt to merge into or reconcile an existing repository.

## Next remote boundary

Remote **agent execution** is deliberately separate. It needs an explicit policy for Codex/Claude credentials, provider version pinning, execution telemetry, token lifetime, and how agent commits are pushed back. The repository bootstrap App must not become an implicit container for model credentials.
