namespace Conditor.Core

open System
open System.Diagnostics

module ProcessRunner =
    let private runCommand workingDirectory executable arguments =
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
            runCommand workingDirectory executable arguments
        | GitHubSourceProcess(source, arguments) ->
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
                    match source.Entrypoint with
                    | NodeScript _ ->
                        runCommand workingDirectory "node" (entrypoint :: arguments)
        | EnsureFile _ ->
            { ExitCode = -1
              StandardOutput = String.Empty
              StandardError = "EnsureFile must be executed by the Conditor installer, not the process runner." }
