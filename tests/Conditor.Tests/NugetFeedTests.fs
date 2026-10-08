module NugetFeedTests

// The NuGet release-asset feed (docs/nuget-feed-contract.md): a Registry
// `nuget-library` release whose packages are GitHub release assets is
// installed into the consumer's `vendor/nuget` feed, proven against the
// Registry digests, locked, and mapped in NuGet.config so its package ids can
// never come from a public feed. Exercised as Arca's packages, which ship
// this way until nuget.org publishing exists.

open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open Conditor.Core
open Conditor.Core.Workstation

let private temp label =
    let path = Path.Combine(Path.GetTempPath(), $"conditor-feed-{label}-{Guid.NewGuid():N}")
    Directory.CreateDirectory path |> ignore
    path

let private sha256 (bytes: byte array) =
    Convert.ToHexString(SHA256.HashData bytes).ToLowerInvariant()

let private sha256File (path: string) = sha256 (File.ReadAllBytes path)

let private packageBytes id version = Encoding.UTF8.GetBytes $"fixture nupkg {id} {version}"

let private arcaComponent (version: string) (coreSha: string) (githubSha: string) =
    $$"""
    {
      "systemId": "arca",
      "role": "project-binding",
      "required": false,
      "version": "{{version}}",
      "repository": "kemiller2002/arca",
      "tag": "v{{version}}",
      "commit": "{{String.replicate 40 "a"}}",
      "releaseStage": "stable",
      "lifecycleState": "active",
      "distributionClass": "nuget-library",
      "executable": null,
      "releaseManifest": { "schema": "echelon.release/v2", "sha256": "{{String.replicate 64 "e"}}" },
      "distribution": {
        "mechanism": "github-release",
        "url": "https://github.com/kemiller2002/arca/releases/tag/v{{version}}"
      },
      "artifacts": [
        { "name": "EchelonFoundry.Arca.Core.{{version}}.nupkg", "purpose": "package", "platform": null, "sha256": "{{coreSha}}" },
        { "name": "EchelonFoundry.Arca.GitHub.{{version}}.nupkg", "purpose": "package", "platform": null, "sha256": "{{githubSha}}" },
        { "name": "checksums.txt", "purpose": "checksums", "platform": null, "sha256": "{{String.replicate 64 "f"}}" }
      ]
    }"""

let private arca version =
    arcaComponent
        version
        (sha256 (packageBytes "EchelonFoundry.Arca.Core" version))
        (sha256 (packageBytes "EchelonFoundry.Arca.GitHub" version))

/// A resolved set: one native host tool (a set needs one) and the given components.
let private resolvedSet (path: string) (components: string list) =
    let rid = Platform.runtimeIdentifier ()

    let native =
        $$"""
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
    }"""

    let joined = String.concat "," (native :: components)

    File.WriteAllText(
        path,
        $$"""{
  "schema": "echelon.resolved-release-set/v1",
  "profile": { "id": "echelon-current-test", "version": "1.0.0", "sha256": "{{String.replicate 64 "1"}}" },
  "platform": "{{rid}}",
  "resolver": { "name": "test", "version": "1.0.0" },
  "catalogSnapshot": { "sha256": "{{String.replicate 64 "2"}}" },
  "components": [{{joined}}]
}
"""
    )

    sha256File path

let private release version =
    use document = JsonDocument.Parse(arca version)

    match NugetFeed.ofComponent document.RootElement with
    | Ok value -> value
    | Error errors -> failwith (String.concat "; " errors)

/// Answers from fixture bytes, as a mirror or GitHub would.
let private fetchFixtures (tamper: string option) : NugetFeed.Fetch =
    fun package ->
        if tamper = Some package.ArtifactName then
            Ok(Encoding.UTF8.GetBytes "tampered")
        else
            Ok(packageBytes package.PackageId package.Version)

