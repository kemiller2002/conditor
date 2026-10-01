# Echelon Distribution Requirements - Pass 2: Resilience, Security, and Operations

Status: draft
Owner: Conditor
Depends on: Echelon Registry distribution catalog requirements

This pass stress-tests the installation design against partial failure, stale state, hostile or corrupted inputs, offline operation, upgrades, and long-lived workstation use.

## Trust and supply-chain requirements

**CON-DIST-100** Conditor SHALL verify every downloaded artifact against the digest declared by authoritative release metadata before extraction or execution.

**CON-DIST-101** Conditor SHALL verify available release provenance/attestation when a profile policy requires it and SHALL distinguish integrity verification from provenance verification.

**CON-DIST-102** Conditor SHALL never infer trust from a filename, repository owner string, executable name, or successful process exit alone.

**CON-DIST-103** Conditor SHALL prevent path traversal and archive escape when extracting release assets.

**CON-DIST-104** Temporary downloads and staged installations SHALL use locations that cannot overwrite active versions before verification succeeds.

**CON-DIST-105** Credentials SHALL never be written into profile documents, lock files, receipts, logs, diagnostics, process arguments that are likely to be persisted, or generated repository files.

**CON-DIST-106** Conditor SHALL not execute arbitrary package lifecycle/post-install scripts from third-party packages as a hidden consequence of installing an Echelon profile.

**CON-DIST-107** A profile SHALL be able to require a minimum trust class for every component, such as digest-verified or provenance-verified.

## Atomicity, rollback, and recovery

**CON-DIST-110** Where platform semantics permit, a version upgrade SHALL stage the new version beside the active version and activate it only after verification.

**CON-DIST-111** Failed activation SHALL preserve or restore the previously verified active version.

**CON-DIST-112** Rollback SHALL be driven by recorded ownership and receipts rather than by filename guessing.

**CON-DIST-113** A process interruption after a mutating step but before observation SHALL leave that step indeterminate until reconciliation proves its state.

**CON-DIST-114** Conditor SHALL provide deterministic reconciliation for interrupted operations and SHALL not assume that an attempted effect succeeded or failed.

**CON-DIST-115** Repair, resume, and reconciliation SHALL be idempotent.

**CON-DIST-116** Conditor SHALL preserve enough history to explain which profile/release set produced the current active state and which previous active version can be rolled back to when rollback is supported.

**CON-DIST-117** Activation of command shims/PATH entries SHALL be separately receipted from artifact installation.

## Dependency and compatibility behavior

**CON-DIST-120** Dependency resolution SHALL operate only on declared Echelon/system prerequisites and SHALL NOT evolve into a general-purpose arbitrary package dependency solver.

**CON-DIST-121** Resolution SHALL detect incompatible exact pins, unsatisfied ranges, cycles, duplicate canonical identities, and unsupported platforms before mutation.

**CON-DIST-122** A component MAY depend on a capability rather than a particular provider where Registry contracts support capability resolution.

**CON-DIST-123** Optional capability resolution SHALL preserve the three Registry states available, unavailable, and misconfigured where applicable.

**CON-DIST-124** A newer release SHALL NOT be selected merely because it is newer; compatibility and profile policy govern selection.

**CON-DIST-125** Version selection SHALL be explainable in machine-readable plan output, including the constraint or profile rule that selected each version.

## Release channels and upgrade policy

**CON-DIST-130** Conditor SHALL support stable, preview, and nightly release stages without conflating them with GitHub/NuGet/npm distribution mechanisms.

**CON-DIST-131** Stable profiles SHALL NOT silently consume preview or nightly releases.

**CON-DIST-132** Upgrading a profile SHALL calculate and disclose the release-set delta before mutation.

**CON-DIST-133** Downgrades SHALL require explicit policy support and SHALL be refused when a component declares an unsafe or unsupported downgrade path.

**CON-DIST-134** Conditor SHALL support exact historical version installation when authoritative metadata and compatible artifacts remain available.

## Offline bundles and caching

**CON-DIST-140** Conditor SHALL support creating a portable bundle containing the resolved profile, exact release metadata, required artifacts, integrity metadata, and Conditor version needed to consume it.

**CON-DIST-141** An offline bundle SHALL be sufficient to install its declared profile without network access after the bundle is obtained.

**CON-DIST-142** Bundle creation SHALL verify all artifacts before declaring the bundle complete.

**CON-DIST-143** Bundle installation SHALL re-verify artifacts and catalog/profile identity rather than trusting that bundle creation occurred correctly.

**CON-DIST-144** A bundle SHALL identify the host platforms it can satisfy and SHALL fail before mutation on an unsupported platform.

**CON-DIST-145** Local artifact caches SHALL be content-addressed or otherwise digest-bound and SHALL never make a mutable cache entry authoritative over release metadata.

**CON-DIST-146** Cache corruption SHALL result in re-fetch or a clear offline failure, not execution of an unverified cached artifact.

## Doctor, status, and drift

**CON-DIST-150** Status SHALL compare desired profile/lock state, Conditor receipts, component self-reported identity, and actual active command resolution.

