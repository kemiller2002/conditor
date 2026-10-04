module CurrentChannelTests

open System
open System.IO
open Conditor.Core
open Conditor.Core.Workstation

let run check =
    let valid =
        """{
  "schema": "echelon.current-channel/v1",
  "id": "echelon-current",
  "profile": {
    "id": "echelon-current",
    "version": "1.0.0",
    "sha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
  },
  "catalogSnapshot": {
    "path": "snapshots/echelon-current.catalog.json",
    "sha256": "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"
  },
  "platforms": [
    {
      "platform": "linux-x64",
      "path": "channels/echelon-current/linux-x64.json",
      "sha256": "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc"
    },
    {
      "platform": "osx-arm64",
      "path": "channels/echelon-current/osx-arm64.json",
      "sha256": "dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd"
    }
  ]
}"""

    match CurrentChannel.parse valid with
    | Error error ->
        check $"current channel parses: {error}" false
    | Ok channel ->
        check "current channel preserves profile identity" (channel.ProfileId = "echelon-current" && channel.ProfileVersion = "1.0.0")
        check "current channel preserves both platforms" (channel.Entries.Length = 2)

        match CurrentChannel.selectPlatform "osx-arm64" channel with
        | Error error ->
            check $"current channel selects supported platform: {error}" false
        | Ok selected ->
            check "current channel selects exact digest-bound set" (selected.Sha256 = String.replicate 64 "d")

        check
            "current channel refuses unsupported platform"
            (CurrentChannel.selectPlatform "linux-musl-x64" channel |> Result.isError)

    let unsafe =
        valid.Replace(
            "channels/echelon-current/linux-x64.json",
            "../outside.json"
        )

    check
        "current channel refuses path traversal"
        (CurrentChannel.parse unsafe |> Result.isError)

    let duplicate =
        valid.Replace(
            "\"platform\": \"osx-arm64\"",
            "\"platform\": \"linux-x64\""
        )

    check
        "current channel refuses duplicate platform entries"
        (CurrentChannel.parse duplicate |> Result.isError)

    let malformedDigest =
        valid.Replace(
            "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc",
            "not-a-digest"
        )

    check
        "current channel refuses malformed resolved-set digest"
        (CurrentChannel.parse malformedDigest |> Result.isError)


    if Environment.GetEnvironmentVariable("CONDITOR_TEST_CURRENT_CHANNEL") = "1" then
        let home = Path.Combine(Path.GetTempPath(), $"conditor-current-channel-{Guid.NewGuid():N}")
        Directory.CreateDirectory home |> ignore

        try
            match CurrentChannel.resolve home CurrentChannel.DefaultBaseUrl (Platform.runtimeIdentifier ()) with
            | Error error ->
                check $"canonical Registry current channel resolves: {error}" false
            | Ok resolved ->
                check "canonical current channel id" (resolved.ChannelId = "echelon-current")
                check "canonical current profile id" (resolved.ProfileId = "echelon-current")
                check "canonical current set is cached" (File.Exists resolved.ResolvedSetPath)
                check "canonical current set digest is present" (resolved.ResolvedSetSha256.Length = 64)
        finally
            if Directory.Exists home then Directory.Delete(home, true)
