namespace Conditor.Core

open System
open System.Diagnostics
open System.IO

module ProcessRunner =
    let private hasDirectoryPart (executable: string) =
        Path.IsPathRooted executable
        || executable.IndexOfAny [| Path.DirectorySeparatorChar; Path.AltDirectorySeparatorChar |] >= 0

    let private searchPath () =
        Environment.GetEnvironmentVariable "PATH"
        |> Option.ofObj
        |> Option.map (fun value ->
            value.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            |> Array.toList)
        |> Option.defaultValue []

    /// Resolves the program a process request names. On Unix, .NET looks for a
    /// bare command name in the Conditor process's own working directory before
    /// PATH, so a repository-local launcher (for example a governed
    /// repository's ./praxis) would shadow the host tool being probed. A bare
    /// name therefore resolves through PATH only; a name with a directory part
    /// is used as given. Windows keeps the platform's own resolution.
    let resolveExecutable (executable: string) =
        if OperatingSystem.IsWindows() || hasDirectoryPart executable then
            Some executable
        else
            searchPath ()
            |> List.map (fun directory -> Path.Combine(directory, executable))
            |> List.tryFind File.Exists

    let private start workingDirectory (executable: string) (resolved: string) arguments =
        try
            let info = ProcessStartInfo()
            info.FileName <- resolved
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

    let runProcess workingDirectory (executable: string) arguments =
        match resolveExecutable executable with
        | Some resolved -> start workingDirectory executable resolved arguments
        | None ->
            { ExitCode = -1
              StandardOutput = String.Empty
              StandardError = $"Unable to execute '{executable}': not found on PATH." }

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