**CON-DIST-151** Doctor SHALL distinguish missing, mismatched, corrupted, shadowed, stale, unsupported, and indeterminate states using stable diagnostic codes.

**CON-DIST-152** Doctor SHALL detect stale aliases or PATH precedence that resolves an Echelon command to a version different from the active receipt.

**CON-DIST-153** Doctor SHALL detect canonical-id/alias collisions, including renamed systems that would otherwise appear installed twice.

**CON-DIST-154** Repair SHALL propose or execute only changes that return the host to the already-authorized desired state.

**CON-DIST-155** A full environment verification SHALL be able to run without network access unless the profile explicitly requires an online integration check.

## Cross-system publishing contract

**CON-DIST-160** Conditor SHALL consume a standard release contract shared by all distributable Echelon systems.

**CON-DIST-161** Conditor SHALL not require a custom hard-coded downloader for each Echelon repository that conforms to the standard release contract.

**CON-DIST-162** A missing or malformed release contract SHALL produce a specific diagnostic naming the publishing defect.

**CON-DIST-163** Conditor SHALL expose enough resolution detail to identify which system release metadata must be fixed when a profile cannot resolve.

## Installation inventory and privacy

**CON-DIST-170** Environment registration SHALL use a user-configured logical target id rather than inferred machine identity.

**CON-DIST-171** Conditor SHALL never use hostname, username, home directory, MAC address, hardware serial, or absolute local path as an installation target identity.

**CON-DIST-172** Registration retries SHALL be idempotent through the installation protocol operation id.

**CON-DIST-173** Uninstall SHALL emit removal registration only after local removal receipts prove the intended removal.

## Pass 2 acceptance criteria

A deliberately interrupted, corrupted, stale, or partially upgraded Echelon environment can be diagnosed without guessing, reconciled to a known state, repaired or rolled back within declared policy, and verified offline. No unverified artifact becomes active and no user-owned/adopted resource is removed as collateral damage.


## Bootstrap and mutation hardening discovered by ecosystem audit

**CON-DIST-180** A successful first-time bootstrap SHALL leave the installed Conditor executable directly usable by the documented next step in the same user session. The canonical flow SHALL NOT require a logout, shell restart, or manual PATH edit.

**CON-DIST-181** Conditor SHALL define an explicit version lifecycle for Conditor itself, including exact-version install, current-version inspection, approved upgrade, and recovery/rollback semantics where platform replacement rules permit. Re-running an unpinned `latest` bootstrap script SHALL NOT be the only supported upgrade contract.

**CON-DIST-182** Bootstrap/platform resolution SHALL distinguish materially incompatible runtime variants, including Linux libc/runtime differences where they affect artifact compatibility. OS name plus CPU architecture alone SHALL NOT be considered sufficient when the produced artifact has additional runtime constraints.

**CON-DIST-183** Conditor SHALL prevent concurrent mutating operations against the same workstation state or target repository through a deterministic lock/lease protocol.

**CON-DIST-184** A stale or interrupted Conditor mutation lock SHALL NOT be silently discarded. Recovery SHALL establish whether an operation remains active, completed, failed, or indeterminate before allowing a conflicting mutation.

**CON-DIST-185** Before the first irreversible mutation, Conditor SHALL preflight, to the extent deterministically knowable, destination write access, required free space, platform compatibility, required authentication boundaries, and availability of all mandatory online sources.

**CON-DIST-186** When practical, all immutable artifacts needed for an installation transaction SHALL be downloaded, integrity-verified, and staged before activation/mutation of existing active versions.

**CON-DIST-187** Conditor self-replacement SHALL be atomic or restart-safe. Platforms that cannot replace the running executable directly SHALL use a staged replacement mechanism with an observable postcondition.

**CON-DIST-188** Bootstrap scripts SHALL have a version/source identity that can be reported in diagnostics and competition evidence. The bootstrap script is part of the supply-chain boundary and SHALL NOT be treated as unversioned glue.

**CON-DIST-189** Public native distributions SHOULD use platform-appropriate code signing/notarization where practical. Conditor SHALL distinguish operating-system publisher trust from cryptographic digest integrity and CI provenance; one SHALL NOT be reported as proof of another.

## Supply-chain evidence

**CON-DIST-190** For stable Echelon releases, Conditor SHALL be able to surface Registry-declared SBOM/dependency-inventory references and third-party-license evidence where those artifacts exist.

**CON-DIST-191** Conditor SHALL refuse automatic selection of a Registry release marked security-revoked or withdrawn.

**CON-DIST-192** Explicit historical reproduction of a withdrawn release, if supported at all, SHALL require a distinct unsafe/historical-reproduction policy boundary and SHALL NOT occur through normal stable profile resolution.

**CON-DIST-193** A release being deprecated SHALL be distinguishable from a release being security-revoked. Deprecation MAY remain installable under policy; revocation SHALL fail closed for normal resolution.

**CON-DIST-194** Conditor diagnostics SHALL state when required provenance, SBOM, signing, or license evidence is unavailable rather than implying that digest verification establishes those properties.
