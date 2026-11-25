namespace Conditor.Core

open System
open System.IO

/// One MSBuild file whose exact pins of a NuGet application binding move.
type NugetBindingRewrite =
    { /// Repository-relative, `/`-separated.
      Path: string
      /// The binding's family package ids this file pins.
      PackageIds: string list
      /// How many pins move.
      Pins: int }

/// A nuget.org-distributed application binding (a component descriptor with
/// `"applicationBinding": "nuget"`, such as Aegis) moving between two
/// versions: the conditor.json declaration and every exact
/// PackageReference/PackageVersion pin of the binding's package family move
/// together (docs/nuget-binding-upgrade.md).
type NugetBindingTransition =
    { Id: string
      FromVersion: string
      ToVersion: string
      /// The id prefix whose packages move with the binding.
      PackageFamily: string
      Rewrites: NugetBindingRewrite list }

module NugetBinding =
    /// The plan mode of a NuGet application-binding version change.
    [<Literal>]
    let Mode = "nuget-binding"

    /// The package family of a descriptor package: every package id that
    /// shares its owner prefix, so `EchelonFoundry.Aegis.Core` moves together
    /// with `EchelonFoundry.Aegis.Store.GitHub` and
    /// `EchelonFoundry.Aegis.Integration.GitHub`, which pin it exactly.
    let packageFamily (package: string) : string =
        match package.LastIndexOf '.' with
        | index when index > 0 -> package.Substring(0, index + 1)
        | _ -> package

    /// Whether a component descriptor is a NuGet application binding.
    let isBinding (definition: ComponentDefinition) : bool =
        definition.ApplicationBinding = Some NugetReference

    let private inFamily (family: string) (id: string) =
        id.StartsWith(family, StringComparison.OrdinalIgnoreCase)

    let private errorsOf results =
        results |> List.choose (function Error error -> Some error | Ok _ -> None)

    let private valuesOf results =
        results |> List.choose (function Ok value -> Some value | Error _ -> None)

    /// The transition that moves `definition` from `fromVersion` to
    /// `toVersion` in the repository at `target`. Refused unless this build
    /// qualifies `toVersion`, and refused when any pin of the family names a
    /// version other than exactly `fromVersion` or `toVersion`: Conditor moves
    /// only what it can prove the declaration set. Reads only.
    let plan (target: string) (definition: ComponentDefinition) (fromVersion: string) (toVersion: string) : Result<NugetBindingTransition, string list> =
        if not (isBinding definition) then
            Error [ $"'{definition.Id}' is not a NuGet application binding." ]
        else
            match Registry.qualifiedVersions definition.Id with
            | None -> Error [ $"'{definition.Id}' is unknown to this Conditor build." ]
            | Some versions when not (versions.Contains toVersion) ->
                let known = versions |> Set.toList |> String.concat ", "
                Error [ $"'{definition.Id}' {toVersion} is not qualified by this Conditor build (qualified: {known})." ]
            | Some _ ->
                let family = packageFamily (ComponentDefinition.packageFor toVersion definition)
                let root = Path.GetFullPath target

                let results =
                    NugetFeed.projectFiles target
                    |> List.choose (fun path ->
                        let text = File.ReadAllText path

                        let ids =
                            NugetFeed.pins text
                            |> List.map fst
                            |> List.filter (inFamily family)
                            |> List.distinct

                        if ids.IsEmpty then
                            None
                        else
                            let relative = Path.GetRelativePath(root, path).Replace('\\', '/')

                            NugetFeed.repin ids fromVersion toVersion text
                            |> Result.map (fun (_, moved) ->
                                { Path = relative
                                  PackageIds = ids
                                  Pins = moved })
                            |> Result.mapError (fun error -> $"{relative}: {error}")
                            |> Some)

                match errorsOf results with
                | [] ->
                    Ok
                        { Id = definition.Id
                          FromVersion = fromVersion
                          ToVersion = toVersion
                          PackageFamily = family
                          Rewrites = valuesOf results |> List.filter (fun rewrite -> rewrite.Pins > 0) |> List.sortBy _.Path }
                | errors -> Error errors

    /// Moves every pin the transition names. Every file is re-proven before
    /// any is written, so nothing changes when one no longer matches the plan.
    let apply (target: string) (transition: NugetBindingTransition) : Result<unit, string list> =
        let root = Path.GetFullPath target

        let updates =
            transition.Rewrites
            |> List.map (fun rewrite ->
                let path = Path.Combine(root, rewrite.Path)

                if not (File.Exists path) then
                    Error $"{rewrite.Path}: the file is missing"
                else
                    NugetFeed.repin rewrite.PackageIds transition.FromVersion transition.ToVersion (File.ReadAllText path)
                    |> Result.map (fun (text, _) -> path, text)
                    |> Result.mapError (fun error -> $"{rewrite.Path}: {error}"))

        match errorsOf updates with
        | [] ->
            valuesOf updates |> List.iter (fun (path, text) -> File.WriteAllText(path, text))
            Ok()
        | errors -> Error(errors |> List.map (fun error -> $"NuGet binding {transition.Id} {transition.FromVersion} -> {transition.ToVersion}: {error}"))

    /// The absolute paths a transition edits.
    let editedPaths (target: string) (transition: NugetBindingTransition) : string list =
        transition.Rewrites |> List.map (fun rewrite -> Path.Combine(Path.GetFullPath target, rewrite.Path))

    /// The transition's line in a plan digest.
    let digestMaterial (transition: NugetBindingTransition) : string =
        let files =
            transition.Rewrites
            |> List.map (fun rewrite -> $"""{rewrite.Path}={String.concat "+" rewrite.PackageIds}x{rewrite.Pins}""")
            |> String.concat ","

        $"{Mode}|{transition.Id}|{transition.FromVersion}->{transition.ToVersion}|{transition.PackageFamily}|{files}"

    /// Human-readable lines describing the transition.
    let describe (transition: NugetBindingTransition) : string list =
        [ yield $"{transition.Id}: {transition.FromVersion} -> {transition.ToVersion} ({transition.PackageFamily}* pins)"
          for rewrite in transition.Rewrites do
              yield $"""  {rewrite.Path}: {String.concat ", " rewrite.PackageIds} ({rewrite.Pins} pin(s))""" ]
