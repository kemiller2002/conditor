module DeclaredWebBindingTests

// Declared web-package bindings (CON-330..333, DF-CON-2026-A004 decision 1).
// Chrona, Summa and Signal pin Forma by GitHub release-asset tarball in
// package.json and by capability version in .echelon/foundations.json, and
// declare neither to Conditor. A repository in that state declares the
// package at the version its pins name, and one authorized `upgrade --current`
// adopts it and moves it to the target selection. Conditor then owns
// package.json, the lockfile and the foundations capability. The apps move one
// registered release at a time: 0.4.1 -> 0.5.0 (an echelon-current 1.19.0-like
// set) -> 0.6.1 (1.20.0).

open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open Conditor.Core
open Conditor.Core.Workstation

let private joined (values: string list) = String.concat "; " values

let private temp label =
    let path = Path.Combine(Path.GetTempPath(), $"conditor-declared-{label}-{Guid.NewGuid():N}")
    Directory.CreateDirectory path |> ignore
    path

let private sha256Bytes (bytes: byte array) =
    Convert.ToHexString(SHA256.HashData bytes).ToLowerInvariant()

let private integrity (bytes: byte array) =
    "sha512-" + Convert.ToBase64String(SHA512.HashData bytes)

let private FormaPackage = "@echelon-foundry/design-system"
let private LimenPackage = "@echelon-foundry/limen"

let private formaAsset version =
    $"https://github.com/kemiller2002/forma/releases/download/v{version}/echelon-foundry-design-system-{version}.tgz"

let private folioAsset =
    "https://github.com/kemiller2002/folio/releases/download/v0.3.0/echelon-foundry-print-components-0.3.0.tgz"

let private formaBytes version =
    Encoding.UTF8.GetBytes $"fixture forma {version} package tarball"

let private limenBytes = Encoding.UTF8.GetBytes "fixture limen 0.9.0 package tarball"

let private formaEntry version =
    $$"""
    {
      "systemId": "forma",
      "role": "project-binding",
      "required": true,
      "version": "{{version}}",
      "repository": "kemiller2002/forma",
      "tag": "v{{version}}",
      "commit": "{{String.replicate 40 "2"}}",
      "releaseStage": "stable",
      "lifecycleState": "active",
      "distributionClass": "web-package",
      "executable": null,
      "releaseManifest": { "schema": "echelon.release/v2", "sha256": "{{String.replicate 64 "c"}}" },
      "distribution": {
        "mechanism": "github-release",
        "package": "{{FormaPackage}}",
        "url": "https://github.com/kemiller2002/forma/releases/tag/v{{version}}"
      },
      "artifacts": [
        { "name": "echelon-foundry-design-system-{{version}}.tgz", "purpose": "package", "platform": null, "sha256": "{{sha256Bytes (formaBytes version)}}" }
      ]
    }"""

let private limenEntryAt (limenVersion: string) =
    $$"""
    {
      "systemId": "limen",
      "role": "project-binding",
      "required": true,
      "version": "{{limenVersion}}",
      "repository": "kemiller2002/limen",
      "tag": "v{{limenVersion}}",
      "commit": "{{String.replicate 40 "1"}}",
      "releaseStage": "stable",
      "lifecycleState": "active",
      "distributionClass": "web-package",
      "executable": null,
      "releaseManifest": { "schema": "echelon.release/v2", "sha256": "{{String.replicate 64 "c"}}" },
      "distribution": {
        "mechanism": "npm",
        "package": "{{LimenPackage}}",
        "url": "https://registry.npmjs.org/@echelon-foundry/limen/-/limen-{{limenVersion}}.tgz"
      },
      "artifacts": [
        { "name": "echelon-foundry-limen-{{limenVersion}}.tgz", "purpose": "package", "platform": null, "sha256": "{{sha256Bytes limenBytes}}" }
      ]
    }"""

let private limenEntry = limenEntryAt "0.9.0"

/// A resolved set always carries native host tools; one the manifest does not
/// declare keeps the fixture realistic without adding workstation effects.
let private hostTool (rid: string) =
    $$"""
    {
      "systemId": "gamma",
      "role": "host-tool",
      "required": false,
      "version": "2.0.0",
      "repository": "example/gamma",
      "tag": "v2.0.0",
      "commit": "{{String.replicate 40 "3"}}",
      "releaseStage": "stable",
      "lifecycleState": "active",
      "distributionClass": "self-contained-native-cli",
      "executable": "gamma",
      "releaseManifest": { "schema": "echelon.release/v2", "sha256": "{{String.replicate 64 "c"}}" },
      "distribution": { "mechanism": "github-release", "url": "https://github.com/example/gamma/releases/tag/v2.0.0" },
      "artifacts": [
        { "name": "gamma-{{rid}}.tar.gz", "purpose": "executable", "platform": "{{rid}}", "sha256": "{{String.replicate 64 "d"}}" }
      ]
    }"""

/// A Registry-shaped resolved set selecting Limen 0.9.0 and `forma`.
let private resolvedSetWith (profileVersion: string) (limen: string) (forma: string) =
    let rid = Platform.runtimeIdentifier ()

    $$"""{
  "schema": "echelon.resolved-release-set/v1",
  "profile": { "id": "echelon-current", "version": "{{profileVersion}}", "sha256": "{{String.replicate 64 "a"}}" },
  "platform": "{{rid}}",
  "resolver": { "name": "test", "version": "1.0.0" },
  "catalogSnapshot": { "sha256": "{{String.replicate 64 "b"}}" },
  "components": [{{hostTool rid}},{{limenEntryAt limen}},{{formaEntry forma}}
  ]
}
"""

let private resolvedSet (profileVersion: string) (forma: string) = resolvedSetWith profileVersion "0.9.0" forma

let private manifestText (authoritySha: string) (forma: string option) =
    let formaLine =
        match forma with
        | Some version -> $",\n    {{ \"id\": \"forma\", \"version\": \"{version}\", \"required\": true }}"
        | None -> String.Empty

    $$"""{
  "schemaVersion": 1,
  "name": "chrona-shaped",
  "registryAuthority": {
    "kind": "resolved-release-set",
    "path": ".conditor/authority/resolved-release-set.json",
    "sha256": "{{authoritySha}}"
  },
  "components": [
    { "id": "limen", "version": "0.9.0", "required": true }{{formaLine}}
  ],
  "requirements": [],
  "execution": { "enabled": false }
}
"""

