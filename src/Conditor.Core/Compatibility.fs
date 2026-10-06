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
            [ "praxis", "Native Praxis 3.6.0 is the frozen Indy Init host-tool baseline; 3.7.1 is qualified as the Registry echelon-current selection; 3.1.x remains qualified only for existing compatibility fixtures."
              "ordo", "Native Ordo 1.4.0 is the frozen Indy Init host-tool baseline; 1.4.1 is qualified as the Registry echelon-current selection."
              "percepta", "Native Percepta repository lifecycle 0.1.0 is the frozen Indy Init repository-lifecycle baseline."
              "visual-engineering", "Qualified lifecycle release."
              "communication-engineering", "Pinned immutable-source lifecycle release."
              "limen", "Qualified project-bound WASM/browser package used by the current web scaffold; Conditor does not run a package-manager lifecycle for it. 0.7.0 and 0.7.1 are distributed as @echelon-foundry/limen; 0.6.1 and 0.6.2 stay qualified under their original @echelon-foundry/typescript-wasm-kernel name, and the Indy Init 0.1.0 freeze pins 0.6.2."
              "forma", "Qualified application binding."
              "folio", "Qualified application binding."
              "aegis", "Qualified NuGet application binding."
              "tutela", "Qualified pinned immutable-source lifecycle release." ]

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

    let validateWithExternalIds (externalIds: Set<string>) (manifest: ProjectManifest) =
        let errors = ResizeArray<string>()
        let ids = componentIds manifest

        for request in manifest.Components do
            match Map.tryFind request.Id supportedMap with
            | None ->
                if request.Required && not (externalIds.Contains request.Id) then
                    errors.Add $"Component '{request.Id}' has no Conditor compatibility declaration or verified Registry authority."
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

    let validate (manifest: ProjectManifest) =
        let externallyResolvable =
            if manifest.RegistryAuthority.IsSome then
                manifest.Components
                |> List.choose (fun request ->
                    if Registry.tryFind request.Id |> Option.isNone then Some request.Id else None)
                |> Set.ofList
            else
                Set.empty

        validateWithExternalIds externallyResolvable manifest

    let describe () =
        supported
        |> List.sortBy _.Id
        |> List.map (fun item ->
            let versions = item.Versions |> Seq.sort |> String.concat ", "
            $"{item.Id}: {versions} — {item.Notes}")
