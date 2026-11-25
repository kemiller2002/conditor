module NugetBindingTests

// NuGet application bindings (docs/nuget-binding-upgrade.md): a component
// descriptor with `"applicationBinding": "nuget"` whose package comes from
// nuget.org, such as Aegis. A version change moves the conditor.json
// declaration and every exact PackageReference/PackageVersion pin of the
// binding's package family together, through either `conditor upgrade`
// (version-only) or `conditor upgrade --current`. Conditor moves only pins it
// can prove it set, and only to a version this build qualifies.

open System
open System.IO
open System.Security.Cryptography
open Conditor.Core
open Conditor.Core.Workstation

let private temp label =
    let path = Path.Combine(Path.GetTempPath(), $"conditor-binding-{label}-{Guid.NewGuid():N}")
    Directory.CreateDirectory path |> ignore
    path

let private sha256File (path: string) =
    Convert.ToHexString(SHA256.HashData(File.ReadAllBytes path)).ToLowerInvariant()

let private aegis = (Registry.tryFind "aegis").Value

let private engineProject (coreVersion: string) (integrationVersion: string) =
    $"""<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <PackageReference Include="EchelonFoundry.Aegis.Core" Version="{coreVersion}" />
    <PackageReference Include="EchelonFoundry.Aegis.Integration.GitHub" Version="{integrationVersion}" />
    <PackageReference Include="FSharp.Core" Version="10.0.100" />
  </ItemGroup>
</Project>
"""

let private write (target: string) (relative: string) (text: string) =
    let path = Path.Combine(target, relative)
    Directory.CreateDirectory(Path.GetDirectoryName path |> nonNull) |> ignore
    File.WriteAllText(path, text)
    path

let private joined (errors: string list) = String.concat "; " errors

let private planning (check: string -> bool -> unit) =
    check "the Aegis descriptor is a NuGet application binding" (NugetBinding.isBinding aegis)
    check "a lifecycle descriptor is not a NuGet application binding" (not (NugetBinding.isBinding (Registry.tryFind "praxis").Value))

    check
        "the package family of EchelonFoundry.Aegis.Core is EchelonFoundry.Aegis."
        (NugetBinding.packageFamily "EchelonFoundry.Aegis.Core" = "EchelonFoundry.Aegis.")

    let target = temp "plan"

    try
        let engine = write target "src/engine/App.Engine.fsproj" (engineProject "1.0.0" "1.0.0")
        write target "Directory.Packages.props" """<Project><ItemGroup><PackageVersion Include="EchelonFoundry.Aegis.Store.GitHub" Version="1.0.0" /></ItemGroup></Project>""" |> ignore
        write target "src/engine/bin/Release/stale.fsproj" (engineProject "0.1.0" "0.1.0") |> ignore
        let before = File.ReadAllText engine

        match NugetBinding.plan target aegis "1.0.0" "2.0.0" with
        | Error errors -> check $"Aegis 1.0.0 -> 2.0.0 plans: {joined errors}" false
        | Ok binding ->
            check "the plan names both files that pin the Aegis family" (binding.Rewrites |> List.map _.Path = [ "Directory.Packages.props"; "src/engine/App.Engine.fsproj" ])

            check
                "the engine project moves Aegis.Core and Aegis.Integration.GitHub"
                (binding.Rewrites
                 |> List.exists (fun rewrite ->
                     rewrite.Path = "src/engine/App.Engine.fsproj"
                     && rewrite.Pins = 2
                     && rewrite.PackageIds = [ "EchelonFoundry.Aegis.Core"; "EchelonFoundry.Aegis.Integration.GitHub" ]))

            check "build output is never read or moved" (binding.Rewrites |> List.forall (fun rewrite -> not (rewrite.Path.Contains "/bin/")))
            check "planning writes nothing" (File.ReadAllText engine = before)
            check "the digest names the binding, both versions and the family" ((NugetBinding.digestMaterial binding).StartsWith "nuget-binding|aegis|1.0.0->2.0.0|EchelonFoundry.Aegis.|")

            match NugetBinding.apply target binding with
            | Error errors -> check $"the planned move applies: {joined errors}" false
            | Ok() ->
                let moved = File.ReadAllText engine
                check "Aegis pins move to 2.0.0" (moved.Contains "\"EchelonFoundry.Aegis.Core\" Version=\"2.0.0\"" && moved.Contains "\"EchelonFoundry.Aegis.Integration.GitHub\" Version=\"2.0.0\"")
                check "packages outside the family are untouched" (moved.Contains "\"FSharp.Core\" Version=\"10.0.100\"")
                check "central package versions move too" ((File.ReadAllText(Path.Combine(target, "Directory.Packages.props"))).Contains "Version=\"2.0.0\"")

                check
                    "applying again refuses rather than guessing, because no pin names 1.0.0 any more"
                    (match NugetBinding.plan target aegis "1.0.0" "2.0.0" with
                     | Ok again -> again.Rewrites.IsEmpty
                     | Error _ -> false)

        // A pin Conditor cannot prove it set refuses the whole move.
        write target "src/engine/App.Engine.fsproj" (engineProject "1.0.0" "0.9.0") |> ignore

        match NugetBinding.plan target aegis "1.0.0" "2.0.0" with
        | Ok _ -> check "a family pin at another version must refuse the move" false
        | Error errors ->
            check
                "a family pin at another version refuses the move and names the file and pin"
                (errors |> List.exists (fun error -> error.Contains "src/engine/App.Engine.fsproj" && error.Contains "0.9.0"))

        write target "src/engine/App.Engine.fsproj" (engineProject "1.0.0" "1.0.0") |> ignore

        match NugetBinding.plan target aegis "1.0.0" "3.0.0" with
        | Ok _ -> check "an unqualified Aegis version must be refused" false
        | Error errors -> check "an unqualified Aegis version is refused" (errors |> List.exists (fun error -> error.Contains "not qualified"))

        // A file changed between plan and apply refuses and writes nothing.
        match NugetBinding.plan target aegis "1.0.0" "2.0.0" with
        | Error errors -> check $"the move plans again: {joined errors}" false
        | Ok binding ->
            let changed = write target "src/engine/App.Engine.fsproj" (engineProject "1.5.0" "1.5.0")
            let snapshot = File.ReadAllText changed

            match NugetBinding.apply target binding with
            | Ok() -> check "a file changed after planning must refuse the move" false
            | Error _ -> check "a file changed after planning refuses the move and is left as it was" (File.ReadAllText changed = snapshot)
    finally
        Directory.Delete(target, true)