let private packageJson (forma: string) =
    $$"""{
  "name": "chrona-shaped",
  "private": true,
  "dependencies": {
    "{{FormaPackage}}": "{{formaAsset forma}}",
    "{{LimenPackage}}": "0.9.0",
    "@echelon-foundry/print-components": "{{folioAsset}}"
  }
}
"""

let private lockJson (forma: string) (formaIntegrity: string) =
    $$"""{
  "name": "chrona-shaped",
  "lockfileVersion": 3,
  "requires": true,
  "packages": {
    "": { "name": "chrona-shaped" },
    "node_modules/{{FormaPackage}}": { "version": "{{forma}}", "resolved": "{{formaAsset forma}}"{{formaIntegrity}} },
    "node_modules/{{LimenPackage}}": { "version": "0.9.0", "resolved": "https://registry.npmjs.org/@echelon-foundry/limen/-/limen-0.9.0.tgz" },
    "node_modules/@echelon-foundry/print-components": { "version": "0.3.0", "resolved": "{{folioAsset}}" }
  }
}
"""

let private foundationsJson (forma: string) =
    $$"""{
  "schemaVersion": 1,
  "application": "Chrona",
  "capabilities": {
    "forma": {
      "required": true,
      "version": "{{forma}}"
    },
    "folio": {
      "required": true,
      "version": "0.3.0"
    },
    "limen": {
      "required": true,
      "version": "0.9.0"
    }
  }
}
"""

type private Repo =
    { Target: string
      ManifestPath: string
      Mirror: string
      Context: WorkstationContext
      Bin: string
      Log: string
      AfterInstall: string }

/// A stand-in `npm` that records each invocation and, on `install`, leaves the
/// project in the package-manager-produced state of the fixture directory.
let private fakeNpm (bin: string) (fixture: string) (log: string) =
    let script =
        String.concat
            "\n"
            [ "#!/bin/sh"
              "set -eu"
              $"echo \"$(pwd)|$*\" >> \"{log}\""
              "case \"$1\" in"
              $"  install) cp \"{fixture}/package.json\" package.json; cp \"{fixture}/package-lock.json\" package-lock.json ;;"
              "  *) exit 2 ;;"
              "esac"
              "" ]

    let path = Path.Combine(bin, "npm")
    File.WriteAllText(path, script)
    File.SetUnixFileMode(path, UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute)

let private withPath (bin: string) action =
    let prior = Environment.GetEnvironmentVariable "PATH"
    Environment.SetEnvironmentVariable("PATH", bin + string Path.PathSeparator + prior)

    try
        action ()
    finally
        Environment.SetEnvironmentVariable("PATH", prior)

/// A Chrona-shaped repository: governed by Conditor under a Registry
/// authority that already selects Forma `authorityForma`, declaring only
/// Limen, while package.json, its lockfile and foundations.json pin Forma
/// `pinned` by tarball.
let private chronaShaped label (pinned: string) (foundationsForma: string) (authorityForma: string) =
    let target = temp $"{label}-target"
    let mirror = temp $"{label}-mirror"
    let home = temp $"{label}-home"
    let bin = temp $"{label}-bin"
    let afterInstall = temp $"{label}-after"

    for version in [ "0.5.0"; "0.6.1" ] do
        File.WriteAllBytes(Path.Combine(mirror, $"echelon-foundry-design-system-{version}.tgz"), formaBytes version)

    let authorityText = resolvedSet "1.19.0" authorityForma
    let authorityPath = Path.Combine(target, ".conditor", "authority", "resolved-release-set.json")
    Directory.CreateDirectory(Path.GetDirectoryName authorityPath |> nonNull) |> ignore
    File.WriteAllText(authorityPath, authorityText)
    let authoritySha = sha256Bytes (File.ReadAllBytes authorityPath)

    let manifestPath = Path.Combine(target, "conditor.json")
    File.WriteAllText(manifestPath, manifestText authoritySha None)

    LockFile.write
        target
        manifestPath
        { ProjectName = "chrona-shaped"
          Operation = Init
          Components =
            [ { Id = "limen"
                Version = "0.9.0"
                Distribution = NpmPackage
                Package = LimenPackage
                SourceReference = Some $"npm:{LimenPackage}@0.9.0" } ]
          Actions = [] }
    |> ignore

    File.WriteAllText(Path.Combine(target, "package.json"), packageJson pinned)
    File.WriteAllText(Path.Combine(target, "package-lock.json"), lockJson pinned "")
    Directory.CreateDirectory(Path.Combine(target, ".echelon")) |> ignore
    File.WriteAllText(Path.Combine(target, ".echelon", "foundations.json"), foundationsJson foundationsForma)

    let log = Path.Combine(bin, "npm.log")
    fakeNpm bin afterInstall log

    { Target = target
      ManifestPath = manifestPath
      Mirror = mirror
      Context =
        { Home = home
          ArtifactMirror = Some mirror
          Offline = true
          Praxis = None
          TargetId = Some "declared-web-binding-test" }
      Bin = bin
      Log = log
      AfterInstall = afterInstall }

/// The user's one edit: declare Forma in conditor.json at `version`.
let private declare (repo: Repo) (version: string) =
    let text = File.ReadAllText repo.ManifestPath

    File.WriteAllText(
        repo.ManifestPath,
        text.Replace(
            "{ \"id\": \"limen\", \"version\": \"0.9.0\", \"required\": true }",
            $"{{ \"id\": \"limen\", \"version\": \"0.9.0\", \"required\": true }},\n    {{ \"id\": \"forma\", \"version\": \"{version}\", \"required\": true }}"
        )
    )

/// The package-manager-produced state npm leaves after installing `forma`.
let private npmProduces (repo: Repo) (forma: string) =
    File.WriteAllText(Path.Combine(repo.AfterInstall, "package.json"), packageJson forma)

    File.WriteAllText(
        Path.Combine(repo.AfterInstall, "package-lock.json"),
        lockJson forma $", \"integrity\": \"{integrity (formaBytes forma)}\""
    )

