namespace Conditor.Core.Workstation

open System
open System.IO
open System.Net.Http
open System.Security.Cryptography
open System.Text.Json
open System.Text.Json.Nodes
open Conditor.Core

type OfflineBundleSummary =
    { Root: string
      ManifestPath: string
      ResolvedSetPath: string
      ResolvedSetSha256: string
      Platform: string
      ProfileId: string
      ProfileVersion: string
      ArtifactCount: int
      SourceFileCount: int
      ProjectManifestPath: string option
      NativeMirror: string
      PackageMirror: string
      SourceMirror: string }

module OfflineBundle =
    let private tryProperty (name: string) (element: JsonElement) =
        let mutable value = Unchecked.defaultof<JsonElement>
        if element.ValueKind = JsonValueKind.Object && element.TryGetProperty(name, &value) then Some value else None

    let private str name element =
        match tryProperty name element with
        | Some value when value.ValueKind = JsonValueKind.String ->
            value.GetString() |> Option.ofObj
        | _ -> None

    let private objects name element =
        match tryProperty name element with
        | Some value when value.ValueKind = JsonValueKind.Array ->
            value.EnumerateArray() |> Seq.toList
        | _ -> []

    let private normalizeSha256 (value: string) =
        if value.StartsWith("sha256:", StringComparison.Ordinal) then value.Substring("sha256:".Length) else value

    let private isSha256 (value: string) =
        value.Length = 64 && value |> Seq.forall Char.IsAsciiHexDigitLower

    let private sha256Bytes (bytes: byte array) =
        SHA256.HashData bytes
        |> Convert.ToHexString
        |> fun value -> value.ToLowerInvariant()

    let private sha256File path =
        use stream = File.OpenRead path
        SHA256.HashData stream
        |> Convert.ToHexString
        |> fun value -> value.ToLowerInvariant()

    let private safeSegment label (value: string) =
        if String.IsNullOrWhiteSpace value
           || value = "."
           || value = ".."
           || value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
           || value.Contains("/")
           || value.Contains("\\") then
            Error $"{label} '{value}' is not safe for an offline bundle path."
        else
            Ok value

    let private ensureInside (root: string) (path: string) =
        let root = Path.GetFullPath root
        let full = Path.GetFullPath path
        let prefix = root.TrimEnd(Path.DirectorySeparatorChar) + string Path.DirectorySeparatorChar
        let comparison =
            if OperatingSystem.IsWindows() then StringComparison.OrdinalIgnoreCase else StringComparison.Ordinal

        if full = root || full.StartsWith(prefix, comparison) then Ok full
        else Error $"Offline bundle path escapes its root: {path}"

    let private parentDirectory (path: string) =
        match Path.GetDirectoryName(path) with
        | null
        | "" -> "."
        | value -> value

    let private copyAtomic (source: string) (destination: string) =
        Directory.CreateDirectory(parentDirectory destination) |> ignore
        let partial = destination + ".partial"
        File.Copy(source, partial, true)
        File.Move(partial, destination, true)

    let private downloadAtomic (client: HttpClient) (url: string) (destination: string) =
        Directory.CreateDirectory(parentDirectory destination) |> ignore
        let partial = destination + ".partial"

        if File.Exists partial then File.Delete partial

        use response = client.GetAsync(url).GetAwaiter().GetResult()
        response.EnsureSuccessStatusCode() |> ignore
        use output = File.Create partial
        response.Content.CopyToAsync(output).GetAwaiter().GetResult()
        File.Move(partial, destination, true)

    let private artifactUrl (repository: string) (tag: string) (mechanism: string) (distributionUrl: string option) (artifactName: string) =
        match mechanism with
        | "github-release" ->
            Ok $"https://github.com/{repository}/releases/download/{tag}/{artifactName}"
        | "npm"
        | "nuget" ->
            match distributionUrl with
            | Some url -> Ok url
            | None -> Error $"{mechanism} release {repository}@{tag} does not expose an exact artifact URL."
        | other ->
            Error $"Offline bundle does not support distribution mechanism '{other}'."

    type private ParsedArtifact =
        { SystemId: string
          Version: string
          Role: string
          Repository: string
          Tag: string
          Mechanism: string
          Name: string
          Purpose: string
          Sha256: string
          SourceUrl: string
          IsNative: bool }

    type private ParsedSet =
        { ProfileId: string
          ProfileVersion: string
          Platform: string
          Sha256: string
          Bytes: byte array
          Artifacts: ParsedArtifact list }


    type private BundledSourceFile =
        { Repository: string
          Commit: string
          Entrypoint: string
          Sha256: string
          RelativePath: string }

    type private BundledProject =
        { ManifestRelativePath: string
          ManifestSha256: string
          SourceFiles: BundledSourceFile list }

    let private parseResolvedSet (path: string) (expectedSha256: string) =
        if not (File.Exists path) then
            Error $"Resolved release set not found: {path}"
        else
            let expected = normalizeSha256 expectedSha256

            if not (isSha256 expected) then
                Error "Resolved release set requires a 64-character lowercase SHA-256."
            else
                let bytes = File.ReadAllBytes path
                let actual = sha256Bytes bytes

                if actual <> expected then
                    Error $"Resolved release set digest mismatch: expected sha256:{expected}, observed sha256:{actual}."
                else
                    try
                        use document = JsonDocument.Parse bytes
                        let root = document.RootElement

                        if str "schema" root <> Some "echelon.resolved-release-set/v1" then
                            Error "Resolved release set must declare schema echelon.resolved-release-set/v1."
                        else
                            let profile = tryProperty "profile" root
                            let platform = str "platform" root

                            match profile, platform with
                            | Some profileRef, Some resolvedPlatform ->
                                match str "id" profileRef, str "version" profileRef with
                                | Some profileId, Some profileVersion ->
                                    let errors = ResizeArray<string>()
                                    let parsed = ResizeArray<ParsedArtifact>()
                                    let seenSystems = System.Collections.Generic.HashSet<string>(StringComparer.Ordinal)

                                    for releaseComponent in objects "components" root do
                                        let id = str "systemId" releaseComponent |> Option.defaultValue ""
                                        let version = str "version" releaseComponent |> Option.defaultValue ""
                                        let role = str "role" releaseComponent |> Option.defaultValue ""
                                        let repository = str "repository" releaseComponent |> Option.defaultValue ""
                                        let tag = str "tag" releaseComponent |> Option.defaultValue ""
                                        let distributionClass = str "distributionClass" releaseComponent |> Option.defaultValue ""

                                        if String.IsNullOrWhiteSpace id then
                                            errors.Add "Resolved component is missing systemId."
                                        elif not (seenSystems.Add id) then
                                            errors.Add $"Resolved set contains duplicate system id '{id}'."

                                        match safeSegment "systemId" id, safeSegment "tag" tag with
                                        | Error error, _ -> errors.Add error
                                        | _, Error error -> errors.Add error
                                        | _ -> ()

                                        let distribution = tryProperty "distribution" releaseComponent
                                        let mechanism = distribution |> Option.bind (str "mechanism") |> Option.defaultValue ""
                                        let distributionUrl = distribution |> Option.bind (str "url")
                                        let native =
                                            role = "host-tool" || role = "repository-lifecycle"

                                        if native && distributionClass <> "self-contained-native-cli" then
                                            errors.Add $"Native component '{id}' must be self-contained-native-cli, observed '{distributionClass}'."

                                        for artifact in objects "artifacts" releaseComponent do
                                            let name = str "name" artifact |> Option.defaultValue ""
                                            let purpose = str "purpose" artifact |> Option.defaultValue ""
                                            let digest = str "sha256" artifact |> Option.defaultValue ""

                                            match safeSegment "artifact" name with
                                            | Error error -> errors.Add error
                                            | Ok _ -> ()

                                            if not (isSha256 digest) then
                                                errors.Add $"Artifact '{id}/{name}' does not carry a valid SHA-256."

                                            match artifactUrl repository tag mechanism distributionUrl name with
                                            | Error error -> errors.Add error
                                            | Ok url ->
                                                parsed.Add
                                                    { SystemId = id
                                                      Version = version
                                                      Role = role
                                                      Repository = repository
                                                      Tag = tag
                                                      Mechanism = mechanism
                                                      Name = name
                                                      Purpose = purpose
                                                      Sha256 = digest
                                                      SourceUrl = url
                                                      IsNative = native }

                                    if errors.Count > 0 then
                                        Error(String.Join(" ", errors))
                                    elif parsed.Count = 0 then
                                        Error "Resolved release set contains no artifacts."
                                    else
                                        Ok
                                            { ProfileId = profileId
                                              ProfileVersion = profileVersion
                                              Platform = resolvedPlatform
                                              Sha256 = actual
                                              Bytes = bytes
                                              Artifacts = List.ofSeq parsed }
                                | _ -> Error "Resolved release set profile identity is incomplete."
                            | _ -> Error "Resolved release set needs profile and platform."
                    with :? JsonException as ex ->
                        Error $"Resolved release set is not valid JSON: {ex.Message}"

    let private sourceEntrypoint (source: GitHubSource) =
        match source.Entrypoint with
        | NodeScript path -> path
        | FileArtifact path -> path

    let private sourceManifestEntry (sourceFile: BundledSourceFile) =
        let node = JsonObject()
        node["repository"] <- JsonValue.Create sourceFile.Repository
        node["commit"] <- JsonValue.Create sourceFile.Commit
        node["entrypoint"] <- JsonValue.Create sourceFile.Entrypoint
        node["sha256"] <- JsonValue.Create sourceFile.Sha256
        node["path"] <- JsonValue.Create(sourceFile.RelativePath.Replace('\\', '/'))
        node

    let private materializeProjectSources staging manifestPath =
        match manifestPath with
        | None -> Ok None
        | Some path ->
            match Manifest.load path with
            | Error errors ->
                Error(String.Join(Environment.NewLine, errors))
            | Ok manifest ->
                try
                    let projectDirectory = Path.Combine(staging, "project")
                    Directory.CreateDirectory projectDirectory |> ignore
                    let projectManifestPath = Path.Combine(projectDirectory, "conditor.json")
                    copyAtomic path projectManifestPath
                    let manifestSha = sha256File projectManifestPath

                    let sourceRoot = Path.Combine(staging, "sources")
                    Directory.CreateDirectory sourceRoot |> ignore
                    let bundled = ResizeArray<BundledSourceFile>()
                    let seen = System.Collections.Generic.Dictionary<string, string>(StringComparer.Ordinal)

                    for requirement in manifest.Requirements do
                        let source = requirement.Source
                        let entrypoint = sourceEntrypoint source
                        let identity = $"{source.Repository}|{source.Commit.ToLowerInvariant()}|{entrypoint}"

                        match SourceCache.ensure $"offline-bundle:{requirement.Id}" source with
                        | Error errors ->
                            raise (InvalidDataException(String.Join(Environment.NewLine, errors)))
                        | Ok checkout ->
                            match SourceCache.resolveEntrypoint checkout source with
                            | Error errors ->
                                raise (InvalidDataException(String.Join(Environment.NewLine, errors)))
                            | Ok sourcePath ->
                                let mirrorCheckout = SourceCache.mirrorCheckoutPath sourceRoot source
                                let destination = Path.GetFullPath(Path.Combine(mirrorCheckout, entrypoint))

                                match ensureInside mirrorCheckout destination with
                                | Error error -> raise (InvalidDataException error)
                                | Ok safeDestination ->
                                    let observed = sha256File sourcePath

                                    match seen.TryGetValue identity with
                                    | true, previous when previous <> observed ->
                                        raise (
                                            InvalidDataException(
                                                $"Pinned source identity produced different bytes within one bundle: {identity}."
                                            )
                                        )
                                    | true, _ -> ()
                                    | false, _ ->
                                        copyAtomic sourcePath safeDestination
                                        seen[identity] <- observed
                                        bundled.Add
                                            { Repository = source.Repository
                                              Commit = source.Commit.ToLowerInvariant()
                                              Entrypoint = entrypoint
                                              Sha256 = observed
                                              RelativePath = Path.GetRelativePath(staging, safeDestination) }

                    let groups =
                        bundled
                        |> Seq.groupBy (fun sourceFile -> sourceFile.Repository, sourceFile.Commit)

                    for (repository, commit), files in groups do
                        let example = files |> Seq.head
                        let source =
                            { Repository = repository
                              Commit = commit
                              Entrypoint = FileArtifact example.Entrypoint }

                        let checkout = SourceCache.mirrorCheckoutPath sourceRoot source
                        let metadata = JsonObject()
                        metadata["schema"] <- JsonValue.Create "conditor.source-mirror/v1"
                        metadata["repository"] <- JsonValue.Create repository
                        metadata["commit"] <- JsonValue.Create commit
                        let fileMap = JsonObject()

                        files
                        |> Seq.sortBy _.Entrypoint
                        |> Seq.iter (fun sourceFile ->
                            fileMap[sourceFile.Entrypoint] <- JsonValue.Create sourceFile.Sha256)

                        metadata["files"] <- fileMap
                        File.WriteAllText(
                            Path.Combine(checkout, ".conditor-source.json"),
                            metadata.ToJsonString(JsonSerializerOptions(WriteIndented = true)) + Environment.NewLine
                        )

                    Ok(
                        Some
                            { ManifestRelativePath = Path.GetRelativePath(staging, projectManifestPath)
                              ManifestSha256 = manifestSha
                              SourceFiles = bundled |> Seq.sortBy (fun x -> x.Repository, x.Commit, x.Entrypoint) |> List.ofSeq }
                    )
                with ex ->
                    Error $"Unable to materialize project sources for offline bundle: {ex.Message}"

    let private manifestArtifact (relativePath: string) (artifact: ParsedArtifact) =
        let node = JsonObject()
        node["systemId"] <- JsonValue.Create artifact.SystemId
        node["version"] <- JsonValue.Create artifact.Version
        node["role"] <- JsonValue.Create artifact.Role
        node["mechanism"] <- JsonValue.Create artifact.Mechanism
        node["purpose"] <- JsonValue.Create artifact.Purpose
        node["name"] <- JsonValue.Create artifact.Name
        node["sourceUrl"] <- JsonValue.Create artifact.SourceUrl
        node["sha256"] <- JsonValue.Create artifact.Sha256
        node["path"] <- JsonValue.Create(relativePath.Replace('\\', '/'))
        node

    let rec create (resolvedSetPath: string) (expectedResolvedSetSha256: string) (outputRoot: string) (projectManifestPath: string option) =
        parseResolvedSet resolvedSetPath expectedResolvedSetSha256
        |> Result.bind (fun resolved ->
            try
                let root = Path.GetFullPath outputRoot
                let staging = root + ".staging"

                if Directory.Exists staging then Directory.Delete(staging, true)
                Directory.CreateDirectory staging |> ignore

                let resolvedTarget = Path.Combine(staging, "resolved-set.json")
                File.WriteAllBytes(resolvedTarget, resolved.Bytes)

                use client = new HttpClient()
                client.DefaultRequestHeaders.UserAgent.ParseAdd("Conditor-OfflineBundle/1.0")

                let manifestArtifacts = JsonArray()

                for artifact in resolved.Artifacts do
                    let artifactPath = Path.Combine(staging, "artifacts", artifact.SystemId, artifact.Name)
                    downloadAtomic client artifact.SourceUrl artifactPath

                    let observed = sha256File artifactPath

                    if observed <> artifact.Sha256 then
                        raise (
                            InvalidDataException(
                                $"Artifact digest mismatch for {artifact.SystemId}/{artifact.Name}: expected sha256:{artifact.Sha256}, observed sha256:{observed}."
                            )
                        )

                    let relativePath = Path.GetRelativePath(staging, artifactPath)
                    manifestArtifacts.Add(manifestArtifact relativePath artifact)

                    if artifact.IsNative then
                        let mirrorPath = Path.Combine(staging, "mirror", artifact.Tag, artifact.Name)
                        copyAtomic artifactPath mirrorPath

                    if artifact.Purpose = "package" then
                        let packagePath = Path.Combine(staging, "packages", artifact.SystemId, artifact.Name)
                        copyAtomic artifactPath packagePath

                let bundledProject =
                    match materializeProjectSources staging projectManifestPath with
                    | Ok value -> value
                    | Error error -> raise (InvalidDataException error)

                let manifest = JsonObject()
                manifest["schema"] <- JsonValue.Create "conditor.offline-bundle/v1"
                manifest["profileId"] <- JsonValue.Create resolved.ProfileId
                manifest["profileVersion"] <- JsonValue.Create resolved.ProfileVersion
                manifest["platform"] <- JsonValue.Create resolved.Platform
                manifest["resolvedSetSha256"] <- JsonValue.Create resolved.Sha256
                manifest["resolvedSetPath"] <- JsonValue.Create "resolved-set.json"
                manifest["artifacts"] <- manifestArtifacts

                match bundledProject with
                | None ->
                    manifest["projectManifest"] <- null
                    manifest["sources"] <- JsonArray()
                | Some project ->
                    let projectNode = JsonObject()
                    projectNode["path"] <- JsonValue.Create(project.ManifestRelativePath.Replace('\\', '/'))
                    projectNode["sha256"] <- JsonValue.Create project.ManifestSha256
                    manifest["projectManifest"] <- projectNode

                    let sourceNodes = JsonArray()
                    project.SourceFiles
                    |> List.iter (fun sourceFile -> sourceNodes.Add(sourceManifestEntry sourceFile))
                    manifest["sources"] <- sourceNodes

                let manifestPath = Path.Combine(staging, "bundle.json")
                File.WriteAllText(
                    manifestPath,
                    manifest.ToJsonString(JsonSerializerOptions(WriteIndented = true)) + Environment.NewLine
                )

                match verify staging with
                | Error error ->
                    if Directory.Exists staging then Directory.Delete(staging, true)
                    Error error
                | Ok summary ->
                    if Directory.Exists root then Directory.Delete(root, true)
                    Directory.Move(staging, root)
                    Ok
                        { summary with
                            Root = root
                            ManifestPath = Path.Combine(root, "bundle.json")
                            ResolvedSetPath = Path.Combine(root, "resolved-set.json")
                            NativeMirror = Path.Combine(root, "mirror")
                            PackageMirror = Path.Combine(root, "packages")
                            SourceMirror = Path.Combine(root, "sources")
                            ProjectManifestPath =
                                bundledProject
                                |> Option.map (fun _ -> Path.Combine(root, "project", "conditor.json")) }
            with ex ->
                Error $"Unable to create offline bundle: {ex.Message}")

    and verify (bundleRoot: string) =
        try
            let root = Path.GetFullPath bundleRoot
            let manifestPath = Path.Combine(root, "bundle.json")

            if not (File.Exists manifestPath) then
                Error $"Offline bundle manifest is missing: {manifestPath}"
            else
                use document = JsonDocument.Parse(File.ReadAllBytes manifestPath)
                let manifest = document.RootElement

                if str "schema" manifest <> Some "conditor.offline-bundle/v1" then
                    Error "Offline bundle must declare schema conditor.offline-bundle/v1."
                else
                    match str "profileId" manifest, str "profileVersion" manifest, str "platform" manifest, str "resolvedSetSha256" manifest, str "resolvedSetPath" manifest with
                    | Some profileId, Some profileVersion, Some platform, Some resolvedSha, Some resolvedRelative when isSha256 resolvedSha ->
                        let errors = ResizeArray<string>()

                        match ensureInside root (Path.Combine(root, resolvedRelative)) with
                        | Error error -> errors.Add error
                        | Ok resolvedPath ->
                            if not (File.Exists resolvedPath) then
                                errors.Add $"Offline bundle resolved set is missing: {resolvedRelative}."
                            else
                                let observed = sha256File resolvedPath
                                if observed <> resolvedSha then
                                    errors.Add $"Offline bundle resolved-set digest mismatch: expected sha256:{resolvedSha}, observed sha256:{observed}."

                        let artifacts = objects "artifacts" manifest

                        if artifacts.IsEmpty then
                            errors.Add "Offline bundle contains no artifacts."

                        for artifact in artifacts do
                            match str "path" artifact, str "sha256" artifact with
                            | Some relativePath, Some expected when isSha256 expected ->
                                match ensureInside root (Path.Combine(root, relativePath)) with
                                | Error error -> errors.Add error
                                | Ok artifactPath ->
                                    if not (File.Exists artifactPath) then
                                        errors.Add $"Offline bundle artifact is missing: {relativePath}."
                                    else
                                        let observed = sha256File artifactPath
                                        if observed <> expected then
                                            errors.Add $"Offline bundle artifact digest mismatch for {relativePath}: expected sha256:{expected}, observed sha256:{observed}."
                            | _ ->
                                errors.Add "Offline bundle artifact entry needs path and valid sha256."

                        if errors.Count > 0 then
                            Error(String.Join(" ", errors))
                        else
                            Ok
                                { Root = root
                                  ManifestPath = manifestPath
                                  ResolvedSetPath = Path.Combine(root, resolvedRelative)
                                  ResolvedSetSha256 = resolvedSha
                                  Platform = platform
                                  ProfileId = profileId
                                  ProfileVersion = profileVersion
                                  ArtifactCount = artifacts.Length
                                  NativeMirror = Path.Combine(root, "mirror")
                                  PackageMirror = Path.Combine(root, "packages") }
                    | _ ->
                        Error "Offline bundle manifest is missing profile/platform/resolved-set identity."
        with
        | :? JsonException as ex ->
            Error $"Offline bundle manifest is not valid JSON: {ex.Message}"
        | ex ->
            Error $"Unable to verify offline bundle: {ex.Message}"
