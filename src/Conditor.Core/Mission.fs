namespace Conditor.Core

open System
open System.IO
open System.Text.Json

module Mission =
    type private ExistingMission =
        { Title: string
          Description: string option
          Status: string
          Source: string option
          SourceReference: string option }

    let private tryProperty (name: string) (element: JsonElement) =
        let mutable value = Unchecked.defaultof<JsonElement>
        if element.TryGetProperty(name, &value) then Some value else None

    let private optionalString name (element: JsonElement) =
        match tryProperty name element with
        | Some value when value.ValueKind = JsonValueKind.String ->
            value.GetString() |> Option.ofObj
        | _ -> None

    let private requiredString name (element: JsonElement) =
        optionalString name element |> Option.defaultValue String.Empty

    let private readExisting target missionId =
        let queuePath = Path.Combine(target, ".ros", "work", "queue.json")

        if not (File.Exists queuePath) then
            Error [ $"Praxis work queue is missing: {queuePath}" ]
        else
            try
                use document = JsonDocument.Parse(File.ReadAllText queuePath)
                let root = document.RootElement

                match tryProperty "items" root with
                | Some items when items.ValueKind = JsonValueKind.Array ->
                    let found =
                        items.EnumerateArray()
                        |> Seq.tryFind (fun item -> requiredString "id" item = missionId)

                    match found with
                    | None -> Ok None
                    | Some item ->
                        Ok(
                            Some
                                { Title = requiredString "title" item
                                  Description = optionalString "description" item
                                  Status = requiredString "status" item
                                  Source = optionalString "source" item
                                  SourceReference = optionalString "sourceReference" item }
                        )
                | _ ->
                    Error [ "Praxis work queue does not contain an 'items' array." ]
            with ex ->
                Error [ $"Unable to inspect Praxis work queue: {ex.Message}" ]

    let private runRos target arguments =
        let launcher = Path.Combine(target, "ros")

        if not (File.Exists launcher) then
            Error [ $"Praxis repository launcher is missing: {launcher}" ]
        else
            let result = ProcessRunner.runProcess target "node" (launcher :: arguments)

            if result.ExitCode = 0 then
                Ok()
            else
                let argumentText = String.Join(" ", arguments)

                Error
                    [ $"Praxis command failed with exit code {result.ExitCode}: ros {argumentText}"
                      result.StandardOutput.Trim()
                      result.StandardError.Trim() ]
                |> Result.mapError (List.filter (String.IsNullOrWhiteSpace >> not))

    let private markReady target mission =
        runRos target [ "work"; "ready"; mission.Id ]

    let private create target mission =
        runRos
            target
            [ "add"
              mission.Title
              "--id"
              mission.Id
              "--priority"
              "high"
              "--description"
              mission.Description
              "--tag"
              "conditor"
              "--tag"
              "mission"
              "--source"
              "conditor"
              "--source-reference"
              mission.ContractPath
              "--actor"
              "conditor" ]

    let private validateExisting mission existing =
        [ if existing.Title <> mission.Title then
              yield $"Praxis mission '{mission.Id}' has a different title; Conditor will not overwrite it."
          if existing.Description <> Some mission.Description then
              yield $"Praxis mission '{mission.Id}' has a different description; Conditor will not overwrite it."
          if existing.Source <> Some "conditor" then
              yield $"Praxis mission '{mission.Id}' is not owned by the Conditor handoff."
          if existing.SourceReference <> Some mission.ContractPath then
              yield $"Praxis mission '{mission.Id}' references a different execution contract." ]

    let ensure target mission =
        match readExisting target mission.Id with
        | Error errors -> Error errors
        | Ok None ->
            match create target mission with
            | Error errors -> Error errors
            | Ok() ->
                markReady target mission
        | Ok(Some existing) ->
            let conflicts = validateExisting mission existing

            if not conflicts.IsEmpty then
                Error conflicts
            else
                match existing.Status with
                | "captured" -> markReady target mission
                | "ready"
                | "active"
                | "blocked"
                | "complete" -> Ok()
                | "abandoned" ->
                    Error [ $"Praxis mission '{mission.Id}' was abandoned; Conditor will not resurrect it automatically." ]
                | state ->
                    Error [ $"Praxis mission '{mission.Id}' has unsupported state '{state}'." ]