let private parsing (check: string -> bool -> unit) =
    let parsed = release "0.1.0"
    check "a feed release names each package id from its asset" (parsed.Packages |> List.map _.PackageId = [ "EchelonFoundry.Arca.Core"; "EchelonFoundry.Arca.GitHub" ])
    check "checksums and other assets are not packages" (parsed.Packages.Length = 2)

    check
        "each package downloads from its release asset URL"
        (parsed.Packages.Head.DownloadUrl = "https://github.com/kemiller2002/arca/releases/download/v0.1.0/EchelonFoundry.Arca.Core.0.1.0.nupkg")

    let refused (fragment: string) (json: string) =
        use document = JsonDocument.Parse json

        match NugetFeed.ofComponent document.RootElement with
        | Error errors -> errors |> List.exists (fun error -> error.Contains fragment)
        | Ok _ -> false

    check "an asset not named <id>.<version>.nupkg is refused" (refused "must be" ((arca "0.1.0").Replace("EchelonFoundry.Arca.Core.0.1.0.nupkg", "core.nupkg")))
    check "an asset without a valid digest is refused" (refused "SHA-256" ((arca "0.1.0").Replace(sha256 (packageBytes "EchelonFoundry.Arca.Core" "0.1.0"), "nope")))
    check "an inactive release is refused" (refused "withdrawn" ((arca "0.1.0").Replace("\"active\"", "\"withdrawn\"")))
    check "a release without a manifest digest is refused" (refused "release manifest" ((arca "0.1.0").Replace(String.replicate 64 "e", "x")))

    check
        "a package id named twice is refused"
        (refused "more than once" ((arca "0.1.0").Replace("EchelonFoundry.Arca.GitHub.0.1.0.nupkg", "EchelonFoundry.Arca.Core.0.1.0.nupkg")))

    let set = temp "set"

    try
        let path = Path.Combine(set, "set.json")
        resolvedSet path [ arca "0.1.0" ] |> ignore

        match NugetFeed.loadAll path with
        | Ok feeds -> check "a resolved set's feed releases load by system id" (feeds |> Map.keys |> List.ofSeq = [ "arca" ])
        | Error errors -> check $"""resolved set feeds load: {String.concat "; " errors}""" false
    finally
        Directory.Delete(set, true)

let private configuration (check: string -> bool -> unit) =
    let ids = [ "EchelonFoundry.Arca.Core"; "EchelonFoundry.Arca.GitHub" ]

    match NugetFeed.configure None ids with
    | Error error -> check $"a new NuGet.config is created: {error}" false
    | Ok created ->
        check "a new NuGet.config maps the ids to the feed only" (NugetFeed.checkConfiguration created ids).IsEmpty
        check "a new NuGet.config keeps everything else on nuget.org" (created.Contains "pattern=\"*\"" && created.Contains "api.nuget.org")
        check "configuring is idempotent" (NugetFeed.configure (Some created) ids = Ok created)

    // Praxis's own NuGet.config, which vendors ordo-core this way by hand.
    let praxis =
        """<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <!-- EchelonFoundry.Ordo.Core comes from the vendored release asset. -->
  <packageSources>
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" protocolVersion="3" />
    <add key="echelon-vendor" value="vendor/nuget" />
  </packageSources>
  <packageSourceMapping>
    <packageSource key="echelon-vendor">
      <package pattern="EchelonFoundry.Ordo.Core" />
    </packageSource>
    <packageSource key="nuget.org">
      <package pattern="*" />
    </packageSource>
  </packageSourceMapping>
</configuration>
"""

    match NugetFeed.configure (Some praxis) ids with
    | Error error -> check $"an existing vendor mapping is extended: {error}" false
    | Ok extended ->
        check "existing mappings and comments are kept" (extended.Contains "EchelonFoundry.Ordo.Core" && extended.Contains "vendored release asset")
        check "the new ids join the existing vendor source" (NugetFeed.checkConfiguration extended ("EchelonFoundry.Ordo.Core" :: ids)).IsEmpty

    let conflictingKey = praxis.Replace("value=\"vendor/nuget\"", "value=\"elsewhere\"")
    check "a feed source key pointing elsewhere is refused" (Result.isError (NugetFeed.configure (Some conflictingKey) ids))

    let mappedElsewhere = praxis.Replace("<package pattern=\"*\" />", "<package pattern=\"*\" />\n      <package pattern=\"EchelonFoundry.Arca.Core\" />")
    check "an id another source already maps is refused, not overridden" (Result.isError (NugetFeed.configure (Some mappedElsewhere) ids))

    check
        "a configuration that maps an id to two sources is reported"
        (NugetFeed.checkConfiguration mappedElsewhere [ "EchelonFoundry.Arca.Core" ] |> List.exists (fun problem -> problem.Contains "not only"))

