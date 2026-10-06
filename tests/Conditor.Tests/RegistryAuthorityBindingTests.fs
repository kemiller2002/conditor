module RegistryAuthorityBindingTests

// CON-F1: when conditor.json declares registryAuthority, Registry is the single
// version authority for every requested component — including components that
// also exist in Conditor's embedded catalog. See
// docs/decisions/0001-registry-version-authority.md.

open System
open System.IO
open System.Security.Cryptography
open System.Text.Json.Nodes
open Conditor.Core
open Conditor.Core.Workstation

[<Literal>]
let private AuthorityPath = ".conditor/authority/resolved-release-set.json"

/// How a fixture release is described in the resolved set. Embedded host-tool
/// and lifecycle-npm components are modelled as native host tools; npm/NuGet
/// application bindings as project bindings, mirroring the live Registry.
type private FixtureRole =
    | NativeHost
    | WebBinding
    | NugetBinding

let private fileSha256 (path: string) =
    use stream = File.OpenRead path
    Convert.ToHexString(SHA256.HashData stream).ToLowerInvariant()

let private node (value: string) = JsonValue.Create value

let private release rid (id: string, version: string, role: FixtureRole) : JsonObject =
    let item = JsonObject()
    item["systemId"] <- node id
    item["required"] <- JsonValue.Create true
    item["version"] <- node version
    item["repository"] <- node $"example/{id}"
    item["tag"] <- node $"v{version}"
    item["commit"] <- node (String.replicate 40 "a")
    item["releaseStage"] <- node "stable"
    item["lifecycleState"] <- node "active"

    let distribution = JsonObject()
    let artifact = JsonObject()
    artifact["sha256"] <- node (String.replicate 64 "c")

    match role with
    | NativeHost ->
        item["role"] <- node "host-tool"
        item["distributionClass"] <- node "self-contained-native-cli"
        item["executable"] <- node id
        distribution["mechanism"] <- node "github-release"
        artifact["name"] <- node $"{id}-{rid}.tar.gz"
        artifact["purpose"] <- node "executable"
        artifact["platform"] <- node rid
    | WebBinding
    | NugetBinding ->
        item["role"] <- node "project-binding"
        item["distributionClass"] <- node (if role = WebBinding then "web-package" else "nuget-library")
        distribution["mechanism"] <- node (if role = WebBinding then "npm" else "nuget")
        distribution["package"] <- node id
        artifact["name"] <- node $"{id}-{version}.pkg"
        artifact["purpose"] <- node "package"

    item["distribution"] <- distribution
    let artifacts = JsonArray()
    artifacts.Add artifact
    item["artifacts"] <- artifacts
    item

/// Writes a verified authority into the target and returns its digest.
let private writeAuthority (target: string) (releases: (string * string * FixtureRole) list) =
    let rid = Platform.runtimeIdentifier ()
    let root = JsonObject()
    root["schema"] <- node "echelon.resolved-release-set/v1"
    let profile = JsonObject()
    profile["id"] <- node "authority-binding-test"
    profile["version"] <- node "1.0.0"
    profile["sha256"] <- node (String.replicate 64 "d")
    root["profile"] <- profile
    root["platform"] <- node rid
    let catalog = JsonObject()
    catalog["sha256"] <- node (String.replicate 64 "e")
    root["catalogSnapshot"] <- catalog
    let components = JsonArray()
    releases |> List.iter (release rid >> components.Add)
    root["components"] <- components

    let path = Path.Combine(target, AuthorityPath)
    Directory.CreateDirectory(Path.GetDirectoryName path |> Option.ofObj |> Option.defaultValue target) |> ignore
    File.WriteAllText(path, root.ToJsonString())
    fileSha256 path

let private manifestJson (digest: string) (requests: (string * string option) list) =
    let components =
        requests
        |> List.map (fun (id, version) ->
            match version with
            | Some value -> $"{{\"id\":\"{id}\",\"version\":\"{value}\"}}"
            | None -> $"{{\"id\":\"{id}\"}}")
        |> String.concat ","

    $"{{\"schemaVersion\":1,\"name\":\"authority-binding\",\"registryAuthority\":{{\"kind\":\"resolved-release-set\",\"path\":\"{AuthorityPath}\",\"sha256\":\"{digest}\"}},\"components\":[{components}],\"execution\":{{\"enabled\":false}}}}"