let private writeSet (repo: Repo) (name: string) (profileVersion: string) (forma: string) =
    let path = Path.Combine(repo.Mirror, name)
    File.WriteAllText(path, resolvedSet profileVersion forma)
    path, sha256Bytes (File.ReadAllBytes path)

let private preview (repo: Repo) (setPath: string, setSha: string) =
    match Manifest.load repo.ManifestPath with
    | Error errors -> Error errors
    | Ok manifest ->
        let probe executable arguments = ProcessRunner.runProcess repo.Target executable arguments
        CurrentUpgrade.preview repo.Target repo.ManifestPath manifest setPath setSha repo.Context probe

let private apply (repo: Repo) (setPath: string, setSha: string) digest =
    match Manifest.load repo.ManifestPath with
    | Error errors -> Error errors
    | Ok manifest ->
        let probe executable arguments = ProcessRunner.runProcess repo.Target executable arguments
        CurrentUpgrade.apply repo.Target repo.ManifestPath manifest setPath setSha repo.Context probe digest

let private declaredForma (repo: Repo) =
    match Manifest.load repo.ManifestPath with
    | Ok manifest -> manifest.Components |> List.tryFind (fun item -> item.Id = "forma") |> Option.bind _.Version
    | Error _ -> None

let private foundationsText (repo: Repo) =
    File.ReadAllText(Path.Combine(repo.Target, ".echelon", "foundations.json"))

let private stepwiseAdoption check =
    let repo = chronaShaped "stepwise" "0.4.1" "0.4.1" "0.5.0"
    let set119 = writeSet repo "echelon-current-1.19.0.json" "1.19.0" "0.5.0"
    let set120 = writeSet repo "echelon-current-1.20.0.json" "1.20.0" "0.6.1"

    withPath repo.Bin (fun () ->
        // Undeclared, Forma is invisible to --current: the gap this closes.
        match preview repo set120 with
        | Error errors -> check $"undeclared Forma plan is produced: {joined errors}" false
        | Ok plan ->
            check
                "an undeclared tarball-pinned Forma plans no transition"
                (plan.Transitions |> List.forall (fun transition -> transition.Id <> "forma"))

        // Step 1: declare 0.4.1 (what the pins name) and move to 0.5.0.
        declare repo "0.4.1"
        npmProduces repo "0.5.0"
        let foundationsBefore = foundationsText repo

        match preview repo set119 with
        | Error errors -> check $"Forma opt-in plan is produced: {joined errors}" false
        | Ok plan ->
            check $"declaring Forma at its pinned 0.4.1 is accepted: {joined plan.Refusals}" plan.Refusals.IsEmpty

            check
                "the opt-in is disclosed and moves Forma 0.4.1 -> 0.5.0 through npm"
                (plan.OptIns |> List.map _.Id = [ "forma" ]
                 && plan.Transitions
                    |> List.exists (fun transition ->
                        transition.Id = "forma"
                        && transition.FromVersion = "0.4.1"
                        && transition.ToVersion = "0.5.0"
                        && transition.Mode = "web-package-binding"))

            check
                "the plan moves the foundations capability with the pins"
                (plan.WebPackageTransitions
                 |> List.exists (fun transition ->
                     transition.Id = "forma"
                     && transition.OptIn
                     && transition.Foundations
                        |> Option.exists (fun edit -> edit.FromVersion = "0.4.1" && edit.ToVersion = "0.5.0")))

            check "opt-in planning is read-only" (not (File.Exists repo.Log) && foundationsText repo = foundationsBefore)

            match apply repo set119 plan.Digest with
            | Error errors -> check $"authorized Forma opt-in succeeds: {joined errors}" false
            | Ok result ->
                check "the opt-in reports Forma as declared" (result.OptedIn = [ "forma@0.4.1" ])
                check "the opt-in leaves no version drift" result.NoRemainingVersionChanges
                check "conditor.json now declares Forma 0.5.0" (declaredForma repo = Some "0.5.0")

                check
                    "npm installed the exact Forma 0.5.0 release asset"
                    (let pin = FormaPackage + "@" + formaAsset "0.5.0"

                     File.ReadAllLines repo.Log
                     |> Array.exists (fun line -> line.EndsWith(pin, StringComparison.Ordinal)))

                check
                    "foundations.json moved only the Forma capability version"
                    (foundationsText repo = foundationsBefore.Replace("\"version\": \"0.4.1\"", "\"version\": \"0.5.0\""))

        // Step 2: the next registered release, as an ordinary declared move.
        npmProduces repo "0.6.1"
        File.Delete repo.Log

        match preview repo set120 with
        | Error errors -> check $"Forma 0.5.0 -> 0.6.1 plan is produced: {joined errors}" false
        | Ok plan ->
            check $"declared Forma moves to 0.6.1: {joined plan.Refusals}" plan.Refusals.IsEmpty

            check
                "the second step is not an opt-in"
                (plan.OptIns.IsEmpty
                 && plan.WebPackageTransitions |> List.forall (fun transition -> not transition.OptIn))

            match apply repo set120 plan.Digest with
            | Error errors -> check $"authorized Forma 0.6.1 step succeeds: {joined errors}" false
            | Ok result ->
                check "the 0.6.1 step leaves no version drift" result.NoRemainingVersionChanges
                check "conditor.json now declares Forma 0.6.1" (declaredForma repo = Some "0.6.1")

                check
                    "package.json pins the Forma 0.6.1 release asset"
                    (File.ReadAllText(Path.Combine(repo.Target, "package.json")).Contains(formaAsset "0.6.1"))

                check
                    "foundations.json declares Forma 0.6.1 and leaves Folio and Limen alone"
                    (foundationsText repo = (foundationsJson "0.6.1")))