let private repinning (check: string -> bool -> unit) =
    let ids = [ "EchelonFoundry.Arca.Core"; "EchelonFoundry.Arca.GitHub" ]

    let props =
        """<Project>
  <ItemGroup>
    <PackageVersion Include="FSharp.Core" Version="10.1.400" />
    <PackageVersion Include="EchelonFoundry.Arca.Core" Version="0.1.0" />
    <PackageVersion Include="echelonfoundry.arca.github" Version="0.1.0" />
  </ItemGroup>
</Project>
"""

    match NugetFeed.repin ids "0.1.0" "0.2.0" props with
    | Ok(text, moved) ->
        check "exact pins of the package ids move, in any letter case" (moved = 2 && text.Contains "\"EchelonFoundry.Arca.Core\" Version=\"0.2.0\"" && text.Contains "\"echelonfoundry.arca.github\" Version=\"0.2.0\"")
        check "other packages are untouched" (text.Contains "\"FSharp.Core\" Version=\"10.1.400\"")
    | Error error -> check $"repinning succeeds: {error}" false

    let reference = """<PackageReference Include="EchelonFoundry.Arca.Core" Version="[0.1.0,)" />"""
    check "a range or another version is refused, not rewritten" (Result.isError (NugetFeed.repin ids "0.1.0" "0.2.0" reference))

let private feedInRepository (check: string -> bool -> unit) =
    let target = temp "repo"

    try
        let first = release "0.1.0"

        match NugetFeed.ensure (fetchFixtures (Some "EchelonFoundry.Arca.GitHub.0.1.0.nupkg")) target None first with
        | Error errors ->
            check "a package that does not match the Registry digest stops the feed" (errors |> List.exists (fun error -> error.Contains "nothing was changed"))
            check "nothing is written when a proof fails" (not (Directory.Exists(Path.Combine(target, "vendor"))) && not (File.Exists(Path.Combine(target, "NuGet.config"))))
        | Ok() -> check "a tampered package must be refused" false

        match NugetFeed.ensure (fetchFixtures None) target None first with
        | Error errors -> check $"""the feed is established: {String.concat "; " errors}""" false
        | Ok() ->
            check "the feed holds the exact release assets" (File.Exists(Path.Combine(target, "vendor/nuget/EchelonFoundry.Arca.Core.0.1.0.nupkg")))
            check "the lock records the release" ((File.ReadAllText(Path.Combine(target, "vendor/nuget/arca.lock"))).Contains "version 0.1.0")
            check "an established feed verifies" (NugetFeed.verify target first).IsEmpty

        File.WriteAllText(
            Path.Combine(target, "Directory.Packages.props"),
            """<Project><ItemGroup><PackageVersion Include="EchelonFoundry.Arca.Core" Version="0.1.0" /></ItemGroup></Project>"""
        )

        let second = release "0.2.0"

        match NugetFeed.ensure (fetchFixtures None) target (Some "0.1.0") second with
        | Error errors -> check $"""the feed upgrades: {String.concat "; " errors}""" false
        | Ok() ->
            check "an upgrade removes the previous version's packages" (not (File.Exists(Path.Combine(target, "vendor/nuget/EchelonFoundry.Arca.Core.0.1.0.nupkg"))))
            check "an upgrade moves exact pins to the new version" ((File.ReadAllText(Path.Combine(target, "Directory.Packages.props"))).Contains "Version=\"0.2.0\"")
            check "the upgraded feed verifies" (NugetFeed.verify target second).IsEmpty

        File.WriteAllText(Path.Combine(target, "vendor/nuget/EchelonFoundry.Arca.GitHub.0.2.0.nupkg"), "changed by hand")
        check "a package changed in place is reported" (NugetFeed.verify target second |> List.exists (fun problem -> problem.Contains "not the Registry's"))
        check "a lock for another version is reported" (NugetFeed.verify target first |> List.exists (fun problem -> problem.Contains "does not record"))
    finally
        Directory.Delete(target, true)

