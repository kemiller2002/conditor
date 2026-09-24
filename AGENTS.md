# Conditor agent instructions

Conditor is infrastructure that can mutate otherwise empty repositories. Treat installation behavior as a high-integrity boundary.

1. Plan before mutation.
2. Prefer typed state and explicit transitions.
3. Never silently choose a package target when more than one target is plausible.
4. Never execute an unpinned remote component in a reproducible preset.
5. Stop on unknown required components, compatibility failures, or failed verification.
6. Write Conditor lock state only after the requested operation succeeds.
7. Component-owned files remain component-owned. Do not copy their implementation into Conditor.
8. Keep external dependencies minimal; the F# core should prefer the BCL.
9. Tests must cover manifest validation, deterministic planning, refusal behavior, and failure boundaries.
10. Agent completion claims are not evidence. Use component and project verification commands.
