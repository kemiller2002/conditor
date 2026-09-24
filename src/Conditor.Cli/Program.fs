open System
open System.IO
open Conditor.Core

let private usage () =
    Console.WriteLine "Conditor"
    Console.WriteLine "  conditor plan   [--target PATH] [--manifest PATH]"
    Console.WriteLine "  conditor init   [--target PATH] [--manifest PATH]"
    Console.WriteLine "  conditor verify [--target PATH] [--manifest PATH]"
    Console.WriteLine "  conditor doctor [--target PATH] [--manifest PATH]"

let private optionValue name (args: string array) =
    args
    |> Array.tryFindIndex ((=) name)
    |> Option.bind (fun index ->
        if index + 1 < args.Length then Some args[index + 1] else None)

let private run operation shouldExecute target manifestPath =
    match Manifest.load manifestPath with
    | Error errors ->
        errors |> List.iter Console.Error.WriteLine
        2
    | Ok manifest ->
        match Planner.create target operation manifest with
        | Error errors ->
            errors |> List.iter Console.Error.WriteLine
            3
        | Ok plan ->
            Console.WriteLine $"Conditor plan for '{plan.ProjectName}'"
            Installer.describe plan |> List.iter Console.WriteLine

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
                    errors |> List.iter Console.Error.WriteLine
                    4

[<EntryPoint>]
let main args =
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
        | _ ->
            usage ()
            1