let private manifestFor (target: string) (authoritySha: string) (components: string) =
    let path = Path.Combine(target, "conditor.json")

    File.WriteAllText(
        path,
        $$"""{
  "schemaVersion": 1,
  "name": "consumer",
  "registryAuthority": { "kind": "resolved-release-set", "path": ".conditor/authority/resolved-release-set.json", "sha256": "{{authoritySha}}" },
  "components": [{{components}}],
  "requirements": [],
  "execution": { "enabled": false }
}
"""
    )

    path

let private planning (check: string -> bool -> unit) =
    let target = temp "plan"
    let mirror = temp "mirror"
    let priorMirror = Environment.GetEnvironmentVariable "CONDITOR_ARTIFACT_MIRROR"

    try
        let authorityDirectory = Path.Combine(target, ".conditor", "authority")
        Directory.CreateDirectory authorityDirectory |> ignore
        let authoritySha = resolvedSet (Path.Combine(authorityDirectory, "resolved-release-set.json")) [ arca "0.1.0" ]
        let manifestPath = manifestFor target authoritySha """{ "id": "arca", "version": "0.1.0", "required": true }"""

        for package in (release "0.1.0").Packages do
            File.WriteAllBytes(Path.Combine(mirror, package.ArtifactName), packageBytes package.PackageId package.Version)

        Environment.SetEnvironmentVariable("CONDITOR_ARTIFACT_MIRROR", mirror)

        match Manifest.load manifestPath |> Result.bind (Planner.create target Init) with
        | Error errors -> check $"""a Registry-selected feed component plans: {String.concat "; " errors}""" false
        | Ok plan ->
            let kinds = plan.Actions |> List.map _.Kind
            check "init establishes and then verifies the feed" (kinds = [ NugetFeedInstall; NugetFeedVerify ])

            check
                "the resolved component names its packages and release"
                (plan.Components |> List.exists (fun c -> c.Id = "arca" && c.Package = "EchelonFoundry.Arca.Core,EchelonFoundry.Arca.GitHub"))

            match Installer.execute target manifestPath plan with
            | Error errors -> check $"""init installs the feed: {String.concat "; " errors}""" false
            | Ok _ ->
                check "init installs the feed from the release assets" (NugetFeed.verify target (release "0.1.0")).IsEmpty

        match Manifest.load manifestPath |> Result.bind (Planner.create target Verify) with
        | Error errors -> check $"""verify plans: {String.concat "; " errors}""" false
        | Ok plan ->
            check "verify only proves the feed" (plan.Actions |> List.map _.Kind = [ NugetFeedVerify ])
            File.Delete(Path.Combine(target, "vendor/nuget/EchelonFoundry.Arca.Core.0.1.0.nupkg"))

            match Installer.execute target manifestPath plan with
            | Error errors -> check "verify fails when a package is missing" (errors |> List.exists (fun error -> error.Contains "is missing"))
            | Ok _ -> check "verify must fail when a package is missing" false

        let otherVersion = manifestFor target authoritySha """{ "id": "arca", "version": "0.2.0", "required": true }"""

        match Manifest.load otherVersion |> Result.bind (Planner.create target Init) with
        | Error errors -> check "a version the authority does not select is refused" (not errors.IsEmpty)
        | Ok _ -> check "a version the authority does not select must be refused" false
    finally
        Environment.SetEnvironmentVariable("CONDITOR_ARTIFACT_MIRROR", priorMirror)
        Directory.Delete(target, true)
        Directory.Delete(mirror, true)

