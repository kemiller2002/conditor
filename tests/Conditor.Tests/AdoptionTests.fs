module AdoptionTests

open System
open System.IO
open Conditor.Core

let private result exitCode output error =
    { ExitCode = exitCode
      StandardOutput = output
      StandardError = error }

let private withTarget action =
    let target = Path.Combine(Path.GetTempPath(), $"conditor-adoption-{Guid.NewGuid():N}")
    Directory.CreateDirectory target |> ignore

    try
        action target
    finally
        if Directory.Exists target then
            Directory.Delete(target, true)

let private healthyPraxisRunner _ executable arguments =
    let name = Path.GetFileName executable

    if name = "praxis" && arguments = [ "--version" ] then
        result 0 "ros-fs 3.6.0" ""
    elif name = "praxis" && arguments = [ "verify"; "--strict" ] then
        result 0 "healthy" ""
    else
        result -1 "" $"Unable to execute '{executable}'."

let run check =
    withTarget (fun target ->
        let plan = Adoption.planWith healthyPraxisRunner target None

        check "adoption discovers verified Praxis" (plan.Components |> List.map _.Id = [ "praxis" ])
        check "adoption records exact qualified version" (plan.Components.Head.Version = "3.6.0")
        check "adoption proposal is mutation-free" (not (File.Exists(Path.Combine(target, "conditor.json"))))
        check "adoption proposal has no refusal for healthy component" plan.Refusals.IsEmpty
        check
            "application bindings are explicitly skipped rather than guessed"
            (plan.Observations
             |> List.exists (fun observation ->
                 observation.ComponentId = "forma"
                 && observation.Status = "skipped"
                 && observation.Detail.Contains("explicit project target")))

        match Adoption.applyWith healthyPraxisRunner target None "bad-digest" with
        | Ok _ -> check "adoption rejects stale authorization digest" false
        | Error errors ->
            check
                "adoption rejects stale authorization digest"
                (errors |> List.exists (fun error -> error.Contains("does not match")))
            check
                "stale adoption authorization performs no mutation"
                (not (File.Exists(Path.Combine(target, "conditor.json"))))

        match Adoption.applyWith healthyPraxisRunner target None plan.Digest with
        | Error errors ->
            check $"authorized adoption succeeds: {String.concat "; " errors}" false
        | Ok adopted ->
            check "authorized adoption writes manifest" (File.Exists adopted.ManifestPath)
            check "authorized adoption writes lock" (File.Exists adopted.LockPath)

            match Manifest.load adopted.ManifestPath with
            | Error errors ->
                check $"adopted manifest parses: {String.concat "; " errors}" false
            | Ok manifest ->
                check
                    "adopted manifest records only proven lifecycle component"
                    (manifest.Components
                     |> List.exists (fun entry ->
                         entry.Id = "praxis"
                         && entry.Version = Some "3.6.0"
                         && entry.Required))

            let second = Adoption.planWith healthyPraxisRunner target None

            check
                "adoption refuses repository already governed by Conditor"
                (second.Refusals
                 |> List.exists (fun refusal -> refusal.Contains("already contains Conditor governance"))))

    withTarget (fun target ->
        let unknownVersionRunner _ executable arguments =
            let name = Path.GetFileName executable

            if name = "praxis" && arguments = [ "--version" ] then
                result 0 "ros-fs 99.0.0" ""
            else
                result -1 "" "missing"

        let plan = Adoption.planWith unknownVersionRunner target None

        check
            "adoption refuses installed but unqualified version"
            (plan.Refusals
             |> List.exists (fun refusal -> refusal.Contains("not unambiguously qualified")))
        check "unqualified component is not adopted" (plan.Components |> List.forall (fun item -> item.Id <> "praxis")))

    withTarget (fun target ->
        let unhealthyRunner _ executable arguments =
            let name = Path.GetFileName executable

            if name = "praxis" && arguments = [ "--version" ] then
                result 0 "ros-fs 3.6.0" ""
            elif name = "praxis" && arguments = [ "verify"; "--strict" ] then
                result 4 "" "verification failed"
            else
                result -1 "" "missing"

        let plan = Adoption.planWith unhealthyRunner target None

        check
            "adoption refuses detected component that fails verify"
            (plan.Refusals
             |> List.exists (fun refusal -> refusal.Contains("did not verify successfully")))
        check
            "failed verification does not create Conditor governance"
            (not (File.Exists(Path.Combine(target, "conditor.json")))))

    withTarget (fun target ->
        let ambiguousRunner _ executable arguments =
            let name = Path.GetFileName executable

            if name = "praxis" && arguments = [ "--version" ] then
                result 0 "compatibility: 3.1.4 current: 3.6.0" ""
            else
                result -1 "" "missing"

        let plan = Adoption.planWith ambiguousRunner target None

        check
            "adoption refuses ambiguous version identity"
            (plan.Refusals
             |> List.exists (fun refusal -> refusal.Contains("version identity is ambiguous"))))
