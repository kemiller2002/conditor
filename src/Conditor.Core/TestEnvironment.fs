namespace Conditor.Core

open System
open System.Diagnostics
open System.IO
open System.Net
open System.Net.Http
open System.Net.Sockets
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.RegularExpressions

/// The standard test-environment contract and its preflight (CON-368..375,
/// EII Workstream E). One authoritative file per consuming repository,
/// `conditor.test-environment.json`, declares every endpoint, port, fixture,
/// build and runtime configuration, artifact identity, browser and diagnostics
/// location a browser test run depends on. `conditor test-env preflight`
/// observes the machine once and decides, before any browser starts, whether a
/// run could be trusted.
///
/// The decision is a pure function (`evaluate`: contract + observed facts ->
/// findings). Everything that touches the file system, the network or a
/// process lives in `observe`, a thin shell that only gathers facts.
module TestEnvironment =
    [<Literal>]
    let ContractSchema = "conditor.test-environment/v1"

    [<Literal>]
    let ReportSchema = "conditor.test-environment-report/v1"

    /// The one authoritative contract location, relative to the repository root.
    [<Literal>]
    let DefaultContractPath = "conditor.test-environment.json"

    // ------------------------------------------------------------------
    // Contract
    // ------------------------------------------------------------------

    type Fixture = { Path: string; Sha256: string }

    type Browser =
        { Name: string
          Executable: string option
          ExecutableVariable: string option
          Revision: string option }

    type BuildInfo =
        { Path: string
          ConfigurationField: string
          DigestField: string }

    type Contract =
        { ApplicationEndpoint: string option
          BaseUrl: string
          BaseUrlVariable: string option
          Port: int
          PortVariable: string option
          ReadinessPath: string
          BuildConfiguration: string
          BuildCommand: string option
          RuntimeConfiguration: string
          RuntimeEnvironment: (string * string) list
          ArtifactPath: string
          ArtifactServedAt: string
          ArtifactIdentityPath: string option
          ArtifactBuildInfo: BuildInfo option
          ArtifactSha256: string option
          ArtifactSha256Variable: string option
          Fixtures: Fixture list
          Browsers: Browser list
          DiagnosticsPath: string }

    let private sha256Pattern = Regex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant)

    let private property (name: string) (element: JsonElement) =
        let mutable value = Unchecked.defaultof<JsonElement>

        if element.ValueKind = JsonValueKind.Object && element.TryGetProperty(name, &value) then
            Some value
        else
            None

    /// Parses the contract. Every problem is reported, with the JSON location.
    let parse (text: string) : Result<Contract, string list> =
        let errors = ResizeArray<string>()

        let str (location: string) (element: JsonElement option) =
            match element with
            | Some value when value.ValueKind = JsonValueKind.String && not (String.IsNullOrWhiteSpace(value.GetString())) ->
                Some(value.GetString() |> nonNull)
            | _ -> None

        let required location element =
            match str location element with
            | Some value -> value
            | None ->
                errors.Add $"{location} must be a non-empty string."
                ""

        let optional location (element: JsonElement option) =
            match element with
            | None -> None
            | Some value when value.ValueKind = JsonValueKind.Null -> None
            | Some _ ->
                match str location element with
                | Some value -> Some value
                | None ->
                    errors.Add $"{location} must be a non-empty string or null."
                    None

        let digest location (value: string option) =
            value
            |> Option.iter (fun value ->
                if not (sha256Pattern.IsMatch value) then
                    errors.Add $"{location} must be a lowercase hex sha256 (64 characters).")

            value

        let section name (root: JsonElement) =
            match property name root with
            | Some value when value.ValueKind = JsonValueKind.Object -> value
            | _ ->
                errors.Add $"#/{name} must be an object."
                JsonDocument.Parse("{}").RootElement

        try
            use document = JsonDocument.Parse text
            let root = document.RootElement

            match str "#/schema" (property "schema" root) with
            | Some ContractSchema -> ()
            | other ->
                let found = other |> Option.defaultValue "(missing)"
                errors.Add $"#/schema must be '{ContractSchema}', not '{found}'."

            let application = section "application" root
            let test = section "test" root
            let build = section "build" root
            let runtime = section "runtime" root
            let artifact = section "artifact" root
            let diagnostics = section "diagnostics" root

            let port =
                match property "port" test with
                | Some value when value.ValueKind = JsonValueKind.Number ->
                    match value.TryGetInt32() with
                    | true, port when port > 0 && port < 65536 -> port
                    | _ ->
                        errors.Add "#/test/port must be an integer from 1 to 65535."
                        0
                | _ ->
                    errors.Add "#/test/port must be an integer from 1 to 65535."
                    0

            let runtimeEnvironment =
                match property "environment" runtime with
                | Some value when value.ValueKind = JsonValueKind.Object ->
                    [ for item in value.EnumerateObject() do
                          if item.Value.ValueKind = JsonValueKind.String then
                              item.Name, item.Value.GetString() |> nonNull
                          else
                              errors.Add $"#/runtime/environment/{item.Name} must be a string." ]
                | None -> []
                | Some _ ->
                    errors.Add "#/runtime/environment must be an object of strings."
                    []

            let buildInfo =
                match property "buildInfo" artifact with
                | None -> None
                | Some value when value.ValueKind = JsonValueKind.Null -> None
                | Some value when value.ValueKind = JsonValueKind.Object ->
                    Some
                        { Path = required "#/artifact/buildInfo/path" (property "path" value)
                          ConfigurationField =
                            optional "#/artifact/buildInfo/configurationField" (property "configurationField" value)
                            |> Option.defaultValue "configuration"
                          DigestField =
                            optional "#/artifact/buildInfo/digestField" (property "digestField" value)
                            |> Option.defaultValue "contentSha256" }
                | Some _ ->
                    errors.Add "#/artifact/buildInfo must be an object or null."
                    None

            let array name (element: JsonElement) =
                match property name element with
                | Some value when value.ValueKind = JsonValueKind.Array -> value.EnumerateArray() |> Seq.toList
                | _ ->
                    errors.Add $"#/{name} must be an array."
                    []

            let fixtures =
                array "fixtures" root
                |> List.mapi (fun index item ->
                    let location = $"#/fixtures/{index}"

                    { Path = required $"{location}/path" (property "path" item)
                      Sha256 =
                        required $"{location}/sha256" (property "sha256" item)
                        |> Some
                        |> digest $"{location}/sha256"
                        |> Option.defaultValue "" })

            let browsers =
                array "browsers" root
                |> List.mapi (fun index item ->
                    let location = $"#/browsers/{index}"

                    let browser =
                        { Name = required $"{location}/name" (property "name" item)
                          Executable = optional $"{location}/executable" (property "executable" item)
                          ExecutableVariable = optional $"{location}/executableVariable" (property "executableVariable" item)
                          Revision = optional $"{location}/revision" (property "revision" item) }

                    if browser.Executable.IsNone && browser.ExecutableVariable.IsNone then
                        errors.Add $"{location} must declare executable, executableVariable or both."

                    browser)

            let contract =
                { ApplicationEndpoint = optional "#/application/endpoint" (property "endpoint" application)
                  BaseUrl = required "#/test/baseUrl" (property "baseUrl" test)
                  BaseUrlVariable = optional "#/test/baseUrlVariable" (property "baseUrlVariable" test)
                  Port = port
                  PortVariable = optional "#/test/portVariable" (property "portVariable" test)
                  ReadinessPath = optional "#/test/readinessPath" (property "readinessPath" test) |> Option.defaultValue ""
                  BuildConfiguration = required "#/build/configuration" (property "configuration" build)
                  BuildCommand = optional "#/build/command" (property "command" build)
                  RuntimeConfiguration = required "#/runtime/configuration" (property "configuration" runtime)
                  RuntimeEnvironment = runtimeEnvironment
                  ArtifactPath = required "#/artifact/path" (property "path" artifact)
                  ArtifactServedAt =
                    optional "#/artifact/servedAt" (property "servedAt" artifact)
                    |> Option.defaultValue ""
                    |> fun value -> value.TrimStart('/')
                  ArtifactIdentityPath = optional "#/artifact/identityPath" (property "identityPath" artifact)
                  ArtifactBuildInfo = buildInfo
                  ArtifactSha256 = optional "#/artifact/sha256" (property "sha256" artifact) |> digest "#/artifact/sha256"
                  ArtifactSha256Variable = optional "#/artifact/sha256Variable" (property "sha256Variable" artifact)
                  Fixtures = fixtures
                  Browsers = browsers
                  DiagnosticsPath = required "#/diagnostics/path" (property "path" diagnostics) }

            if errors.Count = 0 then Ok contract else Error(List.ofSeq errors)
        with :? JsonException as error ->
            Error [ $"The contract is not valid JSON: {error.Message}" ]

    // ------------------------------------------------------------------
    // Effective settings (pure)
    // ------------------------------------------------------------------

    type Endpoint = { Uri: Uri; IsLocal: bool }

    let private loopbackHosts = set [ "127.0.0.1"; "localhost"; "::1"; "[::1]" ]

    let tryEndpoint (text: string) =
        match Uri.TryCreate(text, UriKind.Absolute) with
        | true, NonNull uri when uri.Scheme = Uri.UriSchemeHttp || uri.Scheme = Uri.UriSchemeHttps ->
            Some { Uri = uri; IsLocal = loopbackHosts.Contains(uri.Host.ToLowerInvariant()) }
        | _ -> None

    /// The environment variables a contract reads; the shell observes only these.
    let variables (contract: Contract) =
        [ yield! Option.toList contract.PortVariable
          yield! Option.toList contract.BaseUrlVariable
          yield! Option.toList contract.ArtifactSha256Variable
          for browser in contract.Browsers do
              yield! Option.toList browser.ExecutableVariable
          for name, _ in contract.RuntimeEnvironment do
              yield name ]
        |> List.distinct

    type Effective =
        { Port: Result<int, string>
          BaseUrl: Result<Endpoint, string> }

    /// The port and base URL a run will actually use: an environment override
    /// wins over the contract default, exactly as the consuming runner reads it.
    let effective (contract: Contract) (environment: Map<string, string>) =
        let port =
            match contract.PortVariable |> Option.bind (fun name -> environment.TryFind name |> Option.map (fun value -> name, value)) with
            | None -> Ok contract.Port
            | Some(name, value) ->
                match Int32.TryParse value with
                | true, port when port > 0 && port < 65536 -> Ok port
                | _ -> Error $"{name}='{value}' is not a TCP port."

        let baseUrl =
            match contract.BaseUrlVariable |> Option.bind (fun name -> environment.TryFind name |> Option.map (fun value -> name, value)) with
            | Some(name, value) ->
                tryEndpoint value |> Option.map Ok |> Option.defaultValue (Error $"{name}='{value}' is not an absolute http(s) URL.")
            | None ->
                match tryEndpoint contract.BaseUrl, port with
                | None, _ -> Error $"#/test/baseUrl '{contract.BaseUrl}' is not an absolute http(s) URL."
                | Some endpoint, Ok port when endpoint.IsLocal ->
                    let builder = UriBuilder(endpoint.Uri)
                    builder.Port <- port
                    Ok { endpoint with Uri = builder.Uri }
                | Some endpoint, _ -> Ok endpoint

        { Port = port; BaseUrl = baseUrl }

    /// The artifact-relative file whose served bytes prove which artifact a
    /// running server serves: `identityPath`, else the build-info file when it
    /// lives inside the artifact, else `index.html`.
    let identityPath (contract: Contract) =
        match contract.ArtifactIdentityPath with
        | Some path -> path
        | None ->
            let artifact = contract.ArtifactPath.TrimEnd('/') + "/"

            match contract.ArtifactBuildInfo with
            | Some info when info.Path.StartsWith(artifact, StringComparison.Ordinal) -> info.Path.Substring artifact.Length
            | _ -> "index.html"

    // ------------------------------------------------------------------
    // Observed facts
    // ------------------------------------------------------------------

    type FileFact =
        | FileMissing
        | FilePresent of sha256: string

    type BuildInfoFact =
        | BuildInfoNotDeclared
        | BuildInfoMissing
        | BuildInfoUnreadable of reason: string
        | BuildInfoRead of configuration: string option * digest: string option

    type ArtifactFact =
        { Exists: bool
          ContentSha256: string option
          BuildInfo: BuildInfoFact
          /// sha256 of the local identity file, when present.
          IdentitySha256: string option }

    type PortFact =
        | PortFree
        | PortInUse
        | PortNotLocal
        | PortUnusable of reason: string
        | PortNotChecked

    type ServedFact =
        | Served of status: int * sha256: string
        | NotServed of reason: string

    type ProbeFact =
        | ProbeAnswered of status: int
        | ProbeNoResponse of reason: string

    type ServerFact =
        { Identity: ServedFact
          /// None when the port did not serve the artifact over HTTP, so
          /// there is no artifact server to probe.
          Undecodable: ProbeFact option
          AliveAfterProbe: bool }

    type BrowserFact =
        { Name: string
          /// The path the run would launch, and where it came from.
          Candidate: (string * string) option
          Exists: bool
          /// None when the shared-library check is unavailable on this host.
          MissingLibraries: string list option
          LibraryCheckNote: string option
          Revision: string option }

    type Observations =
        { Environment: Map<string, string>
          Fixtures: (string * FileFact) list
          Artifact: ArtifactFact
          Port: PortFact
          Server: ServerFact option
          Browsers: BrowserFact list }

    // ------------------------------------------------------------------
    // Findings (pure)
    // ------------------------------------------------------------------

    type Severity =
        | Info
        | Warning
        | Error

    type Finding =
        { Code: string
          Severity: Severity
          /// What is wrong (or what was proved).
          What: string
          /// Where: the file, contract location, variable or endpoint.
          Where: string
          /// How to fix it; present on every warning and error.
          Fix: string option }

    let severityText =
        function
        | Info -> "info"
        | Warning -> "warning"
        | Error -> "error"

    let private ok code what where =
        { Code = code; Severity = Info; What = what; Where = where; Fix = None }

    let private fail code what where fix =
        { Code = code; Severity = Error; What = what; Where = where; Fix = Some fix }

    let private warn code what where fix =
        { Code = code; Severity = Warning; What = what; Where = where; Fix = Some fix }

    let private portOf (endpoint: Endpoint) = endpoint.Uri.Port

    let private configurationFindings (contract: Contract) (contractPath: string) (environment: Map<string, string>) =
        let code = "COND-TENV-CONFIG"
        let at pointer = $"{contractPath}{pointer}"
        let resolved = effective contract environment
        let portVariableName = contract.PortVariable |> Option.defaultValue "?"
        let portSource = contract.PortVariable |> Option.defaultValue "(test.port)"

        [ match tryEndpoint contract.BaseUrl with
          | Some endpoint when endpoint.IsLocal && portOf endpoint <> contract.Port ->
              fail
                  code
                  $"The contract disagrees with itself: test.baseUrl uses port {portOf endpoint} but test.port is {contract.Port}."
                  (at "#/test/baseUrl" + " and " + at "#/test/port")
                  "Make test.baseUrl's port equal test.port. The contract is the only place the test port is written; every config, spec and script must read it from there."
          | _ -> ()

          match resolved.Port with
          | Result.Error reason ->
              fail code reason $"environment variable {portVariableName}" "Unset the variable or set it to a free TCP port (1-65535)."
          | Ok port when port <> contract.Port ->
              warn
                  code
                  $"The port override is in effect: this run tests on port {port}, not the contract default {contract.Port}."
                  $"environment variable {portVariableName}"
                  "Expected for parallel worktrees. Every runner must read the same variable; unset it to use the contract default."
          | Ok _ -> ()

          match resolved.BaseUrl, resolved.Port, contract.BaseUrlVariable with
          | Result.Error reason, _, _ ->
              fail code reason (contract.BaseUrlVariable |> Option.map (sprintf "environment variable %s") |> Option.defaultValue (at "#/test/baseUrl")) "Give an absolute http(s) URL."
          | Ok endpoint, Ok port, Some variable when environment.ContainsKey variable && endpoint.IsLocal && portOf endpoint <> port ->
              fail
                  code
                  $"The overrides disagree: {variable} points at port {portOf endpoint} while the effective test port is {port}."
                  $"environment variables {variable} and {portSource}"
                  $"Set only one of them, or make {variable}'s port equal the test port."
          | _ -> ()

          match contract.ApplicationEndpoint with
          | None -> ()
          | Some text ->
              match tryEndpoint text, resolved.Port with
              | None, _ ->
                  fail code $"application.endpoint '{text}' is not an absolute http(s) URL." (at "#/application/endpoint") "Give an absolute http(s) URL, or null when the repository serves no separate application."
              | Some app, Ok port when app.IsLocal && portOf app = port ->
                  fail
                      code
                      $"Duplicate port: the application endpoint and the test site both use port {port}; one server would answer for both builds."
                      (at "#/application/endpoint" + " and " + at "#/test/port")
                      "Give the application (production) endpoint and the test site different ports in the contract."
              | _ -> ()

          for name, expected in contract.RuntimeEnvironment do
              match environment.TryFind name with
              | Some actual when actual <> expected ->
                  fail
                      code
                      $"Runtime configuration '{contract.RuntimeConfiguration}' needs {name}='{expected}', but the environment sets '{actual}'."
                      ("environment variable " + name + "; " + at ("#/runtime/environment/" + name))
                      $"Unset {name} (the runner applies the contract value) or set it to '{expected}'."
              | _ -> () ]
        |> fun findings ->
            if findings |> List.exists (fun finding -> finding.Severity = Error) then
                findings
            else
                ok code "Endpoints, ports, overrides and runtime configuration agree." contractPath :: findings

    let private fixtureFindings (contract: Contract) (contractPath: string) (observed: (string * FileFact) list) =
        let code = "COND-TENV-FIXTURE"

        let findings =
            contract.Fixtures
            |> List.mapi (fun index fixture ->
                match observed |> List.tryFind (fst >> (=) fixture.Path) |> Option.map snd with
                | None
                | Some FileMissing ->
                    Some(
                        fail
                            code
                            $"Missing fixture {fixture.Path}."
                            $"{fixture.Path} ({contractPath}#/fixtures/{index})"
                            "Restore the fixture from version control, or remove it from the contract if no test uses it."
                    )
                | Some(FilePresent actual) when actual <> fixture.Sha256 ->
                    Some(
                        fail
                            code
                            $"Fixture digest mismatch: {fixture.Path} is sha256 {actual}, the contract expects {fixture.Sha256}."
                            $"{fixture.Path} ({contractPath}#/fixtures/{index}/sha256)"
                            "Restore the expected fixture, or, when the change is intended, record its new sha256 in the contract in the same commit."
                    )
                | Some(FilePresent _) -> None)
            |> List.choose id

        if findings.IsEmpty then
            [ ok code $"{contract.Fixtures.Length} fixture(s) present with the expected digests." contractPath ]
        else
            findings

    let private expectedDigests (contract: Contract) (contractPath: string) (environment: Map<string, string>) (artifact: ArtifactFact) =
        [ match contract.ArtifactSha256 with
          | Some digest -> yield $"{contractPath}#/artifact/sha256", digest
          | None -> ()
          match contract.ArtifactSha256Variable |> Option.bind (fun name -> environment.TryFind name |> Option.map (fun value -> name, value)) with
          | Some(name, value) -> yield $"environment variable {name}", value.Trim().ToLowerInvariant()
          | None -> ()
          match contract.ArtifactBuildInfo, artifact.BuildInfo with
          | Some info, BuildInfoRead(_, Some digest) -> yield $"{info.Path} field '{info.DigestField}'", digest.Trim().ToLowerInvariant()
          | _ -> () ]

    let private artifactFindings (contract: Contract) (contractPath: string) (environment: Map<string, string>) (artifact: ArtifactFact) =
        let code = "COND-TENV-ARTIFACT"

        let rebuild =
            match contract.BuildCommand with
            | Some command -> $"Rebuild it with '{command}'"
            | None -> "Rebuild it"

        if not artifact.Exists then
            [ fail
                  code
                  $"The artifact under test does not exist: {contract.ArtifactPath}."
                  contract.ArtifactPath
                  $"{rebuild}, or download the exact candidate artifact the build job produced, before running browsers." ]
        else
            match expectedDigests contract contractPath environment artifact, artifact.ContentSha256 with
            | [], _ ->
                [ warn
                      code
                      "The artifact's identity is unpinned: no expected content digest is declared, so a stale or foreign build cannot be detected."
                      $"{contractPath}#/artifact"
                      "Have the build write its content digest to build-info (artifact.buildInfo.digestField), or pass the candidate's digest in artifact.sha256Variable." ]
            | _, None ->
                [ fail code "The artifact's content digest could not be computed." contract.ArtifactPath "Check the artifact's permissions." ]
            | expected, Some actual ->
                let disagreeing = expected |> List.filter (snd >> (<>) actual)

                if disagreeing.IsEmpty then
                    [ ok code $"The artifact is the expected candidate (content sha256 {actual})." contract.ArtifactPath ]
                else
                    [ for source, digest in disagreeing do
                          fail
                              code
                              $"Stale artifact: {contract.ArtifactPath} has content sha256 {actual}, but {source} expects {digest}."
                              $"{contract.ArtifactPath}; {source}"
                              $"Reuse the exact candidate artifact whose digest is {digest} (for example, download the build job's artifact), or {rebuild.ToLowerInvariant()}. The preflight refuses to test any other bytes." ]

    let private buildFindings (contract: Contract) (contractPath: string) (artifact: ArtifactFact) =
        let code = "COND-TENV-BUILD"

        match contract.ArtifactBuildInfo, artifact.BuildInfo with
        | _, _ when not artifact.Exists -> []
        | None, _
        | _, BuildInfoNotDeclared ->
            [ warn
                  code
                  $"The build configuration cannot be verified: the contract declares no build-info file for the artifact (expected '{contract.BuildConfiguration}')."
                  $"{contractPath}#/artifact/buildInfo"
                  "Have the build write a build-info JSON file recording its configuration, and declare it in artifact.buildInfo." ]
        | Some info, BuildInfoMissing ->
            [ fail code $"The artifact has no build-info file, so its build configuration is unknown." info.Path $"Rebuild the artifact in configuration '{contract.BuildConfiguration}'." ]
        | Some info, BuildInfoUnreadable reason ->
            [ fail code $"The build-info file cannot be read: {reason}" info.Path $"Rebuild the artifact in configuration '{contract.BuildConfiguration}'." ]
        | Some info, BuildInfoRead(None, _) ->
            [ fail code $"The build-info file has no '{info.ConfigurationField}' field." info.Path "Make the build record its configuration in build-info." ]
        | Some info, BuildInfoRead(Some actual, _) when actual <> contract.BuildConfiguration ->
            [ fail
                  code
                  $"Wrong build configuration: the artifact was built as '{actual}', the contract requires '{contract.BuildConfiguration}'."
                  $"{info.Path} field '{info.ConfigurationField}'"
                  (match contract.BuildCommand with
                   | Some command -> $"Rebuild with '{command}' (configuration '{contract.BuildConfiguration}')."
                   | None -> $"Rebuild in configuration '{contract.BuildConfiguration}'.") ]
        | Some info, BuildInfoRead(Some actual, _) ->
            [ ok code $"The artifact was built in the required configuration '{actual}'." info.Path ]

    let private portFindings (contract: Contract) (resolved: Effective) (artifact: ArtifactFact) (port: PortFact) (server: ServerFact option) =
        let code = "COND-TENV-PORT"
        let portText = resolved.Port |> Result.map string |> Result.defaultValue "?"
        let where = $"127.0.0.1:{portText}"

        let freeIt =
            let variable =
                match contract.PortVariable with
                | Some name -> $", or set {name} to a free port"
                | None -> ""

            $"Stop the process listening on {portText} (find it with 'ss -ltnp \"sport = :{portText}\"' or 'lsof -iTCP:{portText} -sTCP:LISTEN'){variable}."

        match port, server with
        | PortNotChecked, _ -> []
        | PortNotLocal, _ ->
            [ ok code "The base URL is not on this machine; local port and server probes do not apply." (resolved.BaseUrl |> Result.map (fun e -> string e.Uri) |> Result.defaultValue "") ]
        | PortFree, _ -> [ ok code $"Port {portText} is free; the runner will start the server for the artifact under test." where ]
        | PortUnusable reason, _ -> [ fail code $"Port {portText} is unavailable: {reason}" where freeIt ]
        | PortInUse, None -> [ fail code $"Port {portText} is occupied and its server could not be probed." where freeIt ]
        | PortInUse, Some server ->
            match server.Identity, artifact.IdentitySha256 with
            | Served(200, served), Some expected when served = expected ->
                [ ok code $"Port {portText} is already served by a server serving the expected artifact (identity sha256 {served}); reusing it is safe." where ]
            | Served(200, served), Some expected ->
                [ fail
                      code
                      $"Port {portText} is occupied by a server that is not serving the expected artifact: its {identityPath contract} is sha256 {served}, the artifact under test has {expected}. A reused server would test other bytes."
                      where
                      freeIt ]
            | Served(status, _), _ ->
                [ fail code $"Port {portText} is occupied by a server that answers {status} for the artifact identity file {identityPath contract}." where freeIt ]
            | NotServed reason, _ ->
                [ fail code $"Port {portText} is occupied by something that does not serve the artifact: {reason}" where freeIt ]

    let private serverFindings (server: ServerFact option) (resolved: Effective) =
        let code = "COND-TENV-SERVER"
        let where = resolved.BaseUrl |> Result.map (fun e -> string e.Uri) |> Result.defaultValue ""

        let fix =
            "Make the static server reject an undecodable path with 400 instead of throwing (for example, guard decodeURIComponent with try/catch), then restart it. A crashed server turns every later test into 'connection refused'."

        match server with
        | None -> [ ok code "No server is running on the test port; the undecodable-path probe was not needed." where ]
        | Some server ->
            match server.Undecodable, server.AliveAfterProbe with
            | None, _ -> []
            | Some(ProbeAnswered status), true when status < 500 ->
                [ ok code $"The running server answered an undecodable URL path with {status} and stayed up." where ]
            | Some(ProbeAnswered status), alive ->
                let after = if alive then "" else " and then stopped answering"
                [ fail code $"The running server answered an undecodable URL path with {status}{after}." where fix ]
            | Some(ProbeNoResponse reason), _ ->
                [ fail code $"The running server crashed or dropped the connection on an undecodable URL path: {reason}" where fix ]

    let private browserFindings (contract: Contract) (contractPath: string) (browsers: BrowserFact list) =
        let code = "COND-TENV-BROWSER"

        if contract.Browsers.IsEmpty then
            [ warn code "The contract declares no browser." $"{contractPath}#/browsers" "Declare each browser the suite launches, with its executable and expected revision." ]
        else
            contract.Browsers
            |> List.mapi (fun index declared ->
                let at = $"{contractPath}#/browsers/{index}"

                let install =
                    let variable =
                        match declared.ExecutableVariable with
                        | Some name -> $", or set {name} to an installed {declared.Name} executable"
                        | None -> ""

                    $"Install it with 'npx playwright install {declared.Name}'{variable}."

                match browsers |> List.tryFind (fun fact -> fact.Name = declared.Name) with
                | None
                | Some { Candidate = None } ->
                    [ fail code $"No {declared.Name} executable is configured for this run." at install ]
                | Some({ Candidate = Some(path, source) } as fact) ->
                    [ if not fact.Exists then
                          fail code $"Missing browser: {declared.Name} executable {path} (from {source}) does not exist." path install
                      else
                          match fact.MissingLibraries with
                          | Some((_ :: _) as missing) ->
                              let libraries = String.Join(", ", missing)
                              fail
                                  code
                                  $"{declared.Name} cannot start: missing shared libraries {libraries}."
                                  path
                                  $"Install the operating-system libraries with 'npx playwright install-deps {declared.Name}' (bounded, non-interactive), then re-run the preflight."
                          | Some [] -> ()
                          | None ->
                              let note = fact.LibraryCheckNote |> Option.defaultValue "no ldd on this host"

                              warn
                                  code
                                  $"The shared-library check is unavailable for {declared.Name}: {note}."
                                  path
                                  "Launch the browser once by hand to prove its libraries."

                          match declared.Revision, fact.Revision with
                          | None, _ -> ()
                          | Some expected, Some actual when expected = actual -> ()
                          | Some expected, Some actual ->
                              let executableName = declared.ExecutableVariable |> Option.defaultValue "the executable"

                              fail
                                  code
                                  $"Wrong {declared.Name} revision: {path} is revision {actual}, the contract requires {expected}."
                                  $"{path}; {at}/revision"
                                  $"Install revision {expected} (the Playwright version that ships it), point {executableName} at it, or update the contract with the Playwright upgrade."
                          | Some expected, None ->
                              warn
                                  code
                                  $"The {declared.Name} revision cannot be determined from {path} (expected {expected})."
                                  path
                                  "Use an executable inside a Playwright install directory (<name>-<revision>/...), whose revision is recorded in its path." ]
                    |> function
                        | [] ->
                            let revision = fact.Revision |> Option.map (sprintf ", revision %s") |> Option.defaultValue ""
                            [ ok code $"{declared.Name} is installed with its shared libraries ({path}{revision})." path ]
                        | findings -> findings)
            |> List.concat

    /// The pure core: contract + observed facts -> findings.
    let evaluate (contract: Contract) (contractPath: string) (observed: Observations) : Finding list =
        let resolved = effective contract observed.Environment

        [ yield! configurationFindings contract contractPath observed.Environment
          yield! portFindings contract resolved observed.Artifact observed.Port observed.Server
          yield! fixtureFindings contract contractPath observed.Fixtures
          yield! buildFindings contract contractPath observed.Artifact
          yield! browserFindings contract contractPath observed.Browsers
          yield! artifactFindings contract contractPath observed.Environment observed.Artifact
          yield! serverFindings observed.Server resolved ]

    let passed (findings: Finding list) =
        findings |> List.forall (fun finding -> finding.Severity <> Error)

    // ------------------------------------------------------------------
    // Pure helpers the shell uses
    // ------------------------------------------------------------------

    /// Libraries `ldd` reports as "not found".
    let missingLibraries (lddOutput: string) =
        lddOutput.Split('\n')
        |> Array.choose (fun line ->
            let trimmed = line.Trim()

            if trimmed.EndsWith("not found", StringComparison.Ordinal) then
                let parts = trimmed.Split([| "=>" |], StringSplitOptions.None)
                Some(parts[0].Trim())
            else
                None)
        |> Array.toList

    let private revisionDirectory = Regex("^[A-Za-z][A-Za-z_]*-(\\d+)$", RegexOptions.CultureInvariant)

    /// The Playwright browser revision recorded in an install path
    /// (`.../chromium-1243/chrome-linux64/chrome` -> 1243), when there is one.
    let revisionOf (path: string) =
        path.Split([| '/'; '\\' |], StringSplitOptions.RemoveEmptyEntries)
        |> Array.rev
        |> Array.tryPick (fun segment ->
            let found = revisionDirectory.Match segment
            if found.Success then Some found.Groups[1].Value else None)

    /// The artifact content digest: sha256 over `<sha256>  <relative path>\n`
    /// lines for every regular file, sorted by ordinal relative path with '/'
    /// separators, excluding the build-info file that records the digest.
    /// Reproducible with
    /// `find . -type f ! -path ./build-info.json | sed 's|^\./||' | LC_ALL=C sort | xargs -d '\n' sha256sum | sha256sum`.
    let contentDigest (entries: (string * string) list) =
        let text =
            entries
            |> List.sortWith (fun (left, _) (right, _) -> String.CompareOrdinal(left, right))
            |> List.map (fun (path, digest) -> $"{digest}  {path}\n")
            |> String.concat ""

        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes text)).ToLowerInvariant()

    // ------------------------------------------------------------------
    // The I/O shell
    // ------------------------------------------------------------------

    let private sha256File (path: string) =
        use stream = File.OpenRead path
        Convert.ToHexString(SHA256.HashData stream).ToLowerInvariant()

    let private relativeTo (root: string) (path: string) =
        Path.GetRelativePath(root, path).Replace('\\', '/')

    let private resolvePath (root: string) (relative: string) = Path.GetFullPath(Path.Combine(root, relative))

    let private artifactDigest (artifactRoot: string) (excluded: string option) =
        Directory.EnumerateFiles(artifactRoot, "*", SearchOption.AllDirectories)
        |> Seq.map (fun file -> relativeTo artifactRoot file, file)
        |> Seq.filter (fun (relative, _) -> Some relative <> excluded)
        |> Seq.map (fun (relative, file) -> relative, sha256File file)
        |> Seq.toList
        |> contentDigest

    /// The content digest of the contract's artifact, as `test-env digest` prints it.
    let computeArtifactDigest (root: string) (contract: Contract) =
        let artifactRoot = resolvePath root contract.ArtifactPath

        if Directory.Exists artifactRoot then
            let excluded =
                contract.ArtifactBuildInfo
                |> Option.map (fun info -> relativeTo artifactRoot (resolvePath root info.Path))

            Ok(artifactDigest artifactRoot excluded)
        elif File.Exists artifactRoot then
            Ok(sha256File artifactRoot)
        else
            Result.Error $"The artifact {contract.ArtifactPath} does not exist."

    let private observeArtifact (root: string) (contract: Contract) =
        let artifactRoot = resolvePath root contract.ArtifactPath
        let exists = Directory.Exists artifactRoot || File.Exists artifactRoot

        let buildInfo =
            match contract.ArtifactBuildInfo with
            | None -> BuildInfoNotDeclared
            | Some info ->
                let path = resolvePath root info.Path

                if not (File.Exists path) then
                    BuildInfoMissing
                else
                    try
                        use document = JsonDocument.Parse(File.ReadAllText path)

                        let field name =
                            match property name document.RootElement with
                            | Some value when value.ValueKind = JsonValueKind.String -> value.GetString() |> Option.ofObj
                            | _ -> None

                        BuildInfoRead(field info.ConfigurationField, field info.DigestField)
                    with error ->
                        BuildInfoUnreadable error.Message

        let identityFile = Path.Combine(artifactRoot, identityPath contract)

        { Exists = exists
          ContentSha256 = if exists then computeArtifactDigest root contract |> Result.toOption else None
          BuildInfo = buildInfo
          IdentitySha256 = if File.Exists identityFile then Some(sha256File identityFile) else None }

    let private loopbackAddress (endpoint: Endpoint) =
        if endpoint.Uri.Host.Contains ":" then IPAddress.IPv6Loopback else IPAddress.Loopback

    let private canConnect (address: IPAddress) (port: int) =
        use client = new TcpClient(address.AddressFamily)

        try
            client.ConnectAsync(address, port).Wait(TimeSpan.FromMilliseconds 500.0) && client.Connected
        with _ ->
            false

    let private observePort (endpoint: Endpoint) =
        if not endpoint.IsLocal then
            PortNotLocal
        else
            let address = loopbackAddress endpoint
            let port = endpoint.Uri.Port

            if canConnect address port then
                PortInUse
            else
                let listener = new TcpListener(address, port)

                try
                    try
                        listener.Start()
                        PortFree
                    with :? SocketException as error ->
                        PortUnusable $"{error.SocketErrorCode}: {error.Message}"
                finally
                    listener.Stop()

    let private client = lazy (new HttpClient(Timeout = TimeSpan.FromSeconds 2.0))

    let private get (uri: Uri) =
        try
            use response = client.Value.GetAsync(uri).GetAwaiter().GetResult()
            let bytes = response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult()
            Served(int response.StatusCode, Convert.ToHexString(SHA256.HashData bytes).ToLowerInvariant())
        with error ->
            NotServed(error.GetBaseException().Message)

    /// Sends a raw request for an undecodable percent-encoded path. A raw
    /// socket is used because HttpClient would re-encode the path.
    let private probeUndecodable (endpoint: Endpoint) =
        let path = endpoint.Uri.AbsolutePath.TrimEnd('/') + "/%E0%A4%A"

        try
            use client = new TcpClient(endpoint.Uri.Host.Trim('[', ']'), endpoint.Uri.Port)
            client.ReceiveTimeout <- 2000
            client.SendTimeout <- 2000
            use stream = client.GetStream()
            let request = $"GET {path} HTTP/1.1\r\nHost: {endpoint.Uri.Authority}\r\nConnection: close\r\n\r\n"
            let bytes = Encoding.ASCII.GetBytes request
            stream.Write(bytes, 0, bytes.Length)
            let buffer = Array.zeroCreate<byte> 256
            let read = stream.Read(buffer, 0, buffer.Length)

            if read <= 0 then
                ProbeNoResponse "the connection closed without a response"
            else
                let lines = Encoding.ASCII.GetString(buffer, 0, read).Split('\n')
                let statusLine = lines[0].Trim()

                match statusLine.Split(' ') with
                | [| _; status |]
                | [| _; status; _ |] ->
                    match Int32.TryParse status with
                    | true, code -> ProbeAnswered code
                    | _ -> ProbeNoResponse $"unparseable status line '{statusLine}'"
                | parts when parts.Length > 2 ->
                    match Int32.TryParse parts[1] with
                    | true, code -> ProbeAnswered code
                    | _ -> ProbeNoResponse $"unparseable status line '{statusLine}'"
                | _ -> ProbeNoResponse $"unparseable status line '{statusLine}'"
        with error ->
            ProbeNoResponse(error.GetBaseException().Message)

    let private observeServer (contract: Contract) (endpoint: Endpoint) =
        let under (relative: string) = Uri(endpoint.Uri, relative)
        let identity = get (under (contract.ArtifactServedAt + identityPath contract))

        match identity with
        | NotServed _ ->
            { Identity = identity
              Undecodable = None
              AliveAfterProbe = false }
        | Served _ ->
            let undecodable = probeUndecodable endpoint
            // A server that throws on the path may exit just after answering.
            Threading.Thread.Sleep 150

            let alive =
                canConnect (loopbackAddress endpoint) endpoint.Uri.Port
                && match get (under contract.ReadinessPath) with
                   | Served(status, _) -> status < 500
                   | NotServed _ -> false

            { Identity = identity
              Undecodable = Some undecodable
              AliveAfterProbe = alive }

    let private observeBrowser (root: string) (environment: Map<string, string>) (browser: Browser) =
        let candidate =
            match browser.ExecutableVariable |> Option.bind (fun name -> environment.TryFind name |> Option.map (fun value -> name, value)) with
            | Some(name, value) -> Some(value, $"environment variable {name}")
            | None -> browser.Executable |> Option.map (fun path -> resolvePath root path, "the contract")

        match candidate with
        | None ->
            { Name = browser.Name
              Candidate = None
              Exists = false
              MissingLibraries = None
              LibraryCheckNote = None
              Revision = None }
        | Some(path, _) when not (File.Exists path) ->
            { Name = browser.Name
              Candidate = candidate
              Exists = false
              MissingLibraries = None
              LibraryCheckNote = None
              Revision = revisionOf path }
        | Some(path, _) ->
            let real =
                try
                    match FileInfo(path).ResolveLinkTarget true with
                    | null -> path
                    | target -> target.FullName
                with _ ->
                    path

            let libraries, note =
                if not (OperatingSystem.IsLinux()) then
                    None, Some "the ldd check runs on Linux only"
                else
                    let result = ProcessRunner.runProcess root "ldd" [ real ]

                    if result.ExitCode = -1 then
                        None, Some "ldd is not installed"
                    else
                        Some(missingLibraries (result.StandardOutput + "\n" + result.StandardError)), None

            { Name = browser.Name
              Candidate = candidate
              Exists = true
              MissingLibraries = libraries
              LibraryCheckNote = note
              Revision = revisionOf real }

    /// Gathers every fact the contract needs. `environment` is the process
    /// environment (injected for tests).
    let observe (root: string) (contract: Contract) (environment: string -> string option) : Observations =
        let environment =
            variables contract
            |> List.choose (fun name -> environment name |> Option.map (fun value -> name, value))
            |> Map.ofList

        let resolved = effective contract environment

        let port =
            match resolved.BaseUrl, resolved.Port with
            | Ok endpoint, Ok _ -> observePort endpoint
            | _ -> PortNotChecked

        { Environment = environment
          Fixtures =
            contract.Fixtures
            |> List.map (fun fixture ->
                let path = resolvePath root fixture.Path
                fixture.Path, (if File.Exists path then FilePresent(sha256File path) else FileMissing))
          Artifact = observeArtifact root contract
          Port = port
          Server =
            match port, resolved.BaseUrl with
            | PortInUse, Ok endpoint -> Some(observeServer contract endpoint)
            | _ -> None
          Browsers = contract.Browsers |> List.map (observeBrowser root environment) }

    // ------------------------------------------------------------------
    // Report
    // ------------------------------------------------------------------

    type Report =
        { ContractPath: string
          Passed: bool
          ElapsedMilliseconds: int64
          DiagnosticsPath: string
          Findings: Finding list }

    let toJson (report: Report) =
        use stream = new MemoryStream()
        let mutable options = JsonWriterOptions()
        options.Indented <- true
        options.Encoder <- System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping

        (use writer = new Utf8JsonWriter(stream, options)
         writer.WriteStartObject()
         writer.WriteString("schema", ReportSchema)
         writer.WriteString("contract", report.ContractPath)
         writer.WriteBoolean("passed", report.Passed)
         writer.WriteNumber("elapsedMilliseconds", report.ElapsedMilliseconds)
         writer.WriteString("diagnostics", report.DiagnosticsPath)
         writer.WriteStartArray "findings"

         for finding in report.Findings do
             writer.WriteStartObject()
             writer.WriteString("code", finding.Code)
             writer.WriteString("severity", severityText finding.Severity)
             writer.WriteString("what", finding.What)
             writer.WriteString("where", finding.Where)

             match finding.Fix with
             | Some fix -> writer.WriteString("fix", fix)
             | None -> writer.WriteNull "fix"

             writer.WriteEndObject()

         writer.WriteEndArray()
         writer.WriteEndObject())

        Encoding.UTF8.GetString(stream.ToArray()) + "\n"

    let toText (report: Report) =
        let builder = StringBuilder()
        let verdict = if report.Passed then "passed" else "FAILED"
        builder.AppendLine $"Test-environment preflight {verdict} in {report.ElapsedMilliseconds} ms ({report.ContractPath})" |> ignore

        for finding in report.Findings do
            builder.AppendLine $"  [{severityText finding.Severity}] {finding.Code} {finding.What}" |> ignore
            builder.AppendLine $"      where: {finding.Where}" |> ignore

            finding.Fix |> Option.iter (fun fix -> builder.AppendLine $"      fix:   {fix}" |> ignore)

        builder.AppendLine $"Diagnostics: {report.DiagnosticsPath}" |> ignore
        builder.ToString()

    /// Loads the contract at `contractPath` (relative paths in it resolve
    /// against the contract's directory), observes, evaluates and, when
    /// `writeDiagnostics`, writes `preflight.json` and `preflight.txt` to the
    /// contract's diagnostics location.
    let preflight (contractPath: string) (environment: string -> string option) (writeDiagnostics: bool) =
        let stopwatch = Stopwatch.StartNew()
        let contractPath = Path.GetFullPath contractPath

        if not (File.Exists contractPath) then
            Result.Error [ $"No test-environment contract at {contractPath}. Create {DefaultContractPath} (schemas/conditor-test-environment.schema.json) or pass --contract PATH." ]
        else
            match parse (File.ReadAllText contractPath) with
            | Result.Error errors -> Result.Error(errors |> List.map (sprintf "%s: %s" contractPath))
            | Ok contract ->
                let root = Path.GetDirectoryName contractPath |> nonNull
                let observed = observe root contract environment
                let findings = evaluate contract (relativeTo root contractPath) observed
                let diagnostics = resolvePath root contract.DiagnosticsPath
                stopwatch.Stop()

                let report =
                    { ContractPath = contractPath
                      Passed = passed findings
                      ElapsedMilliseconds = stopwatch.ElapsedMilliseconds
                      DiagnosticsPath = diagnostics
                      Findings = findings }

                if writeDiagnostics then
                    Directory.CreateDirectory diagnostics |> ignore
                    File.WriteAllText(Path.Combine(diagnostics, "preflight.json"), toJson report)
                    File.WriteAllText(Path.Combine(diagnostics, "preflight.txt"), toText report)

                Ok report

    let processEnvironment (name: string) =
        Environment.GetEnvironmentVariable name |> Option.ofObj

    // ------------------------------------------------------------------
    // Resolved settings: the single source for consumer configs
    // ------------------------------------------------------------------

    /// The settings a browser run actually uses, after the contract's override
    /// variables are applied by `effective`, the same function the preflight
    /// uses. Consumer configs and scripts read these instead of re-implementing
    /// the override rules, so the port is never written twice
    /// (Echelon development speed optimization, Priority Six; WI-0059).
    type Resolved =
        { ContractPath: string
          TestPort: int
          TestBaseUrl: string
          ReadinessUrl: string
          Application: string option
          Browsers: (string * string option) list
          Runtime: (string * string) list
          Variables: (string * string) list
          Diagnostics: string }

    let ResolvedSchema = "conditor.test-environment-resolved/v1"

    /// `CONDITOR_BROWSER_<NAME>_EXECUTABLE` for a browser name.
    let browserVariable (name: string) =
        let upper = name.ToUpperInvariant() |> String.map (fun c -> if Char.IsAsciiLetterOrDigit c then c else '_')
        $"CONDITOR_BROWSER_{upper}_EXECUTABLE"

    /// Pure: the resolved settings, or the configuration findings that refuse
    /// them (exactly the errors the preflight would report as COND-TENV-CONFIG).
    let resolve (root: string) (contractPath: string) (contract: Contract) (environment: Map<string, string>) : Result<Resolved, Finding list> =
        let refusals =
            configurationFindings contract (relativeTo root contractPath) environment
            |> List.filter (fun finding -> finding.Severity = Error)

        match refusals, (effective contract environment) with
        | [], { Port = Ok port; BaseUrl = Ok endpoint } ->
            let baseUrl = endpoint.Uri.AbsoluteUri
            let readiness = Uri(endpoint.Uri, contract.ReadinessPath).AbsoluteUri

            let browsers =
                contract.Browsers
                |> List.map (fun browser ->
                    let fromVariable = browser.ExecutableVariable |> Option.bind environment.TryFind
                    browser.Name, fromVariable |> Option.orElse (browser.Executable |> Option.map (resolvePath root)))

            let variables =
                [ yield "CONDITOR_TEST_PORT", string port
                  yield "CONDITOR_TEST_BASE_URL", baseUrl
                  yield "CONDITOR_TEST_READINESS_URL", readiness
                  yield! contract.ApplicationEndpoint |> Option.map (fun app -> "CONDITOR_APPLICATION_ENDPOINT", app) |> Option.toList
                  // The contract's own override variables, so a runner that
                  // reads them sees the value the preflight checked.
                  yield! contract.PortVariable |> Option.map (fun name -> name, string port) |> Option.toList
                  yield! contract.BaseUrlVariable |> Option.map (fun name -> name, baseUrl) |> Option.toList
                  for (name, executable), browser in List.zip browsers contract.Browsers do
                      match executable with
                      | Some path ->
                          yield browserVariable name, path
                          yield! browser.ExecutableVariable |> Option.map (fun variable -> variable, path) |> Option.toList
                      | None -> ()
                  yield! contract.RuntimeEnvironment ]
                |> List.distinctBy fst

            Ok
                { ContractPath = contractPath
                  TestPort = port
                  TestBaseUrl = baseUrl
                  ReadinessUrl = readiness
                  Application = contract.ApplicationEndpoint
                  Browsers = browsers
                  Runtime = contract.RuntimeEnvironment
                  Variables = variables
                  Diagnostics = resolvePath root contract.DiagnosticsPath }
        | [], _ -> Result.Error(configurationFindings contract (relativeTo root contractPath) environment)
        | refusals, _ -> Result.Error refusals

    /// POSIX single-quoting: safe to `eval`.
    let shellQuote (value: string) = "'" + value.Replace("'", "'\\''") + "'"

    /// `export NAME='value'` lines for `eval "$(conditor test-env resolve --shell)"`.
    let resolvedToShell (resolved: Resolved) =
        resolved.Variables
        |> List.map (fun (name, value) -> $"export {name}={shellQuote value}\n")
        |> String.concat ""

    let resolvedToJson (resolved: Resolved) =
        use stream = new MemoryStream()
        let mutable options = JsonWriterOptions()
        options.Indented <- true
        options.Encoder <- System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping

        (use writer = new Utf8JsonWriter(stream, options)
         writer.WriteStartObject()
         writer.WriteString("schema", ResolvedSchema)
         writer.WriteString("contract", resolved.ContractPath)
         writer.WriteNumber("port", resolved.TestPort)
         writer.WriteString("baseUrl", resolved.TestBaseUrl)
         writer.WriteString("readinessUrl", resolved.ReadinessUrl)

         match resolved.Application with
         | Some app -> writer.WriteString("applicationEndpoint", app)
         | None -> writer.WriteNull "applicationEndpoint"

         writer.WriteStartArray "browsers"

         for name, executable in resolved.Browsers do
             writer.WriteStartObject()
             writer.WriteString("name", name)

             match executable with
             | Some path -> writer.WriteString("executable", path)
             | None -> writer.WriteNull "executable"

             writer.WriteEndObject()

         writer.WriteEndArray()
         writer.WriteStartObject "runtimeEnvironment"

         for name, value in resolved.Runtime do
             writer.WriteString(name, value)

         writer.WriteEndObject()
         writer.WriteStartObject "environment"

         for name, value in resolved.Variables do
             writer.WriteString(name, value)

         writer.WriteEndObject()
         writer.WriteString("diagnostics", resolved.Diagnostics)
         writer.WriteEndObject())

        Encoding.UTF8.GetString(stream.ToArray()) + "\n"

    /// Shell: loads the contract and resolves it against the process (or an
    /// injected) environment. Outer error: the contract cannot be read (exit 2);
    /// inner error: its configuration refuses a run (exit 3).
    let resolveContract (contractPath: string) (environment: string -> string option) =
        let contractPath = Path.GetFullPath contractPath

        if not (File.Exists contractPath) then
            Result.Error [ $"No test-environment contract at {contractPath}. Create {DefaultContractPath} (schemas/conditor-test-environment.schema.json) or pass --contract PATH." ]
        else
            match parse (File.ReadAllText contractPath) with
            | Result.Error errors -> Result.Error(errors |> List.map (sprintf "%s: %s" contractPath))
            | Ok contract ->
                let root = Path.GetDirectoryName contractPath |> nonNull

                let observed =
                    variables contract
                    |> List.choose (fun name -> environment name |> Option.map (fun value -> name, value))
                    |> Map.ofList

                Ok(resolve root contractPath contract observed)
