module DriftTests

// The rolling-upgrade advisory (CON-300..320, DF-CON-2026-A003). Unit tests of
// the version, range, policy and assessment rules, and a fixture-based
// integration run. The fixture has a digest-chained Registry checkout with Git
// registration history, and a Git-dated target repository whose web package
// (Forma, pinned by GitHub release-asset tarball in package.json and in
// .echelon/foundations.json) is two registered minor lines behind.

open System
open System.Diagnostics
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open Conditor.Core

let private joined (values: string list) = String.concat "; " values

let private temp label =
    let path = Path.Combine(Path.GetTempPath(), $"conditor-drift-{label}-{Guid.NewGuid():N}")
    Directory.CreateDirectory path |> ignore
    path

let private sha256File (path: string) =
    Convert.ToHexString(SHA256.HashData(File.ReadAllBytes path)).ToLowerInvariant()

let private write (root: string) (relative: string) (text: string) =
    let path = Path.Combine(root, relative)
    Directory.CreateDirectory(Path.GetDirectoryName path |> nonNull) |> ignore
    File.WriteAllText(path, text)
    path

/// Runs git with fixed identity and dates so history is deterministic.
let private git (directory: string) (date: string) (arguments: string list) =
    let info = ProcessStartInfo("git")
    info.WorkingDirectory <- directory
    info.UseShellExecute <- false
    info.RedirectStandardOutput <- true
    info.RedirectStandardError <- true

    for argument in arguments do
        info.ArgumentList.Add argument

    for name, value in
        [ "GIT_AUTHOR_NAME", "Fixture"
          "GIT_AUTHOR_EMAIL", "fixture@example.invalid"
          "GIT_COMMITTER_NAME", "Fixture"
          "GIT_COMMITTER_EMAIL", "fixture@example.invalid"
          "GIT_AUTHOR_DATE", date
          "GIT_COMMITTER_DATE", date ] do
        info.Environment[name] <- value

    use proc = Process.Start info |> nonNull
    proc.StandardOutput.ReadToEnd() |> ignore
    proc.StandardError.ReadToEnd() |> ignore
    proc.WaitForExit()

    if proc.ExitCode <> 0 then
        failwith $"""git {String.concat " " arguments} failed in {directory}"""

let private commitAll (directory: string) (date: string) (message: string) =
    git directory date [ "add"; "-A" ]
    git directory date [ "commit"; "-q"; "--no-gpg-sign"; "-m"; message ]

let private initRepository (directory: string) =
    git directory "2026-01-01T00:00:00Z" [ "init"; "-q" ]

// ---- Registry fixture --------------------------------------------------------

type private Release =
    { System: string
      Version: string
      Class: string
      Mechanism: string
      Package: string option
      Artifacts: string list
      MigrationNotes: string option
      State: string }

let private webRelease system package stem version =
    { System = system
      Version = version
      Class = "web-package"
      Mechanism = "github-release"
      Package = Some package
      Artifacts = [ $"{stem}-{version}.tgz" ]
      MigrationNotes = None
      State = "active" }

let private nugetRelease system (ids: string list) version =
    { System = system
      Version = version
      Class = "nuget-library"
      Mechanism = "github-release"
      Package = None
      Artifacts = ids |> List.map (fun id -> $"{id}.{version}.nupkg")
      MigrationNotes = None
      State = "active" }

let private releaseJson (release: Release) =
    let package =
        release.Package |> Option.map (fun name -> $"\"{name}\"") |> Option.defaultValue "null"

    let notes =
        release.MigrationNotes
        |> Option.map (fun url -> $",\n  \"migrationNotes\": \"{url}\"")
        |> Option.defaultValue ""

    let artifacts =
        release.Artifacts
        |> List.map (fun name ->
            let digest = String.replicate 64 "e"
            $"    {{ \"name\": \"{name}\", \"purpose\": \"package\", \"platform\": null, \"sha256\": \"{digest}\" }}")
        |> String.concat ",\n"

    $$"""{
  "schema": "echelon.release/v2",
  "systemId": "{{release.System}}",
  "version": "{{release.Version}}",
  "repository": "kemiller2002/{{release.System}}",
  "tag": "v{{release.Version}}",
  "commit": "{{String.replicate 40 "a"}}",
  "releaseStage": "stable",
  "lifecycleState": "{{release.State}}",
  "distributionClass": "{{release.Class}}",
  "executable": null,
  "distributions": [
    { "mechanism": "{{release.Mechanism}}", "package": {{package}}, "url": "https://github.com/kemiller2002/{{release.System}}/releases/tag/v{{release.Version}}" }
  ],
  "artifacts": [
{{artifacts}}
  ]{{notes}}
}
"""

