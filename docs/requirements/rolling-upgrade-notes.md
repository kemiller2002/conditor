# Rolling-upgrade requirement notes for other owners

Status: proposed (for routing by the program lead)
Owner of this note: Conditor
Decisions: `DF-CON-2026-A003` (accepted), `DF-CON-2026-A004` (accepted 2026-10-13; decision 2 conditional on signing)
Conditor requirements: CON-300..CON-320, CON-330..CON-346 (`requirements/CONDITOR-REQUIREMENTS.md`)

Conditor reports version drift (`conditor drift`) and plans stepwise current
upgrades. Several needs fall outside Conditor's authority. This note records
them as requirements for their owners to accept, reword or reject. Conditor
does not implement them, and does not edit those repositories.

## Praxis (route to kemiller2002/praxis)

- **PRX-RU-001** `praxis work start` SHOULD run the drift advisory for the
  repository when the repository declares one in `ros.json` (for example
  `workProtocol.driftAdvisory.command`). An example value is:

  ```text
  conditor drift --target . --registry <path> --json
  ```

  It SHOULD show the human summary to the agent before work begins.
- **PRX-RU-002** The advisory SHALL NOT block `work start` by default. It
  SHOULD treat Conditor's exit code 3 as blocking only when the repository
  opts in with `--fail-on`, set in the same configuration. Exit code 2 (the
  advisory could not run) SHALL be recorded as "drift advisory unavailable",
  not as "no drift".
- **PRX-RU-003** Praxis SHOULD attach the JSON report
  (`conditor.drift-report/v1`) to the execution as evidence, so that a
  reviewer can see which upgrades were recommended when the work started and
  whether the work took one.
- **PRX-RU-004** When an application repository is being worked on and the
  advisory recommends a next step, Praxis SHOULD offer to capture that step as
  a backlog item, for example "Move forma 0.4.1 → 0.5.0". It SHOULD NOT
  silently add it to the active work item.

## Echelon Registry (route to kemiller2002/echelon-registry)

- **REG-RU-001** Each library's release workflow SHALL open a Registry
  registration pull request automatically when it publishes a stable release.
  The pull request records the release manifest, adds the release to the
  catalog snapshot, regenerates the channel sets and index, and bumps the
  channel's profile version. The release-to-registration gap is what left
  Forma 0.6.1 (released 2026-10-10) invisible to Chrona, Summa and Signal.
- **REG-RU-002** `echelon.release/v2` (or a successor) SHOULD carry:
  - `releasedAt`, the publication time;
  - `migrationNotes`, a URL to the notes for consumers moving to that
    release.

  Conditor already prefers `migrationNotes` when present. Without it,
  Conditor links the GitHub release page. Without `releasedAt`, Conditor
  derives registration time from Registry Git history, which is unavailable
  in a shallow clone.
- **REG-RU-003** The Registry SHOULD keep one channel bump per registered
  release line (no two minor lines of one system in a single bump). It MAY
  instead publish a "stepping-stone" resolved set per registered release
  line. Either way, every intermediate step that stepwise upgrade needs then
  exists as a Registry-produced set, and Conditor never has to report a gap
  or compose a set (`DF-CON-2026-A003` decision 5).
- **REG-RU-004** The Registry MAY declare a `stalenessPolicy` in each channel's
  resolved sets, using the shape Conditor reads (`docs/drift-advisory.md`),
  so that the portfolio's default thresholds are Registry data and not
  Conditor's built-in default.
- **REG-RU-005** A profile's `version.range` lower bound is Conditor's
  "minimum supported" line (CON-318). The Registry SHOULD raise those lower
  bounds deliberately, as releases leave support, and record each change in
  the profile's version history.

- **REG-RU-006** (`DF-CON-2026-A004` decision 2, accepted)
  Release manifests of project-binding systems SHOULD declare the consumer
  contract they honour, for example
  `consumerContracts: ["echelon.web-package-binding/v1"]`. Registry review
  SHALL check the claim when it records the release, and SHALL backfill the
  claim on active web-package and NuGet-library releases. The claim takes
  effect in Conditor only inside a signed release record (REG-SIG-001..008).

## Echelon Registry: signed release records (precondition of DF-CON-2026-A004 decision 2)

The user accepted contract-and-line qualification only behind signed release
records. Until these requirements are met, Conditor keeps exact per-version
qualification (CON-346).

