namespace Conditor.Core

// The exact release a Registry resolved release set selects for a
// package-distributed web binding (distribution class `web-package`), and the
// package.json specifier its declared distribution calls for. One reading of
// the Registry entry serves both the scaffold, which seeds the pin
// (Scaffolding.fs), and the current-upgrade contract, which moves it
// (WebPackageBinding.fs, docs/web-package-upgrade-contract.md).

open System
open System.IO
open System.Text.Json
open System.Text.RegularExpressions

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

module WebPackageRelease =
    [<Literal>]
    let DistributionClass = "web-package"

    let private sha256Pattern = Regex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant)
    let private repositoryPattern = Regex("^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$", RegexOptions.CultureInvariant)

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

    let private components (root: JsonElement) =
        match tryProperty "components" root with
        | Some value when value.ValueKind = JsonValueKind.Array -> value.EnumerateArray() |> Seq.toList
        | _ -> []

    /// The release one resolved-set component entry describes, or why
    /// Conditor cannot pin it: the package specifier follows the entry's
    /// declared distribution mechanism (npm -> the exact registry version;
    /// github-release -> the release asset of its one package artifact).
    let ofEntry (id: string) (entry: JsonElement) : Result<WebPackageRelease, string list> =
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

    /// Every `web-package` selection of resolved-release-set bytes, by
    /// component id, each with its release or why it cannot be pinned.
    /// Callers verify the bytes' digest first (ResolvedReleaseSets.parseVerified).
    let selectionsIn (bytes: byte array) : Map<string, Result<WebPackageRelease, string list>> =
        try
            use document = JsonDocument.Parse bytes

            components document.RootElement
            |> List.filter (fun entry -> str "distributionClass" entry = Some DistributionClass)
            |> List.choose (fun entry -> str "systemId" entry |> Option.map (fun id -> id, ofEntry id entry))
            |> Map.ofList
        with :? JsonException ->
            Map.empty

    /// The exact release a resolved release set selects for `id`.
    let releaseIn (setPath: string) (id: string) : Result<WebPackageRelease, string list> =
        try
            use document = JsonDocument.Parse(File.ReadAllBytes setPath)

            match components document.RootElement |> List.filter (fun entry -> str "systemId" entry = Some id) with
            | [ entry ] -> ofEntry id entry
            | [] -> Error [ $"Registry release set {setPath} does not select '{id}'." ]
            | _ -> Error [ $"Registry release set {setPath} selects '{id}' more than once." ]
        with ex ->
            Error [ $"Unable to read Registry release set {setPath}: {ex.Message}" ]
