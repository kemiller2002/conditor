# Conditor Code-Quality and Compatibility Work Items

Status: proposed requirements
Date: 2026-10-04

## COND-QUAL-001 - Compatibility authority beyond equal versions
Priority: critical

Extend component metadata so self-hosting and cross-component compatibility can be expressed as protocol/state-schema ranges and capability contracts rather than simple version equality.

Planning SHALL refuse an executor/tool combination that cannot interpret the target repository state even when both versions independently exist.

## COND-QUAL-002 - Attested post-release pin advancement
Priority: high

Support a release sequence in which source version may advance before its immutable release artifact exists. A component pin may advance only after the exact release assets, checksums, and required attestations are published and compatibility verification passes.

## COND-QUAL-003 - Ecosystem compatibility verification
Priority: high

Conditor verify/doctor SHALL report incompatible Praxis/Ordo/Dokimos/Aegis/component combinations separately from merely out-of-date combinations, with stable remediation.

## COND-QUAL-004 - Standard engineering quality profile
Priority: high

Add an installable profile that can establish the coordinated quality system in repositories: Ordo method rules, Praxis completion/evidence gates, Dokimos change-quality policy, and Aegis boundary-failure conventions where applicable.

Component-owned policy remains component-owned; Conditor installs/pins/verifies contracts rather than copying their implementation.

## COND-QUAL-005 - Staged upgrade proof
Priority: high

For upgrades that change state/protocol compatibility, plan against a staged target, verify all declared compatibility contracts, then atomically commit authority/lock changes. Refuse partial compatibility advancement.

## COND-QUAL-006 - Quality-policy drift reporting
Priority: medium

Doctor/status SHOULD report when a repository's installed quality-policy components are internally compatible but behind the selected governed profile, without automatically upgrading them.

## Principle

Conditor should make it difficult to run a newer repository with an older incompatible control-plane tool, while preserving reproducibility and explicit authorization.