type private RegistryFixture =
    { Root: string
      Platform: string }

/// Writes a digest-chained Registry checkout: release manifests, a profile, a
/// catalog snapshot, one platform resolved set, and the channel index. Older
/// releases are committed first so their registration times are earlier.
let private buildRegistry
    (root: string)
    (history: (string * Release list) list)
    (ranges: (string * string) list)
    (selected: (string * string) list)
    (setPolicy: string option)
    (withGit: bool)
    =
    let platform = "linux-x64"

    if withGit then initRepository root

    let manifests = ResizeArray<Release * string>()

    for date, releases in history do
        for release in releases do
            let relative = $"releases/{release.System}/{release.Version}.release.json"
            write root relative (releaseJson release) |> ignore
            manifests.Add(release, relative)

        if withGit then commitAll root date $"record releases at {date}"

    let profileComponents =
        ranges
        |> List.map (fun (id, range) -> $"    {{ \"systemId\": \"{id}\", \"role\": \"project-binding\", \"required\": true, \"version\": {{ \"range\": \"{range}\" }} }}")
        |> String.concat ",\n"

    let profilePath =
        write
            root
            "profiles/test.profile.json"
            $$"""{
  "schema": "echelon.profile/v1",
  "id": "test-current",
  "version": "2.0.0",
  "allowedReleaseStages": [ "stable" ],
  "components": [
{{profileComponents}}
  ]
}
"""

    let profileSha = sha256File profilePath

    let releaseEntries =
        manifests
        |> Seq.map (fun (release, relative) ->
            $"    {{ \"systemId\": \"{release.System}\", \"version\": \"{release.Version}\", \"manifest\": \"{relative}\", \"sha256\": \"{sha256File (Path.Combine(root, relative))}\" }}")
        |> String.concat ",\n"

    let snapshotPath =
        write
            root
            "snapshots/test.catalog.json"
            $$"""{
  "schema": "echelon.catalog-snapshot/v1",
  "systems": [],
  "releases": [
{{releaseEntries}}
  ],
  "profiles": [
    { "id": "test-current", "version": "2.0.0", "manifest": "profiles/test.profile.json", "sha256": "{{profileSha}}" }
  ]
}
"""

    let selections =
        selected
        |> List.map (fun (id, version) -> $"    {{ \"systemId\": \"{id}\", \"version\": \"{version}\", \"role\": \"project-binding\" }}")
        |> String.concat ",\n"

    let policy =
        setPolicy |> Option.map (fun text -> $",\n  \"stalenessPolicy\": {text}") |> Option.defaultValue ""

    let setPath =
        write
            root
            $"channels/test-current/{platform}.json"
            $$"""{
  "schema": "echelon.resolved-release-set/v1",
  "profile": { "id": "test-current", "version": "2.0.0", "sha256": "{{profileSha}}" },
  "platform": "{{platform}}",
  "components": [
{{selections}}
  ]{{policy}}
}
"""

    write
        root
        "channels/test-current/channel.json"
        $$"""{
  "schema": "echelon.current-channel/v1",
  "id": "test-current",
  "profile": { "id": "test-current", "version": "2.0.0", "sha256": "{{profileSha}}" },
  "catalogSnapshot": { "path": "snapshots/test.catalog.json", "sha256": "{{sha256File snapshotPath}}" },
  "platforms": [
    { "platform": "{{platform}}", "path": "channels/test-current/{{platform}}.json", "sha256": "{{sha256File setPath}}" }
  ]
}
"""
    |> ignore

    if withGit then commitAll root (fst (List.last history)) "channel"

    { Root = root; Platform = platform }

