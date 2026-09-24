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
            | [] ->
                if plan.Operation = Init then
                    let lockPath = LockFile.write target manifestPath plan
                    Ok(Some lockPath)
                else
                    Ok None
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

        loop plan.Actions
