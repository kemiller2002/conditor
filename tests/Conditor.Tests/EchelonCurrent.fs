/// The Registry echelon-current channel, as Conditor's tests pin it.
module EchelonCurrent

/// The linux-x64 selection of echelon-current 1.31.0 (the proposed cut,
/// resolved with the Registry's own tools, channels/echelon-current/linux-x64.json,
/// sha256 4d30d15f...) for every system that Conditor carries a component
/// descriptor for. Against 1.30.0 (sha256 491041e9...) it moves Praxis 3.22.0
/// -> 3.23.0, Ordo 1.18.0 -> 1.19.0 and Limen 0.9.0 -> 0.9.1; it also moves
/// Dokimos 0.8.0 -> 0.9.0, which, like Percepta Semantic, Strata, Arca,
/// Fides, Summa Contracts and Limen F#, is selected but has no Conditor
/// descriptor. 1.30.0 moved Forma 0.9.0 -> 0.10.0 while this list and the
/// Indy preset stayed on 1.29.0; they follow the channel again here. When the
/// channel moves, update this list in the same change that qualifies the new
/// versions and moves the presets that follow the channel.
let selections =
    [ "praxis", "3.23.0"
      "ordo", "1.19.0"
      "percepta", "0.1.0"
      "visual-engineering", "1.0.1"
      "communication-engineering", "1.0.0"
      "tutela", "0.1.0"
      "aegis", "1.0.0"
      "limen", "0.9.1"
      "forma", "0.10.0"
      "folio", "0.4.1" ]
