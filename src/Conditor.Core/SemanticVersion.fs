namespace Conditor.Core

open System

/// Minimal, pure semantic-version ordering for component capability gates.
/// Only `MAJOR.MINOR.PATCH` with an optional `-prerelease` suffix is ordered;
/// anything else is incomparable (`None`) so callers never guess.
module SemanticVersion =
    type Parsed =
        { Core: int * int * int
          Prerelease: string option }

    let tryParse (text: string) =
        let value = text.Trim()
        let withoutBuild = value.Split('+').[0]

        let core, prerelease =
            match withoutBuild.IndexOf '-' with
            | -1 -> withoutBuild, None
            | index -> withoutBuild.Substring(0, index), Some(withoutBuild.Substring(index + 1))

        let number (part: string) =
            match Int32.TryParse part with
            | true, parsed when parsed >= 0 && part.Length > 0 && part |> Seq.forall Char.IsAsciiDigit -> Some parsed
            | _ -> None

        match core.Split('.') |> Array.map number with
        | [| Some major; Some minor; Some patch |] when prerelease |> Option.forall (String.IsNullOrWhiteSpace >> not) ->
            Some
                { Core = major, minor, patch
                  Prerelease = prerelease }
        | _ -> None

    /// A prerelease sorts before its release; prerelease identifiers compare ordinally.
    let compare (left: Parsed) (right: Parsed) =
        match Operators.compare left.Core right.Core with
        | 0 ->
            match left.Prerelease, right.Prerelease with
            | None, None -> 0
            | None, Some _ -> 1
            | Some _, None -> -1
            | Some l, Some r -> String.CompareOrdinal(l, r) |> sign
        | other -> other

    /// `Some true` when `version` is at or after `since`, `None` when either is not a comparable version.
    let isAtLeast since version =
        match tryParse since, tryParse version with
        | Some minimum, Some candidate -> Some(compare candidate minimum >= 0)
        | _ -> None
