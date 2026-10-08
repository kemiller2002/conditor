module LibraryScaffoldTests

open System
open System.IO
open System.Text.Json
open Conditor.Core

// The fsharp-nuget-library scaffold establishes what a new project-bound
// NuGet package repository needs beyond its lifecycle components: a buildable
// library and test project, the foundations declaration and its CI check, a
// build-and-test workflow, and the Registry release workflow for the
// nuget-library distribution class.

let private withTarget action =
    let target = Path.Combine(Path.GetTempPath(), $"conditor-library-{Guid.NewGuid():N}")
    Directory.CreateDirectory target |> ignore

    try
        action target
    finally
        if Directory.Exists target then
            Directory.Delete(target, true)

let private planFor target (json: string) =
    let path = Path.Combine(Path.GetTempPath(), $"conditor-library-{Guid.NewGuid():N}.json")

    try
        File.WriteAllText(path, json)

        Manifest.load path
        |> Result.bind (Planner.create target Init)
    finally
        if File.Exists path then
            File.Delete path

let private scaffoldFiles (plan: InstallationPlan) =
    plan.Actions
    |> List.choose (fun action ->
        match action.Kind, action.Execution with
        | ScaffoldFile, EnsureFile(path, content) -> Some(path, content)
        | ScaffoldFile, EnsureManagedRegion(path, _, content) -> Some(path, content)
        | _ -> None)
    |> Map.ofList

let private libraryManifest components =
    $"""{{"schemaVersion":1,"name":"arca","components":[{components}],"requirements":[],"execution":{{"enabled":false}},"scaffold":{{"kind":"fsharp-nuget-library","name":"Arca"}}}}"""

let private joined (errors: string list) = String.concat "; " errors

let private expectedPaths =
    [ "Directory.Build.props"
      "Arca.slnx"
      "src/Arca/Arca.fsproj"
      "src/Arca/Library.fs"
      "tests/Arca.Tests/Arca.Tests.fsproj"
      "tests/Arca.Tests/LibraryTests.fs"
      ".echelon/foundations.json"
      ".github/workflows/build-and-test.yml"
      ".github/workflows/echelon-foundations.yml"
      ".github/workflows/release.yml"
      "release/echelon.release-input.json"
      "SDE-MAP.md"
      "context/CURRENT-STATE.md"
      ".gitignore" ]

let private contains (needle: string) (files: Map<string, string>) path =
    files |> Map.tryFind path |> Option.exists (fun text -> text.Contains(needle, StringComparison.Ordinal))