let private refusals check =
    let refused label (prepare: Repo -> unit) (forma: string) (needle: string) =
        let repo = chronaShaped label "0.4.1" "0.4.1" forma
        prepare repo
        let set = writeSet repo "target.json" "1.19.0" forma

        withPath repo.Bin (fun () ->
            match preview repo set with
            | Error errors -> check $"{label} plan is produced: {joined errors}" false
            | Ok plan ->
                check
                    $"current upgrade refuses {label}: {joined plan.Refusals}"
                    (plan.Refusals |> List.exists (fun refusal -> refusal.Contains(needle, StringComparison.Ordinal)))

                check $"refused {label} runs no package manager" (not (File.Exists repo.Log)))

    refused "a declaration that disagrees with the pins" (fun repo -> declare repo "0.3.0") "0.5.0" "not an exact pin"

    refused
        "a foundations capability that disagrees with the pins"
        (fun repo ->
            declare repo "0.4.1"
            File.WriteAllText(Path.Combine(repo.Target, ".echelon", "foundations.json"), foundationsJson "0.3.0"))
        "0.5.0"
        "neither the source 0.4.1 nor the target 0.5.0"

    refused
        "a foundations capability that names one release by another key"
        (fun repo ->
            declare repo "0.4.1"

            File.WriteAllText(
                Path.Combine(repo.Target, ".echelon", "foundations.json"),
                (let commit = String.replicate 40 "9"
                 (foundationsJson "0.4.1").Replace("\"version\": \"0.4.1\"", $"\"version\": \"0.4.1\",\n      \"sourceCommit\": \"{commit}\""))
            ))
        "0.5.0"
        "sourceCommit"

    refused
        "a declaration at the target while the pins are older"
        (fun repo -> declare repo "0.5.0")
        "0.5.0"
        "not the exact pin"

    // Declaring at exactly the target the pins already name is a pure
    // declaration: no transition and no package manager.
    let current = chronaShaped "at-target" "0.5.0" "0.5.0" "0.5.0"
    declare current "0.5.0"
    let set = writeSet current "target.json" "1.19.0" "0.5.0"

    withPath current.Bin (fun () ->
        match preview current set with
        | Error errors -> check $"at-target declaration plan is produced: {joined errors}" false
        | Ok plan ->
            check $"declaring Forma at the pinned target is accepted: {joined plan.Refusals}" plan.Refusals.IsEmpty

            check
                "an at-target declaration moves nothing"
                (plan.OptIns |> List.map _.Id = [ "forma" ] && plan.Transitions.IsEmpty)

            match apply current set plan.Digest with
            | Error errors -> check $"authorized at-target declaration succeeds: {joined errors}" false
            | Ok result ->
                check "an at-target declaration runs no package manager" (not (File.Exists current.Log))
                check "an at-target declaration is recorded" (declaredForma current = Some "0.5.0" && result.NoRemainingVersionChanges))

let private requiredWithoutScaffold check =
    // CON-333: a required web package with no scaffold is planned when an
    // existing package.json binds it, and still refused when nothing does.
    let bound = temp "planner-bound"
    let unbound = temp "planner-unbound"
    File.WriteAllText(Path.Combine(bound, "package.json"), packageJson "0.5.0")

    let manifest =
        Manifest.parseText """{"schemaVersion":1,"name":"bound","components":[{"id":"forma","version":"0.5.0","required":true}]}"""

    match manifest with
    | Error errors -> check $"required Forma manifest parses: {joined errors}" false
    | Ok manifest ->
        check
            "a required web package bound by an existing package.json plans without a scaffold"
            (Planner.create bound Verify manifest |> Result.isOk)

        check
            "a required web package with no scaffold and no binding is still refused"
            (match Planner.create unbound Verify manifest with
             | Error errors -> errors |> List.exists (fun error -> error.Contains "existing package.json binding is required")
             | Ok _ -> false)

// ---- Stepwise current upgrade (CON-310..315) --------------------------------
// A Registry checkout whose echelon-current channel moved one release at a
// time, recorded in Git: each channel index commit binds that day's platform
// set by SHA-256. Stepwise planning targets only those Registry-produced sets.

let private gitAt (directory: string) (arguments: string list) =
    let info = Diagnostics.ProcessStartInfo("git")
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
          "GIT_COMMITTER_EMAIL", "fixture@example.invalid" ] do
        info.Environment[name] <- value

    use proc = Diagnostics.Process.Start info |> nonNull
    proc.StandardOutput.ReadToEnd() |> ignore
    proc.StandardError.ReadToEnd() |> ignore
    proc.WaitForExit()

    if proc.ExitCode <> 0 then
        failwith $"""git {String.concat " " arguments} failed in {directory}"""

let private writeText (root: string) (relative: string) (text: string) =
    let path = Path.Combine(root, relative)
    Directory.CreateDirectory(Path.GetDirectoryName path |> nonNull) |> ignore
    File.WriteAllText(path, text)
    sha256Bytes (File.ReadAllBytes path)

let private releaseManifest (system: string) (version: string) (mechanism: string) (package: string) (artifact: string) =
    $$"""{
  "schema": "echelon.release/v2",
  "systemId": "{{system}}",
  "version": "{{version}}",
  "repository": "kemiller2002/{{system}}",
  "tag": "v{{version}}",
  "commit": "{{String.replicate 40 "2"}}",
  "releaseStage": "stable",
  "lifecycleState": "active",
  "distributionClass": "web-package",
  "executable": null,
  "distributions": [ { "mechanism": "{{mechanism}}", "package": "{{package}}", "url": "https://github.com/kemiller2002/{{system}}/releases/tag/v{{version}}" } ],
  "artifacts": [ { "name": "{{artifact}}", "purpose": "package", "platform": null, "sha256": "{{String.replicate 64 "e"}}" } ]
}
"""

