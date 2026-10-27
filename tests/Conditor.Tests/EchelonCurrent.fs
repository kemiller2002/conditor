/// The Registry echelon-current channel, as Conditor's tests pin it.
module EchelonCurrent

/// The linux-x64 selection of echelon-current 1.28.0 (echelon-registry main
/// e6d8a734, channels/echelon-current/linux-x64.json, sha256 5aee6c3c...) for
/// every system that Conditor carries a component descriptor for. Dokimos,
/// Percepta Semantic, Strata, Arca, Fides, Summa Contracts and Limen F# are
/// selected too but have no Conditor descriptor. Until WI-0054 this list was the
/// 1.17.0 selection with only Praxis moved, so the Indy preset that follows it
/// stayed on Ordo 1.5.0, Forma 0.5.0 and Folio 0.3.0 while 1.28.0 selects
/// Ordo 1.17.0, Forma 0.9.0 and Folio 0.4.1. When the channel moves, update
/// this list in the same change that qualifies the new versions and moves the
/// presets that follow the channel.
let selections =
    [ "praxis", "3.21.0"
      "ordo", "1.17.0"
      "percepta", "0.1.0"
      "visual-engineering", "1.0.1"
      "communication-engineering", "1.0.0"
      "tutela", "0.1.0"
      "aegis", "1.0.0"
      "limen", "0.9.0"
      "forma", "0.9.0"
      "folio", "0.4.1" ]
