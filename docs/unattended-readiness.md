# Unattended readiness

`conditor unattended-readiness` answers one question: **can this repository complete the authorized development lifecycle without the owner?** It answers it by doing that lifecycle once, for real, on GitHub.

Conditor decides no authorization itself. Every ALLOW, DENY and merge is made by Praxis (`praxis autonomy`). The contracts, the architecture and the recorded results are owned by Praxis, in `kemiller2002/praxis` `docs/autonomous-execution/`:

- EXECUTION-POLICY.md
- ARCHITECTURE.md
- INTEGRATION-TEST-RESULTS.md

```bash
conditor unattended-readiness --repository OWNER/REPO --base BRANCH \
  --authorization AUTHORIZATION.json --trust-store /outside/any/repo/trust.json \
  --journal RUN-JOURNAL-DIR --workdir LOCAL-CLONE --praxis PATH-TO-praxis \
  [--run-id ID] [--trace WI-…] [--probe-branch NAME] [--poll-seconds N] [--out receipt.json] [--json]
```

## Checks

| ID | Check | How it is checked |
|---|---|---|
| UR-01 | Praxis available | `praxis version` |
| UR-02 | GitHub authentication and permissions | `gh api repos/R` → `permissions.push` |
| UR-03 | Branch-rule compatibility | `praxis autonomy diagnose`. A required review or a code-owner review is **BLOCKED**. Unreadable rules are BLOCKED; they are never assumed compatible. |
| UR-04 | Policy on the target branch | Fetched from the branch, then `praxis autonomy policy check` |
| UR-05 | Required CI checks declared, and workflows present | The policy plus `.github/workflows` |
| UR-06 | Valid run authorization | `praxis autonomy authorize verify --policy` |
| UR-07 | Revocation effective | A revocation of the run must make verification fail |
| UR-08 | Branch, harmless commit, push, pull request | `git`, `gh api …/pulls` |
| UR-09 | CI, independent review, Praxis authorization, merge | `praxis autonomy merge`. Only `MERGED` passes. |
| UR-10 | Merge verified on GitHub | The pull request is merged with the reported commit, and the target branch contains it |
| UR-11 | Evidence bound to the candidate | The Praxis receipt has a successful independent-review check from `github-actions` at the candidate |
| UR-12 | Receipt persisted, chain intact | `praxis autonomy receipts verify` |
| UR-13 | Recovery | Repeating the merge request returns `ALREADY_MERGED` with the same commit |
| UR-14 | Deployment permissions | Not applicable unless deployment is authorized |

## Verdicts

The receipt is `conditor.unattended-readiness/1`, with a self digest.

| Verdict | Meaning | Exit code |
|---|---|---|
| **READY** | Every check passed or does not apply. Configuration inspection alone never yields READY. | 0 |
| **BLOCKED** | A check failed, or was not run because an earlier one failed. | 3 |
| **NOT_RUN** | Praxis is unavailable, or the run authorization is invalid. | 4 |