/// A Registry checkout whose channel history is `history`, oldest first:
/// (profile version, Limen, Forma) per channel bump. With `tamperAt`, the
/// channel index at that profile version records a wrong set digest.
let private stepRegistry (history: (string * string * string) list) (tamperAt: string option) =
    let root = temp "step-registry"
    let rid = Platform.runtimeIdentifier ()
    gitAt root [ "init"; "-q" ]

    let releases =
        [ for version in [ "0.4.1"; "0.5.0"; "0.6.1" ] ->
              "forma", version, releaseManifest "forma" version "github-release" FormaPackage $"echelon-foundry-design-system-{version}.tgz"
          for version in [ "0.8.0"; "0.9.0" ] ->
              "limen", version, releaseManifest "limen" version "npm" LimenPackage $"echelon-foundry-limen-{version}.tgz" ]
        |> List.map (fun (system, version, text) ->
            let relative = $"releases/{system}/{version}.release.json"
            system, version, relative, writeText root relative text)

    let profiles =
        history
        |> List.map (fun (profileVersion, _, _) ->
            let relative = $"profiles/echelon-current-{profileVersion}.profile.json"

            let sha =
                writeText
                    root
                    relative
                    $$"""{
  "schema": "echelon.profile/v1",
  "id": "echelon-current",
  "version": "{{profileVersion}}",
  "allowedReleaseStages": [ "stable" ],
  "components": [
    { "systemId": "limen", "role": "project-binding", "required": true, "version": { "range": ">=0.6.0 <1.0.0" } },
    { "systemId": "forma", "role": "project-binding", "required": true, "version": { "range": ">=0.3.0 <1.0.0" } }
  ]
}
"""

            profileVersion, (relative, sha))
        |> Map.ofList

    let releaseRows =
        releases
        |> List.map (fun (system, version, relative, sha) ->
            $"    {{ \"systemId\": \"{system}\", \"version\": \"{version}\", \"manifest\": \"{relative}\", \"sha256\": \"{sha}\" }}")
        |> String.concat ",\n"

    let profileRows =
        profiles
        |> Map.toList
        |> List.map (fun (version, (relative, sha)) ->
            $"    {{ \"id\": \"echelon-current\", \"version\": \"{version}\", \"manifest\": \"{relative}\", \"sha256\": \"{sha}\" }}")
        |> String.concat ",\n"

    let snapshotSha =
        writeText
            root
            "snapshots/echelon-current.catalog.json"
            $"{{\n  \"schema\": \"echelon.catalog-snapshot/v1\",\n  \"systems\": [],\n  \"releases\": [\n{releaseRows}\n  ],\n  \"profiles\": [\n{profileRows}\n  ]\n}}\n"

    for profileVersion, limen, forma in history do
        let setRelative = $"channels/echelon-current/{rid}.json"
        let setSha = writeText root setRelative (resolvedSetWith profileVersion limen forma)
        let recorded = if tamperAt = Some profileVersion then String.replicate 64 "0" else setSha
        let _, profileSha = profiles[profileVersion]

        writeText
            root
            "channels/echelon-current/channel.json"
            $$"""{
  "schema": "echelon.current-channel/v1",
  "id": "echelon-current",
  "profile": { "id": "echelon-current", "version": "{{profileVersion}}", "sha256": "{{profileSha}}" },
  "catalogSnapshot": { "path": "snapshots/echelon-current.catalog.json", "sha256": "{{snapshotSha}}" },
  "platforms": [ { "platform": "{{rid}}", "path": "{{setRelative}}", "sha256": "{{recorded}}" } ]
}
"""
        |> ignore

        gitAt root [ "add"; "-A" ]
        gitAt root [ "commit"; "-q"; "--no-gpg-sign"; "-m"; $"echelon-current {profileVersion}" ]

    root

let private stepwisePreview (repo: Repo) (registry: string) =
    let finalPath = Path.Combine(registry, "channels", "echelon-current", $"{Platform.runtimeIdentifier ()}.json")
    let finalSha = sha256Bytes (File.ReadAllBytes finalPath)

    match Manifest.load repo.ManifestPath with
    | Error errors -> Error errors
    | Ok manifest ->
        let probe executable arguments = ProcessRunner.runProcess repo.Target executable arguments
        StepwiseUpgrade.preview ProcessRunner.runProcess repo.Target repo.ManifestPath manifest finalPath finalSha registry "echelon-current" repo.Context probe

let private movesOf (step: UpgradeStep) =
    step.Moves |> List.map (fun move -> move.Id, move.FromVersion, move.ToVersion)

