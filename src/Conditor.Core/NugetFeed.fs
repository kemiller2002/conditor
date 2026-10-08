namespace Conditor.Core

// The NuGet release-asset feed (docs/nuget-feed-contract.md): installing a
// Registry-selected `nuget-library` release whose packages are GitHub release
// assets (not on nuget.org) into a consuming repository's local NuGet feed.
// Conditor downloads every package asset, proves it against the Registry's
// SHA-256 before anything changes, writes it to `vendor/nuget`, records a
// lock, and maps exactly those package ids to that feed in NuGet.config so
// they can never be restored from a public feed. This is the general form of
// what Praxis does by hand for Ordo's `ordo-core.nupkg`.

open System
open System.IO
open System.Net.Http
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.RegularExpressions
open System.Xml.Linq

module NugetFeed =
    [<Literal>]
    let DistributionClass = "nuget-library"

    [<Literal>]
    let Mechanism = "github-release"

    /// The plan mode of a feed opt-in or version change.
    [<Literal>]
    let Mode = "nuget-feed"

    /// The feed directory, relative to the repository root.
    [<Literal>]
    let FeedDirectory = "vendor/nuget"

    /// The NuGet.config package source key of the feed.
    [<Literal>]
    let SourceKey = "echelon-vendor"

    [<Literal>]
    let private LockSchema = "conditor.nuget-feed-lock/v1"

    let private tryProperty (name: string) (element: JsonElement) =
        let mutable value = Unchecked.defaultof<JsonElement>
        if element.ValueKind = JsonValueKind.Object && element.TryGetProperty(name, &value) then Some value else None

    let private str name element =
        tryProperty name element
        |> Option.bind (fun value -> if value.ValueKind = JsonValueKind.String then value.GetString() |> Option.ofObj else None)

    let private isSha256 (value: string) =
        value.Length = 64 && value |> Seq.forall Char.IsAsciiHexDigitLower

    let private normalizeSha256 (value: string) =
        if value.StartsWith("sha256:", StringComparison.Ordinal) then value.Substring("sha256:".Length) else value

    let private packageIdPattern = Regex(@"^[A-Za-z0-9_]+(\.[A-Za-z0-9_-]+)*$", RegexOptions.CultureInvariant)

    let private repositoryPattern = Regex(@"^[A-Za-z0-9_.-]+/[A-Za-z0-9._-]+$", RegexOptions.CultureInvariant)

    let sha256Bytes (bytes: byte array) =
        Convert.ToHexString(SHA256.HashData bytes).ToLowerInvariant()

    /// The release asset URL of an artifact.
    let downloadUrl (repository: string) (tag: string) (artifactName: string) =
        $"https://github.com/{repository}/releases/download/{Uri.EscapeDataString tag}/{Uri.EscapeDataString artifactName}"

    /// True when a resolved-set component is a release this feed installs.
    let isFeedComponent (element: JsonElement) =
        str "role" element = Some "project-binding"
        && str "distributionClass" element = Some DistributionClass
        && (tryProperty "distribution" element |> Option.bind (str "mechanism")) = Some Mechanism

    /// The feed release a resolved-set component describes, or every reason
    /// it cannot be installed safely.
    let ofComponent (element: JsonElement) : Result<NugetFeedRelease, string list> =
        let id = str "systemId" element |> Option.defaultValue "<unknown>"

        match str "version" element, str "repository" element, str "tag" element, str "lifecycleState" element with
        | Some version, Some repository, Some tag, Some "active" when repositoryPattern.IsMatch repository ->
            let releaseManifest =
                tryProperty "releaseManifest" element
                |> Option.bind (str "sha256")
                |> Option.map normalizeSha256
                |> Option.filter isSha256

            let artifacts =
                match tryProperty "artifacts" element with
                | Some values when values.ValueKind = JsonValueKind.Array ->
                    values.EnumerateArray()
                    |> Seq.filter (fun artifact -> str "purpose" artifact = Some "package")
                    |> Seq.toList
                | _ -> []

            let suffix = $".{version}.nupkg"

            let packages =
                artifacts
                |> List.map (fun artifact ->
                    match str "name" artifact, str "sha256" artifact |> Option.map normalizeSha256 with
                    | Some name, Some digest when name.EndsWith(suffix, StringComparison.Ordinal) && isSha256 digest ->
                        let packageId = name.Substring(0, name.Length - suffix.Length)

                        if packageIdPattern.IsMatch packageId then
                            let package: NugetFeedPackage =
                                { PackageId = packageId
                                  Version = version
                                  ArtifactName = name
                                  Sha256 = digest
                                  DownloadUrl = downloadUrl repository tag name }

                            Ok package
                        else
                            Error $"'{id}' package artifact '{name}' does not name a valid NuGet package id"
                    | Some name, _ ->
                        Error $"'{id}' package artifact '{name}' must be '<PackageId>{suffix}' with a 64-character SHA-256"
                    | None, _ -> Error $"'{id}' has a package artifact without a name")

            let errors =
                [ if releaseManifest.IsNone then
                      $"'{id}' has no release manifest SHA-256"
                  if artifacts.IsEmpty then
                      $"'{id}' has no package artifact"
                  yield!
                      packages
                      |> List.choose (function
                          | Error error -> Some error
                          | Ok _ -> None)
                  let ids =
                      packages
                      |> List.choose (function
                          | Ok package -> Some(package.PackageId.ToLowerInvariant())
                          | Error _ -> None)
                  if ids.Length <> (List.distinct ids).Length then
                      $"'{id}' names a package id more than once" ]

            match errors, releaseManifest with
            | [], Some manifestSha ->
                Ok(
                    { Id = id
                      Version = version
                      Repository = repository
                      Tag = tag
                      ReleaseManifestSha256 = manifestSha
                      Packages =
                        packages
                        |> List.choose (function
                            | Ok package -> Some package
                            | Error _ -> None)
                        |> List.sortBy _.PackageId }: NugetFeedRelease
                )
            | errors, _ -> Error errors
        | _, _, _, Some state when state <> "active" -> Error [ $"'{id}' is {state}; only an active release is installed" ]
        | _ -> Error [ $"'{id}' is missing its version, repository or tag" ]

    /// Every feed release in a resolved release set file, by system id. The
    /// caller has already bound the file's integrity.
    let loadAll (setPath: string) : Result<Map<string, NugetFeedRelease>, string list> =
        try
            use document = JsonDocument.Parse(File.ReadAllBytes setPath)

            let components =
                match tryProperty "components" document.RootElement with
                | Some values when values.ValueKind = JsonValueKind.Array -> values.EnumerateArray() |> Seq.toList
                | _ -> []

            components
            |> List.filter isFeedComponent
            |> List.map ofComponent
            |> List.fold
                (fun state item ->
                    match state, item with
                    | Ok releases, Ok release -> Ok(Map.add release.Id release releases)
                    | Error errors, Error more -> Error(errors @ more)
                    | Error errors, Ok _ -> Error errors
                    | Ok _, Error errors -> Error errors)
                (Ok Map.empty)
        with ex ->
            Error [ $"unable to read the NuGet feed releases of {setPath}: {ex.Message}" ]

    /// The lock's path, relative to the repository root.
    let lockPath (release: NugetFeedRelease) = $"{FeedDirectory}/{release.Id}.lock"

    /// A package's file in the feed, relative to the repository root.
    let packagePath (package: NugetFeedPackage) = $"{FeedDirectory}/{package.ArtifactName}"

    /// The lock: exactly which release and which bytes the feed holds.
    let renderLock (release: NugetFeedRelease) =
        let lines =
            [ $"# {LockSchema}. Written by Conditor; do not edit. The packages beside this"
              $"# file are the unmodified release assets named here, each proven against the"
              $"# Registry SHA-256. NuGet restores these ids only from this folder (NuGet.config)."
              $"system {release.Id}"
              $"version {release.Version}"
              $"repository {release.Repository}"
              $"tag {release.Tag}"
              $"release-manifest sha256:{release.ReleaseManifestSha256}" ]
            @ (release.Packages
               |> List.map (fun package ->
                   $"package {package.PackageId} {package.Version} {package.ArtifactName} sha256:{package.Sha256} {package.DownloadUrl}"))

        String.concat "\n" lines + "\n"

    /// Package ids a lock records, for removing a previous version's files.
    let lockedArtifacts (lockText: string) =
        lockText.Replace("\r\n", "\n").Split('\n')
        |> Array.choose (fun line ->
            match line.Split(' ') with
            | [| "package"; _; _; artifact; _; _ |] -> Some artifact
            | _ -> None)
        |> List.ofArray

    let private element (name: string) = XName.Get name

    let private attribute (name: string) (node: XElement) =
        match node.Attribute(XName.Get name) with
        | null -> None
        | value -> Some value.Value

    /// NuGet.config with the feed source and a package source mapping that
    /// sends exactly `packageIds` to the feed and everything else where it
    /// went before (nuget.org when nothing was mapped). Existing content is
    /// kept. A conflicting source key, or a package id another source already
    /// maps, is refused rather than overridden.
    let configure (existing: string option) (packageIds: string list) : Result<string, string> =
        try
            let document =
                match existing with
                | Some text when not (String.IsNullOrWhiteSpace text) -> XDocument.Parse(text, LoadOptions.None)
                | _ -> XDocument(XElement(element "configuration"))

            match document.Root with
            | null -> Error "NuGet.config has no root element"
            | root when root.Name.LocalName <> "configuration" -> Error "NuGet.config's root element is not <configuration>"
            | root ->
                let child (name: string) =
                    match root.Element(element name) with
                    | null ->
                        let created = XElement(element name)
                        root.Add created
                        created
                    | found -> found

                let sources = child "packageSources"

                let adds =
                    sources.Elements(element "add")
                    |> Seq.map (fun add -> attribute "key" add, attribute "value" add)
                    |> List.ofSeq

                let hadMapping = root.Element(element "packageSourceMapping") |> isNull |> not
                let mapping = child "packageSourceMapping"

                let mappedElsewhere =
                    mapping.Elements(element "packageSource")
                    |> Seq.filter (fun source -> attribute "key" source <> Some SourceKey)
                    |> Seq.collect (fun source ->
                        source.Elements(element "package")
                        |> Seq.choose (attribute "pattern")
                        |> Seq.map (fun pattern -> attribute "key" source |> Option.defaultValue "", pattern))
                    |> List.ofSeq

                let conflicts =
                    packageIds
                    |> List.choose (fun id ->
                        mappedElsewhere
                        |> List.tryFind (fun (_, pattern) -> String.Equals(pattern, id, StringComparison.OrdinalIgnoreCase))
                        |> Option.map (fun (key, _) -> $"NuGet.config already maps {id} to package source '{key}'"))

                match adds |> List.tryFind (fun (key, _) -> key = Some SourceKey) with
                | Some(_, value) when value <> Some FeedDirectory ->
                    Error $"NuGet.config already defines package source '{SourceKey}' as '{value |> Option.defaultValue String.Empty}', not '{FeedDirectory}'"
                | _ when not conflicts.IsEmpty -> Error(String.concat "; " conflicts)
                | known ->
                    if known.IsNone then
                        sources.Add(XElement(element "add", XAttribute(XName.Get "key", SourceKey), XAttribute(XName.Get "value", FeedDirectory)))

                    // A mapping, once present, must cover every package. When
                    // there was none, everything else keeps coming from
                    // nuget.org, which this file then declares explicitly.
                    if not hadMapping then
                        let hasNugetOrg =
                            adds |> List.exists (fun (key, _) -> key = Some "nuget.org")

                        if not hasNugetOrg then
                            sources.AddFirst(
                                XElement(
                                    element "add",
                                    XAttribute(XName.Get "key", "nuget.org"),
                                    XAttribute(XName.Get "value", "https://api.nuget.org/v3/index.json"),
                                    XAttribute(XName.Get "protocolVersion", "3")
                                )
                            )

                        mapping.Add(XElement(element "packageSource", XAttribute(XName.Get "key", "nuget.org"), XElement(element "package", XAttribute(XName.Get "pattern", "*"))))

                    let vendor =
                        match
                            mapping.Elements(element "packageSource")
                            |> Seq.tryFind (fun source -> attribute "key" source = Some SourceKey)
                        with
                        | Some found -> found
                        | None ->
                            let created = XElement(element "packageSource", XAttribute(XName.Get "key", SourceKey))
                            mapping.AddFirst created
                            created

                    let present =
                        vendor.Elements(element "package") |> Seq.choose (attribute "pattern") |> Set.ofSeq

                    for id in packageIds |> List.sort do
                        if not (present.Contains id) then
                            vendor.Add(XElement(element "package", XAttribute(XName.Get "pattern", id)))

                    let settings = Xml.XmlWriterSettings(Indent = true, Encoding = UTF8Encoding(false))
                    use stream = new MemoryStream()

                    do
                        use writer = Xml.XmlWriter.Create(stream, settings)
                        document.Save writer

                    Ok(Encoding.UTF8.GetString(stream.ToArray()) + "\n")
        with :? Xml.XmlException as ex ->
            Error $"NuGet.config is not valid XML: {ex.Message}"

    /// Problems with how NuGet.config maps the feed's packages: each id must
    /// map to the feed and to no other source.
    let checkConfiguration (text: string) (packageIds: string list) =
        try
            let document = XDocument.Parse text

            match document.Root with
            | null -> [ "NuGet.config has no root element" ]
            | root ->
                let sources =
                    root.Element(element "packageSources")
                    |> Option.ofObj
                    |> Option.map (fun node -> node.Elements(element "add") |> List.ofSeq)
                    |> Option.defaultValue []

                let mappings =
                    root.Element(element "packageSourceMapping")
                    |> Option.ofObj
                    |> Option.map (fun node -> node.Elements(element "packageSource") |> List.ofSeq)
                    |> Option.defaultValue []

                let mappedTo id =
                    mappings
                    |> List.filter (fun source ->
                        source.Elements(element "package")
                        |> Seq.choose (attribute "pattern")
                        |> Seq.exists (fun pattern -> String.Equals(pattern, id, StringComparison.OrdinalIgnoreCase)))
                    |> List.choose (attribute "key")

                [ if not (sources |> List.exists (fun add -> attribute "key" add = Some SourceKey && attribute "value" add = Some FeedDirectory)) then
                      $"NuGet.config does not define package source '{SourceKey}' as '{FeedDirectory}'"
                  for id in packageIds do
                      match mappedTo id with
                      | [ SourceKey ] -> ()
                      | [] -> $"NuGet.config does not map {id} to '{SourceKey}'"
                      | keys ->
                          let named = String.concat ", " keys
                          $"NuGet.config maps {id} to {named}, not only '{SourceKey}'" ]
        with :? Xml.XmlException as ex ->
            [ $"NuGet.config is not valid XML: {ex.Message}" ]

    let private versionAttribute = Regex("""(?<prefix><(?:PackageVersion|PackageReference)\b[^>]*?\bInclude\s*=\s*"(?<id>[^"]+)"[^>]*?\bVersion\s*=\s*")(?<version>[^"]*)(?<suffix>")""", RegexOptions.CultureInvariant ||| RegexOptions.IgnoreCase)

    /// Moves exact pins of the package ids from `fromVersion` to `toVersion`
    /// in one MSBuild file's text (PackageVersion or PackageReference
    /// `Version` attributes). Returns the new text and how many pins moved. A
    /// pin of these ids at any other version or range is refused: Conditor
    /// changes only what it can prove it set.
    let repin (packageIds: string list) (fromVersion: string) (toVersion: string) (text: string) : Result<string * int, string> =
        let ids = packageIds |> List.map (fun id -> id.ToLowerInvariant()) |> Set.ofList

        let matches =
            versionAttribute.Matches text
            |> Seq.filter (fun m -> ids.Contains(m.Groups["id"].Value.ToLowerInvariant()))
            |> List.ofSeq

        match matches |> List.tryFind (fun m -> m.Groups["version"].Value <> fromVersion && m.Groups["version"].Value <> toVersion) with
        | Some unexpected ->
            let id = unexpected.Groups["id"].Value
            let pinned = unexpected.Groups["version"].Value
            Error $"{id} is pinned at '{pinned}', not exactly {fromVersion}"
        | None ->
            let mutable moved = 0

            let replaced =
                versionAttribute.Replace(
                    text,
                    MatchEvaluator(fun m ->
                        if ids.Contains(m.Groups["id"].Value.ToLowerInvariant()) && m.Groups["version"].Value = fromVersion then
                            moved <- moved + 1
                            m.Groups["prefix"].Value + toVersion + m.Groups["suffix"].Value
                        else
                            m.Value)
                )

            Ok(replaced, moved)

    /// How the feed obtains a package's bytes.
    type Fetch = NugetFeedPackage -> Result<byte array, string>

    /// Fetches from an artifact mirror directory when one holds the asset,
    /// otherwise from its release URL (never when offline).
    let fetchWith (mirror: string option) (offline: bool) : Fetch =
        fun package ->
            let mirrored =
                mirror
                |> Option.map (fun directory -> Path.Combine(directory, package.ArtifactName))
                |> Option.filter File.Exists

            match mirrored with
            | Some path -> Ok(File.ReadAllBytes path)
            | None when offline -> Error $"{package.ArtifactName} is not in the artifact mirror and Conditor is offline."
            | None ->
                try
                    use client = new HttpClient()
                    Ok(client.GetByteArrayAsync(package.DownloadUrl).GetAwaiter().GetResult())
                with ex ->
                    Error $"Unable to download {package.DownloadUrl}: {ex.Message}"

    /// The default fetch: the mirror named by CONDITOR_ARTIFACT_MIRROR, if
    /// any, then the release URL.
    let defaultFetch: Fetch =
        fun package ->
            let mirror =
                Environment.GetEnvironmentVariable "CONDITOR_ARTIFACT_MIRROR"
                |> Option.ofObj
                |> Option.filter (String.IsNullOrWhiteSpace >> not)

            fetchWith mirror false package

    let private full (target: string) (relative: string) =
        Path.GetFullPath(Path.Combine(Path.GetFullPath target, relative))

    let private nugetConfigPath target = full target "NuGet.config"

    /// Problems with the feed in a repository: the lock must be exactly this
    /// release's, every package file must hold the Registry's bytes, and
    /// NuGet.config must map every package id to the feed only. Empty means
    /// the feed is established.
    let verify (target: string) (release: NugetFeedRelease) =
        let lock = full target (lockPath release)
        let config = nugetConfigPath target

        [ if not (File.Exists lock) then
              $"{lockPath release} is missing"
          elif File.ReadAllText lock <> renderLock release then
              $"{lockPath release} does not record {release.Id}@{release.Version} as the Registry selects it"
          for package in release.Packages do
              let path = full target (packagePath package)

              if not (File.Exists path) then
                  $"{packagePath package} is missing"
              else
                  let observed = sha256Bytes (File.ReadAllBytes path)

                  if observed <> package.Sha256 then
                      $"{packagePath package} is sha256:{observed}, not the Registry's sha256:{package.Sha256}"
          if not (File.Exists config) then
              "NuGet.config is missing"
          else
              yield! checkConfiguration (File.ReadAllText config) (release.Packages |> List.map _.PackageId) ]

    let private projectFiles (target: string) =
        let excluded = set [ "bin"; "obj"; ".git"; "node_modules"; ".conditor"; "vendor" ]

        let rec walk (directory: string) =
            seq {
                for file in Directory.EnumerateFiles directory do
                    let name: string = Path.GetFileName file |> Option.ofObj |> Option.defaultValue ""

                    if name.EndsWith(".fsproj", StringComparison.OrdinalIgnoreCase)
                       || name.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
                       || name.EndsWith(".props", StringComparison.OrdinalIgnoreCase)
                       || name.EndsWith(".targets", StringComparison.OrdinalIgnoreCase) then
                        yield file

                for child in Directory.EnumerateDirectories directory do
                    let info = DirectoryInfo child

                    if not (excluded.Contains info.Name) && not (info.Attributes.HasFlag FileAttributes.ReparsePoint) then
                        yield! walk child
            }

        walk (Path.GetFullPath target) |> List.ofSeq

    /// Establishes the feed for a release: every asset is fetched and proven
    /// against the Registry before anything is written; then the packages,
    /// the lock and NuGet.config are written, files of the previously locked
    /// version are removed, and exact pins of the previous version (when
    /// `previousVersion` is given) move to this one. Nothing is written when
    /// any proof fails.
    let ensure (fetch: Fetch) (target: string) (previousVersion: string option) (release: NugetFeedRelease) : Result<unit, string list> =
        let fetched =
            release.Packages
            |> List.map (fun package ->
                fetch package
                |> Result.bind (fun bytes ->
                    let observed = sha256Bytes bytes

                    if observed = package.Sha256 then
                        Ok(package, bytes)
                    else
                        Error $"{package.ArtifactName} is sha256:{observed}, not the Registry's sha256:{package.Sha256}; nothing was changed."))

        match fetched |> List.choose (function Error e -> Some e | Ok _ -> None) with
        | _ :: _ as errors -> Error errors
        | [] ->
            let verified = fetched |> List.choose (function Ok value -> Some value | Error _ -> None)
            let config = nugetConfigPath target

            let existingConfig =
                if File.Exists config then Some(File.ReadAllText config) else None

            let ids = release.Packages |> List.map _.PackageId

            let repins =
                match previousVersion with
                | Some fromVersion when fromVersion <> release.Version ->
                    projectFiles target
                    |> List.map (fun path ->
                        repin ids fromVersion release.Version (File.ReadAllText path)
                        |> Result.map (fun (text, moved) -> path, text, moved)
                        |> Result.mapError (fun error -> $"{Path.GetRelativePath(Path.GetFullPath target, path)}: {error}"))
                | _ -> []

            match configure existingConfig ids, repins |> List.choose (function Error e -> Some e | Ok _ -> None) with
            | Error error, _ -> Error [ error ]
            | Ok _, (_ :: _ as errors) -> Error errors
            | Ok configText, [] ->
                try
                    let feed = full target FeedDirectory
                    Directory.CreateDirectory feed |> ignore
                    let lock = full target (lockPath release)

                    let stale =
                        if File.Exists lock then
                            lockedArtifacts (File.ReadAllText lock)
                            |> List.filter (fun artifact -> not (release.Packages |> List.exists (fun package -> package.ArtifactName = artifact)))
                        else
                            []

                    for package, bytes in verified do
                        File.WriteAllBytes(full target (packagePath package), bytes)

                    for artifact in stale do
                        let path = full target $"{FeedDirectory}/{artifact}"
                        if File.Exists path then File.Delete path

                    File.WriteAllText(lock, renderLock release)
                    File.WriteAllText(config, configText)

                    for path, text, moved in repins |> List.choose (function Ok value -> Some value | Error _ -> None) do
                        if moved > 0 then File.WriteAllText(path, text)

                    match verify target release with
                    | [] -> Ok()
                    | problems -> Error problems
                with ex ->
                    Error [ $"Unable to write the NuGet feed for {release.Id}: {ex.Message}" ]
