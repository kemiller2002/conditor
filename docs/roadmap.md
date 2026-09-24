# Conditor roadmap

## Slice 1: deterministic foundation

- manifest parser and validation
- built-in registry
- deterministic lifecycle planning
- lifecycle execution through pinned npm packages
- post-install verification
- lock file
- dependency-free test harness
- CI

## Slice 2: true empty-repository bootstrap

- native Conditor release artifacts for macOS, Linux, and Windows
- one-command installer that does not require a preinstalled .NET SDK
- project-type/scaffold contract
- package binding for Forma, Folio, Aegis, and other application dependencies
- compatibility graph and preflight
- dry-run/check mode with zero mutation
- status and upgrade

## Slice 3: requirements to mission

- requirements source contract
- canonical requirements ingestion
- Ordo state initialization from requirements
- Praxis work/mission creation
- evidence obligations derived before coding starts
- execution readiness gate

## Slice 4: agent launch

- launcher adapter contract
- OpenAI/Codex launcher
- Claude launcher
- explicit credential capability checks
- resumable execution record
- stop conditions tied to requirements and verification, not model self-report

## Indy Init acceptance test

Given a newly created empty Git repository and a pinned Conditor preset, one installation entry point must establish the engineering environment, validate it, ingest the competition requirements, create the initial mission, and start the selected agent without manual file copying or hand editing.