let private stepwise check =
    let registry = stepRegistry [ "1.18.0", "0.9.0", "0.4.1"; "1.19.0", "0.9.0", "0.5.0"; "1.20.0", "0.9.0", "0.6.1" ] None
    let repo = chronaShaped "stepwise-plan" "0.4.1" "0.4.1" "0.5.0"
    declare repo "0.4.1"
    npmProduces repo "0.5.0"
    let manifestBefore = File.ReadAllText repo.ManifestPath
    let packageBefore = File.ReadAllText(Path.Combine(repo.Target, "package.json"))

    withPath repo.Bin (fun () ->
        match stepwisePreview repo registry with
        | Error errors -> check $"stepwise plan is produced: {joined errors}" false
        | Ok plan ->
            check
                "stepwise plans one registered minor line per step: 0.4.1 -> 0.5.0 -> 0.6.1"
                (plan.Steps |> List.map movesOf = [ [ "forma", "0.4.1", "0.5.0" ]; [ "forma", "0.5.0", "0.6.1" ] ])

            check
                "every step targets a Registry-produced set: the historical 1.19.0 set, then the supplied target"
                (match plan.Steps with
                 | [ first; second ] ->
                     first.Candidate.ProfileVersion = SemanticVersion.tryParse "1.19.0"
                     && not first.Candidate.IsFinal
                     && second.Candidate.IsFinal
                 | _ -> false)

            check "no step reports a gap when the Registry moved one line at a time" (plan.Steps |> List.forall (fun step -> step.Gaps.IsEmpty))

            check
                "each step links the releases it crosses"
                (plan.Steps
                 |> List.collect (fun step -> step.Moves |> List.collect (fun move -> move.Crosses))
                 |> List.map (fun crossed -> crossed.Version, crossed.MigrationNotes) = [ "0.5.0", "https://github.com/kemiller2002/forma/releases/tag/v0.5.0"
                                                                                          "0.6.1", "https://github.com/kemiller2002/forma/releases/tag/v0.6.1" ])

            let first = plan.Steps.Head

            check
                "the historical set is materialized content-addressed in the workstation cache with its Registry digest"
                (first.Candidate.Path = StepwiseUpgrade.cachePath repo.Context first.Candidate.Sha256
                 && sha256Bytes (File.ReadAllBytes first.Candidate.Path) = first.Candidate.Sha256)

            check
                "stepwise planning is read-only for the repository"
                (File.ReadAllText repo.ManifestPath = manifestBefore
                 && File.ReadAllText(Path.Combine(repo.Target, "package.json")) = packageBefore
                 && not (File.Exists repo.Log))

            match plan.FirstStepPlan with
            | None -> check "step 1 is checked against the repository" false
            | Some firstPlan ->
                check
                    $"step 1 is the ordinary current-upgrade plan with no refusals: {joined firstPlan.Refusals}"
                    (firstPlan.Refusals.IsEmpty
                     && firstPlan.TargetSetSha256 = first.Candidate.Sha256
                     && firstPlan.OptIns |> List.map _.Id = [ "forma" ])

                // Step 1 is authorized exactly as direct mode authorizes it.
                match apply repo (first.Candidate.Path, first.Candidate.Sha256) firstPlan.Digest with
                | Error errors -> check $"step 1 is authorized through the direct current upgrade: {joined errors}" false
                | Ok result ->
                    check "step 1 lands on Forma 0.5.0 with no drift against its set" (result.NoRemainingVersionChanges && declaredForma repo = Some "0.5.0")

                    npmProduces repo "0.6.1"

                    match stepwisePreview repo registry with
                    | Error errors -> check $"the remaining stepwise plan is produced: {joined errors}" false
                    | Ok rest ->
                        check
                            "after step 1 the plan has one step left, to the supplied target"
                            (rest.Steps |> List.map movesOf = [ [ "forma", "0.5.0", "0.6.1" ] ]
                             && rest.Steps.Head.Candidate.IsFinal
                             && rest.FirstStepPlan |> Option.exists (fun next -> next.Refusals.IsEmpty && next.OptIns.IsEmpty)))

    // CON-312: the Registry recorded 0.5.0 and 0.6.1 in one bump, so no
    // Registry set selects 0.5.0. The plan discloses the gap and takes the
    // smallest Registry step instead of composing a set.
    let gapRegistry = stepRegistry [ "1.18.0", "0.9.0", "0.4.1"; "1.20.0", "0.9.0", "0.6.1" ] None
    let gapRepo = chronaShaped "stepwise-gap" "0.4.1" "0.4.1" "0.5.0"
    declare gapRepo "0.4.1"

    match stepwisePreview gapRepo gapRegistry with
    | Error errors -> check $"stepwise gap plan is produced: {joined errors}" false
    | Ok plan ->
        check
            "a release line no Registry set selects is reported as a gap, never composed"
            (plan.Steps |> List.map movesOf = [ [ "forma", "0.4.1", "0.6.1" ] ]
             && plan.Steps.Head.Gaps = [ { Id = "forma"; SkippedLines = [ "0.5" ] } ]
             && plan.Steps.Head.Candidate.IsFinal)

    // CON-312: a historical set that would downgrade a declared component is
    // never a step.
    let downgradeRegistry = stepRegistry [ "1.18.0", "0.9.0", "0.4.1"; "1.19.0", "0.8.0", "0.5.0"; "1.20.0", "0.9.0", "0.6.1" ] None
    let downgradeRepo = chronaShaped "stepwise-downgrade" "0.4.1" "0.4.1" "0.5.0"
    declare downgradeRepo "0.4.1"

    match stepwisePreview downgradeRepo downgradeRegistry with
    | Error errors -> check $"stepwise downgrade plan is produced: {joined errors}" false
    | Ok plan ->
        check
            "a historical set that would downgrade Limen is not a step"
            (plan.Steps |> List.map movesOf = [ [ "forma", "0.4.1", "0.6.1" ] ] && not plan.Steps.Head.Gaps.IsEmpty)

    // CON-311: a historical set whose bytes do not match the digest its channel
    // index recorded is not a Registry-produced set.
    let tamperedRegistry = stepRegistry [ "1.18.0", "0.9.0", "0.4.1"; "1.19.0", "0.9.0", "0.5.0"; "1.20.0", "0.9.0", "0.6.1" ] (Some "1.19.0")

    check
        "a historical set that does not match its channel index digest is excluded"
        (StepwiseUpgrade.historicalSets ProcessRunner.runProcess tamperedRegistry "echelon-current" (Platform.runtimeIdentifier ())
         |> List.choose _.ProfileVersion
         |> List.map string
         |> List.sort = [ "1.18.0"; "1.20.0" ])

// CON-353: a repository whose declared Forma 0.5.0 has a stale foundations
// pin (0.4.1) gets a pin repair against its recorded authority as step 1,
// never "nothing behind".
let private stepwiseRepairFirst check =
    let registry = stepRegistry [ "1.19.0", "0.9.0", "0.5.0"; "1.20.0", "0.9.0", "0.6.1" ] None
    let repo = chronaShaped "stepwise-repair" "0.5.0" "0.4.1" "0.5.0"
    declare repo "0.5.0"

    LockFile.write
        repo.Target
        repo.ManifestPath
        { ProjectName = "chrona-shaped"
          Operation = Init
          Components =
            [ { Id = "limen"
                Version = "0.9.0"
                Distribution = NpmPackage
                Package = LimenPackage
                SourceReference = Some $"npm:{LimenPackage}@0.9.0" }
              { Id = "forma"
                Version = "0.5.0"
                Distribution = NpmPackage
                Package = FormaPackage
                SourceReference = Some $"npm:{FormaPackage}@0.5.0" } ]
          Actions = [] }
    |> ignore

    match stepwisePreview repo registry with
    | Error errors -> check $"stepwise repair plan is produced: {joined errors}" false
    | Ok plan ->
        check
            "a pending pin repair is stepwise step 1, against the recorded authority (CON-353)"
            (match plan.Steps with
             | first :: second :: _ ->
                 first.Moves.IsEmpty
                 && first.Candidate.Path = Path.Combine(repo.Target, ".conditor", "authority", "resolved-release-set.json")
                 && movesOf second = [ "forma", "0.5.0", "0.6.1" ]
             | _ -> false)

        check
            "step 1's checked plan repairs the stale foundations pin with no transition"
            (plan.FirstStepPlan
             |> Option.exists (fun first ->
                 first.Refusals.IsEmpty
                 && first.Transitions.IsEmpty
                 && first.PinRepairs |> List.map (fun edit -> edit.Capability, edit.FromVersion, edit.ToVersion) = [ "forma", "0.4.1", "0.5.0" ]))

// ---- Attested qualification (CON-340..346, DF-CON-2026-A004 decision 2) ------
// A web-package release outside this build's exact qualifiedVersions (Forma
// 0.7.0) is qualified only by a Registry release attestation whose ECDSA
// P-256 signature verifies under a pinned, unrevoked key and that claims the
// web-package binding contract, inside the descriptor's qualifiedRange. Every
// other case stays under exact qualification and is refused.

