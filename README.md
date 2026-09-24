# Conditor

Conditor establishes a governed engineering environment from an empty repository.

Its first target is the Indy Init demonstration: start with an empty Git repository, resolve a declarative project manifest, install the selected Echelon capabilities, verify the result, create the initial mission, and hand execution to an agent without manual file copying.

This repository contains the reusable Conditor CLI and installation contracts. It is not specific to the competition application.

## Intended lifecycle

```text
empty repository
      |
      v
  condidor init
      |
      +-- resolve manifest
      +-- plan changes
      +-- install capabilities
      +-- verify each capability
      +-- record lock/evidence
      +-- create initial mission
      +-- launch execution
```

> The CLI is under active construction. See `docs/architecture.md` and `docs/component-contract.md` for the initial contracts.
