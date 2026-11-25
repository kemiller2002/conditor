namespace Conditor.Core

open System.IO

/// File-name extraction that compiles warning-free under both nullness views of
/// `Path.GetFileName`: older reference assemblies annotate its result as
/// `string | null`, while .NET 11's annotate it non-null for non-null input, so
/// F# reports `Option.ofObj` on it as FS3262. Routing the result through a
/// `string | null` parameter is valid under both, and keeps the empty-name
/// fallback the call sites relied on.
module PathText =
    let private orElse (fallback: string) (value: string | null) : string =
        match value with
        | null -> fallback
        | text -> text

    let private ofNullable (value: string | null) : string option =
        match value with
        | null -> None
        | text -> Some text

    /// The file or directory name of `path`, or `fallback` when there is none.
    let fileNameOr (fallback: string) (path: string) : string = Path.GetFileName path |> orElse fallback

    /// The file or directory name of `path`, if it has one.
    let tryFileName (path: string) : string option = Path.GetFileName path |> ofNullable