let private FormaPackage = "@echelon-foundry/design-system"
let private FormaStem = "echelon-foundry-design-system"
let private FolioPackage = "@echelon-foundry/print-components"
let private FolioStem = "echelon-foundry-print-components"

let private formaAsset version =
    $"https://github.com/kemiller2002/forma/releases/download/v{version}/{FormaStem}-{version}.tgz"

/// The standard Registry history: Forma 0.4.1 then 0.5.0 then 0.6.1 (with a
/// migration-notes link), Folio, Limen, Arca and Fides.
let private standardRegistry (root: string) (setPolicy: string option) (withGit: bool) =
    buildRegistry
        root
        [ "2026-09-01T00:00:00Z",
          [ webRelease "forma" FormaPackage FormaStem "0.3.0"
            webRelease "forma" FormaPackage FormaStem "0.4.1"
            webRelease "folio" FolioPackage FolioStem "0.3.0"
            webRelease "limen" "@echelon-foundry/limen" "echelon-foundry-limen" "0.9.0"
            nugetRelease "arca" [ "EchelonFoundry.Arca.Core"; "EchelonFoundry.Arca.GitHub" ] "0.4.1"
            nugetRelease "fides" [ "EchelonFoundry.Fides" ] "0.1.0" ]
          "2026-10-08T14:28:26Z",
          [ webRelease "forma" FormaPackage FormaStem "0.5.0"
            nugetRelease "arca" [ "EchelonFoundry.Arca.Core"; "EchelonFoundry.Arca.GitHub" ] "0.5.0" ]
          "2026-10-13T12:00:00Z",
          [ { webRelease "forma" FormaPackage FormaStem "0.6.1" with
                MigrationNotes = Some "https://github.com/kemiller2002/forma/blob/v0.6.1/MIGRATION.md" }
            { webRelease "forma" FormaPackage FormaStem "0.6.0" with State = "retired" } ] ]
        [ "forma", ">=0.3.0 <1.0.0"
          "folio", ">=0.3.0 <1.0.0"
          "limen", ">=0.6.0 <1.0.0"
          "arca", ">=0.4.1 <1.0.0"
          "fides", ">=0.1.0 <1.0.0" ]
        [ "forma", "0.6.1"; "folio", "0.3.0"; "limen", "0.9.0"; "arca", "0.5.0"; "fides", "0.1.0" ]
        setPolicy
        withGit

/// A Chrona-shaped target: Forma by tarball (two registered lines behind) in
/// package.json and foundations.json, Limen declared and current, Arca one
/// line behind through its NuGet feed lock and MSBuild pin, Folio pinned by a
/// range Conditor will not guess, and Fides below its supported range.
let private chronaShaped (root: string) (repositoryPolicy: string option) (withGit: bool) =
    let policy =
        repositoryPolicy |> Option.map (fun text -> $",\n  \"stalenessPolicy\": {text}") |> Option.defaultValue ""

    write
        root
        "conditor.json"
        $$"""{
  "schemaVersion": 1,
  "name": "chrona-shaped",
  "components": [
    {
      "id": "limen",
      "version": "0.9.0"
    },
    {
      "id": "fides",
      "version": "0.0.9"
    }
  ]{{policy}}
}
"""
    |> ignore

    write
        root
        "package.json"
        $$"""{
  "name": "chrona-shaped",
  "dependencies": {
    "{{FormaPackage}}": "{{formaAsset "0.4.1"}}",
    "@echelon-foundry/limen": "0.9.0",
    "{{FolioPackage}}": "^0.3.0",
    "left-pad": "1.3.0"
  }
}
"""
    |> ignore

    write
        root
        ".echelon/foundations.json"
        """{
  "schemaVersion": 1,
  "application": "Chrona",
  "capabilities": {
    "forma": {
      "required": true,
      "version": "0.4.1"
    },
    "limen": {
      "required": true,
      "version": "0.9.0"
    },
    "routing": {
      "required": true
    }
  }
}
"""
    |> ignore

    write
        root
        "vendor/nuget/arca.lock"
        "# conditor.nuget-feed-lock/v1. Written by Conditor; do not edit.\nsystem arca\nversion 0.4.1\nrepository kemiller2002/arca\n"
    |> ignore

    write
        root
        "Directory.Packages.props"
        """<Project>
  <ItemGroup>
    <PackageVersion Include="EchelonFoundry.Arca.Core" Version="0.4.1" />
    <PackageVersion Include="FSharp.Core" Version="10.1.401" />
  </ItemGroup>
</Project>
"""
    |> ignore

    // Ignored directories never contribute pins.
    write root "node_modules/x/package.json" $$"""{ "dependencies": { "{{FormaPackage}}": "0.3.0" } }""" |> ignore

    if withGit then
        initRepository root
        commitAll root "2026-09-01T00:00:00Z" "pins"

