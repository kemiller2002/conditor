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

    /// The executable names a bare command can be on this platform.
    let private candidates (executable: string) =
        if OperatingSystem.IsWindows() then [ executable; executable + ".exe"; executable + ".cmd" ] else [ executable ]

    /// Resolves the program a process request names, trying the `preferred`
    /// directories before PATH. On Unix, .NET looks for a bare command name in
    /// the Conditor process's own working directory before PATH, so a
    /// repository-local launcher (for example a governed repository's
    /// ./praxis) would shadow the host tool being probed. A bare name therefore
    /// resolves through the preferred directories and PATH only; a name with a
    /// directory part is used as given. Windows keeps the platform's own
    /// resolution for anything the preferred directories do not hold.
    let resolveExecutableIn (preferred: string list) (executable: string) =
        if hasDirectoryPart executable then
            Some executable
        else
            let inPreferred =
                preferred
                |> List.collect (fun directory -> candidates executable |> List.map (fun name -> Path.Combine(directory, name)))
                |> List.tryFind File.Exists

            match inPreferred with
            | Some found -> Some found
            | None when OperatingSystem.IsWindows() -> Some executable
            | None ->
                searchPath ()
                |> List.map (fun directory -> Path.Combine(directory, executable))
                |> List.tryFind File.Exists

    let resolveExecutable (executable: string) = resolveExecutableIn [] executable

    let private start (preferred: string list) workingDirectory (executable: string) (resolved: string) arguments =
        try
            let info = ProcessStartInfo()
            info.FileName <- resolved
            info.WorkingDirectory <- workingDirectory
            info.UseShellExecute <- false
            info.RedirectStandardOutput <- true
            info.RedirectStandardError <- true

            // A tool that runs another tool (praxis running ordo) must find the
            // same copies Conditor resolved, so the child sees the preferred
            // directories first on its PATH as well.
            if not preferred.IsEmpty then
                info.Environment["PATH"] <- String.Join(string Path.PathSeparator, preferred @ searchPath ())

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

    /// Runs a command attached to this console: its output streams to the
    /// terminal as it happens instead of being captured. For long-running
    /// interactive work (a headless agent); returns the exit code.
    let runAttached workingDirectory (executable: string) (arguments: string list) =
        match resolveExecutable executable with
        | None -> Error $"Unable to execute '{executable}': not found on PATH."
        | Some resolved ->
            try
                let info = ProcessStartInfo()
                info.FileName <- resolved
                info.WorkingDirectory <- workingDirectory
                info.UseShellExecute <- false

                for argument in arguments do
                    info.ArgumentList.Add argument

                use childProcess = new Process()
                childProcess.StartInfo <- info

                if childProcess.Start() then
                    childProcess.WaitForExit()
                    Ok childProcess.ExitCode
                else
                    Error $"Unable to start '{executable}'."
            with ex ->
                Error $"Unable to execute '{executable}': {ex.Message}"

    /// Runs a command, resolving a bare name through `preferred` before PATH.
    let runProcessIn (preferred: string list) workingDirectory (executable: string) arguments =
        match resolveExecutableIn preferred executable with
        | Some resolved -> start preferred workingDirectory executable resolved arguments
        | None ->
            { ExitCode = -1
              StandardOutput = String.Empty
              StandardError = $"Unable to execute '{executable}': not found on PATH." }

    let runProcess workingDirectory (executable: string) arguments =
        runProcessIn [] workingDirectory executable arguments

    /// Runs one plan action, resolving its executables through `preferred`
    /// before PATH -- for example the workstation bin directory a current
    /// upgrade has just installed into.
    let runIn (preferred: string list) workingDirectory (action: PlanAction) =
        let runProcess = runProcessIn preferred

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
        | EnsureNugetFeed _
        | VerifyNugetFeed _ ->
            { ExitCode = -1
              StandardOutput = String.Empty
              StandardError = "NuGet feed actions must be executed by the Conditor installer, not the process runner." }

    let run workingDirectory (action: PlanAction) = runIn [] workingDirectory action
