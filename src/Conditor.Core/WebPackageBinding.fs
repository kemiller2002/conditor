namespace Conditor.Core

// The current-upgrade contract for package-distributed web bindings: Registry
// `project-binding` selections whose distribution class is `web-package`
// (Limen, Forma, Folio). Conditor proves the exact source and target releases,
// changes the repository's pins only through its package manager, proves the
// result against the Registry package digest, and refuses every state it
// cannot prove. See docs/web-package-upgrade-contract.md.

open System
open System.IO
open System.Net.Http
open System.Security.Cryptography
open System.Text.Json
open System.Text.Json.Nodes
open System.Text.RegularExpressions
open Conditor.Core.Workstation

type WebPackageMechanism =
    | NpmRegistry
    | GitHubReleaseAsset

/// One exact Registry-selected web-package release.
type WebPackageRelease =
    { Id: string
      Version: string
      Package: string
      Repository: string
      Tag: string
      Mechanism: WebPackageMechanism
      ArtifactName: string
      ArtifactSha256: string
      ReleaseManifestSha256: string
      DownloadUrl: string
      /// The package.json dependency specifier that pins exactly this release.
      Specifier: string }

type DependencySection =
    | Dependencies
    | DevDependencies
    | OptionalDependencies
    | PeerDependencies

/// One package.json declaration of the component, observed at its exact source.
type WebPackageBinding =
    { ProjectDirectory: string
      Section: DependencySection
      Package: string
      Specifier: string
      PackageJsonPath: string
      PackageJsonSha256: string
      LockPath: string
      LockSha256: string }

type WebPackageTransition =
    { Id: string
      FromVersion: string
      SourcePackage: string
      Target: WebPackageRelease
      Bindings: WebPackageBinding list }

module DependencySection =
    let all = [ Dependencies; DevDependencies; OptionalDependencies; PeerDependencies ]

    let jsonName =
        function
        | Dependencies -> "dependencies"
        | DevDependencies -> "devDependencies"
        | OptionalDependencies -> "optionalDependencies"
        | PeerDependencies -> "peerDependencies"

    let npmSaveFlag =
        function
        | Dependencies -> "--save-prod"
        | DevDependencies -> "--save-dev"
        | OptionalDependencies -> "--save-optional"
        | PeerDependencies -> "--save-peer"

module WebPackageTransition =
    [<Literal>]
    let DeclarationMode = "web-package-declaration"

    [<Literal>]
    let BindingMode = "web-package-binding"

    let mode (transition: WebPackageTransition) =
        if transition.Bindings.IsEmpty then DeclarationMode else BindingMode

    let private quiet = [ "--ignore-scripts"; "--no-audit"; "--no-fund" ]

    /// The ordered npm invocations, each as (working directory, arguments).
    let steps (transition: WebPackageTransition) =
        transition.Bindings
        |> List.collect (fun binding ->
            let removal =
                if binding.Package <> transition.Target.Package then
                    [ binding.ProjectDirectory, [ "uninstall" ] @ quiet @ [ binding.Package ] ]
                else
                    []

            removal
            @ [ binding.ProjectDirectory,
                [ "install"; "--save-exact"; DependencySection.npmSaveFlag binding.Section ]
                @ quiet
                @ [ $"{transition.Target.Package}@{transition.Target.Specifier}" ] ])

    let digestMaterial (transition: WebPackageTransition) =
        let target = transition.Target

        let header =
            $"web|{transition.Id}|{transition.FromVersion}|{transition.SourcePackage}|{target.Version}|{target.Package}|{target.Specifier}|package=sha256:{target.ArtifactSha256}|release=sha256:{target.ReleaseManifestSha256}"

        transition.Bindings
        |> List.map (fun binding ->
            $"  {binding.PackageJsonPath}|{DependencySection.jsonName binding.Section}|{binding.Package}|{binding.Specifier}|sha256:{binding.PackageJsonSha256}|{binding.LockPath}|sha256:{binding.LockSha256}")
        |> fun lines -> String.concat "\n" (header :: lines)