let run (check: string -> bool -> unit) =
    withTarget (fun target ->
        match planFor target (libraryManifest """{"id":"praxis","version":"3.1.4"},{"id":"ordo","version":"1.4.0"}""") with
        | Error errors ->
            check $"library scaffold plans: {joined errors}" false
        | Ok plan ->
            let files = scaffoldFiles plan
            let has needle path = contains needle files path

            check "library scaffold emits exactly the package repository surface" (Set.ofList expectedPaths = (files |> Map.keys |> Set.ofSeq))

            check "library project is packable under the EchelonFoundry prefix"
                (has "<PackageId>EchelonFoundry.Arca</PackageId>" "src/Arca/Arca.fsproj"
                 && has "<IsPackable>true</IsPackable>" "src/Arca/Arca.fsproj")

            check "a fresh library declares the unreleased version" (has "<Version>0.0.0</Version>" "Directory.Build.props")
            check "solution names the library and its tests"
                (has "src/Arca/Arca.fsproj" "Arca.slnx" && has "tests/Arca.Tests/Arca.Tests.fsproj" "Arca.slnx")
            check "the test project is a dotnet test (xUnit) project, so test-result consumers get evidence"
                (has "<IsTestProject>true</IsTestProject>" "tests/Arca.Tests/Arca.Tests.fsproj"
                 && has "Include=\"Microsoft.NET.Test.Sdk\" Version=\"17.11.1\"" "tests/Arca.Tests/Arca.Tests.fsproj"
                 && has "Include=\"xunit\" Version=\"2.9.2\"" "tests/Arca.Tests/Arca.Tests.fsproj"
                 && has "Include=\"xunit.runner.visualstudio\" Version=\"2.8.2\"" "tests/Arca.Tests/Arca.Tests.fsproj"
                 && has "[<Fact>]" "tests/Arca.Tests/LibraryTests.fs"
                 && has "Arca.Library.scaffoldReady" "tests/Arca.Tests/LibraryTests.fs")

            check "build-and-test builds and runs the tests"
                (has "dotnet build Arca.slnx -c Release" ".github/workflows/build-and-test.yml"
                 && has "dotnet test Arca.slnx -c Release --no-build" ".github/workflows/build-and-test.yml"
                 && has "dotnet test Arca.slnx -c Release --no-build" ".github/workflows/release.yml")

            check "the test step fails an empty or skipped run"
                ([ ".github/workflows/build-and-test.yml"; ".github/workflows/release.yml" ]
                 |> List.forall (fun path ->
                     has "Passed:[[:space:]]*[1-9]" path && has "Skipped:[[:space:]]*[1-9]" path))

            check "foundations CI pins the Praxis verifier commit"
                (has $"foundations-verify.yml@{Scaffolding.PraxisFoundationsRef}" ".github/workflows/echelon-foundations.yml"
                 && has $"praxis_ref: {Scaffolding.PraxisFoundationsRef}" ".github/workflows/echelon-foundations.yml")

            check "release workflow pins the Registry release contract"
                (has $"release-contract@{Scaffolding.RegistryReleaseContractRef}" ".github/workflows/release.yml"
                 && has $"ECHELON_REGISTRY_REF: {Scaffolding.RegistryReleaseContractRef}" ".github/workflows/release.yml")

            check "release workflow never publishes the unreleased version"
                (has "if [ \"$declared\" = \"0.0.0\" ]; then" ".github/workflows/release.yml"
                 && has "needs.version.outputs.publishable == 'true'" ".github/workflows/release.yml")

            check "release workflow refuses to guess the Trusted Publishing account"
                (has "NUGET_USER: ${{ vars.NUGET_USER }}" ".github/workflows/release.yml"
                 && has "Nothing was published." ".github/workflows/release.yml")

            check "no unrendered template tokens remain"
                (files |> Map.forall (fun _ text -> not (text.Contains("@@", StringComparison.Ordinal))))

            match files |> Map.tryFind "release/echelon.release-input.json" with
            | None -> check "release input is generated" false
            | Some text ->
                use document = JsonDocument.Parse text
                let root = document.RootElement
                let distribution = root.GetProperty "distribution"
                let artifacts = root.GetProperty "artifacts" |> _.EnumerateArray() |> Seq.map (fun a -> a.GetProperty("name").GetString()) |> List.ofSeq

                check "release input declares the nuget-library class"
                    (root.GetProperty("schema").GetString() = "echelon.release-input/v1"
                     && root.GetProperty("systemId").GetString() = "arca"
                     && root.GetProperty("distributionClass").GetString() = "nuget-library"
                     && root.GetProperty("executable").ValueKind = JsonValueKind.Null)

                check "release input distributes through NuGet"
                    (distribution.GetProperty("mechanism").GetString() = "nuget"
                     && distribution.GetProperty("package").GetString() = "EchelonFoundry.Arca")

                check "release artifacts are the versioned package and its checksums"
                    (artifacts = [ "EchelonFoundry.Arca.{version}.nupkg"; "checksums.txt" ])

            match files |> Map.tryFind ".echelon/foundations.json" with
            | None -> check "foundations are declared" false
            | Some text ->
                use document = JsonDocument.Parse text
                let capabilities = document.RootElement.GetProperty "capabilities"
                check "foundations require the installed Praxis and Ordo"
                    (capabilities.GetProperty("praxis").GetProperty("required").GetBoolean()
                     && capabilities.GetProperty("ordo").GetProperty("required").GetBoolean()
                     && not (capabilities.GetProperty("limen").GetProperty("required").GetBoolean()))

                let mutable routing = Unchecked.defaultof<JsonElement>
                check "a library has no navigable state, so it declares no routing foundation"
                    (not (capabilities.TryGetProperty("routing", &routing)))

            check "Ordo baseline routes the first semantic area to the library"
                (has "`src/Arca/`" "SDE-MAP.md" && has "`src/Arca/Arca.fsproj`" "SDE-MAP.md")
            check "Ordo baseline names the library placeholder" (has "`src/Arca/Library.fs` value" "context/CURRENT-STATE.md")

            check "library scaffold ignores build and pack outputs"
                (has "bin/\n" ".gitignore" && has "obj/\n" ".gitignore" && has "dist/\n" ".gitignore" && has "TestResults/\n" ".gitignore" && has "*.nupkg" ".gitignore")

            check "the .gitignore region is a bounded managed region, never a whole-file write"
                (plan.Actions
                 |> List.exists (fun action ->
                     match action.Execution with
                     | EnsureManagedRegion(".gitignore", "build-outputs", _) -> true
                     | _ -> false)))

    withTarget (fun target ->
        match planFor target (libraryManifest """{"id":"aegis","version":"1.0.0"}""") with
        | Error errors -> check $"library scaffold binds NuGet packages: {joined errors}" false
        | Ok plan ->
            let files = scaffoldFiles plan
            check "library scaffold binds a NuGet component into the library project"
                (contains "<PackageReference Include=\"EchelonFoundry.Aegis.Core\" Version=\"1.0.0\" />" files "src/Arca/Arca.fsproj"))

    withTarget (fun target ->
        match planFor target (libraryManifest """{"id":"limen","version":"0.6.1"}""") with
        | Ok _ -> check "library scaffold refuses an npm binding it has no target for" false
        | Error errors ->
            check "library scaffold refuses an npm binding it has no target for"
                (errors |> List.exists (fun error -> error.Contains("binds NuGet packages only", StringComparison.Ordinal))))

    withTarget (fun target ->
        Directory.CreateDirectory(Path.Combine(target, ".github", "workflows")) |> ignore
        File.WriteAllText(Path.Combine(target, ".github", "workflows", "release.yml"), "user-owned")

        match planFor target (libraryManifest """{"id":"praxis","version":"3.1.4"}""") with
        | Ok _ -> check "library scaffold never overwrites an existing different file" false
        | Error errors ->
            check "library scaffold never overwrites an existing different file"
                (errors |> List.exists (fun error -> error.Contains("release.yml", StringComparison.Ordinal) && error.Contains("will not overwrite", StringComparison.Ordinal))))

    withTarget (fun target ->
        let web =
            """{"schemaVersion":1,"name":"web-baseline","components":[{"id":"ordo","version":"1.3.0"},{"id":"limen","version":"0.6.1"}],"scaffold":{"kind":"fsharp-limen-web"}}"""

        match planFor target web with
        | Error errors -> check $"web scaffold still plans: {joined errors}" false
        | Ok plan ->
            let files = scaffoldFiles plan
            check "web scaffold Ordo baseline is unchanged"
                (contains "`src/engine/`" files "SDE-MAP.md"
                 && contains "`src/kernel/bootstrap.ts`" files "SDE-MAP.md"
                 && contains "Boundary checks: installed Limen and Aegis contracts where required." files "SDE-MAP.md"
                 && contains "The generated `src/engine/Domain.fs` state is a scaffold placeholder" files "context/CURRENT-STATE.md"))

    // A .gitignore region uses '#' markers and keeps what the lifecycle
    // components (and the user) already put in the file.
    withTarget (fun target ->
        let ignorePath = Path.Combine(target, ".gitignore")
        File.WriteAllText(ignorePath, "# Node\nnode_modules/\n")

        let plan region =
            { ProjectName = "gitignore-region"
              Operation = Verify
              Components = []
              Actions =
                [ { Sequence = 1
                    ComponentId = "scaffold:fsharp-nuget-library"
                    ComponentVersion = "1"
                    Kind = ScaffoldFile
                    Execution = EnsureManagedRegion(".gitignore", "build-outputs", region) } ] }

        match Installer.execute target "test-manifest.json" (plan "bin/\nobj/\n") with
        | Error errors -> check $".gitignore region executes: {joined errors}" false
        | Ok _ ->
            let first = File.ReadAllText ignorePath

            check ".gitignore region uses hash-comment markers and keeps existing entries"
                (first.StartsWith("# Node\nnode_modules/\n", StringComparison.Ordinal)
                 && first.Contains("# conditor:build-outputs:start\nbin/\nobj/\n# conditor:build-outputs:end", StringComparison.Ordinal)
                 && not (first.Contains("<!--", StringComparison.Ordinal)))

            match Installer.execute target "test-manifest.json" (plan "bin/\nobj/\n") with
            | Error errors -> check $".gitignore region is idempotent: {joined errors}" false
            | Ok _ -> check ".gitignore region is idempotent" (File.ReadAllText ignorePath = first))

    // A scaffold is a seed. Once the lock records it as established, the
    // project owns its files: growing past the scaffold (more projects, a
    // renamed library, customised workflows) must not block later planning,
    // repair or upgrade, and Conditor must not recreate a file the project
    // removed. Found aligning kemiller2002/arca to echelon-current 1.3.0.
    withTarget (fun target ->
        let manifestPath = Path.Combine(target, "conditor.json")
        File.WriteAllText(manifestPath, libraryManifest "")

        let planInit () =
            Manifest.load manifestPath |> Result.bind (Planner.create target Init)

        let seedActions (plan: InstallationPlan) =
            plan.Actions
            |> List.choose (fun action ->
                match action.Kind, action.Execution with
                | ScaffoldFile, EnsureFile(path, _) -> Some path
                | _ -> None)

        let managedRegions (plan: InstallationPlan) =
            plan.Actions
            |> List.choose (fun action ->
                match action.Execution with
                | EnsureManagedRegion(path, _, _) -> Some path
                | _ -> None)
            |> Set.ofList

        match planInit () |> Result.bind (fun plan -> Installer.execute target manifestPath plan) with
        | Error errors -> check $"library scaffold is established by init: {joined errors}" false
        | Ok _ ->
            check "init records the scaffold in the lock" (Scaffolding.establishedScaffold target = Some { Kind = "fsharp-nuget-library"; Name = Some "Arca" })

            // The project grows: the test project changes, the library is renamed.
            File.AppendAllText(Path.Combine(target, "tests", "Arca.Tests", "Arca.Tests.fsproj"), "<!-- grown -->\n")
            Directory.Delete(Path.Combine(target, "src", "Arca"), true)
            Directory.CreateDirectory(Path.Combine(target, "src", "Arca.Core")) |> ignore

            match planInit () with
            | Error errors -> check $"an established scaffold that grew still plans: {joined errors}" false
            | Ok plan ->
                check "no seed file is compared or recreated once established" (seedActions plan).IsEmpty
                check "managed regions are still ensured" (managedRegions plan |> Set.contains ".gitignore")

        // Without an established lock, a differing file is still refused rather than overwritten.
        File.Delete(Path.Combine(target, ".conditor", "lock.json"))

        match planInit () with
        | Error errors ->
            check
                "before establishment a differing scaffold file is refused, not overwritten"
                (errors |> List.exists (fun error -> error.Contains "already exists with different content"))
        | Ok _ -> check "before establishment a differing scaffold file must be refused" false)