let private withTarget (action: string -> 'a) : 'a =
    let target = Path.Combine(Path.GetTempPath(), $"conditor-authority-binding-{Guid.NewGuid():N}")
    Directory.CreateDirectory target |> ignore

    try
        action target
    finally
        if Directory.Exists target then
            Directory.Delete(target, true)

/// Plans an Init against an authority fixture. The anchor host tool keeps the
/// resolved set installable, as the live Registry sets always are.
let private planAgainst releases requests =
    withTarget (fun target ->
        let digest = writeAuthority target (("anchor", "1.0.0", NativeHost) :: releases)

        Manifest.parseText (manifestJson digest requests)
        |> Result.bind (Planner.create target Init))

let private refusedWith (fragments: string list) (outcome: Result<InstallationPlan, string list>) =
    match outcome with
    | Ok _ -> false
    | Error errors ->
        errors
        |> List.exists (fun error -> fragments |> List.forall (fun fragment -> error.Contains(fragment, StringComparison.Ordinal)))

let run (check: string -> bool -> unit) =
    // --- CON-F1 characterization: each embedded distribution kind -------------
    // The exact discovery reproduction: praxis/ordo requested below the
    // Registry release set, previously planned silently.
    let conF1 =
        planAgainst
            [ "praxis", "3.6.0", NativeHost; "ordo", "1.4.0", NativeHost ]
            [ "praxis", Some "3.1.4"; "ordo", Some "1.3.0" ]

    check
        "CON-F1 host-tool: embedded praxis requested below Registry authority is refused naming component, versions and source"
        (conF1 |> refusedWith [ "'praxis'"; "'3.1.4'"; "'3.6.0'"; AuthorityPath ])

    check
        "CON-F1 host-tool: embedded ordo requested below Registry authority is refused"
        (conF1 |> refusedWith [ "'ordo'"; "'1.3.0'"; "'1.4.0'"; AuthorityPath ])

    check
        "CON-F1 lifecycle-npm: embedded visual-engineering conflicting with Registry authority is refused"
        (planAgainst [ "visual-engineering", "2.0.0", NativeHost ] [ "visual-engineering", Some "1.0.0" ]
         |> refusedWith [ "'visual-engineering'"; "'1.0.0'"; "'2.0.0'"; AuthorityPath ])

    check
        "CON-F1 npm binding: embedded forma conflicting with Registry authority is refused"
        (planAgainst [ "forma", "0.3.0", WebBinding ] [ "forma", Some "0.2.0" ]
         |> refusedWith [ "'forma'"; "'0.2.0'"; "'0.3.0'"; AuthorityPath ])

    check
        "CON-F1 nuget binding: embedded aegis conflicting with Registry authority is refused"
        (planAgainst [ "aegis", "1.1.0", NugetBinding ] [ "aegis", Some "1.0.0" ]
         |> refusedWith [ "'aegis'"; "'1.0.0'"; "'1.1.0'"; AuthorityPath ])

    // --- Binding, not just refusal --------------------------------------------
    let unpinned =
        planAgainst [ "praxis", "3.6.0", NativeHost; "ordo", "1.3.0", NativeHost ] [ "praxis", None; "ordo", None ]

    check
        "unpinned embedded component binds to the Registry authority version, not the embedded default"
        (match unpinned with
         | Ok plan ->
             plan.Components
             |> List.exists (fun item -> item.Id = "ordo" && item.Version = "1.3.0")
         | Error _ -> false)

    check
        "embedded component matching the Registry authority plans at the authority version"
        (match planAgainst [ "praxis", "3.6.0", NativeHost ] [ "praxis", Some "3.6.0" ] with
         | Ok plan -> plan.Components |> List.exists (fun item -> item.Id = "praxis" && item.Version = "3.6.0")
         | Error _ -> false)

    check
        "embedded component absent from the declared Registry authority is refused rather than falling back to the embedded catalog"
        (planAgainst [ "praxis", "3.6.0", NativeHost ] [ "praxis", Some "3.6.0"; "ordo", Some "1.4.0" ]
         |> refusedWith [ "'ordo'"; "not selected by the declared Registry authority"; AuthorityPath ])

    // --- The bound version decides the package identity ------------------------
    // Limen 0.7.0 and later are distributed as @echelon-foundry/limen; 0.6.2 and earlier as
    // @echelon-foundry/typescript-wasm-kernel. The package follows the version
    // the authority selects, not the embedded default.
    let scaffoldedLimenAgainst (selected: string) =
        withTarget (fun target ->
            let digest = writeAuthority target [ "anchor", "1.0.0", NativeHost; "limen", selected, WebBinding ]

            (manifestJson digest [ "limen", None ])
                .Replace("\"execution\":{\"enabled\":false}", "\"execution\":{\"enabled\":false},\"scaffold\":{\"kind\":\"fsharp-limen-web\"}")
            |> Manifest.parseText
            |> Result.bind (Planner.create target Init))

    let boundLimenPackage selected =
        match scaffoldedLimenAgainst selected with
        | Ok plan ->
            plan.Components
            |> List.tryFind (fun item -> item.Id = "limen" && item.Version = selected)
            |> Option.map _.Package
        | Error _ -> None

    check
        "unpinned Limen bound by the Registry authority to 0.7.0 uses the @echelon-foundry/limen package"
        (boundLimenPackage "0.7.0" = Some "@echelon-foundry/limen")

    check
        "unpinned Limen bound by the Registry authority to 0.7.1 uses the @echelon-foundry/limen package"
        (boundLimenPackage "0.7.1" = Some "@echelon-foundry/limen")

    check
        "unpinned Limen bound by the Registry authority to 0.6.2 keeps the @echelon-foundry/typescript-wasm-kernel package"
        (boundLimenPackage "0.6.2" = Some "@echelon-foundry/typescript-wasm-kernel")

    // --- Typed decision (pure) --------------------------------------------------
    let source: RegistryAuthorityBinding.AuthoritySource =
        { Path = AuthorityPath
          Sha256 = String.replicate 64 "f"
          Profile = "property@1.0.0" }

    let authorityOf (selections: (string * string) list) : RegistryAuthorityBinding.RegistryAuthoritySet =
        { Source = source
          Selections =
            selections
            |> List.map (fun (id, version) ->
                id, ({ Id = id; Version = version; Role = "host-tool" }: RegistryAuthorityBinding.AuthoritySelection))
            |> Map.ofList }

    let request id version : ComponentRequest =
        { Id = id
          Version = version
          Required = true }

    let decides authority req (expected: RegistryAuthorityBinding.AuthorityDecision) =
        RegistryAuthorityBinding.decide authority req = expected

    check
        "authority conflict is a typed VersionConflict carrying component, requested version, authority version and source"
        (decides
            (Some(authorityOf [ "praxis", "3.6.0" ]))
            (request "praxis" (Some "3.1.4"))
            (RegistryAuthorityBinding.Refused(RegistryAuthorityBinding.VersionConflict("praxis", "3.1.4", "3.6.0", source))))

    // --- Systemic prevention: the rule holds for every embedded component -------
    // A newly embedded descriptor is covered automatically; it cannot bypass
    // the Registry authority without failing here.
    let conflicting (version: string) = $"{version}-registry-conflict"

    let catalogResults =
        Registry.all
        |> List.map (fun definition ->
            let id = definition.Id
            let embedded = definition.DefaultVersion
            let selected = conflicting embedded
            let authority = Some(authorityOf [ id, selected ])
            let absent = Some(authorityOf [ "unrelated-registry-component", "1.0.0" ])

            let selection: RegistryAuthorityBinding.AuthoritySelection =
                { Id = id; Version = selected; Role = "host-tool" }

            let ruleHolds =
                decides
                    authority
                    (request id (Some embedded))
                    (RegistryAuthorityBinding.Refused(RegistryAuthorityBinding.VersionConflict(id, embedded, selected, source)))
                && decides
                    authority
                    (request id None)
                    (RegistryAuthorityBinding.BoundToAuthority(request id (Some selected), selection))
                && decides
                    absent
                    (request id (Some embedded))
                    (RegistryAuthorityBinding.Refused(RegistryAuthorityBinding.AbsentFromAuthority(id, Some embedded, source)))
                && decides
                    None
                    (request id (Some embedded))
                    (RegistryAuthorityBinding.NoAuthorityDeclared(request id (Some embedded)))

            let role =
                match definition.Distribution with
                | HostTool
                | LifecycleNpm -> NativeHost
                | NpmPackage -> WebBinding
                | NugetPackage -> NugetBinding

            // End to end: the real planner must refuse the embedded default
            // when the authority selects a different version.
            let planned =
                planAgainst [ id, selected, role ] [ id, Some embedded ]
                |> refusedWith [ $"'{id}'"; $"'{embedded}'"; $"'{selected}'"; AuthorityPath ]

            id, ruleHolds && planned)

    check
        "every embedded component id is covered by the Registry authority binding property"
        (catalogResults |> List.map fst |> Set.ofList = (Registry.all |> List.map _.Id |> Set.ofList)
         && not catalogResults.IsEmpty)

    for id, holds in catalogResults do
        check $"Registry authority binding holds for embedded component '{id}' (pure rule and planner)" holds

    // --- Adoption cannot write a manifest the planner would refuse --------------
    withTarget (fun target ->
        let sourceDir = Path.Combine(target, "authority-source")
        Directory.CreateDirectory sourceDir |> ignore
        let digest = writeAuthority sourceDir [ "anchor", "1.0.0", NativeHost; "praxis", "3.1.4", NativeHost ]
        let sourcePath = Path.Combine(sourceDir, AuthorityPath)

        let praxisRunner _ (executable: string) (arguments: string list) =
            match Path.GetFileName executable, arguments with
            | "praxis", [ "--version" ] -> { ExitCode = 0; StandardOutput = "ros-fs 3.6.0"; StandardError = "" }
            | "praxis", [ "verify"; "--strict" ] -> { ExitCode = 0; StandardOutput = "healthy"; StandardError = "" }
            | _ -> { ExitCode = -1; StandardOutput = ""; StandardError = "missing" }

        match Adoption.loadRegistryAuthority target sourcePath digest with
        | Error error -> check $"adoption authority fixture loads: {error}" false
        | Ok authority ->
            let plan = Adoption.planWithAuthority praxisRunner target None (Some authority)

            check
                "adoption refuses an installed embedded component whose version conflicts with the Registry authority"
                (plan.Refusals
                 |> List.exists (fun refusal ->
                     [ "'praxis'"; "'3.6.0'"; "'3.1.4'"; AuthorityPath ]
                     |> List.forall (fun fragment -> refusal.Contains(fragment, StringComparison.Ordinal)))))
