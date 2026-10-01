namespace Conditor.Core

open System

module Compatibility =
    type SupportedComponent =
        { Id: string
          Versions: Set<string>
          Notes: string }

    type Requirement =
        { Subject: string
          Requires: string
          Reason: string }

    let private notes =
        Map.ofList
            [ "praxis", "Praxis 3.6.0 is the Registry-qualified native work/runtime authority."
              "ordo", "Ordo 1.4.0 is the Registry-qualified native methodology/lifecycle release."
              "percepta", "Percepta repository lifecycle 0.1.0 is installed as a native lifecycle command."
              "limen", "Limen 0.6.2 is a project-bound web package; Conditor no longer requires npx lifecycle delivery for Indy Init."
              "forma", "Forma 0.3.0 is the qualified project-bound design-system package."
              "folio", "Folio 0.3.0 is the qualified project-bound print package."
              "aegis", "Aegis 1.0.0 is the qualified NuGet project binding."
              "visual-engineering", "Optional repository capability; not required by the canonical Indy Init profile."
              "communication-engineering", "Optional repository capability; not required by the canonical Indy Init profile."
              "tutela", "Optional security capability; not required by the canonical Indy Init profile." ]

    let supported =
        Registry.descriptors
        |> List.map (fun descriptor ->
            let id = descriptor.Definition.Id

            { Id = id
              Versions = descriptor.QualifiedVersions
              Notes =
                notes
                |> Map.tryFind id
                |> Option.defaultValue "Qualified by the embedded Conditor component descriptor." })

    let requirements =
        [ { Subject = "scaffold:fsharp-limen-web"
            Requires = "limen"
            Reason = "The scaffold's browser boundary is Limen by definition." }
          { Subject = "execution:enabled"
            Requires = "praxis"
            Reason = "Conditor establishes and activates execution through a Praxis mission." } ]

    let private supportedMap =
        supported |> List.map (fun item -> item.Id, item) |> Map.ofList

    let private requestedVersion (request: ComponentRequest) =
        match Registry.tryFind request.Id with
        | Some definition ->
            request.Version |> Option.defaultValue definition.DefaultVersion
        | None ->
            request.Version |> Option.defaultValue String.Empty

    let private componentIds (manifest: ProjectManifest) =
        manifest.Components |> List.map _.Id |> Set.ofList

    let validate (manifest: ProjectManifest) =
        let errors = ResizeArray<string>()
        let ids = componentIds manifest

        for request in manifest.Components do
            match Map.tryFind request.Id supportedMap with
            | None ->
                if request.Required then
                    errors.Add $"Component '{request.Id}' has no Conditor compatibility declaration."
            | Some support ->
                let version = requestedVersion request

                if not (support.Versions.Contains version) then
                    let known = support.Versions |> Seq.sort |> String.concat ", "
                    errors.Add
                        $"Component '{request.Id}' version '{version}' is not qualified by this Conditor compatibility graph. Qualified versions: {known}."

        match manifest.Scaffold with
        | Some scaffold when scaffold.Kind = "fsharp-limen-web" && not (ids.Contains "limen") ->
            errors.Add
                "Scaffold 'fsharp-limen-web' requires component 'limen'. Add an explicitly qualified Limen version to components."
        | _ -> ()

        let executionEnabled =
            manifest.Execution |> Option.exists (fun execution -> execution.Enabled)

        if executionEnabled && not (ids.Contains "praxis") then
            errors.Add
                "Enabled execution requires component 'praxis'. Conditor will not create or launch untracked work."

        List.ofSeq errors

    let describe () =
        supported
        |> List.sortBy _.Id
        |> List.map (fun item ->
            let versions = item.Versions |> Seq.sort |> String.concat ", "
            $"{item.Id}: {versions} — {item.Notes}")
