/// The Registry echelon-current channel, as Conditor's tests pin it.
module EchelonCurrent

/// The linux-x64 selection of echelon-current 1.37.2 (the proposed cut,
/// resolved with the Registry's own tools, channels/echelon-current/linux-x64.json,
/// sha256 3902cb89...) for every system that Conditor carries a component
/// descriptor for. Against 1.37.1 (sha256 98220b45...) it moves only Forma
/// 0.13.1 -> 0.14.0 (AIS-001, forma#181). Arca, Dokimos,
/// Percepta Semantic, Strata, Fides, Summa Contracts and Limen F# are selected
/// but have no Conditor descriptor (Conditor installs Arca through the
/// release-asset NuGet feed on Registry authority). When the channel moves,
/// update this list in the same change that qualifies the new versions and
/// moves the presets that follow the channel.
let selections =
    [ "praxis", "3.25.0"
      "ordo", "1.20.0"
      "percepta", "0.1.0"
      "visual-engineering", "1.0.1"
      "communication-engineering", "1.0.0"
      "tutela", "0.1.0"
      "aegis", "1.0.0"
      "limen", "0.9.1"
      "forma", "0.14.0"
      "folio", "0.4.1" ]
