/// The Registry echelon-current channel, as Conditor's tests pin it.
module EchelonCurrent

/// The linux-x64 selection of echelon-current 1.33.0 (the proposed cut,
/// resolved with the Registry's own tools, channels/echelon-current/linux-x64.json,
/// sha256 de658f5a...) for every system that Conditor carries a component
/// descriptor for. Against 1.32.0 (sha256 bf52d105...) it moves Praxis 3.23.0
/// -> 3.24.0 and Forma 0.11.0 -> 0.12.0; it also moves Arca 0.6.0 -> 0.7.0,
/// which, like Dokimos, Percepta Semantic, Strata, Fides, Summa Contracts and
/// Limen F#, is selected but has no Conditor descriptor (Conditor installs it
/// through the release-asset NuGet feed on Registry authority). When the
/// channel moves, update this list in the same change that qualifies the new
/// versions and moves the presets that follow the channel.
let selections =
    [ "praxis", "3.24.0"
      "ordo", "1.19.0"
      "percepta", "0.1.0"
      "visual-engineering", "1.0.1"
      "communication-engineering", "1.0.0"
      "tutela", "0.1.0"
      "aegis", "1.0.0"
      "limen", "0.9.1"
      "forma", "0.12.0"
      "folio", "0.4.1" ]
