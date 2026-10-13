namespace Conditor.Core

// Signed Registry release attestations (DF-CON-2026-A004 decision 2, CON-340..346,
// REG-SIG-001..008). A project-binding release outside a Conditor build's exact
// qualifiedVersions may be qualified by contract and line only when the
// resolved-set entry carries an attestation whose ECDSA P-256 signature verifies
// against a key this build pins and has not revoked. Anything else -- no
// attestation, an unknown or revoked key, a bad signature -- is "not attested",
// and the release stays under exact qualification. There is no fallback to an
// unsigned claim.

open System
open System.IO
open System.Reflection
open System.Security.Cryptography
open System.Text
open System.Text.Json

/// One pinned Registry release-attestation public key.
type TrustedKey =
    { KeyId: string
      /// DER SubjectPublicKeyInfo of an ECDSA P-256 key.
      PublicKey: byte array
      RevokedAt: DateTimeOffset option }

type AttestationOutcome =
    /// The signature verifies under `keyId`; the release claims `contracts`.
    | Attested of keyId: string * contracts: string list
    | NotAttested of reason: string

module ReleaseAttestation =
    [<Literal>]
    let StatementSchema = "echelon.release-attestation/v1"

    [<Literal>]
    let Algorithm = "ecdsa-p256-sha256"

    /// The web-package binding contract of docs/web-package-upgrade-contract.md.
    [<Literal>]
    let WebPackageBindingContract = "echelon.web-package-binding/v1"

    [<Literal>]
    let private TrustSchema = "conditor.registry-release-keys/v1"

    /// The exact signed statement (REG-SIG-002).
    let statement (systemId: string) (version: string) (releaseManifestSha256: string) (contracts: string list) =
        String.concat "|" [ StatementSchema; systemId; version; releaseManifestSha256; contracts |> List.sort |> String.concat "," ]

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

    let private fromBase64 (text: string) =
        try
            Some(Convert.FromBase64String text)
        with :? FormatException ->
            None

    /// The keys of a `conditor.registry-release-keys/v1` document.
    let parseTrust (text: string) : Result<TrustedKey list, string list> =
        try
            use document = JsonDocument.Parse text
            let root = document.RootElement

            if str "schema" root <> Some TrustSchema then
                Error [ $"Trust document schema must be {TrustSchema}." ]
            else
                match tryProperty "keys" root with
                | Some keys when keys.ValueKind = JsonValueKind.Array ->
                    let parsed =
                        keys.EnumerateArray()
                        |> Seq.map (fun key ->
                            match str "keyId" key, str "algorithm" key, str "publicKey" key |> Option.bind fromBase64 with
                            | Some id, Some Algorithm, Some publicKey ->
                                let revoked =
                                    match str "revokedAt" key with
                                    | None -> Ok None
                                    | Some text ->
                                        match DateTimeOffset.TryParse text with
                                        | true, at -> Ok(Some at)
                                        | _ -> Error $"Trusted key '{id}' has an invalid revokedAt."

                                revoked
                                |> Result.map (fun revokedAt ->
                                    { KeyId = id
                                      PublicKey = publicKey
                                      RevokedAt = revokedAt })
                            | _ -> Error $"Every trusted key needs keyId, algorithm '{Algorithm}' and a base64 publicKey.")
                        |> Seq.toList

                    match parsed |> List.choose (function Error e -> Some e | Ok _ -> None) with
                    | [] -> Ok(parsed |> List.choose (function Ok k -> Some k | Error _ -> None))
                    | errors -> Error errors
                | _ -> Error [ "Trust document must list 'keys'." ]
        with :? JsonException as ex ->
            Error [ $"Trust document is not valid JSON: {ex.Message}" ]

    /// The keys pinned in this Conditor build (trust/registry-release-keys.json).
    let embedded: Lazy<TrustedKey list> =
        lazy
            (let assembly = typeof<TrustedKey>.Assembly

             use stream =
                 assembly.GetManifestResourceStream "Conditor.Trust.registry-release-keys.json"
                 |> Option.ofObj
                 |> Option.defaultWith (fun () -> invalidOp "The pinned Registry release-key trust set is missing from this build.")

             use reader = new StreamReader(stream)

             match parseTrust (reader.ReadToEnd()) with
             | Ok keys -> keys
             | Error errors -> invalidOp $"""Invalid pinned Registry release-key trust set: {String.concat "; " errors}""")

    /// Verifies the `releaseAttestation` of one resolved-set component entry
    /// (REG-SIG-004, REG-SIG-005).
    let verify (trust: TrustedKey list) (entry: JsonElement) : AttestationOutcome =
        match tryProperty "releaseAttestation" entry with
        | None -> NotAttested "the Registry release record carries no signed attestation"
        | Some attestation ->
            let contracts =
                match tryProperty "consumerContracts" attestation with
                | Some values when values.ValueKind = JsonValueKind.Array ->
                    values.EnumerateArray()
                    |> Seq.choose (fun value -> if value.ValueKind = JsonValueKind.String then value.GetString() |> Option.ofObj else None)
                    |> Seq.toList
                | _ -> []

            let manifestSha = tryProperty "releaseManifest" entry |> Option.bind (str "sha256")

            match str "schema" attestation, str "algorithm" attestation, str "keyId" attestation, str "signature" attestation |> Option.bind fromBase64 with
            | schema, _, _, _ when schema <> Some StatementSchema -> NotAttested $"the attestation is not {StatementSchema}"
            | _, algorithm, _, _ when algorithm <> Some Algorithm -> NotAttested $"the attestation algorithm is not {Algorithm}"
            | _, _, None, _
            | _, _, _, None -> NotAttested "the attestation has no key id or no base64 signature"
            | _, _, Some keyId, Some signature ->
                match trust |> List.tryFind (fun key -> key.KeyId = keyId), str "systemId" entry, str "version" entry, manifestSha with
                | None, _, _, _ -> NotAttested $"key '{keyId}' is not pinned by this Conditor build"
                | Some { RevokedAt = Some revoked }, _, _, _ -> NotAttested $"key '{keyId}' was revoked at {revoked:O}"
                | Some key, Some systemId, Some version, Some sha when contracts.Length > 0 ->
                    let payload = statement systemId version sha contracts |> Encoding.UTF8.GetBytes

                    let valid =
                        try
                            use ecdsa = ECDsa.Create()
                            ecdsa.ImportSubjectPublicKeyInfo(ReadOnlySpan key.PublicKey, ref 0) |> ignore
                            ecdsa.KeySize = 256
                            && ecdsa.VerifyData(payload, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)
                        with :? CryptographicException ->
                            false

                    if valid then Attested(keyId, contracts) else NotAttested $"the signature does not verify under key '{keyId}'"
                | Some _, _, _, _ -> NotAttested "the attestation claims no contract, or the entry lacks its system id, version or release manifest digest"

    /// The (system id, version) selections of resolved-set bytes that are
    /// qualified by attestation: a project binding whose descriptor declares
    /// a qualifiedRange containing the version, which it does not deny, and
    /// whose entry carries an attestation that verifies under `trust` and
    /// claims the web-package binding contract (CON-340..346).
    let attestedSelections (trust: TrustedKey list) (setBytes: byte array) : Set<string * string> =
        try
            use document = JsonDocument.Parse setBytes

            match tryProperty "components" document.RootElement with
            | Some components when components.ValueKind = JsonValueKind.Array ->
                components.EnumerateArray()
                |> Seq.choose (fun entry ->
                    match str "systemId" entry, str "version" entry, str "role" entry with
                    | Some id, Some version, Some "project-binding" ->
                        match Registry.attestedQualification id with
                        | Some(range, denied) when not (denied.Contains version) ->
                            let inRange =
                                Option.map2 VersionRange.contains (VersionRange.tryParse range) (SemanticVersion.tryParse version)
                                |> Option.defaultValue false

                            match inRange, verify trust entry with
                            | true, Attested(_, contracts) when contracts |> List.contains WebPackageBindingContract -> Some(id, version)
                            | _ -> None
                        | _ -> None
                    | _ -> None)
                |> Set.ofSeq
            | _ -> Set.empty
        with :? JsonException ->
            Set.empty