let private attestedSet (repo: Repo) (name: string) (forma: string) (attestation: (string * string * string list) option) =
    let root = Nodes.JsonNode.Parse(resolvedSetWith "1.21.0" "0.9.0" forma) |> nonNull :?> Nodes.JsonObject

    match attestation with
    | None -> ()
    | Some(keyId, signature, contracts) ->
        for item in (root["components"] |> nonNull :?> Nodes.JsonArray) do
            let entry = item |> nonNull :?> Nodes.JsonObject

            if string entry["systemId"] = "forma" then
                let attestationNode = Nodes.JsonObject()
                attestationNode["schema"] <- Nodes.JsonValue.Create ReleaseAttestation.StatementSchema
                let claimed = Nodes.JsonArray()

                for contract in contracts do
                    claimed.Add(Nodes.JsonValue.Create contract)

                attestationNode["consumerContracts"] <- claimed
                attestationNode["keyId"] <- Nodes.JsonValue.Create keyId
                attestationNode["algorithm"] <- Nodes.JsonValue.Create ReleaseAttestation.Algorithm
                attestationNode["signature"] <- Nodes.JsonValue.Create signature
                entry["releaseAttestation"] <- attestationNode

    let path = Path.Combine(repo.Mirror, name)
    File.WriteAllText(path, root.ToJsonString(JsonSerializerOptions(WriteIndented = true)))
    path, sha256Bytes (File.ReadAllBytes path)

let private sign (key: System.Security.Cryptography.ECDsa) (version: string) (contracts: string list) =
    let statement = ReleaseAttestation.statement "forma" version (String.replicate 64 "c") contracts
    let signature = key.SignData(Encoding.UTF8.GetBytes statement, HashAlgorithmName.SHA256, System.Security.Cryptography.DSASignatureFormat.IeeeP1363FixedFieldConcatenation)
    Convert.ToBase64String signature

let private previewTrusting trust (repo: Repo) (setPath: string, setSha: string) =
    match Manifest.load repo.ManifestPath with
    | Error errors -> Error errors
    | Ok manifest ->
        let probe executable arguments = ProcessRunner.runProcess repo.Target executable arguments
        CurrentUpgrade.previewTrusting trust repo.Target repo.ManifestPath manifest setPath setSha repo.Context probe

