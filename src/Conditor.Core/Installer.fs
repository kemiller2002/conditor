namespace Conditor.Core

open System
open System.IO

module Installer =
    let private normalize (value: string) =
        let carriageReturn = string (char 13)
        let lineFeed = string (char 10)
        value.Replace(carriageReturn + lineFeed, lineFeed)

    let private sourceEntrypointText source =
        match source.Entrypoint with
        | NodeScript path -> $"node {path}"
        | FileArtifact path -> $"file {path}"

    let private commandText (action: PlanAction) =
        match action.Execution with
        | ExternalProcess(executable, arguments) ->
            String.Join(" ", executable :: arguments)
        | GitHubSourceProcess(source, arguments) ->
            let suffix =
                if arguments.IsEmpty then
                    String.Empty
                else
                    " " + String.Join(" ", arguments)

            $"github:{source.Repository}#{source.Commit} -> {sourceEntrypointText source}{suffix}"
        | EnsureFile(relativePath, _) ->
            $"ensure {relativePath}"
        | EnsureManagedRegion(relativePath, regionId, _) ->
            $"ensure managed region {regionId} in {relativePath}"
        | MaterializeSourceFile(source, relativePath) ->
            $"materialize github:{source.Repository}#{source.Commit} -> {relativePath}"
        | EnsurePraxisMission mission ->
            $"praxis mission {mission.Id} -> ready"

    let private safePath target relativePath =
        let root = Path.GetFullPath target
        let full = Path.GetFullPath(Path.Combine(root, relativePath))
        let rootPrefix =
            root.TrimEnd(Path.DirectorySeparatorChar) + string Path.DirectorySeparatorChar

        let comparison =
            if OperatingSystem.IsWindows() then
                StringComparison.OrdinalIgnoreCase
            else
                StringComparison.Ordinal

        if full.StartsWith(rootPrefix, comparison) then Some full else None

    let private ensureParent (fullPath: string) =
        match Path.GetDirectoryName(fullPath) with
        | null -> ()
        | parent when String.IsNullOrWhiteSpace parent -> ()
        | parent -> Directory.CreateDirectory parent |> ignore

    let private ensureFile target relativePath content =
        match safePath target relativePath with
        | None ->
            Error $"Scaffold path escapes the target repository: {relativePath}"
        | Some fullPath ->
            if File.Exists fullPath then
                let existing = File.ReadAllText fullPath

                if normalize existing = normalize content then
                    Ok()
                else
                    Error $"Scaffold file '{relativePath}' changed after planning; Conditor will not overwrite it."
            else
                ensureParent fullPath
                let temporary = $"{fullPath}.conditor-{Guid.NewGuid():N}.tmp"

                try
                    try
                        File.WriteAllText(temporary, content)
                        File.Move(temporary, fullPath)
                        Ok()
                    with ex ->
                        Error $"Unable to create scaffold file '{relativePath}': {ex.Message}"
                finally
                    if File.Exists temporary then
                        File.Delete temporary


    let private managedRegionStart regionId =
        $"<!-- conditor:{regionId}:start -->"

    let private managedRegionEnd regionId =
        $"<!-- conditor:{regionId}:end -->"

    let private renderManagedRegion (regionId: string) (content: string) =
        let body = normalize content |> fun value -> value.TrimEnd('\r', '\n')
        $"{managedRegionStart regionId}\n{body}\n{managedRegionEnd regionId}\n"

    let private writeAtomically (fullPath: string) (content: string) (errorLabel: string) =
        ensureParent fullPath
        let temporary = $"{fullPath}.conditor-{Guid.NewGuid():N}.tmp"

        try
            try
                File.WriteAllText(temporary, content)

                if File.Exists fullPath then
                    File.Move(temporary, fullPath, true)
                else
                    File.Move(temporary, fullPath)

                Ok()
            with ex ->
                Error $"{errorLabel}: {ex.Message}"
        finally
            if File.Exists temporary then
                File.Delete temporary

    let private ensureManagedRegion (target: string) (relativePath: string) (regionId: string) (content: string) =
        match safePath target relativePath with
        | None ->
            Error $"Managed scaffold path escapes the target repository: {relativePath}"
        | Some fullPath ->
            let managed = renderManagedRegion regionId content

            if not (File.Exists fullPath) then
                writeAtomically fullPath managed $"Unable to create managed scaffold file '{relativePath}'"
            else
                let existing = File.ReadAllText fullPath
                let normalizedExisting = normalize existing
                let startMarker = managedRegionStart regionId
                let endMarker = managedRegionEnd regionId
                let startIndex = normalizedExisting.IndexOf(startMarker, StringComparison.Ordinal)
                let endIndex = normalizedExisting.IndexOf(endMarker, StringComparison.Ordinal)

                match startIndex >= 0, endIndex >= 0 with
                | false, false ->
                    let prefix = normalizedExisting.TrimEnd('\r', '\n')

                    let combined =
                        if String.IsNullOrWhiteSpace prefix then
                            managed
                        else
                            $"{prefix}\n\n{managed}"

                    writeAtomically fullPath combined $"Unable to append managed region '{regionId}' to '{relativePath}'"
                | true, true when endIndex > startIndex ->
                    let suffixStart = endIndex + endMarker.Length
                    let prefix = normalizedExisting.Substring(0, startIndex)
                    let suffix = normalizedExisting.Substring(suffixStart)
                    let combined = prefix + managed.TrimEnd('\r', '\n') + suffix

                    if normalize existing = normalize combined then
                        Ok()
                    else
                        writeAtomically fullPath combined $"Unable to update managed region '{regionId}' in '{relativePath}'"
                | _ ->
                    Error $"Managed region '{regionId}' in '{relativePath}' is malformed; Conditor will not overwrite content outside a valid bounded region."

    let private filesEqual left right =
        let leftInfo = FileInfo left
        let rightInfo = FileInfo right

        if leftInfo.Length <> rightInfo.Length then
            false
        else
            use leftStream = File.OpenRead left
            use rightStream = File.OpenRead right
            let leftBuffer = Array.zeroCreate<byte> 81920
            let rightBuffer = Array.zeroCreate<byte> 81920

            let rec compare () =
                let leftCount = leftStream.Read(leftBuffer, 0, leftBuffer.Length)
                let rightCount = rightStream.Read(rightBuffer, 0, rightBuffer.Length)

                if leftCount <> rightCount then
                    false
                elif leftCount = 0 then
                    true
                else
                    let mutable same = true
                    let mutable index = 0

                    while same && index < leftCount do
                        if leftBuffer[index] <> rightBuffer[index] then
                            same <- false

                        index <- index + 1

                    same && compare ()

            compare ()

    let private materializeSourceFile target componentId source relativePath =
        match safePath target relativePath with
        | None ->
            Error $"Requirement target path escapes the target repository: {relativePath}"
        | Some fullPath ->
            match SourceCache.ensure componentId source with
            | Error errors -> Error(String.Join(Environment.NewLine, errors))
            | Ok checkout ->
                match SourceCache.resolveEntrypoint checkout source with
                | Error errors -> Error(String.Join(Environment.NewLine, errors))
                | Ok sourcePath ->
                    if File.Exists fullPath then
                        if filesEqual sourcePath fullPath then
                            Ok()
                        else
                            Error $"Requirement file '{relativePath}' already exists with different content; Conditor will not overwrite it."
                    else
                        ensureParent fullPath
                        let temporary = $"{fullPath}.conditor-{Guid.NewGuid():N}.tmp"

                        try
                            try
                                File.Copy(sourcePath, temporary, false)
                                File.Move(temporary, fullPath)
                                Ok()
                            with ex ->
                                Error $"Unable to materialize requirement file '{relativePath}': {ex.Message}"
                        finally
                            if File.Exists temporary then
                                File.Delete temporary

    let describe (plan: InstallationPlan) =
        plan.Actions
        |> List.map (fun action -> $"{action.Sequence,2}. {action.ComponentId}@{action.ComponentVersion}: {commandText action}")

    let execute target manifestPath (plan: InstallationPlan) =
        Directory.CreateDirectory target |> ignore

        let rec loop actions =
            match actions with
            | [] -> Ok()
            | action :: remaining ->
                match action.Execution with
                | EnsureFile(relativePath, content) ->
                    match ensureFile target relativePath content with
                    | Ok() -> loop remaining
                    | Error error ->
                        Error
                            [ $"Conditor stopped at action {action.Sequence} ({action.ComponentId})."
                              $"Command: {commandText action}"
                              error ]
                | EnsureManagedRegion(relativePath, regionId, content) ->
                    match ensureManagedRegion target relativePath regionId content with
                    | Ok() -> loop remaining
                    | Error error ->
                        Error
                            [ $"Conditor stopped at action {action.Sequence} ({action.ComponentId})."
                              $"Command: {commandText action}"
                              error ]
                | MaterializeSourceFile(source, relativePath) ->
                    match materializeSourceFile target action.ComponentId source relativePath with
                    | Ok() -> loop remaining
                    | Error error ->
                        Error
                            [ $"Conditor stopped at action {action.Sequence} ({action.ComponentId})."
                              $"Command: {commandText action}"
                              error ]
                | EnsurePraxisMission mission ->
                    match Mission.ensure target mission with
                    | Ok() -> loop remaining
                    | Error errors ->
                        Error
                            ([ $"Conditor stopped at action {action.Sequence} ({action.ComponentId})."
                               $"Command: {commandText action}" ]
                             @ errors)
                | ExternalProcess _
                | GitHubSourceProcess _ ->
                    let result = ProcessRunner.run target action

                    if result.ExitCode = 0 then
                        loop remaining
                    else
                        Error
                            [ $"Conditor stopped at action {action.Sequence} ({action.ComponentId})."
                              $"Command: {commandText action}"
                              $"Exit code: {result.ExitCode}"
                              result.StandardOutput.Trim()
                              result.StandardError.Trim() ]
                        |> Result.mapError (List.filter (String.IsNullOrWhiteSpace >> not))

        // Provenance readiness is checked after every lifecycle verify
        // succeeded and before lock state is written (CON-010, CON-128).
        let provenanceGate () =
            match PraxisProvenance.inspect target plan.Components with
            | Some readiness when readiness.Status = PraxisProvenance.MissingWhenExpected ->
                Error
                    [ "Conditor post-install verification failed: Praxis provenance is missing."
                      readiness.Detail ]
            | _ -> Ok()

        match loop plan.Actions with
        | Error errors -> Error errors
        | Ok() ->
            let gate =
                match plan.Operation with
                | Doctor -> Ok()
                | Init
                | Verify
                | Upgrade -> provenanceGate ()

            match gate with
            | Error errors -> Error errors
            | Ok() ->
                if plan.Operation = Init then
                    let lockPath = LockFile.write target manifestPath plan
                    Ok(Some lockPath)
                else
                    Ok None