let private now = DateTimeOffset(2026, 10, 13, 0, 0, 0, TimeSpan.Zero)

let private runProcess directory executable arguments =
    ProcessRunner.runProcess directory executable arguments

let private reportFor (registry: RegistryFixture) (target: string) =
    Drift.loadRegistry runProcess registry.Root "test-current" registry.Platform
    |> Result.bind (fun loaded -> Drift.report runProcess now loaded target)

let private componentOf (report: DriftReport) id =
    report.Components |> List.tryFind (fun item -> item.Id = id)

let private snapshotTree (root: string) =
    Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
    |> Seq.filter (fun path -> not (path.Contains $"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}"))
    |> Seq.sort
    |> Seq.map (fun path -> path, sha256File path)
    |> Seq.toList

// ---- Unit tests -------------------------------------------------------------

let private units check =
    let v text = SemanticVersion.tryParse text |> Option.get

    check "semantic versions order numerically" (v "0.10.0" > v "0.9.1" && v "1.0.0" > v "0.99.99")
    check "a release sorts after its pre-releases" (v "1.0.0" > v "1.0.0-rc.1")
    check "a non-semantic version does not parse" (SemanticVersion.tryParse "^0.3.0" = None)

    let range = VersionRange.tryParse ">=0.3.0 <1.0.0" |> Option.get
    check "a pin below the lower bound is below the supported range" (VersionRange.isBelow range (v "0.2.9"))
    check "a pin inside the range is not below it" (not (VersionRange.isBelow range (v "0.3.0")))
    check "a pin above the upper bound is not below the range" (not (VersionRange.isBelow range (v "1.2.0")))
    check "an unparseable range is not guessed" (VersionRange.tryParse ">=banana" = None)

    let parse (json: string) =
        use document = JsonDocument.Parse json
        StalenessPolicy.parse "test" (document.RootElement.Clone())

    check
        "the default policy warns above one minor and any major and fails only below range"
        (StalenessPolicy.defaults.WarnAfterMinorsBehind = Some 1
         && StalenessPolicy.defaults.WarnAfterMajorsBehind = Some 0
         && StalenessPolicy.defaults.WarnAfterReleasesBehind = None
         && StalenessPolicy.defaults.WarnAfterPinAgeDays = None
         && StalenessPolicy.defaults.FailBelowSupportedRange)

    check "an unknown policy field is refused" (parse """{"warnAfterMinors": 2}""" |> Result.isError)
    check "a negative threshold is refused" (parse """{"warnAfterMinorsBehind": -1}""" |> Result.isError)

    match parse """{"warnAfterMinorsBehind": 3, "warnAfterPinAgeDays": 10}""", parse """{"warnAfterMinorsBehind": null}""" with
    | Ok registry, Ok repository ->
        let policy, sources = StalenessPolicy.resolve registry repository
        let source field = sources |> List.find (fst >> (=) field) |> snd

        check
            "conditor.json overrides the Registry set, which overrides the default"
            (policy.WarnAfterMinorsBehind = None
             && policy.WarnAfterPinAgeDays = Some 10
             && policy.WarnAfterMajorsBehind = Some 0
             && source "warnAfterMinorsBehind" = "conditor.json"
             && source "warnAfterPinAgeDays" = "registry"
             && source "warnAfterMajorsBehind" = "default")
    | _ -> check "policy fixtures parse" false

    check
        "drift is advisory unless --fail-on asks otherwise"
        (Drift.exitCodeFor "never" DriftFail = 0
         && Drift.exitCodeFor "warn" DriftWarn = 3
         && Drift.exitCodeFor "warn" DriftInfo = 0
         && Drift.exitCodeFor "fail" DriftWarn = 0
         && Drift.exitCodeFor "fail" DriftFail = 3)

