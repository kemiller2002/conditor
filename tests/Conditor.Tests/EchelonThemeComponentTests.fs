module EchelonThemeComponentTests

// The Echelon theme (@echelon-foundry/echelon-theme) is the Echelon brand that
// EDS-ADR-002 moved out of Forma: brand CSS, marketing theme and approved
// marks for Forma 1.x. Conditor qualifies exactly 1.0.0, the GitHub release
// asset of kemiller2002/echelon-design-system v1.0.0 (sha256
// 5d981420cfd4f7a835564a204938eb5db1f1d35350477a755df98daa37ec8394), and
// 1.1.0, v1.1.0 (sha256
// e9360d743ad9e9a62abb3cf22d29bc8a26a4f9fd2b4980dd1d050c27937ec6bd), which
// adds the application layer (dist/echelon-app.css) and self-hosted fonts
// (dist/echelon-fonts.css), loaded after dist/echelon.css. It is
// optional: only a repository that declares it installs it (Echelon
// applications do). It requires Forma 1.0.0 or later, loads after Forma's CSS
// and applies under data-forma-brand="echelon" on <html>.

open System
open System.IO
open Conditor.Core

[<Literal>]
let private Package = "@echelon-foundry/echelon-theme"

[<Literal>]
let private ReleaseAsset110 =
    "https://github.com/kemiller2002/echelon-design-system/releases/download/v1.1.0/echelon-foundry-echelon-theme-1.1.0.tgz"

let private ReleaseAsset =
    "https://github.com/kemiller2002/echelon-design-system/releases/download/v1.0.0/echelon-foundry-echelon-theme-1.0.0.tgz"

/// The scaffold page Conditor has written since before Forma 1.0, byte for
/// byte: a repository on the ef- namespace keeps exactly this page.
[<Literal>]
let private EfNamespacePage =
    "<!doctype html>\n<html lang=\"en\">\n<head>\n  <meta charset=\"utf-8\">\n  <meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">\n  <link rel=\"stylesheet\" href=\"../../node_modules/@echelon-foundry/design-system/dist/all.css\">\n  <title>Application</title>\n</head>\n<body>\n  <main>\n    <ef-button><button type=\"button\">Ready</button></ef-button>\n  </main>\n</body>\n</html>\n"

