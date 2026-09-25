namespace Conditor.Core

open System

module Doctor =
    type FindingSeverity =
        | Info
        | Warning
        | Error

    type Finding =
        { Code: string
          Severity: FindingSeverity
          Area: string
          Detail: string
          Remediation: string option }

    type Report =
        { Project: string
          Healthy: bool
          Findings: Finding list }

    let severityText =
        function
        | Info -> "info"
        | Warning -> "warning"
        | Error -> "error"

    let private addStatusFindings (findings: ResizeArray<Finding>) (report: Status.ProjectStatus) =
        for check in report.Checks do
            let code, remediation =
                match check.Name with
                | "lock" ->
                    "COND-DOC-LOCK",
                    Some "If the declaration is unchanged, run 'conditor repair'. For an intentional supported version change, run 'conditor upgrade --check'."
                | "requirements" ->
                    "COND-DOC-REQ",
                    Some "Restore or remove locally drifted governing artifacts, then run 'conditor repair'. Conditor will not overwrite changed requirement files."
                | "components" ->
                    "COND-DOC-COMP",
                    Some "Run the named component doctor or 'conditor repair'. Resolve local edits to tool-owned files explicitly."
                | "execution" ->
                    "COND-DOC-EXEC",
                    Some "Resolve the reported contract, mission, or verification problem before starting or resuming an agent."
                | other ->
                    $"COND-DOC-{other.ToUpperInvariant()}", None

            let severity =
                match check.State with
                | Status.Healthy -> Info
                | Status.Informational -> Info
                | Status.Failed -> Error

            findings.Add
                { Code = code
                  Severity = severity
                  Area = check.Name
                  Detail = check.Detail
                  Remediation = if severity = Error then remediation else None }

    let private addCompatibilityFindings (findings: ResizeArray<Finding>) manifest =
        for error in Compatibility.validate manifest do
            findings.Add
                { Code = "COND-DOC-COMPAT"
                  Severity = Error
                  Area = "compatibility"
                  Detail = error
                  Remediation =
                    Some "Run 'conditor compatibility' and edit conditor.json to a qualified component/version/dependency set." }

    let private lifecycleDoctorFindings target manifest =
        match Planner.create target Doctor manifest with
        | Result.Error errors ->
            errors
            |> List.map (fun error ->
                { Code = "COND-DOC-LIFECYCLE-PLAN"
                  Severity = Error
                  Area = "lifecycle"
                  Detail = error
                  Remediation = Some "Resolve manifest or compatibility errors before lifecycle diagnosis." })
        | Ok plan ->
            plan.Actions
            |> List.map (fun action ->
                let result = ProcessRunner.run target action
                let detail =
                    [ result.StandardOutput.Trim(); result.StandardError.Trim() ]
                    |> List.filter (String.IsNullOrWhiteSpace >> not)
                    |> function
                        | [] when result.ExitCode = 0 -> $"{action.ComponentId} doctor passed."
                        | [] -> $"{action.ComponentId} doctor exited {result.ExitCode}."
                        | lines -> String.Join(Environment.NewLine, lines)

                if result.ExitCode = 0 then
                    { Code = "COND-DOC-LIFECYCLE"
                      Severity = Info
                      Area = action.ComponentId
                      Detail = detail
                      Remediation = None }
                else
                    { Code = "COND-DOC-LIFECYCLE"
                      Severity = Error
                      Area = action.ComponentId
                      Detail = detail
                      Remediation =
                        Some $"Run the {action.ComponentId} lifecycle remediation named above, then run 'conditor repair' and 'conditor status'." })

    let inspect target manifestPath manifest =
        let findings = ResizeArray<Finding>()

        addCompatibilityFindings findings manifest

        let status = Status.inspect target manifestPath manifest
        addStatusFindings findings status

        lifecycleDoctorFindings target manifest
        |> List.iter findings.Add

        let result = List.ofSeq findings

        { Project = manifest.Name
          Healthy = result |> List.forall (fun finding -> finding.Severity <> Error)
          Findings = result }
