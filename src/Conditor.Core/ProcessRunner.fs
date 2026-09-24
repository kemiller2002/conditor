namespace Conditor.Core

open System
open System.Diagnostics

module ProcessRunner =
    let runProcess workingDirectory executable arguments =
        try
            let info = ProcessStartInfo()
            info.FileName <- executable
            info.WorkingDirectory <- workingDirectory
            info.UseShellExecute <- false
            info.RedirectStandardOutput <- true
            info.RedirectStandardError <- true

            for argument in arguments do
                info.ArgumentList.Add argument

            use childProcess = new Process()
            childProcess.StartInfo <- info

            if not (childProcess.Start()) then
                { ExitCode = -1
                  StandardOutput = String.Empty
                  StandardError = $"Unable to start '{executable}'." }
            else
                let outputTask = childProcess.StandardOutput.ReadToEndAsync()
                let errorTask = childProcess.StandardError.ReadToEndAsync()
                childProcess.WaitForExit()

                { ExitCode = childProcess.ExitCode
                  StandardOutput = outputTask.GetAwaiter().GetResult()
                  StandardError = errorTask.GetAwaiter().GetResult() }
        with ex ->
            { ExitCode = -1
              StandardOutput = String.Empty
              StandardError = $"Unable to execute '{executable}': {ex.Message}" }

    let run workingDirectory (action: PlanAction) =
        match action.Execution with
        | ExternalProcess(executable, arguments) ->
            runProcess workingDirectory executable arguments
        | GitHubSourceProcess(source, arguments) ->
            match source.Entrypoint with
            | FileArtifact _ ->
                { ExitCode = -1
                  StandardOutput = String.Empty
                  StandardError = "FileArtifact sources must be materialized by the Conditor installer." }
            | NodeScript _ ->
                match SourceCache.ensure action.ComponentId source with
                | Error errors ->
                    { ExitCode = -1
                      StandardOutput = String.Empty
                      StandardError = String.Join(Environment.NewLine, errors) }
                | Ok checkout ->
                    match SourceCache.resolveEntrypoint checkout source with
                    | Error errors ->
                        { ExitCode = -1
                          StandardOutput = String.Empty
                          StandardError = String.Join(Environment.NewLine, errors) }
                    | Ok entrypoint ->
                        runProcess workingDirectory "node" (entrypoint :: arguments)
        | EnsureFile _ ->
            { ExitCode = -1
              StandardOutput = String.Empty
              StandardError = "EnsureFile must be executed by the Conditor installer, not the process runner." }
        | EnsureManagedRegion _ ->
            { ExitCode = -1
              StandardOutput = String.Empty
              StandardError = "EnsureManagedRegion must be executed by the Conditor installer, not the process runner." }
        | MaterializeSourceFile _ ->
            { ExitCode = -1
              StandardOutput = String.Empty
              StandardError = "MaterializeSourceFile must be executed by the Conditor installer, not the process runner." }
        | EnsurePraxisMission _ ->
            { ExitCode = -1
              StandardOutput = String.Empty
              StandardError = "EnsurePraxisMission must be executed by the Conditor installer, not the process runner." }