let private manifest (target: string) (authority: string) (aegisVersion: string) =
    let path = Path.Combine(target, "conditor.json")

    File.WriteAllText(
        path,
        $$"""{
  "schemaVersion": 1,
  "name": "consumer",
  {{authority}}"components": [{ "id": "aegis", "version": "{{aegisVersion}}", "required": true }],
  "requirements": [],
  "execution": { "enabled": false }
}
"""
    )

    path

let private versionOnlyUpgrade (check: string -> bool -> unit) =
    let target = temp "version-only"

    try
        let engine = write target "src/engine/App.Engine.fsproj" (engineProject "1.0.0" "1.0.0")
        let manifestPath = manifest target "" "1.0.0"

        match Manifest.load manifestPath |> Result.bind (Planner.create target Init) with
        | Error errors -> check $"a repository declaring Aegis 1.0.0 plans: {joined errors}" false
        | Ok plan ->
            LockFile.write target manifestPath plan |> ignore
            manifest target "" "2.0.0" |> ignore

            match Manifest.load manifestPath with
            | Error errors -> check $"the Aegis 2.0.0 declaration loads: {joined errors}" false
            | Ok declared ->
                match Upgrade.preview target declared with
                | Error errors -> check $"a version-only Aegis move previews: {joined errors}" false
                | Ok preview ->
                    check "the version-only preview changes Aegis" (preview.ChangedComponents = [ "aegis" ])
                    check "the version-only preview carries the pin move" (preview.NugetBindings |> List.map (fun binding -> binding.Id, binding.ToVersion) = [ "aegis", "2.0.0" ])
                    check "the version-only preview writes nothing" ((File.ReadAllText engine).Contains "Version=\"1.0.0\"")

                match Upgrade.apply target manifestPath declared with
                | Error errors -> check $"the version-only Aegis move applies: {joined errors}" false
                | Ok result ->
                    check "the version-only upgrade reports Aegis" (result.ChangedComponents = [ "aegis" ])
                    check "the version-only upgrade moves the pins" (not ((File.ReadAllText engine).Contains "Version=\"1.0.0\""))
                    check "the version-only upgrade re-locks the new declaration" (File.Exists result.LockPath && (File.ReadAllText result.LockPath).Contains "2.0.0")

        // A pin Conditor did not set refuses the version-only move as a whole.
        write target "src/engine/App.Engine.fsproj" (engineProject "1.1.0" "1.1.0") |> ignore
        manifest target "" "1.0.0" |> ignore

        match Manifest.load manifestPath |> Result.bind (Planner.create target Init) with
        | Error errors -> check $"the repository re-locks at Aegis 1.0.0: {joined errors}" false
        | Ok plan ->
            LockFile.write target manifestPath plan |> ignore
            manifest target "" "2.0.0" |> ignore

            match Manifest.load manifestPath |> Result.bind (Upgrade.preview target) with
            | Ok _ -> check "a version-only move over pins Conditor did not set must refuse" false
            | Error errors -> check "a version-only move over pins Conditor did not set refuses" (errors |> List.exists (fun error -> error.Contains "1.1.0"))
    finally
        Directory.Delete(target, true)

