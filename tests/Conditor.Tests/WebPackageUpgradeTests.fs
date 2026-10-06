module WebPackageUpgradeTests

// The current-upgrade contract for package-distributed web bindings
// (Limen, Forma, Folio). These are `project-binding`/`web-package` Registry
// selections: Conditor must prove the exact source and target releases, change
// the repository's pins only through its package manager, prove the result
// against the Registry digests, and refuse every state it cannot prove.

open System
open System.IO
open System.Security.Cryptography
open System.Text
open Conditor.Core
open Conditor.Core.Workstation

let private joined (values: string list) = String.concat "; " values

let private temp label =
    let path = Path.Combine(Path.GetTempPath(), $"conditor-web-{label}-{Guid.NewGuid():N}")
    Directory.CreateDirectory path |> ignore
    path

let private sha256Bytes (bytes: byte array) =
    Convert.ToHexString(SHA256.HashData bytes).ToLowerInvariant()

let private sha256File (path: string) = File.ReadAllBytes path |> sha256Bytes

let private integrity (bytes: byte array) =
    "sha512-" + Convert.ToBase64String(SHA512.HashData bytes)

let private formaTargetUrl = "https://github.com/kemiller2002/forma/releases/download/v0.4.1/echelon-foundry-design-system-0.4.1.tgz"

let private limenBytes = Encoding.UTF8.GetBytes "fixture limen 0.7.1 package tarball"
let private formaBytes = Encoding.UTF8.GetBytes "fixture forma 0.4.1 package tarball"

let private formaUrl version =
    $"https://github.com/kemiller2002/forma/releases/download/v{version}/echelon-foundry-design-system-{version}.tgz"

let private limenComponent (packageSha: string) =
    $$"""
    {
      "systemId": "limen",
      "role": "project-binding",
      "required": true,
      "version": "0.7.1",
      "repository": "kemiller2002/limen",
      "tag": "v0.7.1",
      "commit": "{{String.replicate 40 "1"}}",
      "releaseStage": "stable",
      "lifecycleState": "active",
      "distributionClass": "web-package",
      "executable": null,
      "releaseManifest": { "schema": "echelon.release/v2", "sha256": "{{String.replicate 64 "c"}}" },
      "distribution": {
        "mechanism": "npm",
        "package": "@echelon-foundry/limen",
        "url": "https://registry.npmjs.org/@echelon-foundry/limen/-/limen-0.7.1.tgz"
      },
      "artifacts": [
        { "name": "echelon-foundry-limen-0.7.1.tgz", "purpose": "package", "platform": null, "sha256": "{{packageSha}}" }
      ]
    }"""

