module FormaWorkflowComponentTests

// Forma workflow (@echelon-foundry/forma-workflow) is the Forma workflow
// layout engine, an npm application binding like Forma and Folio. Conditor
// qualifies exactly 1.2.0 (npm registry tarball, integrity
// sha512-6xopcnEOFN/3n9ItU6V+YLFdWzekDGlKjDxFdyGe4mJz15ZId3G505zNWY2gv+MwAuixZZRUHVQW13mvSAUERw==)
// and 2.0.0 (integrity
// sha512-2MFROdc7ox/4/RFCx99M8CM83px+IzNCUBdUzf85gkjbftVeYFlzCYgJz5tPxxtHbB1MrorrUMFzKQX4VvHuZw==).
// 1.2.0's published peerDependencies require @echelon-foundry/design-system
// >=0.4.0, and it emits the ef- names Forma 1.0.0 renamed, so it needs Forma
// 0.4.1 or later and below 1.0.0. 2.0.0, the Forma 1.x release, requires
// >=1.0.0.

open System
open System.IO
open System.Text.Json
open Conditor.Core

[<Literal>]
let private Package = "@echelon-foundry/forma-workflow"

let private withTarget (action: string -> 'a) : 'a =
    let target = Path.Combine(Path.GetTempPath(), $"conditor-forma-workflow-{Guid.NewGuid():N}")
    Directory.CreateDirectory target |> ignore

    try
        action target
    finally
        if Directory.Exists target then
            Directory.Delete(target, true)

let private manifestText (components: (string * string) list) (scaffold: bool) =
    let entries =
        components
        |> List.map (fun (id, version) -> $"{{\"id\":\"{id}\",\"version\":\"{version}\"}}")
        |> String.concat ","

    let scaffoldText =
        if scaffold then ",\"scaffold\":{\"kind\":\"fsharp-limen-web\",\"name\":\"workflow-demo\"}" else ""

    $"{{\"schemaVersion\":1,\"name\":\"workflow-demo\",\"components\":[{entries}]{scaffoldText}}}"

let private planIn target components scaffold =
    manifestText components scaffold |> Manifest.parseText |> Result.bind (Planner.create target Init)

let private refusedWith (fragment: string) (outcome: Result<InstallationPlan, string list>) =
    match outcome with
    | Ok _ -> false
    | Error errors -> errors |> List.exists (fun error -> error.Contains(fragment, StringComparison.Ordinal))

let private scaffoldFile (relativePath: string) (plan: InstallationPlan) =
    plan.Actions
    |> List.tryPick (fun action ->
        match action.Execution with
        | EnsureFile(path, content) when path = relativePath -> Some content
        | _ -> None)

/// An application package.json that already binds the given packages: the
/// explicit binding target an established repository offers (CON-333).
let private writePackageJson target (dependencies: (string * string) list) =
    let body =
        dependencies
        |> List.map (fun (package, specifier) -> $"\"{package}\":\"{specifier}\"")
        |> String.concat ","

    File.WriteAllText(Path.Combine(target, "package.json"), $"{{\"name\":\"app\",\"private\":true,\"dependencies\":{{{body}}}}}")

let private webComponents =
    [ "limen", "0.9.1"; "forma", "0.13.0"; "folio", "0.4.1"; "forma-workflow", "1.2.0" ]

let run (check: string -> bool -> unit) =
    // --- Embedded descriptor -------------------------------------------------
    match Registry.tryFind "forma-workflow", Registry.qualifiedVersions "forma-workflow" with
    | Some definition, Some qualified ->
        check "forma-workflow is a built-in npm application binding of @echelon-foundry/forma-workflow"
            (definition.Distribution = NpmPackage
             && definition.ApplicationBinding = Some NpmDependency
             && definition.Package = Package
             && definition.LifecycleSource.IsNone
             && definition.HistoricalPackages.IsEmpty)

        check "forma-workflow qualifies exactly 1.2.0, its default, and 2.0.0"
            (qualified = Set.ofList [ "1.2.0"; "2.0.0" ] && definition.DefaultVersion = "1.2.0")

        check "forma-workflow 1.2.0 and 2.0.0 are distributed through the npm registry, not a release asset"
            definition.ReleaseArtifacts.IsEmpty

        check "forma-workflow resolves 1.2.0 and 2.0.0 to @echelon-foundry/forma-workflow"
            (ComponentDefinition.packageFor "1.2.0" definition = Package
             && ComponentDefinition.packageFor "2.0.0" definition = Package)
    | _ -> check "the embedded forma-workflow descriptor is present" false

    check "forma-workflow carries a descriptor digest for the lock"
        (Registry.descriptorSha256 "forma-workflow" |> Option.exists (fun digest -> digest.Length = 64))

    check "forma-workflow's compatibility versions are descriptor-derived"
        (Compatibility.supported
         |> List.exists (fun item -> item.Id = "forma-workflow" && item.Versions = Set.ofList [ "1.2.0"; "2.0.0" ]))

    check "the compatibility graph records forma-workflow's Forma peer requirement"
        (Compatibility.requirements
         |> List.exists (fun requirement -> requirement.Subject = "component:forma-workflow" && requirement.Requires = "forma"))

    // --- Qualified version accepted -------------------------------------------
    withTarget (fun target ->
        match planIn target webComponents true with
        | Error errors ->
            let details = String.concat "; " errors
            check $"a scaffold manifest declaring forma-workflow 1.2.0 plans: {details}" false
        | Ok plan ->
            check "the plan resolves forma-workflow 1.2.0 to its exact npm source"
                (plan.Components
                 |> List.exists (fun item ->
                     item.Id = "forma-workflow"
                     && item.Version = "1.2.0"
                     && item.Distribution = NpmPackage
                     && item.Package = Package
                     && item.SourceReference = Some "npm:@echelon-foundry/forma-workflow@1.2.0"))

            check "the scaffold pins @echelon-foundry/forma-workflow to exactly 1.2.0 in package.json"
                (scaffoldFile "package.json" plan
                 |> Option.exists (fun text -> text.Contains("\"@echelon-foundry/forma-workflow\": \"1.2.0\"")))

            check "forma-workflow runs no package-manager lifecycle"
                (plan.Actions |> List.exists (fun action -> action.ComponentId = "forma-workflow") |> not)

            check "the scaffold foundations do not gain a forma-workflow capability (Praxis' foundations schema forbids it)"
                (scaffoldFile ".echelon/foundations.json" plan
                 |> Option.exists (fun text -> not (text.Contains "forma-workflow"))))

    // --- Unqualified versions refused -----------------------------------------
    for version in [ "1.1.0"; "1.0.0"; "1.3.0"; "2.0.1"; "2.1.0"; "3.0.0"; "0.0.0-stage" ] do
        withTarget (fun target ->
            let components = [ "limen", "0.9.1"; "forma", "0.13.0"; "forma-workflow", version ]

            check $"forma-workflow {version} is refused as not qualified, before mutation"
                (planIn target components true
                 |> refusedWith $"Component 'forma-workflow' version '{version}' is not qualified by this Conditor compatibility graph. Qualified versions: 1.2.0, 2.0.0.")

            check $"refusing forma-workflow {version} writes nothing" (Directory.EnumerateFileSystemEntries target |> Seq.isEmpty))

    // --- Peer constraint on Forma ---------------------------------------------
    withTarget (fun target ->
        check "forma-workflow without Forma is refused"
            (planIn target [ "limen", "0.9.1"; "forma-workflow", "1.2.0" ] true
             |> refusedWith "Component 'forma-workflow' 1.2.0 requires component 'forma' at version 0.4.0 or later and below 1.0.0, but the manifest does not declare 'forma'."))

    for forma in [ "0.2.0"; "0.3.0" ] do
        withTarget (fun target ->
            check $"forma-workflow with Forma {forma} (below its >=0.4.0 peer range) is refused"
                (planIn target [ "limen", "0.9.1"; "forma", forma; "forma-workflow", "1.2.0" ] true
                 |> refusedWith $"but the manifest declares 'forma' {forma}."))

    withTarget (fun target ->
        check "forma-workflow with Forma 0.4.1, the lowest qualified Forma in its peer range, is accepted"
            (planIn target [ "limen", "0.9.1"; "forma", "0.4.1"; "forma-workflow", "1.2.0" ] true |> Result.isOk))

    withTarget (fun target ->
        check "forma-workflow 1.2.0 with Forma 0.14.0, the last Forma of the ef- namespace, is accepted"
            (planIn target [ "limen", "0.9.1"; "forma", "0.14.0"; "forma-workflow", "1.2.0" ] true |> Result.isOk))

    // Forma 1.0.0 renamed the ef- names forma-workflow 1.2.0 emits (FORMA-NS-001).
    withTarget (fun target ->
        check "forma-workflow 1.2.0 with Forma 1.0.1 is refused before mutation: it emits the ef- names Forma 1.0 renamed"
            (planIn target [ "limen", "0.9.1"; "forma", "1.0.1"; "forma-workflow", "1.2.0" ] true
             |> refusedWith "Component 'forma-workflow' 1.2.0 requires component 'forma' at version 0.4.0 or later and below 1.0.0, but the manifest declares 'forma' 1.0.1.")

        check "refusing forma-workflow 1.2.0 with Forma 1.0.1 writes nothing" (Directory.EnumerateFileSystemEntries target |> Seq.isEmpty))

    // forma-workflow 2.0.0 is the Forma 1.x release (peer design-system >=1.0.0).
    for forma in [ "0.14.0"; "0.4.1" ] do
        withTarget (fun target ->
            check $"forma-workflow 2.0.0 with Forma {forma} (below its >=1.0.0 peer range) is refused"
                (planIn target [ "limen", "0.9.1"; "forma", forma; "forma-workflow", "2.0.0" ] true
                 |> refusedWith $"Component 'forma-workflow' 2.0.0 requires component 'forma' at version 1.0.0 or later, but the manifest declares 'forma' {forma}."))

    withTarget (fun target ->
        check "forma-workflow 2.0.0 without Forma is refused"
            (planIn target [ "limen", "0.9.1"; "forma-workflow", "2.0.0" ] true
             |> refusedWith "Component 'forma-workflow' 2.0.0 requires component 'forma' at version 1.0.0 or later, but the manifest does not declare 'forma'."))

    withTarget (fun target ->
        match planIn target [ "limen", "0.9.1"; "forma", "1.0.1"; "folio", "0.4.1"; "forma-workflow", "2.0.0" ] true with
        | Error errors ->
            let details = String.concat "; " errors
            check $"forma-workflow 2.0.0 with Forma 1.0.1 plans: {details}" false
        | Ok plan ->
            check "the scaffold pins @echelon-foundry/forma-workflow to exactly 2.0.0 with Forma 1.0.1"
                (plan.Components
                 |> List.exists (fun item -> item.Id = "forma-workflow" && item.SourceReference = Some "npm:@echelon-foundry/forma-workflow@2.0.0")
                 && scaffoldFile "package.json" plan
                    |> Option.exists (fun text -> text.Contains("\"@echelon-foundry/forma-workflow\": \"2.0.0\""))))

    // --- Binding target -------------------------------------------------------
    withTarget (fun target ->
        check "forma-workflow without a scaffold or package.json binding is refused"
            (planIn target [ "forma", "0.13.0"; "forma-workflow", "1.2.0" ] false
             |> refusedWith "Component 'forma-workflow' is an application dependency."))

    // --- Lock generation ------------------------------------------------------
    withTarget (fun target ->
        writePackageJson target [ "@echelon-foundry/design-system", "0.13.0"; Package, "1.2.0" ]
        let manifestPath = Path.Combine(target, "conditor.json")
        let lockPath = Path.Combine(target, ".conditor", "lock.json")

        let established = [ "forma", "0.13.0" ]
        File.WriteAllText(manifestPath, manifestText established false)

        match Manifest.load manifestPath |> Result.bind (Planner.create target Init) with
        | Error errors ->
            let details = String.concat "; " errors
            check $"the manifest without forma-workflow plans: {details}" false
        | Ok plan ->
            LockFile.write target manifestPath plan |> ignore

            check "a lock written without forma-workflow in the manifest does not lock it"
                (File.ReadAllText lockPath |> fun text -> not (text.Contains "forma-workflow"))

        // Declaring forma-workflow afterwards is drift until the lock is re-established.
        File.WriteAllText(manifestPath, manifestText (established @ [ "forma-workflow", "1.2.0" ]) false)

        check "declaring forma-workflow after the lock is detected as manifest drift"
            (LockFile.verifyManifest target manifestPath |> Result.isError)

        match Manifest.load manifestPath |> Result.bind (Planner.create target Init) with
        | Error errors ->
            let details = String.concat "; " errors
            check $"a manifest declaring forma-workflow with a package.json binding plans: {details}" false
        | Ok plan ->
            check "the earlier lock reports forma-workflow as not locked"
                (match LockFile.verifyResolvedComponents target plan.Components with
                 | Error errors -> errors |> List.exists (fun error -> error.Contains "Resolved component 'forma-workflow' is not present in the Conditor lock.")
                 | Ok() -> false)

            LockFile.write target manifestPath plan |> ignore

            use document = JsonDocument.Parse(File.ReadAllText lockPath)

            let locked =
                document.RootElement.GetProperty("components").EnumerateArray()
                |> Seq.tryFind (fun item -> item.GetProperty("id").GetString() = "forma-workflow")

            check "lock generation includes forma-workflow when the manifest lists it"
                (locked
                 |> Option.exists (fun item ->
                     item.GetProperty("version").GetString() = "1.2.0"
                     && item.GetProperty("distribution").GetString() = "npm"
                     && item.GetProperty("package").GetString() = Package
                     && item.GetProperty("authority").GetString() = "embedded-component-descriptor"
                     && Some(item.GetProperty("descriptorSha256").GetString()) = Registry.descriptorSha256 "forma-workflow"
                     && item.GetProperty("sourceReference").GetString() = "npm:@echelon-foundry/forma-workflow@1.2.0"))

            check "the re-established lock matches the manifest and every resolved identity"
                (LockFile.verifyManifest target manifestPath |> Result.isOk
                 && LockFile.verifyResolvedComponents target plan.Components |> Result.isOk))
