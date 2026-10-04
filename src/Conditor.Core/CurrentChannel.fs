namespace Conditor.Core

open System
open System.IO
open System.Net.Http
open System.Security.Cryptography
open System.Text.Json
open Conditor.Core.Workstation

type CurrentChannelEntry =
    { Platform: string
      Path: string
      Sha256: string }

type CurrentChannelDocument =
    { Id: string
      ProfileId: string
      ProfileVersion: string
      ProfileSha256: string
      Entries: CurrentChannelEntry list }

type CurrentChannelResolution =
    { ChannelId: string
      ProfileId: string
      ProfileVersion: string
      ResolvedSetPath: string
      ResolvedSetSha256: string
      SourceUrl: string }

module CurrentChannel =
    [<Literal>]
    let DefaultBaseUrl =
        "https://raw.githubusercontent.com/kemiller2002/echelon-registry/main"

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

    let private isSha256 (value: string) =
        value.Length = 64 && value |> Seq.forall Char.IsAsciiHexDigitLower

    let private safeRelativePath (path: string) =
        not (String.IsNullOrWhiteSpace path)
        && not (Path.IsPathRooted path)
        && not (path.Contains("://", StringComparison.Ordinal))
        && path.Split('/', StringSplitOptions.RemoveEmptyEntries)
           |> Array.forall (fun segment -> segment <> "." && segment <> "..")

    let private sha256Bytes (bytes: byte array) =
        bytes
        |> SHA256.HashData
        |> Convert.ToHexString
        |> fun value -> value.ToLowerInvariant()

    let parse (json: string) : Result<CurrentChannelDocument, string> =
        try
            use document = JsonDocument.Parse json
            let root = document.RootElement

            if str "schema" root <> Some "echelon.current-channel/v1" then
                Error "Registry current channel must declare schema echelon.current-channel/v1."
            else
                match str "id" root, tryProperty "profile" root with
                | Some id, Some profile ->
                    match str "id" profile, str "version" profile, str "sha256" profile with
                    | Some profileId, Some profileVersion, Some profileSha when isSha256 profileSha ->
                        let entries =
                            objects "platforms" root
                            |> List.map (fun item ->
                                match str "platform" item, str "path" item, str "sha256" item with
                                | Some platform, Some path, Some digest when isSha256 digest && safeRelativePath path ->
                                    Ok
                                        { Platform = platform
                                          Path = path
                                          Sha256 = digest }
                                | Some platform, _, _ ->
                                    Error $"Registry current channel entry '{platform}' has an invalid path or SHA-256."
                                | _ ->
                                    Error "Registry current channel contains a malformed platform entry.")

                        let errors = entries |> List.choose (function Error error -> Some error | _ -> None)

                        if not errors.IsEmpty then
                            Error(String.concat "; " errors)
                        else
                            let parsed = entries |> List.choose (function Ok value -> Some value | _ -> None)
                            let duplicatePlatforms =
                                parsed
                                |> List.countBy _.Platform
                                |> List.choose (fun (platform, count) -> if count > 1 then Some platform else None)

                            if not duplicatePlatforms.IsEmpty then
                                Error(
                                    "Registry current channel repeats platform(s): "
                                    + String.concat ", " duplicatePlatforms
                                )
                            else
                                Ok
                                    { Id = id
                                      ProfileId = profileId
                                      ProfileVersion = profileVersion
                                      ProfileSha256 = profileSha
                                      Entries = parsed }
                    | _ ->
                        Error "Registry current channel profile identity is malformed."
                | _ ->
                    Error "Registry current channel id/profile is missing."
        with :? JsonException as ex ->
            Error $"Registry current channel is not valid JSON: {ex.Message}"

    let selectPlatform runtimeIdentifier channel =
        match channel.Entries |> List.tryFind (fun entry -> entry.Platform = runtimeIdentifier) with
        | Some entry -> Ok entry
        | None ->
            let available = channel.Entries |> List.map _.Platform |> List.sort |> String.concat ", "
            Error
                $"Registry current channel '{channel.Id}' does not support {runtimeIdentifier}. Available: {available}."

    let private fetchBytes (url: string) =
        try
            use client = new HttpClient()
            client.Timeout <- TimeSpan.FromSeconds 60.0
            use response = client.GetAsync(url).GetAwaiter().GetResult()
            response.EnsureSuccessStatusCode() |> ignore
            Ok(response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult())
        with ex ->
            Error $"Unable to fetch Registry current channel resource '{url}': {ex.Message}"

    let resolve (home: string) (baseUrl: string) (runtimeIdentifier: string) =
        let root = baseUrl.TrimEnd('/')
        let channelUrl = $"{root}/channels/echelon-current/channel.json"

        fetchBytes channelUrl
        |> Result.bind (fun channelBytes ->
            let channelText = Text.Encoding.UTF8.GetString channelBytes

            parse channelText
            |> Result.bind (fun channel ->
                selectPlatform runtimeIdentifier channel
                |> Result.bind (fun entry ->
                    let setUrl = $"{root}/{entry.Path}"

                    fetchBytes setUrl
                    |> Result.bind (fun setBytes ->
                        let actual = sha256Bytes setBytes

                        if actual <> entry.Sha256 then
                            Error
                                $"Registry current resolved set digest mismatch for {runtimeIdentifier}: expected sha256:{entry.Sha256}, observed sha256:{actual}."
                        else
                            let cacheRoot =
                                Path.Combine(
                                    Path.GetFullPath home,
                                    ".conditor",
                                    "current",
                                    channel.Id
                                )

                            Directory.CreateDirectory cacheRoot |> ignore
                            let destination = Path.Combine(cacheRoot, entry.Sha256 + ".json")

                            if File.Exists destination then
                                let existing = File.ReadAllBytes destination |> sha256Bytes

                                if existing <> entry.Sha256 then
                                    File.Delete destination

                            if not (File.Exists destination) then
                                let temporary = destination + $".{Guid.NewGuid():N}.partial"

                                try
                                    File.WriteAllBytes(temporary, setBytes)
                                    File.Move(temporary, destination)
                                finally
                                    if File.Exists temporary then File.Delete temporary

                            ResolvedReleaseSets.loadFile runtimeIdentifier destination entry.Sha256
                            |> Result.map (fun _ ->
                                { ChannelId = channel.Id
                                  ProfileId = channel.ProfileId
                                  ProfileVersion = channel.ProfileVersion
                                  ResolvedSetPath = destination
                                  ResolvedSetSha256 = entry.Sha256
                                  SourceUrl = setUrl })))))
