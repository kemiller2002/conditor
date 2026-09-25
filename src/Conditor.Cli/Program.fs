open System
open System.IO
open Conditor.Core

type private ManifestSelection =
    { ManifestPath: string
      Preset: ResolvedPreset option }

let private usage () =
    Console.WriteLine "Conditor"
    Console.WriteLine "  conditor presets"
    Console.WriteLine "  conditor plan   [--preset NAME | --manifest PATH] [--target PATH]"
    Console.WriteLine "  conditor init   [--preset NAME | --manifest PATH] [--target PATH]"
    Console.WriteLine "  conditor verify [--manifest PATH] [--target PATH]"
    Console.WriteLine "  conditor doctor [--manifest PATH] [--target PATH]"
    Console.WriteLine "  conditor status [--manifest PATH] [--target PATH]"
    Console.WriteLine "  conditor repair [--manifest PATH] [--target PATH]"
    Console.WriteLine "  conditor start  [--preset NAME | --manifest PATH] [--check] [--launcher codex|claude] [--target PATH]"

let private optionValue name (args: string array) =
    args
    |> Array.tryFindIndex ((=) name)
    |> Option.bind (fun index ->
        if index + 1 < args.Length then Some args[index + 1] else None)

let private hasFlag name (args: string array) =
    args |> Array.contains name

let private writeErrors (errors: string list) =
    errors |> List.iter (fun (error: string) -> Console.Error.WriteLine error)

let private resolveManifestSelection command target (args: string array) =
    let presetName = optionValue "--preset" args
    let manifestPath = optionValue "--manifest" args

    match presetName, manifestPath with
    | Some _, Some _ ->
        Error [ "Choose either --preset or --manifest, not both." ]
    | Some presetName, None ->
        if command <> "plan" && command <> "init" && command <> "start" then
            Error [ $"--preset is supported by plan, init, and start; '{command}' uses the repository's conditor.json." ]
        else
            Presets.resolve presetName
            |> Result.map (fun preset ->
                { ManifestPath = preset.ManifestPath
                  Preset = Some preset })
    | None, Some manifestPath ->
        Ok
            { ManifestPath = Path.GetFullPath manifestPath
              Preset = None }
    | None, None ->
        Ok
            { ManifestPath = Path.Combine(target, "conditor.json") |> Path.GetFullPath
              Preset = None }

let private createPlan operation target selection manifest =
    Planner.create target operation manifest
    |> Result.map (fun plan ->
        match operation, selection.Preset with
        | Init, Some preset -> Presets.bindToTarget preset plan
        | _ -> plan)

let private run operation shouldExecute target selection =
    match Manifest.load selection.ManifestPath with
    | Error errors ->
        writeErrors errors
        2
    | Ok manifest ->
        match createPlan operation target selection manifest with
        | Error errors ->
            writeErrors errors
            3
        | Ok plan ->
            Console.WriteLine $"Conditor plan for '{plan.ProjectName}'"
            Installer.describe plan |> List.iter (fun (line: string) -> Console.WriteLine line)

            if not shouldExecute then
                0
            else
                match Installer.execute target selection.ManifestPath plan with
                | Ok(Some lockPath) ->
                    Console.WriteLine $"Conditor completed successfully. Lock file: {lockPath}"
                    0
                | Ok None ->
                    Console.WriteLine "Conditor completed successfully."
                    0
                | Error errors ->
                    writeErrors errors
                    4

let private withLauncherOverride launcherOverride (manifest: ProjectManifest) =
    match launcherOverride with
    | None -> Ok manifest
    | Some launcher ->
        match manifest.Execution with
        | None -> Error [ "--launcher requires an execution section in the Conditor manifest." ]
        | Some execution when not execution.Enabled ->
            Error [ "--launcher cannot enable execution that the Conditor manifest has disabled." ]
        | Some execution ->
            Ok { manifest with Execution = Some { execution with Launcher = Some launcher } }

