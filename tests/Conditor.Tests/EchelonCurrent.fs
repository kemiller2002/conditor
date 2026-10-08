/// The Registry echelon-current channel, as Conditor's tests pin it.
module EchelonCurrent

/// The linux-x64 selection of echelon-current 1.8.0 (echelon-registry
/// chore/echelon-current-forma-0.5.0 90acf89, kemiller2002/echelon-registry#49,
/// channels/echelon-current/linux-x64.json) for every system that Conditor
/// carries a component descriptor for. Dokimos, Strata, Arca and Fides are
/// selected too but have no Conditor descriptor. When the channel moves,
/// update this list in the same change that qualifies the new versions and
/// moves the presets that follow the channel.
let selections =
    [ "praxis", "3.7.2"
      "ordo", "1.5.0"
      "percepta", "0.1.0"
      "visual-engineering", "1.0.1"
      "communication-engineering", "1.0.0"
      "tutela", "0.1.0"
      "aegis", "1.0.0"
      "limen", "0.7.1"
      "forma", "0.5.0"
      "folio", "0.3.0" ]