let private aegisComponent (version: string) =
    $$"""
    {
      "systemId": "aegis",
      "role": "project-binding",
      "required": true,
      "version": "{{version}}",
      "repository": "kemiller2002/aegis",
      "tag": "nuget:EchelonFoundry.Aegis.Core@{{version}}",
      "commit": "{{String.replicate 40 "a"}}",
      "releaseStage": "stable",
      "lifecycleState": "active",
      "distributionClass": "nuget-library",
      "executable": null,
      "releaseManifest": { "schema": "echelon.release/v2", "sha256": "{{String.replicate 64 "e"}}" },
      "distribution": {
        "mechanism": "nuget",
        "package": "EchelonFoundry.Aegis.Core",
        "url": "https://api.nuget.org/v3-flatcontainer/echelonfoundry.aegis.core/{{version}}/echelonfoundry.aegis.core.{{version}}.nupkg"
      },
      "artifacts": [ { "name": "EchelonFoundry.Aegis.Core.{{version}}.nupkg", "purpose": "package", "platform": null, "sha256": "{{String.replicate 64 "f"}}" } ]
    }"""

/// A resolved set: one native host tool (a set needs one) and Aegis.
let private resolvedSet (path: string) (aegisVersion: string) =
    let rid = Platform.runtimeIdentifier ()

    File.WriteAllText(
        path,
        $$"""{
  "schema": "echelon.resolved-release-set/v1",
  "profile": { "id": "echelon-current-test", "version": "1.0.0", "sha256": "{{String.replicate 64 "1"}}" },
  "platform": "{{rid}}",
  "resolver": { "name": "test", "version": "1.0.0" },
  "catalogSnapshot": { "sha256": "{{String.replicate 64 "2"}}" },
  "components": [
    {
      "systemId": "hosttool",
      "role": "host-tool",
      "required": true,
      "version": "1.0.0",
      "repository": "example/hosttool",
      "tag": "v1.0.0",
      "commit": "{{String.replicate 40 "b"}}",
      "releaseStage": "stable",
      "lifecycleState": "active",
      "distributionClass": "self-contained-native-cli",
      "executable": "hosttool",
      "releaseManifest": { "schema": "echelon.release/v2", "sha256": "{{String.replicate 64 "d"}}" },
      "distribution": { "mechanism": "github-release", "url": "https://github.com/example/hosttool/releases/tag/v1.0.0" },
      "artifacts": [ { "name": "hosttool-{{rid}}.tar.gz", "purpose": "executable", "platform": "{{rid}}", "sha256": "{{String.replicate 64 "c"}}" } ]
    },{{aegisComponent aegisVersion}}
  ]
}
"""
    )

    sha256File path