// ---- Integration ------------------------------------------------------------

let private integration check (schema: string -> Result<unit, string list>) =
    let registry = standardRegistry (temp "registry") None true
    let target = temp "target"
    chronaShaped target None true
    let before = snapshotTree target @ snapshotTree registry.Root

    match reportFor registry target with
    | Error errors -> check $"drift report is produced: {joined errors}" false
    | Ok report ->
        check "drift is read-only for the target and the Registry" (snapshotTree target @ snapshotTree registry.Root = before)

        match componentOf report "forma" with
        | None -> check "the tarball-pinned web package is reported" false
        | Some forma ->
            check
                "Forma is seen through its tarball pin and its foundations capability"
                (forma.Pins |> List.map (fun pin -> pin.Source, pin.Version) |> List.sort = [ "foundations", Some "0.4.1"; "package.json", Some "0.4.1" ])

            check "an ignored directory contributes no pin" (forma.Pins |> List.forall (fun pin -> not (pin.Path.Contains "node_modules")))

            check
                "Forma is two registered minor lines behind the channel's 0.6.1"
                (forma.PinnedVersion = Some "0.4.1"
                 && forma.ChannelSelected = Some "0.6.1"
                 && forma.LatestRegistered = Some "0.6.1"
                 && forma.ReleasesBehind = Some 2
                 && forma.MinorsBehind = Some 2
                 && forma.MajorsBehind = Some 0)

            check "a retired release is not a registered step" (forma.ReleasesBehind = Some 2)
            check "two minor lines behind is warn under the default policy" (forma.Level = DriftWarn)

            check
                "pin age comes from the Git commit that last changed the pin"
                (forma.PinAgeDays = Some 42
                 && forma.Pins |> List.forall (fun pin -> pin.PinnedSince = Some(DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero))))

            check
                "behind-since is the Registry registration of the first newer release"
                (forma.BehindSince = Some(DateTimeOffset(2026, 10, 8, 14, 28, 26, TimeSpan.Zero)))

            match forma.NextStep with
            | None -> check "Forma has a next step" false
            | Some step ->
                check "the next step is one registered minor line: 0.5.0" (step.Version = "0.5.0")

                check
                    "the next step links its crossed release's notes"
                    (step.Crosses = [ { Version = "0.5.0"; MigrationNotes = "https://github.com/kemiller2002/forma/releases/tag/v0.5.0" } ])

                check
                    "the next step names the exact new tarball and foundations pins"
                    (step.PinUpdates
                     |> List.map (fun update -> update.Path, update.To)
                     |> List.sort = [ ".echelon/foundations.json", "0.5.0"; "package.json", formaAsset "0.5.0" ])

                check "an undeclared pin gets no Conditor command" (step.Command = None)

                check
                    "an undeclared web package names the exact conditor.json entry that adopts it"
                    (step.Declare = Some "{ \"id\": \"forma\", \"version\": \"0.4.1\", \"required\": true }")

        match componentOf report "arca" with
        | Some arca ->
            check
                "Arca is seen through its NuGet feed lock and MSBuild pin, one line behind"
                (arca.Pins |> List.map _.Source |> List.sort = [ "msbuild"; "nuget-feed-lock" ]
                 && arca.MinorsBehind = Some 1
                 && arca.Level = DriftInfo
                 && arca.NextStep |> Option.exists (fun step -> step.Version = "0.5.0"))

            check
                "the MSBuild pin's next specifier keeps its package id"
                (arca.NextStep
                 |> Option.exists (fun step -> step.PinUpdates |> List.exists (fun update -> update.To = "EchelonFoundry.Arca.Core 0.5.0")))
        | None -> check "Arca is reported" false

        match componentOf report "limen" with
        | Some limen ->
            check
                "a declared, current component is ok with no next step"
                (limen.Declared && limen.Level = DriftOk && limen.NextStep.IsNone && limen.Consistent)
        | None -> check "Limen is reported" false

        match componentOf report "folio" with
        | Some folio ->
            check
                "a range specifier is reported verbatim and never guessed"
                (folio.Pins |> List.exists (fun pin -> pin.Specifier = "^0.3.0" && pin.Version = None)
                 && folio.Level = DriftWarn)
        | None -> check "Folio is reported" false

        match componentOf report "fides" with
        | Some fides ->
            check
                "a pin below the profile's supported range fails"
                (fides.BelowSupportedRange = Some true && fides.Level = DriftFail)

            check
                "a declared component's next step is the stepwise current upgrade"
                (fides.NextStep |> Option.exists (fun step -> step.Command |> Option.exists (fun command -> command.Contains "upgrade --current --check --stepwise")))
        | None -> check "Fides is reported" false

        check "an unregistered package is not a pin" (report.Components |> List.forall (fun item -> item.Id <> "left-pad"))
        check "the overall level is the worst component level" (report.Level = DriftFail)

        let json = Drift.toJson report

        match schema json with
        | Ok() -> check "the JSON report conforms to conditor-drift-report.schema.json" true
        | Error errors -> check $"the JSON report conforms to conditor-drift-report.schema.json: {joined errors}" false

        let text = Drift.toText report

        check
            "the text report carries the same findings"
            (text.Contains "forma [warn] pinned 0.4.1"
             && text.Contains "next step: 0.5.0"
             && text.Contains(formaAsset "0.5.0")
             && text.Contains "fides [fail]")

    // CON-317: the repository relaxes minors; the Registry adds a pin-age
    // threshold; each field reports its source.
    let relaxed = temp "relaxed"
    chronaShaped relaxed (Some """{ "warnAfterMinorsBehind": 2 }""") true

    match reportFor registry relaxed |> Result.map (fun report -> report, componentOf report "forma") with
    | Ok(report, Some forma) ->
        check "conditor.json can relax the minor threshold: Forma becomes info" (forma.Level = DriftInfo)

        check
            "the report names the repository as the source of the relaxed field"
            (report.PolicySources |> List.contains ("warnAfterMinorsBehind", "conditor.json"))
    | _ -> check "relaxed-policy report is produced" false

    let agedRegistry = standardRegistry (temp "registry-aged") (Some """{ "warnAfterPinAgeDays": 30 }""") true

    match reportFor agedRegistry relaxed |> Result.map (fun report -> report, componentOf report "forma") with
    | Ok(report, Some forma) ->
        check
            "a Registry pin-age threshold warns on a 42-day-old pin that is behind"
            (forma.Level = DriftWarn && forma.Reasons |> List.exists (fun reason -> reason.Contains "pinned for 42 days"))

        check
            "the report names the Registry as the source of the pin-age field"
            (report.PolicySources |> List.contains ("warnAfterPinAgeDays", "registry"))
    | _ -> check "Registry-policy report is produced" false

    // CON-304: no Git history is "unavailable", never zero.
    let bareRegistry = standardRegistry (temp "registry-nogit") None false
    let bareTarget = temp "target-nogit"
    chronaShaped bareTarget None false

    match reportFor bareRegistry bareTarget |> Result.map (fun report -> componentOf report "forma") with
    | Ok(Some forma) ->
        check
            "without Git history pin age and behind-since are unavailable, not zero"
            (forma.PinAgeDays = None
             && forma.BehindSince = None
             && forma.Pins |> List.forall (fun pin -> pin.PinnedSince.IsNone))
    | _ -> check "no-Git report is produced" false

    // CON-303: a broken digest chain stops the advisory with no report.
    let tampered = standardRegistry (temp "registry-tampered") None false
    let manifest = Path.Combine(tampered.Root, "releases", "forma", "0.5.0.release.json")
    File.AppendAllText(manifest, " ")

    match reportFor tampered bareTarget with
    | Ok _ -> check "a tampered release manifest stops the advisory" false
    | Error errors ->
        check
            "a tampered release manifest stops the advisory and names the digest"
            (errors |> List.exists (fun error -> error.Contains "releases/forma/0.5.0.release.json" && error.Contains "sha256"))

    check
        "an unknown channel is refused"
        (Drift.loadRegistry runProcess registry.Root "no-such-channel" registry.Platform |> Result.isError)

let run (check: string -> bool -> unit) (schema: string -> Result<unit, string list>) =
    units check
    integration check schema
