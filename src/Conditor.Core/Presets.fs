namespace Conditor.Core

open System
open System.IO
open System.Reflection
open System.Security.Cryptography
open System.Text

type ResolvedPreset =
    { Id: string
      Content: string
      ManifestPath: string }

module Presets =
    let private definitions =
        Map.ofList
            [ "indy-init", "Conditor.Presets.indy-init.json"
              "clean-room", "Conditor.Presets.clean-room.json"
              "evidence-triage-rehearsal", "Conditor.Presets.evidence-triage-rehearsal.json" ]

    let names =
        definitions |> Map.toList |> List.map fst

    let private assembly = typeof<ProjectManifest>.Assembly

    let private readResource resourceName =
        use stream = assembly.GetManifestResourceStream(resourceName)

        if isNull stream then
            Error [ $"Embedded Conditor preset resource is missing: {resourceName}" ]
        else
            use reader = new StreamReader(stream, Encoding.UTF8, true)
            Ok(reader.ReadToEnd())

    let private contentHash (content: string) =
        Encoding.UTF8.GetBytes content
        |> SHA256.HashData
        |> Convert.ToHexString
        |> fun value -> value.ToLowerInvariant()

    let private cachePath id content =
        let hash = contentHash content
        let directory =
            Path.Combine(Path.GetTempPath(), "conditor", "presets")

        Directory.CreateDirectory directory |> ignore
        Path.Combine(directory, $"{id}-{hash}.json")

    let private ensureCached path content =
        if File.Exists path && File.ReadAllText(path) = content then
            Ok()
        else
            let temporary = $"{path}.{Guid.NewGuid():N}.tmp"

            try
                File.WriteAllText(temporary, content, UTF8Encoding(false))

                if File.Exists path then
                    File.Move(temporary, path, true)
                else
                    File.Move(temporary, path)

                Ok()
            with ex ->
                Error [ $"Unable to stage built-in Conditor preset: {ex.Message}" ]
            finally
                if File.Exists temporary then
                    File.Delete temporary

    let resolve id =
        let normalized = id.Trim().ToLowerInvariant()

        match Map.tryFind normalized definitions with
        | None ->
            let available = String.Join(", ", names)
            Error [ $"Unknown Conditor preset '{id}'. Available presets: {available}." ]
        | Some resourceName ->
            match readResource resourceName with
            | Error errors -> Error errors
            | Ok content ->
                let path = cachePath normalized content

                match ensureCached path content with
                | Error errors -> Error errors
                | Ok() ->
                    Ok
                        { Id = normalized
                          Content = content
                          ManifestPath = path }

    let bindToTarget (preset: ResolvedPreset) (plan: InstallationPlan) =
        let manifestAction =
            { Sequence = 1
              ComponentId = $"conditor:preset:{preset.Id}"
              ComponentVersion = contentHash preset.Content
              Kind = ManifestFile
              Execution = EnsureFile("conditor.json", preset.Content) }

        let shifted =
            plan.Actions
            |> List.map (fun action -> { action with Sequence = action.Sequence + 1 })

        { plan with Actions = manifestAction :: shifted }
