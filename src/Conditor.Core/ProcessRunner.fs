namespace Conditor.Core

open System
open System.Diagnostics

module ProcessRunner =
    let run workingDirectory (action: PlanAction) =
        let info = ProcessStartInfo()
        info.FileName <- action.Executable
        info.WorkingDirectory <- workingDirectory
        info.UseShellExecute <- false
        info.RedirectStandardOutput <- true
        info.RedirectStandardError <- true

        for argument in action.Arguments do
            info.ArgumentList.Add argument

        use childProcess = new Process()
        childProcess.StartInfo <- info

        if not (childProcess.Start()) then
            { ExitCode = -1
              StandardOutput = String.Empty
              StandardError = $"Unable to start '{action.Executable}'." }
        else
            let outputTask = childProcess.StandardOutput.ReadToEndAsync()
            let errorTask = childProcess.StandardError.ReadToEndAsync()
            childProcess.WaitForExit()

            { ExitCode = childProcess.ExitCode
              StandardOutput = outputTask.GetAwaiter().GetResult()
              StandardError = errorTask.GetAwaiter().GetResult() }