let private attested check =
    use key = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256)

    let trusted =
        [ { KeyId = "registry-test-1"
            PublicKey = key.ExportSubjectPublicKeyInfo()
            RevokedAt = None } ]

    let revoked = trusted |> List.map (fun item -> { item with RevokedAt = Some(DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero)) })
    let contract = [ ReleaseAttestation.WebPackageBindingContract ]

    check
        "this build pins no Registry release key yet, so contract qualification is inert until the Registry signs"
        ReleaseAttestation.embedded.Value.IsEmpty

    let scenario label (trust: TrustedKey list) (forma: string) attestation =
        let repo = chronaShaped label "0.6.1" "0.6.1" "0.5.0"
        File.WriteAllBytes(Path.Combine(repo.Mirror, $"echelon-foundry-design-system-{forma}.tgz"), formaBytes forma)
        declare repo "0.6.1"
        let set = attestedSet repo $"{label}.json" forma attestation
        repo, set, previewTrusting trust repo set

    let refusedWith label trust forma attestation (needle: string) =
        let _, _, result = scenario label trust forma attestation

        match result with
        | Error errors -> check $"{label} plan is produced: {joined errors}" false
        | Ok plan ->
            check
                $"{label} is refused: {joined plan.Refusals}"
                (plan.Refusals |> List.exists (fun refusal -> refusal.Contains("target version", StringComparison.Ordinal) && refusal.Contains(needle, StringComparison.Ordinal)))

    // The signed, trusted, in-range, contract-claiming release is qualified.
    let repo, set, result = scenario "attested-0.7.0" trusted "0.7.0" (Some("registry-test-1", sign key "0.7.0" contract, contract))

    match result with
    | Error errors -> check $"attested plan is produced: {joined errors}" false
    | Ok plan ->
        check $"a verified attestation qualifies Forma 0.7.0 without a new Conditor build: {joined plan.Refusals}" plan.Refusals.IsEmpty

        check
            "the plan discloses the attested qualification, its range and key (CON-344)"
            (plan.WebPackageTransitions
             |> List.exists (fun transition -> transition.Qualification = "attested:>=0.2.0 <1.0.0:registry-test-1"))

        npmProduces repo "0.7.0"

        withPath repo.Bin (fun () ->
            match Manifest.load repo.ManifestPath with
            | Error errors -> check $"attested manifest loads: {joined errors}" false
            | Ok manifest ->
                let probe executable arguments = ProcessRunner.runProcess repo.Target executable arguments

                match CurrentUpgrade.applyTrusting trusted repo.Target repo.ManifestPath manifest (fst set) (snd set) repo.Context probe plan.Digest with
                | Error errors -> check $"an attested upgrade applies with full verification: {joined errors}" false
                | Ok result ->
                    check "an attested upgrade lands on Forma 0.7.0 with no drift" (result.NoRemainingVersionChanges && declaredForma repo = Some "0.7.0"))

    // Exact qualification is unchanged and never needs an attestation.
    let _, _, exactResult = scenario "exact-0.6.1" [] "0.6.1" None

    match exactResult with
    | Ok plan -> check "an exactly qualified release needs no attestation" (plan.Refusals.IsEmpty)
    | Error errors -> check $"exact plan is produced: {joined errors}" false

    refusedWith "the same release with this build's (empty) pinned trust set" ReleaseAttestation.embedded.Value "0.7.0" (Some("registry-test-1", sign key "0.7.0" contract, contract)) "is not pinned"
    refusedWith "an unsigned contract claim" trusted "0.7.0" None "never relies on an unsigned contract claim"
    refusedWith "a signature over another version" trusted "0.7.0" (Some("registry-test-1", sign key "0.7.1" contract, contract)) "does not verify"
    refusedWith "an attestation under a revoked key" revoked "0.7.0" (Some("registry-test-1", sign key "0.7.0" contract, contract)) "was revoked"

    refusedWith
        "an attestation that does not claim the web-package binding contract"
        trusted
        "0.7.0"
        (Some("registry-test-1", sign key "0.7.0" [ "echelon.other/v1" ], [ "echelon.other/v1" ]))
        "does not claim"

    refusedWith "an attested release outside the qualified range" trusted "1.0.0" (Some("registry-test-1", sign key "1.0.0" contract, contract)) "outside its qualified range"

    // Sources are gated exactly like targets (CON-346). Forma 0.4.0 is in the
    // qualified range but never exactly qualified: it is an acceptable source
    // only when the repository's recorded authority selects it with a trusted
    // attestation.
    let sourceScenario label (trust: TrustedKey list) (declaredInLock: bool) (authorityAttestation: (string * string * string list) option) =
        let repo = chronaShaped label "0.4.0" "0.4.0" "0.4.0"
        declare repo "0.4.0"

        // The recorded authority: the set the repository was last applied
        // from, selecting Forma 0.4.0, attested or not.
        let authorityPath, authoritySha = attestedSet repo $"{label}-authority.json" "0.4.0" authorityAttestation
        let recorded = Path.Combine(repo.Target, ".conditor", "authority", "resolved-release-set.json")
        File.Copy(authorityPath, recorded, true)
        let manifestText = File.ReadAllText repo.ManifestPath
        let previousSha = Text.RegularExpressions.Regex.Match(manifestText, "\"sha256\": \"([0-9a-f]{64})\"").Groups[1].Value
        File.WriteAllText(repo.ManifestPath, manifestText.Replace(previousSha, authoritySha))

        let lockedComponents =
            [ { Id = "limen"
                Version = "0.9.0"
                Distribution = NpmPackage
                Package = LimenPackage
                SourceReference = Some $"npm:{LimenPackage}@0.9.0" } ]
            @ (if declaredInLock then
                   [ { Id = "forma"
                       Version = "0.4.0"
                       Distribution = NpmPackage
                       Package = FormaPackage
                       SourceReference = Some $"npm:{FormaPackage}@0.4.0" } ]
               else
                   [])

        if declaredInLock then
            LockFile.write
                repo.Target
                repo.ManifestPath
                { ProjectName = "chrona-shaped"
                  Operation = Init
                  Components = lockedComponents
                  Actions = [] }
            |> ignore
        else
            // An opt-in: the lock records the manifest without Forma.
            let withoutForma =
                File.ReadAllText(repo.ManifestPath).Replace(",\n    { \"id\": \"forma\", \"version\": \"0.4.0\", \"required\": true }", "")

            let declared = File.ReadAllText repo.ManifestPath
            File.WriteAllText(repo.ManifestPath, withoutForma)

            LockFile.write
                repo.Target
                repo.ManifestPath
                { ProjectName = "chrona-shaped"
                  Operation = Init
                  Components = lockedComponents
                  Actions = [] }
            |> ignore

            File.WriteAllText(repo.ManifestPath, declared)

        let target = writeSet repo $"{label}-target.json" "1.19.0" "0.5.0"
        previewTrusting trust repo target

    let sourceRefused label trust declaredInLock attestation =
        match sourceScenario label trust declaredInLock attestation with
        | Error errors -> check $"{label} plan is produced: {joined errors}" false
        | Ok plan ->
            check
                $"{label}: an in-range, non-exact source is refused: {joined plan.Refusals}"
                (plan.Refusals
                 |> List.exists (fun refusal -> refusal.Contains("source version 0.4.0 is not qualified", StringComparison.Ordinal)))

    let sourceAccepted label trust declaredInLock attestation =
        match sourceScenario label trust declaredInLock attestation with
        | Error errors -> check $"{label} plan is produced: {joined errors}" false
        | Ok plan -> check $"{label}: an attested source is accepted: {joined plan.Refusals}" plan.Refusals.IsEmpty

    let attestation040 = Some("registry-test-1", sign key "0.4.0" contract, contract)

    sourceRefused "declared source with the shipped empty trust set" ReleaseAttestation.embedded.Value true attestation040
    sourceRefused "opt-in source with the shipped empty trust set" ReleaseAttestation.embedded.Value false attestation040
    sourceRefused "declared source whose recorded authority is unsigned" trusted true None
    sourceRefused "opt-in source whose recorded authority is unsigned" trusted false None
    sourceAccepted "declared source attested by its recorded authority" trusted true attestation040
    sourceAccepted "opt-in source attested by its recorded authority" trusted false attestation040

    // Descriptor rules (CON-343, CON-345).
    let descriptor (extra: string) (distribution: string) =
        let lifecycle =
            if distribution = "host-tool" then
                "\"command\": \"tool\", \"versionArguments\": [\"--version\"],"
            else
                "\"applicationBinding\": \"npm\","

        $$"""{ "schemaVersion": 1, "id": "sample", "displayName": "Sample", "distribution": "{{distribution}}", "package": "sample", "defaultVersion": "1.0.0", "qualifiedVersions": ["1.0.0"], {{lifecycle}} {{extra}} "initArguments": [], "verifyArguments": [], "doctorArguments": [], "upgradeArguments": [] }"""

    check
        "a project binding may declare a qualified range and denied versions"
        (ComponentDescriptors.parse "sample" (descriptor "\"qualifiedRange\": \">=1.0.0 <2.0.0\", \"deniedVersions\": [\"1.2.0\"]," "npm")
         |> Result.isOk)

    check
        "a lifecycle tool may not declare a qualified range (it stays exact)"
        (match ComponentDescriptors.parse "sample" (descriptor "\"qualifiedRange\": \">=1.0.0 <2.0.0\"," "host-tool") with
         | Error errors -> errors |> List.exists (fun error -> error.Contains "CON-343")
         | Ok _ -> false)

    check
        "a denied version may not also be exactly qualified"
        (ComponentDescriptors.parse "sample" (descriptor "\"qualifiedRange\": \">=1.0.0 <2.0.0\", \"deniedVersions\": [\"1.0.0\"]," "npm")
         |> Result.isError)

    check
        "an unparseable qualified range is refused"
        (ComponentDescriptors.parse "sample" (descriptor "\"qualifiedRange\": \"latest\"," "npm") |> Result.isError)

let run (check: string -> bool -> unit) =
    requiredWithoutScaffold check

    if OperatingSystem.IsWindows() then
        check "declared web-binding fixture is skipped on Windows" true
    else
        stepwiseAdoption check
        refusals check
        stepwise check
        stepwiseRepairFirst check
        attested check