let private currentUpgrade (check: string -> bool -> unit) =
    let target = temp "current"
    let home = temp "home"
    let sets = temp "sets"

    try
        let firstSet = Path.Combine(sets, "first.json")
        let firstSha = resolvedSet firstSet "1.0.0"
        let authorityDirectory = Path.Combine(target, ".conditor", "authority")
        Directory.CreateDirectory authorityDirectory |> ignore
        File.Copy(firstSet, Path.Combine(authorityDirectory, "resolved-release-set.json"))

        let authority =
            $$""""registryAuthority": { "kind": "resolved-release-set", "path": ".conditor/authority/resolved-release-set.json", "sha256": "{{firstSha}}" },
  """

        let manifestPath = manifest target authority "1.0.0"
        let engine = write target "src/engine/App.Engine.fsproj" (engineProject "1.0.0" "1.0.0")

        LockFile.write
            target
            manifestPath
            { ProjectName = "consumer"
              Operation = Init
              Components = []
              Actions = [] }
        |> ignore

        let ctx: WorkstationContext =
            { Home = home
              ArtifactMirror = None
              Offline = true
              Praxis = None
              TargetId = Some "nuget-binding-test" }

        let probe executable arguments = ProcessRunner.runProcess target executable arguments
        let upgraded = Path.Combine(sets, "upgraded.json")
        let upgradedSha = resolvedSet upgraded "2.0.0"

        let previewed () =
            Manifest.load manifestPath
            |> Result.bind (fun loaded -> CurrentUpgrade.preview target manifestPath loaded upgraded upgradedSha ctx probe |> Result.map (fun plan -> loaded, plan))

        match previewed () with
        | Error errors -> check $"an Aegis current-upgrade previews: {joined errors}" false
        | Ok(loaded, plan) ->
            check $"""an Aegis current-upgrade has no refusals: {joined plan.Refusals}""" plan.Refusals.IsEmpty
            check "Aegis 1.0.0 -> 2.0.0 is a nuget-binding transition" (plan.Transitions |> List.map (fun t -> t.Id, t.Mode) = [ "aegis", NugetBinding.Mode ])
            check "the plan carries the pin move" (plan.NugetBindingTransitions |> List.map (fun binding -> binding.Id, binding.FromVersion, binding.ToVersion) = [ "aegis", "1.0.0", "2.0.0" ])
            check "previewing writes nothing" ((File.ReadAllText engine).Contains "Version=\"1.0.0\"")

            match CurrentUpgrade.apply target manifestPath loaded upgraded upgradedSha ctx probe plan.Digest with
            | Error errors -> check $"the Aegis current-upgrade applies: {joined errors}" false
            | Ok result ->
                check "the Aegis move is reported with nothing remaining" (result.ChangedComponents = [ "aegis" ] && result.NoRemainingVersionChanges)
                check "every Aegis pin moved to 2.0.0" (not ((File.ReadAllText engine).Contains "Version=\"1.0.0\""))
                check "conditor.json declares Aegis 2.0.0" ((File.ReadAllText manifestPath).Contains "\"2.0.0\"")
                check "the authority is now the set that selects Aegis 2.0.0" (sha256File (Path.Combine(authorityDirectory, "resolved-release-set.json")) = upgradedSha)

        // A repository whose pins Conditor did not set is refused, not moved.
        let other = temp "current-refused"

        try
            Directory.CreateDirectory(Path.Combine(other, ".conditor", "authority")) |> ignore
            File.Copy(firstSet, Path.Combine(other, ".conditor", "authority", "resolved-release-set.json"))
            let otherManifest = manifest other authority "1.0.0"
            write other "src/engine/App.Engine.fsproj" (engineProject "1.0.0" "0.9.0") |> ignore

            LockFile.write
                other
                otherManifest
                { ProjectName = "consumer"
                  Operation = Init
                  Components = []
                  Actions = [] }
            |> ignore

            match
                Manifest.load otherManifest
                |> Result.bind (fun loaded -> CurrentUpgrade.preview other otherManifest loaded upgraded upgradedSha ctx probe)
            with
            | Error errors -> check $"a refused Aegis move still previews: {joined errors}" false
            | Ok plan ->
                check
                    "an Aegis pin Conditor did not set refuses the current-upgrade"
                    (plan.Refusals |> List.exists (fun refusal -> refusal.Contains "0.9.0"))
        finally
            Directory.Delete(other, true)
    finally
        for directory in [ target; home; sets ] do
            if Directory.Exists directory then Directory.Delete(directory, true)

let run (check: string -> bool -> unit) =
    if OperatingSystem.IsWindows() then
        check "NuGet binding repository fixtures are skipped on Windows" true
    else
        planning check
        versionOnlyUpgrade check
        currentUpgrade check