let private formaComponent (packageSha: string) =
    $$"""
    {
      "systemId": "forma",
      "role": "project-binding",
      "required": true,
      "version": "0.4.1",
      "repository": "kemiller2002/forma",
      "tag": "v0.4.1",
      "commit": "{{String.replicate 40 "2"}}",
      "releaseStage": "stable",
      "lifecycleState": "active",
      "distributionClass": "web-package",
      "executable": null,
      "releaseManifest": { "schema": "echelon.release/v2", "sha256": "{{String.replicate 64 "c"}}" },
      "distribution": {
        "mechanism": "github-release",
        "package": "@echelon-foundry/design-system",
        "url": "https://github.com/kemiller2002/forma/releases/tag/v0.4.1"
      },
      "artifacts": [
        { "name": "echelon-foundry-design-system-0.4.1.tgz", "purpose": "package", "platform": null, "sha256": "{{packageSha}}" },
        { "name": "checksums.txt", "purpose": "checksums", "platform": null, "sha256": "{{String.replicate 64 "e"}}" }
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

let private resolvedSet (path: string) (components: string list) =
    let rid = Platform.runtimeIdentifier ()

    let json =
        $$"""{
  "schema": "echelon.resolved-release-set/v1",
  "profile": { "id": "web-current", "version": "1.0.0", "sha256": "{{String.replicate 64 "a"}}" },
  "platform": "{{rid}}",
  "resolver": { "name": "test", "version": "1.0.0" },
  "catalogSnapshot": { "sha256": "{{String.replicate 64 "b"}}" },
  "components": [{{String.concat "," (hostTool rid :: components)}}
  ]
}
"""

    File.WriteAllText(path, json)
    sha256File path

let private manifestText (required: bool) =
    let flag = if required then "true" else "false"

    $$"""{
  "schemaVersion": 1,
  "name": "web-binding",
  "components": [
    { "id": "limen", "version": "0.6.1", "required": {{flag}} },
    { "id": "forma", "version": "0.2.0", "required": {{flag}} }
  ],
  "requirements": [],
  "execution": { "enabled": false }
}
"""

let private establish (target: string) (required: bool) =
    let manifestPath = Path.Combine(target, "conditor.json")
    File.WriteAllText(manifestPath, manifestText required)

    let plan =
        { ProjectName = "web-binding"
          Operation = Init
          Components =
            [ { Id = "limen"
                Version = "0.6.1"
                Distribution = NpmPackage
                Package = "@echelon-foundry/typescript-wasm-kernel"
                SourceReference = Some "npm:@echelon-foundry/typescript-wasm-kernel@0.6.1" }
              { Id = "forma"
                Version = "0.2.0"
                Distribution = NpmPackage
                Package = "@echelon-foundry/design-system"
                SourceReference = Some "npm:@echelon-foundry/design-system@0.2.0" } ]
          Actions = [] }

    LockFile.write target manifestPath plan |> ignore
    manifestPath

let private sourcePackageJson limenSpec =
    $$"""{
  "name": "web-binding-app",
  "private": true,
  "dependencies": {
    "@echelon-foundry/typescript-wasm-kernel": "{{limenSpec}}"
  },
  "devDependencies": {
    "@echelon-foundry/design-system": "{{formaUrl "0.2.0"}}"
  }
}
"""

let private sourceLock limenVersion =
    $$"""{
  "name": "web-binding-app",
  "lockfileVersion": 3,
  "requires": true,
  "packages": {
    "": { "name": "web-binding-app" },
    "node_modules/@echelon-foundry/typescript-wasm-kernel": { "version": "{{limenVersion}}" },
    "node_modules/@echelon-foundry/design-system": { "version": "0.2.0", "resolved": "{{formaUrl "0.2.0"}}", "dev": true }
  }
}
"""

let private targetPackageJson =
    $$"""{
  "name": "web-binding-app",
  "private": true,
  "dependencies": {
    "@echelon-foundry/limen": "0.7.1"
  },
  "devDependencies": {
    "@echelon-foundry/design-system": "{{formaUrl "0.4.1"}}"
  }
}
"""

let private targetLock (limenIntegrity: string) =
    $$"""{
  "name": "web-binding-app",
  "lockfileVersion": 3,
  "requires": true,
  "packages": {
    "": { "name": "web-binding-app" },
    "node_modules/@echelon-foundry/limen": { "version": "0.7.1", "integrity": "{{limenIntegrity}}" },
    "node_modules/@echelon-foundry/design-system": { "version": "0.4.1", "resolved": "{{formaUrl "0.4.1"}}", "integrity": "{{integrity formaBytes}}", "dev": true }
  }
}
"""

/// A stand-in `npm` that records each invocation and, on `install`, leaves the
/// project in the package-manager-produced state given by the fixture files.
let private fakeNpm (bin: string) (fixture: string) (log: string) =
    let script =
        String.concat
            "\n"
            [ "#!/bin/sh"
              "set -eu"
              $"echo \"$(pwd)|$*\" >> \"{log}\""
              "case \"$1\" in"
              $"  install) cp \"{fixture}/package.json\" package.json; cp \"{fixture}/package-lock.json\" package-lock.json ;;"
              "  uninstall) ;;"
              "  *) exit 2 ;;"
              "esac"
              "" ]

    let path = Path.Combine(bin, "npm")
    File.WriteAllText(path, script)
    File.SetUnixFileMode(path, UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute)

let private context home mirror : WorkstationContext =
    { Home = home
      ArtifactMirror = Some mirror
      Offline = true
      Praxis = None
      TargetId = Some "web-binding-test" }

let private withPath (bin: string) action =
    let prior = Environment.GetEnvironmentVariable "PATH"
    Environment.SetEnvironmentVariable("PATH", bin + string Path.PathSeparator + prior)

    try
        action ()
    finally
        Environment.SetEnvironmentVariable("PATH", prior)

type private Fixture =
    { Target: string
      ManifestPath: string
      ResolvedPath: string
      ResolvedSha: string
      Context: WorkstationContext
      Bin: string
      Log: string
      AfterInstall: string }

let private fixture label required (components: string list) =
    let target = temp $"{label}-target"
    let home = temp $"{label}-home"
    let mirror = temp $"{label}-mirror"
    let bin = temp $"{label}-bin"
    let afterInstall = temp $"{label}-after"
    File.WriteAllBytes(Path.Combine(mirror, "echelon-foundry-limen-0.7.1.tgz"), limenBytes)
    File.WriteAllBytes(Path.Combine(mirror, "echelon-foundry-design-system-0.4.1.tgz"), formaBytes)
    let resolvedPath = Path.Combine(mirror, "current.resolved.json")
    let resolvedSha = resolvedSet resolvedPath components
    let log = Path.Combine(bin, "npm.log")
    fakeNpm bin afterInstall log

    { Target = target
      ManifestPath = establish target required
      ResolvedPath = resolvedPath
      ResolvedSha = resolvedSha
      Context = context home mirror
      Bin = bin
      Log = log
      AfterInstall = afterInstall }

let private defaultComponents =
    [ limenComponent (sha256Bytes limenBytes); formaComponent (sha256Bytes formaBytes) ]

let private preview (f: Fixture) =
    match Manifest.load f.ManifestPath with
    | Error errors -> Error errors
    | Ok manifest ->
        let probe executable arguments = ProcessRunner.runProcess f.Target executable arguments
        CurrentUpgrade.preview f.Target f.ManifestPath manifest f.ResolvedPath f.ResolvedSha f.Context probe

let private apply (f: Fixture) digest =
    match Manifest.load f.ManifestPath with
    | Error errors -> Error errors
    | Ok manifest ->
        let probe executable arguments = ProcessRunner.runProcess f.Target executable arguments
        CurrentUpgrade.apply f.Target f.ManifestPath manifest f.ResolvedPath f.ResolvedSha f.Context probe digest

let private refusalsOf (f: Fixture) =
    match preview f with
    | Error errors -> errors
    | Ok plan -> plan.Refusals

let private bindProject (f: Fixture) (packageJson: string) (lockName: string option) (lockText: string) =
    File.WriteAllText(Path.Combine(f.Target, "package.json"), packageJson)
    lockName |> Option.iter (fun name -> File.WriteAllText(Path.Combine(f.Target, name), lockText))

let private declaredVersions (manifestPath: string) =
    match Manifest.load manifestPath with
    | Ok manifest -> manifest.Components |> List.map (fun entry -> entry.Id, entry.Version) |> Map.ofList
    | Error _ -> Map.empty

let private unboundRepository check =
    let f = fixture "unbound" false defaultComponents

    match preview f with
    | Error errors -> check $"unbound web-package current plan is produced: {joined errors}" false
    | Ok plan ->
        check
            $"current upgrade accepts Registry-selected web packages the repository does not consume: {joined plan.Refusals}"
            plan.Refusals.IsEmpty

        check
            "an unconsumed web package is a declaration-only transition"
            (plan.Transitions
             |> List.map (fun transition -> transition.Id, transition.FromVersion, transition.ToVersion, transition.Mode)
             |> List.sort = [ "forma", "0.2.0", "0.4.1", "web-package-declaration"
                              "limen", "0.6.1", "0.7.1", "web-package-declaration" ])

        match apply f plan.Digest with
        | Error errors -> check $"authorized declaration-only web upgrade succeeds: {joined errors}" false
        | Ok result ->
            check "declaration-only web upgrade leaves no version drift" result.NoRemainingVersionChanges
            check "declaration-only web upgrade runs no package manager" (not (File.Exists f.Log))

            check
                "declaration-only web upgrade records the Registry versions"
                (declaredVersions f.ManifestPath = Map.ofList [ "limen", Some "0.7.1"; "forma", Some "0.4.1" ])

let private boundRepository check =
    let f = fixture "bound" false defaultComponents
    bindProject f (sourcePackageJson "0.6.1") (Some "package-lock.json") (sourceLock "0.6.1")
    File.WriteAllText(Path.Combine(f.AfterInstall, "package.json"), targetPackageJson)
    File.WriteAllText(Path.Combine(f.AfterInstall, "package-lock.json"), targetLock (integrity limenBytes))

    withPath f.Bin (fun () ->
        match preview f with
        | Error errors -> check $"bound web-package current plan is produced: {joined errors}" false
        | Ok plan ->
            check
                $"current upgrade accepts exactly pinned, npm-locked web bindings: {joined plan.Refusals}"
                plan.Refusals.IsEmpty

            check
                "a consumed web package is a package-manager binding transition"
                (plan.Transitions
                 |> List.forall (fun transition -> transition.Mode = "web-package-binding")
                 && plan.Transitions.Length = 2)

            check "web binding planning is read-only" (not (File.Exists f.Log))

            let packageBefore = File.ReadAllText(Path.Combine(f.Target, "package.json"))
            File.WriteAllText(Path.Combine(f.Target, "package.json"), packageBefore.Replace("\"private\": true", "\"private\": false"))

            check
                "a binding edited after review invalidates the authorization"
                (apply f plan.Digest |> Result.isError && not (File.Exists f.Log))

            File.WriteAllText(Path.Combine(f.Target, "package.json"), packageBefore)

            match apply f plan.Digest with
            | Error errors -> check $"authorized web binding upgrade succeeds: {joined errors}" false
            | Ok result ->
                let invocations = File.ReadAllLines f.Log |> Array.toList
                let at (arguments: string) = $"{f.Target}|{arguments}"

                check
                    "a renamed package is removed through npm before the new identity is added"
                    (invocations
                     |> List.contains (at "uninstall --ignore-scripts --no-audit --no-fund @echelon-foundry/typescript-wasm-kernel"))

                check
                    "the npm-registry release is installed as an exact production pin"
                    (invocations
                     |> List.contains (at "install --save-exact --save-prod --ignore-scripts --no-audit --no-fund @echelon-foundry/limen@0.7.1"))

                check
                    "the GitHub-release asset is installed as an exact development pin"
                    (invocations
                     |> List.contains (at $"install --save-exact --save-dev --ignore-scripts --no-audit --no-fund @echelon-foundry/design-system@{formaTargetUrl}"))

                check "web binding upgrade leaves no version drift" result.NoRemainingVersionChanges

                check
                    "web binding upgrade records the Registry versions"
                    (declaredVersions f.ManifestPath = Map.ofList [ "limen", Some "0.7.1"; "forma", Some "0.4.1" ]))

let private integrityMismatchRollsBack check =
    let f = fixture "integrity" false defaultComponents
    let packageJson = sourcePackageJson "0.6.1"
    let lockText = sourceLock "0.6.1"
    bindProject f packageJson (Some "package-lock.json") lockText
    File.WriteAllText(Path.Combine(f.AfterInstall, "package.json"), targetPackageJson)
    File.WriteAllText(Path.Combine(f.AfterInstall, "package-lock.json"), targetLock (integrity (Encoding.UTF8.GetBytes "tampered")))

    withPath f.Bin (fun () ->
        match preview f with
        | Error errors -> check $"integrity fixture plan is produced: {joined errors}" false
        | Ok plan ->
            match apply f plan.Digest with
            | Ok _ -> check "a lockfile that does not record the Registry artifact is refused" false
            | Error errors ->
                check
                    "a lockfile that does not record the Registry artifact is refused"
                    (errors |> List.exists (fun error -> error.Contains "integrity"))

                check
                    "a refused web binding restores package.json and the lockfile"
                    (File.ReadAllText(Path.Combine(f.Target, "package.json")) = packageJson
                     && File.ReadAllText(Path.Combine(f.Target, "package-lock.json")) = lockText)

                check
                    "a refused web binding leaves Conditor governance unchanged"
                    (declaredVersions f.ManifestPath = Map.ofList [ "limen", Some "0.6.1"; "forma", Some "0.2.0" ]))

let private refusal check label (needle: string) (prepare: Fixture -> unit) (required: bool) (components: string list) =
    let f = fixture label required components
    prepare f
    let refusals = refusalsOf f

    check
        $"current upgrade refuses {label}: {joined refusals}"
        (refusals |> List.exists (fun refusal -> refusal.Contains(needle, StringComparison.OrdinalIgnoreCase)))

    check $"refused {label} runs no package manager" (not (File.Exists f.Log))

let private refusals check =
    let bound spec lockVersion lockName =
        fun f -> bindProject f (sourcePackageJson spec) lockName (sourceLock lockVersion)

    refusal check "an unpinned range" "exact pin" (bound "^0.6.1" "0.6.1" (Some "package-lock.json")) false defaultComponents
    refusal check "a pin to an unexpected version" "exact pin" (bound "0.6.2" "0.6.2" (Some "package-lock.json")) false defaultComponents
    refusal check "a lockfile that disagrees with the declared source" "lockfile" (bound "0.6.1" "0.6.2" (Some "package-lock.json")) false defaultComponents
    refusal check "a binding with no lockfile" "no lockfile" (bound "0.6.1" "0.6.1" None) false defaultComponents
    refusal check "an unqualified package manager" "pnpm" (bound "0.6.1" "0.6.1" (Some "pnpm-lock.yaml")) false defaultComponents

    refusal
        check
        "a binding already moved outside Conditor"
        "already"
        (fun f ->
            bindProject f targetPackageJson (Some "package-lock.json") (targetLock (integrity limenBytes)))
        false
        defaultComponents

    refusal check "a required binding without a scaffold target" "scaffold" ignore true defaultComponents

    refusal
        check
        "a target release without a package digest"
        "digest"
        ignore
        false
        [ (limenComponent (sha256Bytes limenBytes)).Replace(sha256Bytes limenBytes, "not-a-digest")
          formaComponent (sha256Bytes formaBytes) ]

    refusal
        check
        "a target release whose package identity disagrees with Conditor"
        "@echelon-foundry/other"
        ignore
        false
        [ (limenComponent (sha256Bytes limenBytes)).Replace("\"package\": \"@echelon-foundry/limen\"", "\"package\": \"@echelon-foundry/other\"")
          formaComponent (sha256Bytes formaBytes) ]

let run (check: string -> bool -> unit) =
    if OperatingSystem.IsWindows() then
        check "web-package current upgrade fixture is skipped on Windows" true
    else
        unboundRepository check
        boundRepository check
        integrityMismatchRollsBack check
        refusals check

    check
        "Conditor qualifies the Registry-current Praxis 3.7.1 and Ordo 1.4.1 lifecycle releases"
        (Registry.qualifiedVersions "praxis" |> Option.exists (Set.contains "3.7.1")
         && Registry.qualifiedVersions "ordo" |> Option.exists (Set.contains "1.4.1"))
