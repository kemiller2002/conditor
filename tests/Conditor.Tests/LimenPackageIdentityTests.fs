module LimenPackageIdentityTests

// Limen 0.7.0 is distributed as @echelon-foundry/limen. Releases through 0.6.2
// were distributed as @echelon-foundry/typescript-wasm-kernel, which is now
// deprecated. A component descriptor records earlier package identities in
// `historicalPackages`, so each qualified version resolves to the exact name it
// was published under and legacy plans stay reproducible.

open System
open System.IO
open Conditor.Core

[<Literal>]
let private CurrentLimen = "@echelon-foundry/limen"

[<Literal>]
let private LegacyLimen = "@echelon-foundry/typescript-wasm-kernel"

let private withTarget (action: string -> 'a) : 'a =
    let target = Path.Combine(Path.GetTempPath(), $"conditor-limen-identity-{Guid.NewGuid():N}")
    Directory.CreateDirectory target |> ignore

    try
        action target
    finally
        if Directory.Exists target then
            Directory.Delete(target, true)

let private scaffoldPlan (limenVersion: string) =
    withTarget (fun target ->
        $"{{\"schemaVersion\":1,\"name\":\"limen-identity\",\"components\":[{{\"id\":\"limen\",\"version\":\"{limenVersion}\"}}],\"scaffold\":{{\"kind\":\"fsharp-limen-web\",\"name\":\"limen-identity\"}}}}"
        |> Manifest.parseText
        |> Result.bind (Planner.create target Init))

let private scaffoldFile (relativePath: string) (plan: InstallationPlan) =
    plan.Actions
    |> List.tryPick (fun action ->
        match action.Execution with
        | EnsureFile(path, content) when path = relativePath -> Some content
        | _ -> None)

let private limenComponent (plan: InstallationPlan) =
    plan.Components |> List.tryFind (fun item -> item.Id = "limen")

/// A minimal valid npm descriptor with the given extra properties spliced in.
let private descriptor (extra: string) (defaultVersion: string) (qualified: string list) =
    let versions = qualified |> List.map (fun version -> $"\"{version}\"") |> String.concat ","

    $"{{\"schemaVersion\":1,\"id\":\"fixture\",\"displayName\":\"Fixture\",\"distribution\":\"npm\",\"package\":\"@example/current\",{extra}\"defaultVersion\":\"{defaultVersion}\",\"qualifiedVersions\":[{versions}],\"applicationBinding\":\"npm\",\"initArguments\":[],\"verifyArguments\":[],\"doctorArguments\":[],\"upgradeArguments\":[]}}"

let private refusedWith (fragment: string) (outcome: Result<ComponentDescriptor, string list>) =
    match outcome with
    | Ok _ -> false
    | Error errors -> errors |> List.exists (fun error -> error.Contains(fragment, StringComparison.Ordinal))

let run (check: string -> bool -> unit) =
    // --- Embedded Limen descriptor ----------------------------------------------
    match Registry.tryFind "limen", Registry.qualifiedVersions "limen" with
    | Some limen, Some qualified ->
        check "Limen 0.7.0 is qualified alongside the legacy 0.6.1 and 0.6.2 releases" (qualified = Set.ofList [ "0.6.1"; "0.6.2"; "0.7.0" ])
        check "Limen's current package identity is @echelon-foundry/limen" (limen.Package = CurrentLimen)
        check "Limen's default version is 0.7.0" (limen.DefaultVersion = "0.7.0")
        check "Limen 0.7.0 resolves to @echelon-foundry/limen" (ComponentDefinition.packageFor "0.7.0" limen = CurrentLimen)

        check
            "Limen 0.6.1 and 0.6.2 keep their historical @echelon-foundry/typescript-wasm-kernel identity"
            (ComponentDefinition.packageFor "0.6.1" limen = LegacyLimen
             && ComponentDefinition.packageFor "0.6.2" limen = LegacyLimen)
    | _ -> check "embedded Limen descriptor is present" false

    check
        "every qualified version of every embedded component resolves to exactly one package identity"
        (Registry.descriptors
         |> List.forall (fun descriptor ->
             descriptor.QualifiedVersions
             |> Set.forall (fun version ->
                 let owners =
                     descriptor.Definition.HistoricalPackages
                     |> List.filter (fun entry -> entry.Versions.Contains version)

                 owners.Length <= 1
                 && (owners.IsEmpty
                     || ComponentDefinition.packageFor version descriptor.Definition = owners.Head.Package))))

    // --- Planning and scaffolding use the version's own identity -------------------
    match scaffoldPlan "0.7.0" with
    | Error errors ->
        let details = String.concat "; " errors
        check $"Limen 0.7.0 scaffold plans: {details}" false
    | Ok plan ->
        check
            "Limen 0.7.0 resolves to the @echelon-foundry/limen package and npm binding"
            (limenComponent plan
             |> Option.exists (fun item ->
                 item.Version = "0.7.0"
                 && item.Package = CurrentLimen
                 && item.SourceReference = Some $"npm:{CurrentLimen}@0.7.0"))

        check
            "Limen 0.7.0 scaffold binds @echelon-foundry/limen and never the deprecated package"
            (scaffoldFile "src/kernel/package.json" plan
             |> Option.exists (fun text ->
                 text.Contains($"\"{CurrentLimen}\": \"0.7.0\"", StringComparison.Ordinal)
                 && not (text.Contains(LegacyLimen, StringComparison.Ordinal))))

        check
            "Limen 0.7.0 scaffold kernel imports the protocol from @echelon-foundry/limen"
            (scaffoldFile "src/kernel/bootstrap.ts" plan
             |> Option.exists (fun text ->
                 text.Contains($"from \"{CurrentLimen}/protocol\"", StringComparison.Ordinal)
                 && not (text.Contains(LegacyLimen, StringComparison.Ordinal))))

    for legacy in [ "0.6.1"; "0.6.2" ] do
        match scaffoldPlan legacy with
        | Error errors ->
            let details = String.concat "; " errors
            check $"legacy Limen {legacy} scaffold plans: {details}" false
        | Ok plan ->
            check
                $"legacy Limen {legacy} reproduces its @echelon-foundry/typescript-wasm-kernel binding and import"
                (limenComponent plan
                 |> Option.exists (fun item ->
                     item.Package = LegacyLimen
                     && item.SourceReference = Some $"npm:{LegacyLimen}@{legacy}")
                 && scaffoldFile "src/kernel/package.json" plan
                    |> Option.exists (fun text ->
                        text.Contains($"\"{LegacyLimen}\": \"{legacy}\"", StringComparison.Ordinal)
                        && not (text.Contains($"\"{CurrentLimen}\"", StringComparison.Ordinal)))
                 && scaffoldFile "src/kernel/bootstrap.ts" plan
                    |> Option.exists (fun text -> text.Contains($"from \"{LegacyLimen}/protocol\"", StringComparison.Ordinal)))

    check
        "an unqualified Limen version is still refused"
        (match scaffoldPlan "0.6.3" with
         | Error errors -> errors |> List.exists (fun error -> error.Contains("'limen' version '0.6.3' is not qualified"))
         | Ok _ -> false)

    // --- Clean-room preset ---------------------------------------------------------
    match Presets.resolve "clean-room" with
    | Error errors ->
        let details = String.concat "; " errors
        check $"embedded clean-room preset resolves: {details}" false
    | Ok preset ->
        withTarget (fun target ->
            match Manifest.load preset.ManifestPath |> Result.bind (Planner.create target Init) with
            | Error errors ->
                let details = String.concat "; " errors
                check $"embedded clean-room preset plans: {details}" false
            | Ok plan ->
                check
                    "clean-room preset installs Limen 0.7.0 as @echelon-foundry/limen"
                    (limenComponent plan
                     |> Option.exists (fun item -> item.Version = "0.7.0" && item.Package = CurrentLimen))

                check
                    "clean-room scaffold names @echelon-foundry/limen and not the deprecated package"
                    (scaffoldFile "src/kernel/package.json" plan
                     |> Option.exists (fun text ->
                         text.Contains($"\"{CurrentLimen}\": \"0.7.0\"", StringComparison.Ordinal)
                         && not (text.Contains(LegacyLimen, StringComparison.Ordinal)))))

    // --- historicalPackages descriptor validation ----------------------------------
    let parse text = ComponentDescriptors.parse "fixture.component.json" text

    check
        "a descriptor without historicalPackages keeps a single package identity"
        (match parse (descriptor "" "1.0.0" [ "1.0.0" ]) with
         | Ok parsed ->
             parsed.Definition.HistoricalPackages.IsEmpty
             && ComponentDefinition.packageFor "1.0.0" parsed.Definition = "@example/current"
         | Error _ -> false)

    check
        "a valid historicalPackages entry maps its versions to the earlier identity"
        (match
            parse (
                descriptor
                    "\"historicalPackages\":[{\"package\":\"@example/legacy\",\"versions\":[\"1.0.0\"]}],"
                    "2.0.0"
                    [ "1.0.0"; "2.0.0" ]
            )
         with
         | Ok parsed ->
             ComponentDefinition.packageFor "1.0.0" parsed.Definition = "@example/legacy"
             && ComponentDefinition.packageFor "2.0.0" parsed.Definition = "@example/current"
         | Error _ -> false)

    check
        "historicalPackages refuses a version that is not qualified"
        (parse (
            descriptor
                "\"historicalPackages\":[{\"package\":\"@example/legacy\",\"versions\":[\"0.9.0\"]}],"
                "2.0.0"
                [ "1.0.0"; "2.0.0" ]
         )
         |> refusedWith "names version '0.9.0' under '@example/legacy', but it is not present in qualifiedVersions")

    check
        "historicalPackages refuses an entry that repeats the current package"
        (parse (
            descriptor
                "\"historicalPackages\":[{\"package\":\"@example/current\",\"versions\":[\"1.0.0\"]}],"
                "2.0.0"
                [ "1.0.0"; "2.0.0" ]
         )
         |> refusedWith "repeats the current package '@example/current'")

    check
        "historicalPackages refuses a default version that is not on the current package"
        (parse (
            descriptor
                "\"historicalPackages\":[{\"package\":\"@example/legacy\",\"versions\":[\"2.0.0\"]}],"
                "2.0.0"
                [ "1.0.0"; "2.0.0" ]
         )
         |> refusedWith "the default version must use the current package")

    check
        "historicalPackages refuses a version claimed by two earlier identities"
        (parse (
            descriptor
                "\"historicalPackages\":[{\"package\":\"@example/legacy-a\",\"versions\":[\"1.0.0\"]},{\"package\":\"@example/legacy-b\",\"versions\":[\"1.0.0\"]}],"
                "2.0.0"
                [ "1.0.0"; "2.0.0" ]
         )
         |> refusedWith "assigns version '1.0.0' to more than one package")

    check
        "historicalPackages refuses the same earlier identity declared twice"
        (parse (
            descriptor
                "\"historicalPackages\":[{\"package\":\"@example/legacy\",\"versions\":[\"1.0.0\"]},{\"package\":\"@example/legacy\",\"versions\":[\"1.1.0\"]}],"
                "2.0.0"
                [ "1.0.0"; "1.1.0"; "2.0.0" ]
         )
         |> refusedWith "declares package '@example/legacy' more than once")

    check
        "historicalPackages refuses an entry with no versions"
        (parse (
            descriptor
                "\"historicalPackages\":[{\"package\":\"@example/legacy\",\"versions\":[]}],"
                "2.0.0"
                [ "2.0.0" ]
         )
         |> refusedWith "must list at least one version")

    check
        "historicalPackages refuses an entry with an unknown property"
        (parse (
            descriptor
                "\"historicalPackages\":[{\"package\":\"@example/legacy\",\"versions\":[\"1.0.0\"],\"through\":\"1.0.0\"}],"
                "2.0.0"
                [ "1.0.0"; "2.0.0" ]
         )
         |> refusedWith "do not support property 'through'")

    check
        "historicalPackages refuses a non-array value"
        (parse (descriptor "\"historicalPackages\":{\"package\":\"@example/legacy\"}," "2.0.0" [ "2.0.0" ])
         |> refusedWith "'historicalPackages' must be an array")