module WebPackageBinding =
    [<Literal>]
    let DistributionClass = "web-package"

    let private sha256Pattern = Regex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant)
    let private repositoryPattern = Regex("^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$", RegexOptions.CultureInvariant)
    let private fileName (path: string) =
        Path.GetFileName path |> Option.ofObj |> Option.defaultValue path

    let private ignoredDirectories = set [ "node_modules"; ".git"; "bin"; "obj"; ".conditor" ]

    let private sha256Bytes (bytes: byte array) =
        bytes |> SHA256.HashData |> Convert.ToHexString |> fun value -> value.ToLowerInvariant()

    let private sha256File (path: string) = File.ReadAllBytes path |> sha256Bytes

    let private integrityOf (bytes: byte array) =
        "sha512-" + Convert.ToBase64String(SHA512.HashData bytes)

    let private tryProperty (name: string) (element: JsonElement) =
        let mutable value = Unchecked.defaultof<JsonElement>

        if element.ValueKind = JsonValueKind.Object && element.TryGetProperty(name, &value) then
            Some value
        else
            None

    let private str name element =
        tryProperty name element
        |> Option.bind (fun value ->
            if value.ValueKind = JsonValueKind.String then value.GetString() |> Option.ofObj else None)
        |> Option.filter (String.IsNullOrWhiteSpace >> not)

    let private nodeString (node: JsonNode | null) =
        match node with
        | :? JsonValue as value ->
            match value.TryGetValue<string>() with
            | true, text -> text |> Option.ofObj
            | _ -> None
        | _ -> None

    let private nodeObject (node: JsonNode | null) =
        match node with
        | :? JsonObject as value -> Some value
        | _ -> None

    let private parseObject (path: string) =
        try
            JsonNode.Parse(File.ReadAllText path) |> nodeObject
        with :? JsonException ->
            None

    let private prefix (id: string) (fromVersion: string) (toVersion: string) =
        $"'{id}' {fromVersion} -> {toVersion} web-package binding:"

    // ---- Registry release identity ---------------------------------------

    let private releaseFromEntry (id: string) (entry: JsonElement) : Result<WebPackageRelease, string list> =
        let version = str "version" entry |> Option.defaultValue "<absent>"
        let refuse detail = Error [ $"Registry selection '{id}'@{version} {detail}" ]

        let distribution = tryProperty "distribution" entry
        let mechanism = distribution |> Option.bind (str "mechanism")
        let package = distribution |> Option.bind (str "package")
        let releaseManifestSha = tryProperty "releaseManifest" entry |> Option.bind (str "sha256")

        let packageArtifacts =
            match tryProperty "artifacts" entry with
            | Some artifacts when artifacts.ValueKind = JsonValueKind.Array ->
                artifacts.EnumerateArray()
                |> Seq.filter (fun artifact -> str "purpose" artifact = Some "package")
                |> Seq.map (fun artifact -> str "name" artifact, str "sha256" artifact)
                |> Seq.toList
            | _ -> []

        match str "role" entry, str "distributionClass" entry, str "version" entry, str "repository" entry, str "tag" entry with
        | Some role, _, _, _, _ when role <> "project-binding" -> refuse $"has role '{role}', not project-binding."
        | _, Some distributionClass, _, _, _ when distributionClass <> DistributionClass ->
            refuse $"has distribution class '{distributionClass}', not {DistributionClass}."
        | Some _, Some _, Some exactVersion, Some repository, Some tag when repositoryPattern.IsMatch repository ->
            match releaseManifestSha, package, packageArtifacts with
            | Some manifestSha, _, _ when not (sha256Pattern.IsMatch manifestSha) ->
                refuse "has no valid release manifest digest; Conditor will not bind an unpinned release."
            | None, _, _ -> refuse "has no release manifest digest; Conditor will not bind an unpinned release."
            | _, None, _ -> refuse "names no distribution package; Conditor will not guess the package identity."
            | Some manifestSha, Some packageName, [ Some artifactName, Some artifactSha ]
                when sha256Pattern.IsMatch artifactSha && not (artifactName.Contains '/') ->
                let githubAsset = $"https://github.com/{repository}/releases/download/{tag}/{artifactName}"

                let located =
                    match mechanism, distribution |> Option.bind (str "url") with
                    | Some "npm", Some url when url.StartsWith("https://registry.npmjs.org/", StringComparison.Ordinal) ->
                        Ok(NpmRegistry, url, exactVersion)
                    | Some "npm", _ -> Error "uses npm distribution without an npmjs.org tarball URL."
                    | Some "github-release", _ -> Ok(GitHubReleaseAsset, githubAsset, githubAsset)
                    | Some other, _ -> Error $"uses unsupported distribution mechanism '{other}'."
                    | None, _ -> Error "declares no distribution mechanism."

                match located with
                | Error detail -> refuse detail
                | Ok(kind, downloadUrl, specifier) ->
                    Ok
                        { Id = id
                          Version = exactVersion
                          Package = packageName
                          Repository = repository
                          Tag = tag
                          Mechanism = kind
                          ArtifactName = artifactName
                          ArtifactSha256 = artifactSha
                          ReleaseManifestSha256 = manifestSha
                          DownloadUrl = downloadUrl
                          Specifier = specifier }
            | _, _, [] -> refuse "has no package artifact; Conditor will not bind a release without a package digest."
            | _, _, [ _ ] -> refuse "has no valid package artifact name and SHA-256 digest."
            | _ -> refuse "has more than one package artifact; the binding target is ambiguous."
        | _ -> refuse "is missing its exact version, owner/name repository, or tag."

    /// The exact release a resolved release set selects for `id`.
    let releaseIn (setPath: string) (id: string) : Result<WebPackageRelease, string list> =
        try
            use document = JsonDocument.Parse(File.ReadAllBytes setPath)

            let selections =
                match tryProperty "components" document.RootElement with
                | Some value when value.ValueKind = JsonValueKind.Array ->
                    value.EnumerateArray()
                    |> Seq.filter (fun entry -> str "systemId" entry = Some id)
                    |> Seq.toList
                | _ -> []

            match selections with
            | [ entry ] -> releaseFromEntry id entry
            | [] -> Error [ $"Registry release set {setPath} does not select '{id}'." ]
            | _ -> Error [ $"Registry release set {setPath} selects '{id}' more than once." ]
        with ex ->
            Error [ $"Unable to read Registry release set {setPath}: {ex.Message}" ]

    // ---- Repository observation --------------------------------------------

    let rec private packageJsonFiles (directory: string) : string list =
        let here =
            Path.Combine(directory, "package.json")
            |> fun path -> if File.Exists path then [ path ] else []

        let below =
            Directory.EnumerateDirectories directory
            |> Seq.filter (fun child -> not (ignoredDirectories.Contains(fileName child)))
            |> Seq.filter (fun child -> not (File.GetAttributes(child).HasFlag FileAttributes.ReparsePoint))
            |> Seq.sort
            |> Seq.collect packageJsonFiles
            |> Seq.toList

        here @ below

    let private lockKinds =
        [ "package-lock.json", "npm"
          "npm-shrinkwrap.json", "npm"
          "pnpm-lock.yaml", "pnpm"
          "yarn.lock", "yarn"
          "bun.lock", "bun"
          "bun.lockb", "bun" ]

    /// The nearest lockfile directory at or above `directory`, within `root`.
    let rec private nearestLocks (root: string) (directory: string) =
        let present =
            lockKinds
            |> List.map (fun (name, kind) -> Path.Combine(directory, name), kind)
            |> List.filter (fst >> File.Exists)

        if not present.IsEmpty then
            present
        elif String.Equals(Path.GetFullPath directory, Path.GetFullPath root, StringComparison.Ordinal) then
            []
        else
            match Path.GetDirectoryName directory |> Option.ofObj with
            | Some parent when parent.StartsWith(root, StringComparison.Ordinal) -> nearestLocks root parent
            | _ -> []

    let private lockEntry (lockPath: string) (projectDirectory: string) (package: string) =
        parseObject lockPath
        |> Option.bind (fun root -> nodeObject root["packages"])
        |> Option.bind (fun packages ->
            let lockDirectory = Path.GetDirectoryName lockPath |> Option.ofObj |> Option.defaultValue projectDirectory
            let relative = Path.GetRelativePath(lockDirectory, projectDirectory).Replace('\\', '/')

            [ if relative <> "." then yield $"{relative}/node_modules/{package}"
              yield $"node_modules/{package}" ]
            |> List.tryPick (fun key -> nodeObject packages[key]))

    let private isExactSource (fromVersion: string) (specifier: string) =
        specifier = fromVersion
        || Regex.IsMatch(
            specifier,
            $"^https://github\\.com/[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+/releases/download/[^/\\s]+/[^/\\s]+-{Regex.Escape fromVersion}\\.tgz$",
            RegexOptions.CultureInvariant
        )

    let private observeBinding
        (label: string)
        (root: string)
        (fromVersion: string)
        (sourceSpecifier: string option)
        (sourcePackage: string)
        (target: WebPackageRelease)
        (packageJsonPath: string)
        (manifest: JsonObject)
        (section: DependencySection)
        (package: string)
        (specifier: string)
        : Result<WebPackageBinding, string list> =
        let projectDirectory = Path.GetDirectoryName packageJsonPath |> Option.ofObj |> Option.defaultValue root
        let shown = Path.GetRelativePath(root, packageJsonPath).Replace('\\', '/')
        let where = $"{shown} {DependencySection.jsonName section}"

        let refuse (detail: string) = Error [ $"{label} {where} {detail}" ]

        let sourceAccepted =
            package = sourcePackage
            && (match sourceSpecifier with
                | Some exact -> specifier = exact
                | None -> isExactSource fromVersion specifier)

        if package = target.Package && specifier = target.Specifier then
            refuse
                $"already pins {target.Package}@{target.Specifier}, but conditor.json and the lock declare {fromVersion}; the binding was changed outside Conditor and will not be recorded as a Conditor transition."
        elif not sourceAccepted then
            let expected = sourceSpecifier |> Option.defaultValue fromVersion

            refuse
                $"declares {package}@\"{specifier}\", which is not an exact pin of the declared source {sourcePackage}@{expected}; Conditor refuses unpinned or modified bindings."
        else
            match nodeString manifest["packageManager"] with
            | Some manager when not (manager.StartsWith("npm@", StringComparison.Ordinal)) ->
                refuse $"belongs to a project that declares packageManager '{manager}'; only npm has a qualified Conditor upgrade contract."
            | _ ->
                match nearestLocks root projectDirectory with
                | [] ->
                    refuse "has no lockfile; Conditor will not change a binding whose resolution is not locked (and never writes a lockfile by hand)."
                | [ lockPath, "npm" ] ->
                    match lockEntry lockPath projectDirectory sourcePackage with
                    | None ->
                        refuse $"is not recorded in lockfile {Path.GetRelativePath(root, lockPath)}; the lockfile disagrees with package.json."
                    | Some entry ->
                        let lockedVersion = nodeString entry["version"]
                        let lockedResolved = nodeString entry["resolved"]

                        if lockedVersion <> Some fromVersion then
                            let shownVersion = lockedVersion |> Option.defaultValue "<absent>"
                            refuse $"is locked at {shownVersion} in lockfile {Path.GetRelativePath(root, lockPath)}, not the declared source {fromVersion}."
                        elif specifier <> fromVersion && lockedResolved.IsSome && lockedResolved <> Some specifier then
                            refuse $"resolves to {lockedResolved.Value} in the lockfile, not its declared specifier {specifier}."
                        else
                            Ok
                                { ProjectDirectory = projectDirectory
                                  Section = section
                                  Package = package
                                  Specifier = specifier
                                  PackageJsonPath = packageJsonPath
                                  PackageJsonSha256 = sha256File packageJsonPath
                                  LockPath = lockPath
                                  LockSha256 = sha256File lockPath }
                | [ lockPath, kind ] ->
                    refuse $"is locked by {kind} ({fileName lockPath}); {kind} has no qualified Conditor upgrade contract."
                | several ->
                    let names = several |> List.map (fst >> fileName) |> String.concat ", "
                    refuse $"has ambiguous lockfiles ({names}); Conditor will not choose a package manager."

    let private observe label root fromVersion sourceSpecifier sourcePackage (target: WebPackageRelease) =
        packageJsonFiles root
        |> List.map (fun path ->
            match parseObject path with
            | None -> Error [ $"{label} {Path.GetRelativePath(root, path)} is not a valid JSON object." ]
            | Some manifest ->
                DependencySection.all
                |> List.collect (fun section ->
                    match nodeObject manifest[DependencySection.jsonName section] with
                    | None -> []
                    | Some dependencies ->
                        [ sourcePackage; target.Package ]
                        |> List.distinct
                        |> List.choose (fun package ->
                            match dependencies[package] with
                            | null -> None
                            | node ->
                                match nodeString node with
                                | Some specifier ->
                                    Some(observeBinding label root fromVersion sourceSpecifier sourcePackage target path manifest section package specifier)
                                | None ->
                                    Some(Error [ $"{label} {Path.GetRelativePath(root, path)} declares {package} with a non-string specifier." ])))
                |> List.fold
                    (fun state item ->
                        match state, item with
                        | Ok values, Ok value -> Ok(values @ [ value ])
                        | Error errors, Error more -> Error(errors @ more)
                        | Error errors, Ok _ -> Error errors
                        | Ok _, Error errors -> Error errors)
                    (Ok []))
        |> List.fold
            (fun state item ->
                match state, item with
                | Ok values, Ok more -> Ok(values @ more)
                | Error errors, Error more -> Error(errors @ more)
                | Error errors, Ok _ -> Error errors
                | Ok _, Error errors -> Error errors)
            (Ok [])

    // ---- Source authority ----------------------------------------------------

    /// The source release as the repository's recorded Registry authority
    /// selects it, when the repository has one. Without recorded authority the
    /// source is the lock-verified declaration of a Conditor-qualified version.
    let private sourceSelection target (manifest: ProjectManifest) (id: string) (fromVersion: string) =
        match manifest.RegistryAuthority with
        | None -> Ok None
        | Some authority ->
            let path = Path.Combine(Path.GetFullPath target, authority.Path)

            if not (File.Exists path) then
                Error [ $"Recorded Registry authority is missing: {authority.Path}." ]
            elif sha256File path <> authority.Sha256 then
                Error [ $"Recorded Registry authority {authority.Path} does not match its recorded digest sha256:{authority.Sha256}." ]
            else
                match releaseIn path id with
                | Error errors -> Error errors
                | Ok release when release.Version <> fromVersion ->
                    Error
                        [ $"Recorded Registry authority selects '{id}'@{release.Version}, but conditor.json declares {fromVersion}." ]
                | Ok release -> Ok(Some release)

    // ---- Planning -------------------------------------------------------------

    let plan
        (target: string)
        (manifest: ProjectManifest)
        (request: ComponentRequest)
        (fromVersion: string)
        (targetSetPath: string)
        : Result<WebPackageTransition, string list> =
        let root = Path.GetFullPath target

        match releaseIn targetSetPath request.Id with
        | Error errors -> Error errors
        | Ok release ->
            let label = prefix request.Id fromVersion release.Version

            match Registry.tryFind request.Id, Registry.qualifiedVersions request.Id with
            | None, _
            | _, None -> Error [ $"{label} Conditor has no descriptor for this component." ]
            | Some definition, Some qualified ->
                let descriptorProblems =
                    [ if definition.Distribution <> NpmPackage || definition.ApplicationBinding <> Some NpmDependency then
                          yield $"{label} Conditor's descriptor does not declare an npm application binding."
                      if not (qualified.Contains fromVersion) then
                          yield $"{label} source version {fromVersion} is not qualified by this Conditor build."
                      if not (qualified.Contains release.Version) then
                          yield $"{label} target version {release.Version} is not qualified by this Conditor build."
                      let expected = ComponentDefinition.packageFor release.Version definition

                      if expected <> release.Package then
                          yield
                              $"{label} Registry names package {release.Package}, but Conditor binds {request.Id}@{release.Version} as {expected}."
                      if request.Required && manifest.Scaffold.IsNone then
                          yield
                              $"{label} the component is required but the manifest declares no scaffold; Conditor cannot plan or verify a required application dependency without an explicit scaffold target." ]

                if not descriptorProblems.IsEmpty then
                    Error descriptorProblems
                else
                    let sourcePackage = ComponentDefinition.packageFor fromVersion definition

                    sourceSelection target manifest request.Id fromVersion
                    |> Result.bind (fun source ->
                        match source with
                        | Some selected when selected.Package <> sourcePackage ->
                            Error
                                [ $"{label} recorded authority names source package {selected.Package}, but Conditor binds {request.Id}@{fromVersion} as {sourcePackage}." ]
                        | _ -> Ok(source |> Option.map _.Specifier))
                    |> Result.bind (fun sourceSpecifier ->
                        observe label root fromVersion sourceSpecifier sourcePackage release)
                    |> Result.map (fun bindings ->
                        { Id = request.Id
                          FromVersion = fromVersion
                          SourcePackage = sourcePackage
                          Target = release
                          Bindings = bindings })

    // ---- Execution ------------------------------------------------------------

    /// The Registry package artifact, from the artifact mirror when present,
    /// otherwise downloaded unless the context is offline.
    let fetchArtifact (ctx: WorkstationContext) (release: WebPackageRelease) : Result<byte array, string> =
        let mirrored =
            ctx.ArtifactMirror
            |> Option.map (fun mirror -> Path.Combine(mirror, release.ArtifactName))
            |> Option.filter File.Exists

        match mirrored with
        | Some path -> Ok(File.ReadAllBytes path)
        | None when ctx.Offline ->
            Error $"{release.ArtifactName} is not in the artifact mirror and the current upgrade is offline."
        | None ->
            try
                use client = new HttpClient()
                Ok(client.GetByteArrayAsync(release.DownloadUrl).GetAwaiter().GetResult())
            with ex ->
                Error $"Unable to download {release.DownloadUrl}: {ex.Message}"

    let private verifyResult (transition: WebPackageTransition) (expectedIntegrity: string) (binding: WebPackageBinding) =
        let target = transition.Target
        let shown = binding.PackageJsonPath

        let declared =
            parseObject binding.PackageJsonPath
            |> Option.bind (fun manifest -> nodeObject manifest[DependencySection.jsonName binding.Section])

        let pinned = declared |> Option.bind (fun section -> nodeString section[target.Package])

        let staleSource =
            transition.SourcePackage <> target.Package
            && (parseObject binding.PackageJsonPath
                |> Option.exists (fun manifest ->
                    DependencySection.all
                    |> List.exists (fun section ->
                        nodeObject manifest[DependencySection.jsonName section]
                        |> Option.exists (fun values -> values.ContainsKey transition.SourcePackage))))

        let locked = lockEntry binding.LockPath binding.ProjectDirectory target.Package

        [ if pinned <> Some target.Specifier then
              yield $"{shown} does not pin {target.Package}@{target.Specifier} after the package manager ran."
          if staleSource then
              yield $"{shown} still declares the retired package {transition.SourcePackage}."
          match locked with
          | None -> yield $"{binding.LockPath} does not record {target.Package} after the package manager ran."
          | Some entry ->
              if nodeString entry["version"] <> Some target.Version then
                  yield $"{binding.LockPath} does not lock {target.Package} at {target.Version}."

              if nodeString entry["integrity"] <> Some expectedIntegrity then
                  yield
                      $"{binding.LockPath} integrity for {target.Package} does not match the Registry package artifact {target.ArtifactName} (sha256:{target.ArtifactSha256})."

              if target.Mechanism = GitHubReleaseAsset && nodeString entry["resolved"] <> Some target.Specifier then
                  yield $"{binding.LockPath} does not resolve {target.Package} to the Registry release asset." ]

    let private runSteps run (transition: WebPackageTransition) =
        let label = prefix transition.Id transition.FromVersion transition.Target.Version

        WebPackageTransition.steps transition
        |> List.fold
            (fun state (directory, arguments) ->
                state
                |> Result.bind (fun () ->
                    let result: ProcessResult = run directory "npm" arguments

                    if result.ExitCode = 0 then
                        Ok()
                    else
                        let rendered = String.concat " " arguments

                        Error(
                            [ $"{label} 'npm {rendered}' exited {result.ExitCode} in {directory}."
                              result.StandardOutput.Trim()
                              result.StandardError.Trim() ]
                            |> List.filter (String.IsNullOrWhiteSpace >> not)
                        )))
            (Ok())

    let private verifiedArtifact fetch (transition: WebPackageTransition) =
        let target = transition.Target
        let label = prefix transition.Id transition.FromVersion target.Version

        match fetch target with
        | Error error -> Error [ $"{label} {error}" ]
        | Ok bytes when sha256Bytes bytes <> target.ArtifactSha256 ->
            Error
                [ $"{label} package artifact {target.ArtifactName} has sha256:{sha256Bytes bytes}, not the Registry-selected sha256:{target.ArtifactSha256}." ]
        | Ok bytes -> Ok(transition, integrityOf bytes)

    /// Apply every planned web-package transition through npm as one unit.
    /// Every Registry package artifact is fetched and digest-checked before
    /// anything changes; package.json and lockfiles are restored byte-for-byte
    /// if any npm step or any post-condition fails.
    let executeAll
        (fetch: WebPackageRelease -> Result<byte array, string>)
        (run: string -> string -> string list -> ProcessResult)
        (transitions: WebPackageTransition list)
        : Result<unit, string list> =
        let bound = transitions |> List.filter (fun transition -> not transition.Bindings.IsEmpty)

        let artifacts =
            bound
            |> List.map (verifiedArtifact fetch)
            |> List.fold
                (fun state item ->
                    match state, item with
                    | Ok values, Ok value -> Ok(values @ [ value ])
                    | Error errors, Error more -> Error(errors @ more)
                    | Error errors, Ok _ -> Error errors
                    | Ok _, Error errors -> Error errors)
                (Ok [])

        match artifacts with
        | Error errors -> Error(errors @ [ "No package.json or lockfile was changed." ])
        | Ok [] -> Ok()
        | Ok verified ->
            let backups =
                bound
                |> List.collect (fun transition -> transition.Bindings)
                |> List.collect (fun binding -> [ binding.PackageJsonPath; binding.LockPath ])
                |> List.distinct
                |> List.map (fun path -> path, File.ReadAllBytes path)

            let restore errors =
                backups |> List.iter (fun (path: string, content: byte array) -> File.WriteAllBytes(path, content))

                Error(
                    errors
                    @ [ "Every package.json and lockfile was restored byte-for-byte; run 'npm ci' to realign node_modules." ]
                )

            let applied =
                verified
                |> List.fold
                    (fun state (transition, expectedIntegrity) ->
                        state
                        |> Result.bind (fun () -> runSteps run transition)
                        |> Result.bind (fun () ->
                            let label = prefix transition.Id transition.FromVersion transition.Target.Version

                            match
                                transition.Bindings
                                |> List.collect (verifyResult transition expectedIntegrity)
                                |> List.distinct
                            with
                            | [] -> Ok()
                            | problems -> Error(problems |> List.map (fun problem -> $"{label} {problem}"))))
                    (Ok())

            match applied with
            | Ok() -> Ok()
            | Error errors -> restore errors
