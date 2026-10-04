module CurrentChannelTests

open Conditor.Core

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
