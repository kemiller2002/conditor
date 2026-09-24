open System
open System.IO
open Conditor.Core

let mutable failures = 0

let check name condition =
    if condition then
        Console.WriteLine $"PASS {name}"
    else
        failures <- failures + 1
        Console.Error.WriteLine $"FAIL {name}"

let withManifest (json: string) (test: string -> unit) =
    let path = Path.Combine(Path.GetTempPath(), $"conditor-{Guid.NewGuid():N}.json")

    try
        File.WriteAllText(path, json)
        test path
    finally
        if File.Exists path then
            File.Delete path

withManifest
    """{"schemaVersion":1,"name":"demo","components":[{"id":"praxis","version":"3.1.4"},{"id":"ordo"}]}"""
    (fun path ->
        match Manifest.load path with
        | Error errors ->
            let details = String.concat "; " errors
            check $"valid manifest parses: {details}" false
        | Ok manifest ->
            check "manifest name" (manifest.Name = "demo")
            check "manifest component count" (manifest.Components.Length = 2)

            match Planner.create "/tmp/demo" Init manifest with
            | Error errors ->
                let details = String.concat "; " errors
                check $"plan succeeds: {details}" false
            | Ok plan ->
                check "init emits install and verify per lifecycle component" (plan.Actions.Length = 4)
                check "explicit version is preserved" (plan.Components[0].Version = "3.1.4")
                check "default version resolves" (plan.Components[1].Version = "1.3.0")
                check
                    "registry package source is immutable"
                    (plan.Components[0].SourceReference = Some "@echelon-foundry/repository-operating-system@3.1.4"))

withManifest
    """{"schemaVersion":1,"name":"demo","components":[{"id":"communication-engineering","version":"1.0.0"}]}"""
    (fun path ->
        match Manifest.load path with
        | Error _ ->
            check "communication manifest parses" false
        | Ok manifest ->
            match Planner.create "/tmp/demo" Init manifest with
            | Error _ ->
                check "fixed source resolves" false
            | Ok plan ->
                let expectedSource =
                    Some "github:kemiller2002/communication-engineering#4590d2fe6f7e80b339117d3fbee5803f2dd39122"

                check "fixed source resolves to commit" (plan.Components[0].SourceReference = expectedSource)

withManifest
    """{"schemaVersion":1,"name":"demo","components":[{"id":"communication-engineering","version":"9.9.9"}]}"""
    (fun path ->
        match Manifest.load path with
        | Error _ ->
            check "unmapped version manifest parses" false
        | Ok manifest ->
            match Planner.create "/tmp/demo" Init manifest with
            | Error errors ->
                check
                    "unmapped fixed-source version rejected"
                    (errors |> List.exists (fun error -> error.Contains("no immutable distribution mapping")))
            | Ok _ ->
                check "unmapped fixed-source version rejected" false)

withManifest
    """{"schemaVersion":1,"name":"demo","components":[{"id":"praxis"},{"id":"praxis"}]}"""
    (fun path ->
        match Manifest.load path with
        | Error errors ->
            check "duplicate component rejected" (errors |> List.exists (fun error -> error.Contains("more than once")))
        | Ok _ ->
            check "duplicate component rejected" false)

withManifest
    """{"schemaVersion":1,"name":"demo","components":[{"id":"forma"}]}"""
    (fun path ->
        match Manifest.load path with
        | Error _ ->
            check "forma manifest parses" false
        | Ok manifest ->
            match Planner.create "/tmp/demo" Init manifest with
            | Error errors ->
                check
                    "application package binding is explicit"
                    (errors |> List.exists (fun error -> error.Contains("will not guess where to install it")))
            | Ok _ ->
                check "application package binding is explicit" false)

let exitCode =
    if failures = 0 then
        Console.WriteLine "All Conditor tests passed."
        0
    else
        Console.Error.WriteLine $"{failures} Conditor test(s) failed."
        1

[<EntryPoint>]
let main _ = exitCode
