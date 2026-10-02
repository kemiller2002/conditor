# Registry lifecycle clean-host proof — Dokimos 0.2.0 (2026-10-02)

Workflow: `.github/workflows/registry-lifecycle-proof.yml`
Run: https://github.com/kemiller2002/conditor/actions/runs/37003356848 (job 110825874444), conclusion **success**
Conditor source: `d7965cb6c44c88fa2ebcdd56667ef955083552f4` (PR #23 head)
Host: GitHub-hosted `ubuntu-latest`, clean runner, `linux-x64`, empty `$RUNNER_TEMP/home`

## Registry selection

| Fact | Value |
| --- | --- |
| Registry commit | `3f7f31895d0e480ae0d176ea34670503920d26cf` (kemiller2002/echelon-registry#21) |
| Profile | `dokimos-proof` 0.1.0, sha256 `cc738238d126c35ec847badf56f58a507380f4b52a51a528ffb2dd240aa0a71b` |
| Catalog snapshot | sha256 `32c2e420a621d7e287950e3ec7c914bfd17136d2a606f42bebb4090b04a70779` |
| Resolved set | `resolved/dokimos-proof/0.1.0/linux-x64.json`, sha256 `928cf98dbfff9da7594063738bb35eeeaeb55494ebf77f3510cd0056eb143d49` (re-resolved on the runner, byte-identical) |
| Component | `dokimos` 0.2.0, role `repository-lifecycle`, `echelon.repository-lifecycle` v1 |
| Release | `dokimos-v0.2.0`, commit `e641048c52edd8755c2964b6bafa4ae860e34062` |
| Selected artifact | `dokimos-linux-x64.tar.gz`, sha256 `bb61505cf8fa23bf6b0992019ca2e2805bda46eeba42eecde9497e15a060441a` |

A resolved-set digest of 64 zeros was refused before any effect.

## First application

- `workstation apply`: `shell-path`, `dokimos-download`, `dokimos-extract`,
  `dokimos-shim` completed; every receipt matched. The download came from
  `https://github.com/kemiller2002/dokimos/releases/download/dokimos-v0.2.0/dokimos-linux-x64.tar.gz`
  and matched the Registry digest.
- Identity receipt (`dokimos version`): `SystemId=dokimos`,
  `Repository=kemiller2002/dokimos`, `Executable=dokimos`,
  `ReleaseVersion=0.2.0`, `SourceCommit=e641048c52edd8755c2964b6bafa4ae860e34062`.
- `lifecycle apply --operation init` on a clean Git repository: `dokimos init`
  created its three owned files, then `dokimos verify` reported `Healthy: true`.
  `status`, `verify` and `doctor` each exited 0 with `Healthy: true`.
- The resulting repository state was committed for comparison.

## Second application of the same desired state

- Install plan digest identical; `shell-path`, `dokimos-download`,
  `dokimos-extract`, `dokimos-shim` all **reused** (receipts already matched,
  executable identity revalidated). No step completed anew.
- Lifecycle `init` plan digest identical to the first; `init` and `upgrade`
  reported every file `unchanged`; `verify` and `status` healthy.
- `git status --porcelain` empty, tracked tree byte-identical, user-owned
  `README.md` unchanged: **zero drift**.

No Conditor source names Dokimos; every fact above came from the Registry
resolved release set.