let private withTarget (action: string -> 'a) : 'a =
    let target = Path.Combine(Path.GetTempPath(), $"conditor-echelon-theme-{Guid.NewGuid():N}")
    Directory.CreateDirectory target |> ignore

    try
        action target
    finally
        if Directory.Exists target then
            Directory.Delete(target, true)

let private manifestText (components: (string * string) list) =
    let entries =
        components
        |> List.map (fun (id, version) -> $"{{\"id\":\"{id}\",\"version\":\"{version}\"}}")
        |> String.concat ","

    $"{{\"schemaVersion\":1,\"name\":\"theme-demo\",\"components\":[{entries}],\"scaffold\":{{\"kind\":\"fsharp-limen-web\",\"name\":\"theme-demo\"}}}}"

let private planIn target components =
    manifestText components |> Manifest.parseText |> Result.bind (Planner.create target Init)

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

let private formaOne = [ "limen", "0.9.1"; "forma", "1.0.1"; "folio", "0.4.1" ]

let private planned check (label: string) target components (verify: InstallationPlan -> unit) =
    match planIn target components with
    | Error errors ->
        let details = String.concat "; " errors
        check $"{label} plans: {details}" false
    | Ok plan -> verify plan

let run (check: string -> bool -> unit) =
    // --- Embedded descriptor -------------------------------------------------
    match Registry.tryFind "echelon-theme", Registry.qualifiedVersions "echelon-theme" with
    | Some definition, Some qualified ->
        check "echelon-theme is a built-in npm application binding of @echelon-foundry/echelon-theme"
            (definition.Distribution = NpmPackage
             && definition.ApplicationBinding = Some NpmDependency
             && definition.Package = Package
             && definition.LifecycleSource.IsNone
             && definition.HistoricalPackages.IsEmpty)

        check "echelon-theme qualifies exactly 1.0.0, its default, and 1.1.0"
            (qualified = Set.ofList [ "1.0.0"; "1.1.0" ] && definition.DefaultVersion = "1.0.0")

        check "echelon-theme 1.0.0 is distributed as its GitHub release asset, not through the npm registry"
            (definition.ReleaseArtifacts = Map.ofList [ "1.0.0", ReleaseAsset; "1.1.0", ReleaseAsset110 ])
    | _ -> check "the embedded echelon-theme descriptor is present" false

    check "echelon-theme carries a descriptor digest for the lock"
        (Registry.descriptorSha256 "echelon-theme" |> Option.exists (fun digest -> digest.Length = 64))

    check "the compatibility graph records the Echelon theme's Forma requirement"
        (Compatibility.requirements
         |> List.exists (fun requirement -> requirement.Subject = "component:echelon-theme" && requirement.Requires = "forma"))

    // --- Peer constraint on Forma ---------------------------------------------
    withTarget (fun target ->
        check "the Echelon theme without Forma is refused"
            (planIn target [ "limen", "0.9.1"; "echelon-theme", "1.0.0" ]
             |> refusedWith "Component 'echelon-theme' 1.0.0 requires component 'forma' at version 1.0.0 or later, but the manifest does not declare 'forma'."))

    for forma in [ "0.14.0"; "0.5.0" ] do
        withTarget (fun target ->
            check $"the Echelon theme with Forma {forma} (the ef- namespace) is refused before mutation"
                (planIn target [ "limen", "0.9.1"; "forma", forma; "echelon-theme", "1.0.0" ]
                 |> refusedWith $"Component 'echelon-theme' 1.0.0 requires component 'forma' at version 1.0.0 or later, but the manifest declares 'forma' {forma}.")

            check $"refusing the Echelon theme with Forma {forma} writes nothing" (Directory.EnumerateFileSystemEntries target |> Seq.isEmpty))

    // --- Peer rules follow the release line, not one version -------------------
    // A later release of the line that a verified Registry attestation
    // qualifies (CON-341) carries its line's Forma requirement too.
    let attestedErrors (attested: (string * string) list) (components: (string * string) list) =
        match manifestText components |> Manifest.parseText with
        | Error errors -> errors
        | Ok manifest -> Compatibility.validateWithAttested Set.empty (Set.ofList attested) manifest

    check "an attested Echelon theme 1.0.1 with Forma 0.14.0 still requires Forma 1.0.0 or later"
        (attestedErrors [ "echelon-theme", "1.0.1" ] [ "limen", "0.9.1"; "forma", "0.14.0"; "echelon-theme", "1.0.1" ]
         |> List.exists (fun error ->
             error.Contains "Component 'echelon-theme' 1.0.1 requires component 'forma' at version 1.0.0 or later, but the manifest declares 'forma' 0.14.0."))

    check "an attested Echelon theme 1.0.1 with Forma 1.0.1 is accepted"
        (attestedErrors [ "echelon-theme", "1.0.1" ] (formaOne @ [ "echelon-theme", "1.0.1" ]) |> List.isEmpty)

    check "an attested forma-workflow 2.1.0 with Forma 0.14.0 still requires Forma 1.0.0 or later"
        (attestedErrors [ "forma-workflow", "2.1.0" ] [ "limen", "0.9.1"; "forma", "0.14.0"; "forma-workflow", "2.1.0" ]
         |> List.exists (fun error -> error.Contains "Component 'forma-workflow' 2.1.0 requires component 'forma' at version 1.0.0 or later"))

    check "an attested forma-workflow 1.3.0 (the ef- line) with Forma 1.0.1 is refused"
        (attestedErrors [ "forma-workflow", "1.3.0" ] (formaOne @ [ "forma-workflow", "1.3.0" ])
         |> List.exists (fun error ->
             error.Contains "Component 'forma-workflow' 1.3.0 requires component 'forma' at version 0.4.0 or later and below 1.0.0, but the manifest declares 'forma' 1.0.1."))

    check "the descriptors' qualified ranges cover the versions they qualify (Forma 1.0.1, forma-workflow 2.0.0, the theme 1.0.0)"
        ([ "forma", "1.0.1"; "forma-workflow", "2.0.0"; "echelon-theme", "1.0.0" ]
         |> List.forall (fun (id, version) ->
             match Registry.attestedQualification id, SemanticVersion.tryParse version with
             | Some(range, _), Some parsed -> VersionRange.tryParse range |> Option.exists (fun line -> VersionRange.contains line parsed)
             | _ -> false))

    // The theme 1.x rule covers 1.1.0, which Conditor qualifies exactly.
    for forma in [ "0.14.0"; "0.5.0" ] do
        withTarget (fun target ->
            check $"the Echelon theme 1.1.0 with Forma {forma} is refused before mutation (the theme 1.x rule)"
                (planIn target [ "limen", "0.9.1"; "forma", forma; "echelon-theme", "1.1.0" ]
                 |> refusedWith $"Component 'echelon-theme' 1.1.0 requires component 'forma' at version 1.0.0 or later, but the manifest declares 'forma' {forma}."))

    for version in [ "0.9.0"; "1.0.1"; "1.1.1"; "1.2.0"; "2.0.0" ] do
        withTarget (fun target ->
            check $"echelon-theme {version} is refused as not qualified"
                (planIn target (formaOne @ [ "echelon-theme", version ])
                 |> refusedWith $"Component 'echelon-theme' version '{version}' is not qualified by this Conditor compatibility graph. Qualified versions: 1.0.0, 1.1.0."))

    // --- Forma 1.x scaffold without the theme (optional) ----------------------
    withTarget (fun target ->
        planned check "a Forma 1.0.1 scaffold without the Echelon theme" target formaOne (fun plan ->
            check "the Echelon theme is optional: a Forma 1.0.1 scaffold that does not declare it does not install it"
                (plan.Components |> List.exists (fun item -> item.Id = "echelon-theme") |> not
                 && scaffoldFile "package.json" plan |> Option.exists (fun text -> not (text.Contains Package)))

            check "under Forma 1.0.1 the scaffold page uses the forma- namespace and no brand"
                (scaffoldFile "src/kernel/index.html" plan
                 |> Option.exists (fun text ->
                     text.Contains "<forma-button><button type=\"button\">Ready</button></forma-button>"
                     && text.Contains "<html lang=\"en\">"
                     && not (text.Contains "ef-")
                     && not (text.Contains "data-forma-brand")
                     && not (text.Contains "echelon-theme")))))

    // --- Forma 1.x scaffold with the theme -------------------------------------
    withTarget (fun target ->
        planned check "a Forma 1.0.1 scaffold with the Echelon theme" target (formaOne @ [ "echelon-theme", "1.0.0" ]) (fun plan ->
            check "the plan resolves the Echelon theme 1.0.0 to its npm application binding"
                (plan.Components
                 |> List.exists (fun item ->
                     item.Id = "echelon-theme" && item.Version = "1.0.0" && item.Distribution = NpmPackage && item.Package = Package))

            check "the scaffold pins the Echelon theme to its exact GitHub release asset"
                (scaffoldFile "package.json" plan
                 |> Option.exists (fun text -> text.Contains $"\"{Package}\": \"{ReleaseAsset}\""))

            check "the theme runs no package-manager lifecycle"
                (plan.Actions |> List.exists (fun action -> action.ComponentId = "echelon-theme") |> not)

            check "the scaffold brands <html> with data-forma-brand=\"echelon\" and loads the theme after Forma's CSS"
                (scaffoldFile "src/kernel/index.html" plan
                 |> Option.exists (fun text ->
                     let forma = text.IndexOf("@echelon-foundry/design-system/dist/all.css", StringComparison.Ordinal)
                     let theme = text.IndexOf("@echelon-foundry/echelon-theme/dist/echelon.css", StringComparison.Ordinal)

                     text.Contains "<html lang=\"en\" data-forma-brand=\"echelon\">"
                     && text.Contains "<forma-button>"
                     && forma >= 0
                     && theme > forma
                     && not (text.Contains "data-ef-brand")))

            check "the scaffold foundations do not gain an echelon-theme capability (Praxis' foundations schema forbids it)"
                (scaffoldFile ".echelon/foundations.json" plan
                 |> Option.exists (fun text -> not (text.Contains "echelon-theme")))))

    // --- Theme 1.1.0: fonts and the application layer -----------------------------
    let themePage (version: string) =
        let files =
            if version = "1.0.0" then
                [ "echelon.css" ]
            else
                [ "echelon.css"; "echelon-fonts.css"; "echelon-app.css" ]

        let links =
            files
            |> List.map (fun file -> $"  <link rel=\"stylesheet\" href=\"../../node_modules/@echelon-foundry/echelon-theme/dist/{file}\">\n")
            |> String.concat ""

        "<!doctype html>\n<html lang=\"en\" data-forma-brand=\"echelon\">\n<head>\n  <meta charset=\"utf-8\">\n  <meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">\n  <link rel=\"stylesheet\" href=\"../../node_modules/@echelon-foundry/design-system/dist/all.css\">\n"
        + links
        + "  <title>Application</title>\n</head>\n<body>\n  <main>\n    <forma-button><button type=\"button\">Ready</button></forma-button>\n  </main>\n</body>\n</html>\n"

    withTarget (fun target ->
        planned check "a Forma 1.0.1 scaffold with the Echelon theme 1.1.0" target (formaOne @ [ "echelon-theme", "1.1.0" ]) (fun plan ->
            check "the scaffold pins the Echelon theme 1.1.0 to its exact GitHub release asset"
                (scaffoldFile "package.json" plan
                 |> Option.exists (fun text -> text.Contains $"\"{Package}\": \"{ReleaseAsset110}\""))

            check "under the theme 1.1.0 the page loads Forma's all.css, then echelon.css, echelon-fonts.css and echelon-app.css, in that order"
                (scaffoldFile "src/kernel/index.html" plan = Some(themePage "1.1.0"))))

    withTarget (fun target ->
        planned check "a Forma 1.0.1 scaffold with the Echelon theme 1.0.0" target (formaOne @ [ "echelon-theme", "1.0.0" ]) (fun plan ->
            check "under the theme 1.0.0, which ships neither fonts nor the application layer, the page loads echelon.css only, byte for byte as before"
                (scaffoldFile "src/kernel/index.html" plan = Some(themePage "1.0.0"))))

    // --- A lock written by Conditor 0.10.24 ----------------------------------------
    // This build adds 1.1.0 to the theme descriptor, so a lock 0.10.24 wrote for
    // a project that declares the theme records the previous descriptor digest
    // and is refused until `conditor init` re-locks it, with no version change
    // (docs/indy-day-of.md).
    withTarget (fun target ->
        File.WriteAllText(
            Path.Combine(target, "package.json"),
            $"{{\"name\":\"app\",\"private\":true,\"dependencies\":{{\"@echelon-foundry/design-system\":\"1.0.1\",\"{Package}\":\"{ReleaseAsset}\"}}}}"
        )

        let manifestPath = Path.Combine(target, "conditor.json")
        let lockPath = Path.Combine(target, ".conditor", "lock.json")

        File.WriteAllText(
            manifestPath,
            """{"schemaVersion":1,"name":"theme-demo","components":[{"id":"forma","version":"1.0.1"},{"id":"echelon-theme","version":"1.0.0"}]}"""
        )

        // The theme descriptor digest Conditor 0.10.24 (4fb01c2) embeds and locks.
        let previousDigest = "7b8f581cb1b4eb3e6a6af139b14b8910aded5e458ca63161851573d03cee7536"

        let initialize () =
            Manifest.load manifestPath
            |> Result.bind (Planner.create target Init)
            |> Result.map (fun plan ->
                LockFile.write target manifestPath plan |> ignore
                plan)

        let lockedTheme () =
            use document = Text.Json.JsonDocument.Parse(File.ReadAllText lockPath)

            document.RootElement.GetProperty("components").EnumerateArray()
            |> Seq.tryFind (fun item -> item.GetProperty("id").GetString() = "echelon-theme")
            |> Option.map (fun item ->
                item.GetProperty("version").GetString(),
                item.GetProperty("sourceReference").GetString(),
                item.GetProperty("descriptorSha256").GetString())

        match initialize () with
        | Error errors ->
            let details = String.concat "; " errors
            check $"a project declaring the theme 1.0.0 initializes: {details}" false
        | Ok plan ->
            let current = Registry.descriptorSha256 "echelon-theme"

            check "this build's theme descriptor differs from Conditor 0.10.24's" (current <> Some previousDigest)

            current
            |> Option.iter (fun digest -> File.WriteAllText(lockPath, File.ReadAllText(lockPath).Replace(digest, previousDigest)))

            check "a lock from Conditor 0.10.24 is refused after the theme descriptor changes, without substituting anything"
                (match LockFile.verifyResolvedComponents target plan.Components with
                 | Error errors -> errors |> List.exists (fun error -> error.Contains "Resolved identity for component 'echelon-theme' differs from the Conditor lock")
                 | Ok() -> false)

            match initialize () with
            | Error errors ->
                let details = String.concat "; " errors
                check $"re-running init on the unchanged manifest re-locks: {details}" false
            | Ok replanned ->
                check "re-running init re-locks the theme at 1.0.0 with its source unchanged and this build's descriptor digest"
                    (lockedTheme () = (current |> Option.map (fun digest -> "1.0.0", "npm:@echelon-foundry/echelon-theme@1.0.0", digest))
                     && LockFile.verifyResolvedComponents target replanned.Components |> Result.isOk))

    // --- The ef- namespace scaffold is unchanged ---------------------------------
    withTarget (fun target ->
        planned check "a Forma 0.14.0 scaffold" target [ "limen", "0.9.1"; "forma", "0.14.0"; "folio", "0.4.1" ] (fun plan ->
            check "below Forma 1.0.0 the scaffold page is the ef- namespace page, byte for byte"
                (scaffoldFile "src/kernel/index.html" plan = Some EfNamespacePage)))

    withTarget (fun target ->
        planned check "a scaffold without Forma" target [ "limen", "0.9.1" ] (fun plan ->
            check "without Forma the scaffold page is the ef- namespace page, byte for byte"
                (scaffoldFile "src/kernel/index.html" plan = Some EfNamespacePage)))