let private currentUpgrade (check: string -> bool -> unit) =
    let target = temp "current"
    let home = temp "home"
    let mirror = temp "current-mirror"

    try
        // The repository was established on a set that does not carry arca.
        let firstSet = Path.Combine(mirror, "first.json")
        let firstSha = resolvedSet firstSet []
        let authorityDirectory = Path.Combine(target, ".conditor", "authority")
        Directory.CreateDirectory authorityDirectory |> ignore
        File.Copy(firstSet, Path.Combine(authorityDirectory, "resolved-release-set.json"))
        let manifestPath = manifestFor target firstSha ""

        LockFile.write
            target
            manifestPath
            { ProjectName = "consumer"
              Operation = Init
              Components = []
              Actions = [] }
        |> ignore

        for version in [ "0.1.0"; "0.2.0" ] do
            for package in (release version).Packages do
                File.WriteAllBytes(Path.Combine(mirror, package.ArtifactName), packageBytes package.PackageId package.Version)

        let ctx: WorkstationContext =
            { Home = home
              ArtifactMirror = Some mirror
              Offline = true
              Praxis = None
              TargetId = Some "nuget-feed-test" }

        let probe executable arguments = ProcessRunner.runProcess target executable arguments
        let withArca = Path.Combine(mirror, "with-arca.json")
        let withArcaSha = resolvedSet withArca [ arca "0.1.0" ]

        // Opt in: declare arca at exactly the selected version.
        manifestFor target firstSha """{ "id": "arca", "version": "0.1.0", "required": true }""" |> ignore

        let previewWith setPath setSha =
            Manifest.load manifestPath
            |> Result.bind (fun manifest -> CurrentUpgrade.preview target manifestPath manifest setPath setSha ctx probe |> Result.map (fun plan -> manifest, plan))

        match previewWith withArca withArcaSha with
        | Error errors -> check $"""a feed opt-in previews: {String.concat "; " errors}""" false
        | Ok(manifest, plan) ->
            check "a feed opt-in has no refusals" plan.Refusals.IsEmpty
            check "a feed opt-in is planned as a feed change" (plan.NugetFeedChanges |> List.map (fun (r, previous) -> r.Id, previous) = [ "arca", None ])
            check "opt-in planning writes nothing" (not (Directory.Exists(Path.Combine(target, "vendor"))))

            match CurrentUpgrade.apply target manifestPath manifest withArca withArcaSha ctx probe plan.Digest with
            | Error errors -> check $"""the feed opt-in applies: {String.concat "; " errors}""" false
            | Ok result ->
                check "the opt-in is reported" (result.OptedIn = [ "arca@0.1.0" ])
                check "the opted-in feed is established" (NugetFeed.verify target (release "0.1.0")).IsEmpty
                check "the authority now is the set that selects arca" (sha256File (Path.Combine(authorityDirectory, "resolved-release-set.json")) = withArcaSha)

        // A later set selects 0.2.0: the feed moves and exact pins follow.
        File.WriteAllText(
            Path.Combine(target, "Directory.Packages.props"),
            """<Project><ItemGroup><PackageVersion Include="EchelonFoundry.Arca.Core" Version="0.1.0" /><PackageVersion Include="EchelonFoundry.Arca.GitHub" Version="0.1.0" /></ItemGroup></Project>"""
        )

        let upgraded = Path.Combine(mirror, "upgraded.json")
        let upgradedSha = resolvedSet upgraded [ arca "0.2.0" ]

        match previewWith upgraded upgradedSha with
        | Error errors -> check $"""a feed version change previews: {String.concat "; " errors}""" false
        | Ok(manifest, plan) ->
            check "a feed version change has no refusals" plan.Refusals.IsEmpty
            check "a feed version change is a nuget-feed transition" (plan.Transitions |> List.map (fun t -> t.Id, t.Mode) = [ "arca", NugetFeed.Mode ])

            match CurrentUpgrade.apply target manifestPath manifest upgraded upgradedSha ctx probe plan.Digest with
            | Error errors -> check $"""the feed version change applies: {String.concat "; " errors}""" false
            | Ok result ->
                check "the version change is reported with nothing remaining" (result.ChangedComponents = [ "arca" ] && result.NoRemainingVersionChanges)
                check "the new feed is established" (NugetFeed.verify target (release "0.2.0")).IsEmpty
                check "exact pins moved with the feed" (not ((File.ReadAllText(Path.Combine(target, "Directory.Packages.props"))).Contains "0.1.0"))
                check "conditor.json declares the new version" ((File.ReadAllText manifestPath).Contains "\"0.2.0\"")
    finally
        for directory in [ target; home; mirror ] do
            if Directory.Exists directory then Directory.Delete(directory, true)

let run (check: string -> bool -> unit) =
    parsing check
    configuration check
    repinning check
    feedInRepository check

    if OperatingSystem.IsWindows() then
        check "NuGet feed repository fixtures are skipped on Windows" true
    else
        planning check
        currentUpgrade check
