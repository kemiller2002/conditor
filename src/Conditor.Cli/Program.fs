open System
open System.IO
open Conditor.Core

let private usage () =
    Console.WriteLine "Conditor"
    Console.WriteLine "  conditor plan   [--target PATH] [--manifest PATH]"
    Console.WriteLine "  conditor init   [--target PATH] [--manifest PATH]"
    Console.WriteLine "  conditor verify [--target PATH] [--manifest PATH]"
    Console.WriteLine "  conditor doctor [--target PATH] [--manifest PATH]"
    Console.WriteLine "  conditor start  [--check] [--target PATH] [--manifest PATH]"

let private optionValue name (args: string array) =
    args
    |> Array.tryFindIndex ((=) name)
    |> Option.bind (fun index ->
        if index + 1 < args.Length then Some args[index + 1] else None)

let private hasFlag name (args: string array) =
    args |> Array.contains name

let private writeErrors (errors: string list) =
    errors |> List.iter (fun (error: string) -> Console.Error.WriteLine error)

let private run operation shouldExecute target manifestPath =
    match Manifest.load manifestPath with
    | Error errors ->
        writeErrors errors
        2
    | Ok manifest ->
        match Planner.create target operation manifest with
        | Error errors ->
            writeErrors errors
            3
        | Ok plan ->
            Console.WriteLine $"Conditor plan for '{plan.ProjectName}'"
            Installer.describe plan |> List.iter (fun (line: string) -> Console.WriteLine line)

            if not shouldExecute then
                0
            else
                match Installer.execute target manifestPath plan with
                | Ok(Some lockPath) ->
                    Console.WriteLine $"Conditor completed successfully. Lock file: {lockPath}"
                    0
                | Ok None ->
                    Console.WriteLine "Conditor completed successfully."
                    0
                | Error errors ->
                    writeErrors errors
                    4

let private runStart checkOnly target manifestPath =
    match Manifest.load manifestPath with
    | Error errors ->
        writeErrors errors
        2
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

[<EntryPoint>]
let main (args: string array) =
    if args.Length = 0 then
        usage ()
        1
    else
        let target =
            optionValue "--target" args
            |> Option.defaultValue (Directory.GetCurrentDirectory())
            |> Path.GetFullPath

        let manifestPath =
            optionValue "--manifest" args
            |> Option.defaultValue (Path.Combine(target, "conditor.json"))
            |> Path.GetFullPath

        match args[0].Trim().ToLowerInvariant() with
        | "plan" -> run Init false target manifestPath
        | "init" -> run Init true target manifestPath
        | "verify" -> run Verify true target manifestPath
        | "doctor" -> run Doctor true target manifestPath
        | "start" -> runStart (hasFlag "--check" args) target manifestPath
        | _ ->
            usage ()
            1