let private runEstablishedStart checkOnly launcherOverride target manifestPath =
    match Manifest.load manifestPath with
    | Error errors ->
        writeErrors errors
        2
    | Ok loadedManifest ->
        match withLauncherOverride launcherOverride loadedManifest with
        | Error errors ->
            writeErrors errors
            5
        | Ok manifest when checkOnly ->
            match Execution.check target manifestPath manifest with
            | Error errors ->
                writeErrors errors
                5
            | Ok ready ->
                Console.WriteLine $"Execution ready: launcher={ready.Launcher}; mission={ready.Mission.Id}; state={ready.MissionState}; contract={ready.ContractPath}"
                0
        | Ok manifest ->
            match Execution.start target manifestPath manifest with
            | Error errors ->
                writeErrors errors
                6
            | Ok result ->
                if not (String.IsNullOrWhiteSpace result.StandardOutput) then
                    Console.WriteLine(result.StandardOutput.Trim())

                Console.WriteLine "Launcher exited successfully. Praxis remains authoritative for mission completion."
                0

let private presetTargetState target (preset: ResolvedPreset) =
    let targetManifest = Path.Combine(target, "conditor.json")
    let lockPath = Path.Combine(target, ".conditor", "lock.json")

    if File.Exists targetManifest then
        let existing = File.ReadAllText targetManifest

        if existing <> preset.Content then
            Error
                [ $"Target conditor.json does not match built-in preset '{preset.Id}'."
                  "Conditor will not replace a different project declaration during start." ]
        else
            Ok(File.Exists lockPath, targetManifest)
    else
        Ok(false, targetManifest)

let private runStart checkOnly launcherOverride target selection =
    match selection.Preset with
    | None ->
        runEstablishedStart checkOnly launcherOverride target selection.ManifestPath
    | Some preset ->
        match presetTargetState target preset with
        | Error errors ->
            writeErrors errors
            5
        | Ok(true, targetManifest) ->
            runEstablishedStart checkOnly launcherOverride target targetManifest
        | Ok(false, _) when checkOnly ->
            writeErrors
                [ $"Preset '{preset.Id}' has not established this repository yet."
                  "start --check is read-only. Run 'conditor init --preset <name>' first, or run 'conditor start --preset <name>' to initialize and launch." ]
            5
        | Ok(false, targetManifest) ->
            let initialization = run Init true target selection

            if initialization <> 0 then
                initialization
            else
                runEstablishedStart false launcherOverride target targetManifest

let private runRepair target manifestPath =
    match Manifest.load manifestPath with
    | Error errors ->
        writeErrors errors
        2
    | Ok manifest ->
        match Repair.reconcile target manifestPath manifest with
        | Ok() ->
            Console.WriteLine "Conditor repair completed successfully."
            0
        | Error errors ->
            writeErrors errors
            8

let private runStatus target manifestPath =
    match Manifest.load manifestPath with
    | Error errors ->
        writeErrors errors
        2
    | Ok manifest ->
        let report = Status.inspect target manifestPath manifest
        Console.WriteLine $"Project: {report.Project}"

        if report.Components.IsEmpty then
            Console.WriteLine "Components: none"
        else
            Console.WriteLine "Components:"

            for componentText in report.Components do
                Console.WriteLine $"  {componentText}"

        Console.WriteLine "Checks:"

        for check in report.Checks do
            Console.WriteLine $"  [{Status.stateText check.State}] {check.Name}: {check.Detail}"

        if Status.isHealthy report then 0 else 7

let private printPresets () =
    Console.WriteLine "Built-in Conditor presets:"

    for name in Presets.names do
        Console.WriteLine $"  {name}"

    0

[<EntryPoint>]
let main (args: string array) =
    if args.Length = 0 then
        usage ()
        1
    else
        let command = args[0].Trim().ToLowerInvariant()

        if command = "presets" then
            printPresets ()
        else
            let target =
                optionValue "--target" args
                |> Option.defaultValue (Directory.GetCurrentDirectory())
                |> Path.GetFullPath

            match resolveManifestSelection command target args with
            | Error errors ->
                writeErrors errors
                1
            | Ok selection ->
                match command with
                | "plan" -> run Init false target selection
                | "init" -> run Init true target selection
                | "verify" -> run Verify true target selection
                | "doctor" -> run Doctor true target selection
                | "status" -> runStatus target selection.ManifestPath
                | "repair" -> runRepair target selection.ManifestPath
                | "start" ->
                    runStart
                        (hasFlag "--check" args)
                        (optionValue "--launcher" args)
                        target
                        selection
                | _ ->
                    usage ()
                    1
