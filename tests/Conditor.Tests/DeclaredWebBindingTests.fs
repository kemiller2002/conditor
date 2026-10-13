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

let private limenEntry =
    $$"""
    {
      "systemId": "limen",
      "role": "project-binding",
      "required": true,
      "version": "0.9.0",
      "repository": "kemiller2002/limen",
      "tag": "v0.9.0",
      "commit": "{{String.replicate 40 "1"}}",
      "releaseStage": "stable",
      "lifecycleState": "active",
      "distributionClass": "web-package",
      "executable": null,
      "releaseManifest": { "schema": "echelon.release/v2", "sha256": "{{String.replicate 64 "c"}}" },
      "distribution": {
        "mechanism": "npm",
        "package": "{{LimenPackage}}",
        "url": "https://registry.npmjs.org/@echelon-foundry/limen/-/limen-0.9.0.tgz"
      },
      "artifacts": [
        { "name": "echelon-foundry-limen-0.9.0.tgz", "purpose": "package", "platform": null, "sha256": "{{sha256Bytes limenBytes}}" }
      ]
    }"""

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
let private resolvedSet (profileVersion: string) (forma: string) =
    let rid = Platform.runtimeIdentifier ()

    $$"""{
  "schema": "echelon.resolved-release-set/v1",
  "profile": { "id": "echelon-current", "version": "{{profileVersion}}", "sha256": "{{String.replicate 64 "a"}}" },
  "platform": "{{rid}}",
  "resolver": { "name": "test", "version": "1.0.0" },
  "catalogSnapshot": { "sha256": "{{String.replicate 64 "b"}}" },
  "components": [{{hostTool rid}},{{limenEntry}},{{formaEntry forma}}
  ]
}
"""

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

let run (check: string -> bool -> unit) =
    requiredWithoutScaffold check

    if OperatingSystem.IsWindows() then
        check "declared web-binding fixture is skipped on Windows" true
    else
        stepwiseAdoption check
        refusals check
