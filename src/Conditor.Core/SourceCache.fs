namespace Conditor.Core

open System
open System.Diagnostics
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.RegularExpressions

module SourceCache =
    let private isNonEmpty (value: string) =
        not (String.IsNullOrWhiteSpace value)

    let private environmentValue name =
        match Environment.GetEnvironmentVariable name with
        | null -> None
        | value when String.IsNullOrWhiteSpace value -> None
        | value -> Some value


    let private offlineRequired () =
        match environmentValue "CONDITOR_OFFLINE" with
        | Some value ->
            value = "1"
            || value.Equals("true", StringComparison.OrdinalIgnoreCase)
            || value.Equals("yes", StringComparison.OrdinalIgnoreCase)
        | None -> false

    let private sourceMirrorRoot () =
        environmentValue "CONDITOR_SOURCE_MIRROR"
        |> Option.map Path.GetFullPath

    let private sourceEntrypoint source =
        match source.Entrypoint with
        | NodeScript path -> path
        | FileArtifact path -> path

    let private sha256File path =
        use stream = File.OpenRead path
        SHA256.HashData stream
        |> Convert.ToHexString
        |> fun value -> value.ToLowerInvariant()

    let mirrorCheckoutPath (root: string) (source: GitHubSource) =
        Path.Combine(
            Path.GetFullPath root,
            safeSegment source.Repository,
            source.Commit.ToLowerInvariant()
        )

    let private verifyMirrorCheckout checkout source =
        let metadataPath = Path.Combine(checkout, ".conditor-source.json")

        if not (File.Exists metadataPath) then
            Error [ $"Offline source mirror metadata is missing: {metadataPath}" ]
        else
            try
                use document = JsonDocument.Parse(File.ReadAllBytes metadataPath)
                let root = document.RootElement
                let mutable value = Unchecked.defaultof<JsonElement>

                let stringProperty name =
                    if root.TryGetProperty(name, &value) && value.ValueKind = JsonValueKind.String then
                        value.GetString() |> Option.ofObj
                    else
                        None

                let repository = stringProperty "repository"
                let commit = stringProperty "commit"
                let entrypoint = sourceEntrypoint source

                if repository <> Some source.Repository || commit <> Some(source.Commit.ToLowerInvariant()) then
                    Error
                        [ $"Offline source mirror identity mismatch for {source.Repository}@{source.Commit}." ]
                elif not (root.TryGetProperty("files", &value)) || value.ValueKind <> JsonValueKind.Object then
                    Error [ $"Offline source mirror files map is missing: {metadataPath}" ]
                else
                    let mutable digestElement = Unchecked.defaultof<JsonElement>

                    if not (value.TryGetProperty(entrypoint, &digestElement))
                       || digestElement.ValueKind <> JsonValueKind.String then
                        Error
                            [ $"Offline source mirror does not contain declared entrypoint '{entrypoint}' for {source.Repository}@{source.Commit}." ]
                    else
                        let expected = digestElement.GetString()
                        let candidate = Path.GetFullPath(Path.Combine(checkout, entrypoint))
                        let rootPath = Path.GetFullPath checkout
                        let rootPrefix =
                            rootPath.TrimEnd(Path.DirectorySeparatorChar)
                            + string Path.DirectorySeparatorChar

                        let comparison =
                            if OperatingSystem.IsWindows() then
                                StringComparison.OrdinalIgnoreCase
                            else
                                StringComparison.Ordinal

                        if not (candidate.StartsWith(rootPrefix, comparison)) || not (File.Exists candidate) then
                            Error [ $"Offline source mirror entrypoint is missing or escapes its checkout: {entrypoint}" ]
                        else
                            let observed = sha256File candidate

                            if not (String.Equals(expected, observed, StringComparison.Ordinal)) then
                                Error
                                    [ $"Offline source mirror digest mismatch for {source.Repository}@{source.Commit}:{entrypoint}. Expected sha256:{expected}, observed sha256:{observed}." ]
                            else
                                Ok checkout
            with :? JsonException as ex ->
                Error [ $"Offline source mirror metadata is invalid JSON: {ex.Message}" ]

    let private cacheRoot () =
        match environmentValue "CONDITOR_CACHE_DIR" with
        | Some overridePath -> Path.GetFullPath overridePath
        | None when OperatingSystem.IsWindows() ->
            Path.Combine(
                Environment.GetFolderPath Environment.SpecialFolder.LocalApplicationData,
                "Conditor",
                "Cache"
            )
        | None when OperatingSystem.IsMacOS() ->
            Path.Combine(
                Environment.GetFolderPath Environment.SpecialFolder.UserProfile,
                "Library",
                "Caches",
                "conditor"
            )
        | None ->
            match environmentValue "XDG_CACHE_HOME" with
            | Some xdg -> Path.Combine(xdg, "conditor")
            | None ->
                Path.Combine(
                    Environment.GetFolderPath Environment.SpecialFolder.UserProfile,
                    ".cache",
                    "conditor"
                )

    let private safeSegment (value: string) =
        Regex.Replace(value, "[^A-Za-z0-9_.-]", "_")

    let validate (source: GitHubSource) =
        let repositoryOk =
            Regex.IsMatch(source.Repository, "^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$")

        let commitOk =
            Regex.IsMatch(source.Commit, "^[0-9a-fA-F]{40}$")

        let entrypoint =
            match source.Entrypoint with
            | NodeScript path -> path
            | FileArtifact path -> path

        let entrypointParts =
            entrypoint.Replace('\\', '/').Split('/')

        let entrypointOk =
            isNonEmpty entrypoint
            && not (Path.IsPathRooted entrypoint)
            && not (entrypointParts |> Array.exists ((=) ".."))

        [ if not repositoryOk then
              yield $"Invalid GitHub repository '{source.Repository}'."
          if not commitOk then
              yield $"GitHub source '{source.Repository}' must use a full 40-character commit SHA."
          if not entrypointOk then
              yield $"GitHub source entrypoint '{entrypoint}' must be a safe relative path." ]

    let private runWithEnvironment environment workingDirectory executable arguments =
        try
            let info = ProcessStartInfo()
            info.FileName <- executable
            info.WorkingDirectory <- workingDirectory
            info.UseShellExecute <- false
            info.RedirectStandardOutput <- true
            info.RedirectStandardError <- true

            for argument in arguments do
                info.ArgumentList.Add argument

            for key, value in environment do
                info.Environment[key] <- value

            use childProcess = new Process()
            childProcess.StartInfo <- info

            if not (childProcess.Start()) then
                Error [ $"Unable to start '{executable}'." ]
            else
                let outputTask = childProcess.StandardOutput.ReadToEndAsync()
                let errorTask = childProcess.StandardError.ReadToEndAsync()
                childProcess.WaitForExit()

                let output = outputTask.GetAwaiter().GetResult().Trim()
                let error = errorTask.GetAwaiter().GetResult().Trim()

                if childProcess.ExitCode = 0 then
                    Ok output
                else
                    let argumentText = String.Join(" ", arguments)

                    Error
                        [ $"Command failed with exit code {childProcess.ExitCode}: {executable} {argumentText}"
                          output
                          error ]
                    |> Result.mapError (List.filter isNonEmpty)
        with ex ->
            Error [ $"Unable to execute '{executable}': {ex.Message}" ]

    let private run workingDirectory executable arguments =
        runWithEnvironment [] workingDirectory executable arguments

    let private githubToken () =
        [ "CONDITOR_GITHUB_TOKEN"; "GH_TOKEN"; "GITHUB_TOKEN" ]
        |> List.tryPick environmentValue

    let private gitFetchEnvironment () =
        let baseEnvironment =
            [ "GIT_TERMINAL_PROMPT", "0" ]

        match githubToken () with
        | None -> baseEnvironment
        | Some token ->
            let credentials =
                Encoding.UTF8.GetBytes($"x-access-token:{token}")
                |> Convert.ToBase64String

            baseEnvironment
            @ [ "GIT_CONFIG_COUNT", "1"
                "GIT_CONFIG_KEY_0", "http.https://github.com/.extraheader"
                "GIT_CONFIG_VALUE_0", $"AUTHORIZATION: basic {credentials}" ]

    let private runGitFetch workingDirectory arguments =
        runWithEnvironment (gitFetchEnvironment ()) workingDirectory "git" arguments

    let private currentHead checkout =
        match run checkout "git" [ "rev-parse"; "HEAD" ] with
        | Ok value -> Some(value.Trim())
        | Error _ -> None

    let private entrypointPath checkout source =
        let relative =
            match source.Entrypoint with
            | NodeScript path -> path
            | FileArtifact path -> path

        let root = Path.GetFullPath checkout
        let candidate = Path.GetFullPath(Path.Combine(root, relative))
        let rootPrefix =
            root.TrimEnd(Path.DirectorySeparatorChar) + string Path.DirectorySeparatorChar

        let comparison =
            if OperatingSystem.IsWindows() then
                StringComparison.OrdinalIgnoreCase
            else
                StringComparison.Ordinal

        if candidate.StartsWith(rootPrefix, comparison)
           && File.Exists candidate then
            Ok candidate
        else
            Error [ $"Source entrypoint is missing or escapes its checkout: {relative}" ]

    let private verifyCheckout checkout source =
        match currentHead checkout with
        | Some head when String.Equals(head, source.Commit, StringComparison.OrdinalIgnoreCase) ->
            entrypointPath checkout source |> Result.map (fun _ -> checkout)
        | Some head ->
            Error [ $"Cached source HEAD '{head}' does not match required commit '{source.Commit}'." ]
        | None ->
            Error [ $"Unable to verify cached source at '{checkout}'." ]

    let private populate checkout source =
        match Directory.GetParent checkout with
        | null ->
            Error [ $"Unable to determine parent directory for source cache '{checkout}'." ]
        | parent ->
            Directory.CreateDirectory parent.FullName |> ignore
            let temporary = $"{checkout}.tmp-{Guid.NewGuid():N}"

            try
                Directory.CreateDirectory temporary |> ignore

                match run temporary "git" [ "init" ] with
                | Error errors -> Error errors
                | Ok _ ->
                    let url = $"https://github.com/{source.Repository}.git"

                    match run temporary "git" [ "remote"; "add"; "origin"; url ] with
                    | Error errors -> Error errors
                    | Ok _ ->
                        match runGitFetch temporary [ "fetch"; "--depth"; "1"; "origin"; source.Commit ] with
                        | Error errors -> Error errors
                        | Ok _ ->
                            match run temporary "git" [ "checkout"; "--detach"; source.Commit ] with
                            | Error errors -> Error errors
                            | Ok _ ->
                                match verifyCheckout temporary source with
                                | Error errors -> Error errors
                                | Ok _ ->
                                    try
                                        Directory.Move(temporary, checkout)
                                        Ok checkout
                                    with :? IOException ->
                                        if Directory.Exists checkout then
                                            verifyCheckout checkout source
                                        else
                                            Error [ $"Unable to move prepared source into cache '{checkout}'." ]
            finally
                if Directory.Exists temporary then
                    Directory.Delete(temporary, true)

    let ensure componentId source =
        let validationErrors = validate source

        if not validationErrors.IsEmpty then
            Error validationErrors
        else
            let mirrored =
                sourceMirrorRoot ()
                |> Option.map (fun root ->
                    let checkout = mirrorCheckoutPath root source

                    if Directory.Exists checkout then
                        verifyMirrorCheckout checkout source
                    else
                        Error
                            [ $"Offline source mirror checkout is missing for {source.Repository}@{source.Commit}: {checkout}" ])

            match mirrored with
            | Some(Ok checkout) ->
                Ok checkout
            | mirroredResult ->
                let checkout =
                    Path.Combine(
                        cacheRoot (),
                        "sources",
                        safeSegment componentId,
                        source.Commit.ToLowerInvariant()
                    )

                if Directory.Exists checkout then
                    match verifyCheckout checkout source with
                    | Ok path -> Ok path
                    | Error _ when offlineRequired () ->
                        match mirroredResult with
                        | Some(Error errors) -> Error errors
                        | _ ->
                            Error
                                [ $"Offline mode requires an exact cached or mirrored source for {source.Repository}@{source.Commit}." ]
                    | Error _ ->
                        Directory.Delete(checkout, true)
                        populate checkout source
                elif offlineRequired () then
                    match mirroredResult with
                    | Some(Error errors) -> Error errors
                    | _ ->
                        Error
                            [ $"Offline mode requires CONDITOR_SOURCE_MIRROR or an exact cached source for {source.Repository}@{source.Commit}." ]
                else
                    populate checkout source

    let resolveEntrypoint checkout source =
        entrypointPath checkout source

    let sourceReference source =
        let entrypoint =
            match source.Entrypoint with
            | NodeScript path -> $"node:{path}"
            | FileArtifact path -> $"file:{path}"

        $"github:{source.Repository}#{source.Commit}|{entrypoint}"