- **REG-SIG-001 (key).** The Registry SHALL hold a dedicated release
  attestation signing key, used only to sign release attestations.
  - **Algorithm:** ECDSA P-256 with SHA-256, so that Conditor verifies with
    the .NET base class library and needs no external cryptography
    dependency.
  - **Storage:** the private key SHALL be kept outside the repository and
    outside agent sessions, in a GitHub Actions environment secret
    protected by required reviewers, or in a KMS/HSM.
  - **Publication:** each public key SHALL be published as base64
    SubjectPublicKeyInfo with a stable key id, in a Registry trust document
    such as `trust/release-signing-keys.json` (`echelon.registry-keys/v1`).
  - **Pinning:** each key SHALL also be pinned in Conditor's build
    (`trust/registry-release-keys.json`). A key fetched over the network,
    or a key that only the Registry's trust document names, is never
    trusted on its own.
- **REG-SIG-002 (what is signed).** The signed payload SHALL be one release
  attestation statement, not the manifest file. It is the UTF-8 bytes of
  this exact line:

  ```text
  echelon.release-attestation/v1|<systemId>|<version>|<release manifest sha256>|<consumerContracts sorted, comma-joined>
  ```

  Because the statement binds the release manifest's SHA-256, it binds the
  manifest's bytes as well. Signing SHALL NOT change any manifest, so
  existing digests and resolved sets stay valid.
- **REG-SIG-003 (signing workflow).** Only the Registry's protected release
  registration workflow SHALL sign. It SHALL sign after the registration pull
  request has been reviewed and merged (REG-RU-001), on the default branch,
  in an environment that requires approval. Agents and pull-request
  workflows SHALL NOT have access to the key. The workflow SHALL be able to
  backfill attestations for the active releases that need them.
- **REG-SIG-004 (resolved sets carry the attestation).** The resolver SHALL
  copy the attestation into each resolved-set component entry:

  ```json
  "releaseAttestation": {
    "schema": "echelon.release-attestation/v1",
    "consumerContracts": [ "echelon.web-package-binding/v1" ],
    "keyId": "<id>",
    "algorithm": "ecdsa-p256-sha256",
    "signature": "<base64 IEEE P1363 r||s>"
  }
  ```

  Conditor can then verify a selection from the supplied set, offline, and
  the user-supplied set digest binds the attestation too.
- **REG-SIG-005 (how Conditor verifies).** Conditor SHALL rebuild the
  statement from the entry's own `systemId`, `version`,
  `releaseManifest.sha256` and `consumerContracts`, and SHALL treat a
  contract claim as qualifying only when all of these hold:
  - the signature verifies over that statement;
  - the key id is in Conditor's pinned trust set;
  - the key has not been revoked;
  - the algorithm is `ecdsa-p256-sha256`.

  Any other outcome SHALL leave the release under exact qualification, and
  the plan SHALL disclose the reason.
- **REG-SIG-006 (rotation).** A new key SHALL be introduced by publishing
  it in the trust document, signed by the current key, before it signs
  anything. Releases signed by the previous key SHALL remain valid until that
  key is retired or revoked. Conditor builds SHALL pin the new key before the
  Registry starts relying on it. Overlapping validity windows SHALL be
  recorded.
- **REG-SIG-007 (revocation).** The Registry SHALL publish revocations
  (key id, revocation time and reason) in the signed trust document. A
  compromise SHALL trigger a Conditor release that removes the key from the
  pinned trust set. Releases signed only by a revoked key SHALL fall back to
  exact qualification until they are re-signed by a valid key.
- **REG-SIG-008 (audit).** Every signature SHALL be traceable to the
  workflow run that produced it, for example through a GitHub artifact
  attestation or the run id recorded in the signature file. A conformance
  check (`conformance/validate-release-signatures.fsx`) SHALL verify every
  active release's signature on every Registry pull request.

## Weekly cross-repository drift report (owner to be decided by the lead)

- **ORG-RU-001** A scheduled job, weekly by default, SHALL check out each
  consumer repository of the portfolio read-only and run:

  ```text
  conditor drift --target <repo> --registry <echelon-registry checkout> --json
  ```

  It SHALL aggregate the reports.
- **ORG-RU-002** The aggregate SHALL list, per component, every consumer and
  its effective pin, the next recommended step, and its level. Repositories
  at `fail` sort first, then `warn`.
- **ORG-RU-003** A repository the job cannot check out or advise (exit 2)
  SHALL appear as "unavailable" with the reason, never be omitted.
- **ORG-RU-004** The job SHALL be read-only. It MAY open or update one issue
  per consumer repository that is at `warn` or above. It SHALL NOT open
  upgrade pull requests on its own.
- **ORG-RU-005** The job SHOULD also report released-but-unregistered library
  versions: the newest GitHub release of each registered system that is
  newer than the Registry's newest registered release. Those versions are
  the REG-RU-001 gap, made visible.
