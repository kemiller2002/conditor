/// The Registry echelon-current channel, as Conditor's tests pin it.
module EchelonCurrent

/// The linux-x64 selection of echelon-current 1.29.0 (echelon-registry
/// WI-0066, the proposed cut on claude/rehearsal-5-prep-qlbywr,
/// channels/echelon-current/linux-x64.json, sha256 d3774e7a...) for every
/// system that Conditor carries a component descriptor for. Against 1.28.0
/// (sha256 5aee6c3c...) it moves Praxis 3.21.0 -> 3.22.0 and Ordo 1.17.0 ->
/// 1.18.0; it also moves Dokimos 0.7.0 -> 0.8.0 and Percepta Semantic 0.2.1 ->
/// 0.3.0, which, like Strata, Arca, Fides, Summa Contracts and Limen F#, are
/// selected but have no Conditor descriptor. Until WI-0054 this list was the
/// 1.17.0 selection with only Praxis moved, so the Indy preset that follows it
/// stayed behind the channel. When the channel moves, update this list in the
/// same change that qualifies the new versions and moves the presets that
/// follow the channel.
let selections =
    [ "praxis", "3.22.0"
      "ordo", "1.18.0"
      "percepta", "0.1.0"
      "visual-engineering", "1.0.1"
      "communication-engineering", "1.0.0"
      "tutela", "0.1.0"
      "aegis", "1.0.0"
      "limen", "0.9.0"
      "forma", "0.9.0"
      "folio", "0.4.1" ]
