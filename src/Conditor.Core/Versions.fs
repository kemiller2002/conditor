namespace Conditor.Core

// Semantic versions and Registry profile version ranges, shared by drift
// (CON-300..320) and attested project-binding qualification (CON-340..346).

open System
open System.Text.RegularExpressions

/// A semantic version as Registry releases use it: MAJOR.MINOR.PATCH with an
/// optional pre-release (build metadata is ignored).
[<CustomComparison; CustomEquality>]
type SemanticVersion =
    { Major: int
      Minor: int
      Patch: int
      PreRelease: string option }

    member this.Line = this.Major, this.Minor

    member private this.Key =
        // A release without a pre-release sorts after every pre-release of the
        // same core version.
        this.Major, this.Minor, this.Patch, (if this.PreRelease.IsNone then 1 else 0), this.PreRelease |> Option.defaultValue ""

    override this.Equals other =
        match other with
        | :? SemanticVersion as version -> this.Key = version.Key
        | _ -> false

    override this.GetHashCode() = hash this.Key

    override this.ToString() =
        let core = $"{this.Major}.{this.Minor}.{this.Patch}"

        match this.PreRelease with
        | Some pre -> $"{core}-{pre}"
        | None -> core

    interface IComparable with
        member this.CompareTo other =
            match other with
            | :? SemanticVersion as version -> compare this.Key version.Key
            | _ -> invalidArg "other" "not a SemanticVersion"

module SemanticVersion =
    let private pattern =
        Regex(@"^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-([0-9A-Za-z.-]+))?(?:\+[0-9A-Za-z.-]+)?$", RegexOptions.CultureInvariant)

    let tryParse (text: string) =
        let matched = pattern.Match(text.Trim())

        if matched.Success then
            match Int32.TryParse matched.Groups[1].Value, Int32.TryParse matched.Groups[2].Value, Int32.TryParse matched.Groups[3].Value with
            | (true, major), (true, minor), (true, patch) ->
                Some
                    { Major = major
                      Minor = minor
                      Patch = patch
                      PreRelease = if matched.Groups[4].Success then Some matched.Groups[4].Value else None }
            | _ -> None
        else
            None

/// A Registry profile `version.range`: space-separated comparators, all of
/// which must hold (for example `>=0.3.0 <1.0.0`).
module VersionRange =
    type Comparator =
        | AtLeast of SemanticVersion
        | Above of SemanticVersion
        | AtMost of SemanticVersion
        | Below of SemanticVersion
        | Exactly of SemanticVersion

    let tryParse (text: string) : Comparator list option =
        let parsed =
            text.Split([| ' '; '\t' |], StringSplitOptions.RemoveEmptyEntries)
            |> Array.toList
            |> List.map (fun token ->
                let build (operator: string) make =
                    SemanticVersion.tryParse (token.Substring operator.Length) |> Option.map make

                if token.StartsWith(">=", StringComparison.Ordinal) then build ">=" AtLeast
                elif token.StartsWith("<=", StringComparison.Ordinal) then build "<=" AtMost
                elif token.StartsWith(">", StringComparison.Ordinal) then build ">" Above
                elif token.StartsWith("<", StringComparison.Ordinal) then build "<" Below
                elif token.StartsWith("=", StringComparison.Ordinal) then build "=" Exactly
                else build "" Exactly)

        if not parsed.IsEmpty && parsed |> List.forall Option.isSome then
            Some(parsed |> List.choose id)
        else
            None

    let private holds (version: SemanticVersion) comparator =
        match comparator with
        | AtLeast bound -> version >= bound
        | Above bound -> version > bound
        | AtMost bound -> version <= bound
        | Below bound -> version < bound
        | Exactly bound -> version = bound

    /// True when `version` satisfies every comparator of the range.
    let contains (comparators: Comparator list) (version: SemanticVersion) =
        comparators |> List.forall (holds version)

    /// True when `version` fails one of the range's lower bounds: it is below
    /// the minimum the range supports.
    let isBelow (comparators: Comparator list) (version: SemanticVersion) =
        comparators
        |> List.exists (fun comparator ->
            match comparator with
            | AtLeast _
            | Above _
            | Exactly _ -> not (holds version comparator)
            | AtMost _
            | Below _ -> false)
