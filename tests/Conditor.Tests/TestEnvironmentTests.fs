module TestEnvironmentTests

// The test-environment contract and its preflight (CON-368..375, EII
// Workstream E). Each of the seven failure classes Rehearsal 5 hit or could
// have hit is reproduced on a real fixture (files, sockets, an HTTP server
// and a browser executable), next to a passing control. Every scenario's
// preflight wall-clock time is measured and printed as a TIMING line.

open System
open System.Diagnostics
open System.IO
open System.Net
open System.Net.Sockets
open System.Security.Cryptography
open System.Text
open System.Threading
open Conditor.Core

let private joined (values: string list) = String.concat "; " values

let private sha256Bytes (bytes: byte array) =
    Convert.ToHexString(SHA256.HashData bytes).ToLowerInvariant()

let private sha256Text (text: string) = sha256Bytes (Encoding.UTF8.GetBytes text)

let private write (root: string) (relative: string) (text: string) =
    let path = Path.Combine(root, relative)
    Directory.CreateDirectory(Path.GetDirectoryName path |> nonNull) |> ignore
    File.WriteAllText(path, text)

let private freePort () =
    let listener = new TcpListener(IPAddress.Loopback, 0)
    listener.Start()
    let port = (listener.LocalEndpoint :?> IPEndPoint).Port
    listener.Stop()
    port

// ----------------------------------------------------------------------
// A minimal HTTP server with the behaviours the scenarios need
// ----------------------------------------------------------------------

type private ServerMode =
    /// Serves the artifact; an undecodable path gets 400.
    | Faithful
    /// Serves other bytes (another worktree's build).
    | Foreign
    /// Answers 500 for an undecodable path.
    | ServerError
    /// Drops the connection and stops listening on an undecodable path,
    /// as R5's serve.mjs did when decodeURIComponent threw.
    | Crashes

let private undecodable (path: string) =
    try
        let bytes = ResizeArray<byte>()
        let mutable index = 0

        while index < path.Length do
            if path[index] = '%' then
                bytes.Add(Convert.ToByte(path.Substring(index + 1, 2), 16))
                index <- index + 3
            else
                bytes.Add(byte path[index])
                index <- index + 1

        UTF8Encoding(false, true).GetString(bytes.ToArray()) |> ignore
        false
    with _ ->
        true

type private TestServer(port: int, root: string, mode: ServerMode) =
    let listener = new TcpListener(IPAddress.Loopback, port)
    let mutable running = true

    let respond (stream: NetworkStream) (status: int) (body: byte array) =
        let head = $"HTTP/1.1 {status} X\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n"
        let bytes = Encoding.ASCII.GetBytes head
        stream.Write(bytes, 0, bytes.Length)
        stream.Write(body, 0, body.Length)

    let handle (client: TcpClient) =
        use client = client
        use stream = client.GetStream()
        let buffer = Array.zeroCreate<byte> 4096
        let read = stream.Read(buffer, 0, buffer.Length)
        let lines = Encoding.ASCII.GetString(buffer, 0, read).Split('\n')
        let parts = lines[0].Trim().Split(' ')
        let path = parts[1].TrimStart('/')

        if undecodable path then
            match mode with
            | Crashes ->
                running <- false
                listener.Stop()
            | ServerError -> respond stream 500 (Encoding.UTF8.GetBytes "boom")
            | _ -> respond stream 400 (Encoding.UTF8.GetBytes "bad path")
        else
            let file = Path.Combine(root, (if path = "" then "index.html" else Uri.UnescapeDataString path))

            match mode with
            | Foreign -> respond stream 200 (Encoding.UTF8.GetBytes $"another build of {path}")
            | _ when File.Exists file -> respond stream 200 (File.ReadAllBytes file)
            | _ -> respond stream 404 [||]

    let loop () =
        while running do
            try
                let client = listener.AcceptTcpClient()

                try
                    handle client
                with _ ->
                    ()
            with _ ->
                running <- false

    do
        listener.Start()
        Thread(loop, IsBackground = true).Start()

    interface IDisposable with
        member _.Dispose() =
            running <- false
            listener.Stop()

// ----------------------------------------------------------------------
// The fixture repository
// ----------------------------------------------------------------------

type private Fixture =
    { Root: string
      TestPort: int
      ApplicationPort: int
      Browser: string }

let private buildInfo (configuration: string) (digest: string) =
    $"{{\"commit\":\"0000000\",\"configuration\":\"{configuration}\",\"contentSha256\":\"{digest}\"}}\n"

/// The artifact digest computed independently of the code under test.
let private referenceDigest (artifact: string) =
    Directory.EnumerateFiles(artifact, "*", SearchOption.AllDirectories)
    |> Seq.map (fun file -> Path.GetRelativePath(artifact, file).Replace('\\', '/'), file)
    |> Seq.filter (fun (relative, _) -> relative <> "build-info.json")
    |> Seq.sortWith (fun (a, _) (b, _) -> String.CompareOrdinal(a, b))
    |> Seq.map (fun (relative, file) -> $"{sha256Bytes (File.ReadAllBytes file)}  {relative}\n")
    |> String.concat ""
    |> sha256Text

/// A browser executable that exists, resolves its shared libraries and sits
/// in a Playwright revision directory: a copy of a real system binary, so the
/// fixture needs no browser download. `patchLibc` renames its libc
/// dependency, so `ldd` reports a missing shared library.
let private fakeBrowser (root: string) (revision: string) (patchLibc: bool) =
    let directory = Path.Combine(root, "pw-browsers", $"chromium-{revision}", "chrome-linux64")
    Directory.CreateDirectory directory |> ignore
    let path = Path.Combine(directory, "chrome")
    let source = [ "/usr/bin/true"; "/bin/true" ] |> List.tryFind File.Exists |> Option.defaultValue "/bin/true"
    let bytes = File.ReadAllBytes source

    let bytes =
        if patchLibc then
            let text = Encoding.Latin1.GetString bytes
            Encoding.Latin1.GetBytes(text.Replace("libc.so.6", "libq.so.6"))
        else
            bytes

    File.WriteAllBytes(path, bytes)

    if not (OperatingSystem.IsWindows()) then
        File.SetUnixFileMode(path, UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute)

    path

let private contractText (fixture: Fixture) (fixtureDigest: string) =
    $$"""{
  "schema": "conditor.test-environment/v1",
  "application": { "endpoint": "http://127.0.0.1:{{fixture.ApplicationPort}}/" },
  "test": {
    "baseUrl": "http://127.0.0.1:{{fixture.TestPort}}/",
    "baseUrlVariable": "FIXTURE_BASE_URL",
    "port": {{fixture.TestPort}},
    "portVariable": "FIXTURE_TEST_PORT",
    "readinessPath": "index.html"
  },
  "build": { "configuration": "test", "command": "npm run build:test" },
  "runtime": { "configuration": "fixture", "environment": { "FIXTURE_MODE": "1" } },
  "artifact": {
    "path": "obj/site-test",
    "buildInfo": { "path": "obj/site-test/build-info.json" },
    "sha256": null,
    "sha256Variable": "FIXTURE_CANDIDATE_SHA256"
  },
  "fixtures": [ { "path": "fixtures/state.json", "sha256": "{{fixtureDigest}}" } ],
  "browsers": [
    { "name": "chromium", "executable": "{{fixture.Browser}}", "executableVariable": "FIXTURE_CHROMIUM_PATH", "revision": "1243" }
  ],
  "diagnostics": { "path": "TestResults/test-environment" }
}
"""

/// A repository whose environment matches its contract.
let private withFixture (action: Fixture -> unit) =
    let root = Path.Combine(Path.GetTempPath(), $"conditor-tenv-{Guid.NewGuid():N}")
    Directory.CreateDirectory root |> ignore

    try
        let artifact = Path.Combine(root, "obj", "site-test")
        write artifact "index.html" "<!doctype html><title>site</title>\n"
        write artifact "app.js" "export const ready = true;\n"
        write artifact "assets/style.css" "body { margin: 0 }\n"
        write artifact "build-info.json" (buildInfo "test" (referenceDigest artifact))
        write root "fixtures/state.json" "{\"state\":\"seeded\"}\n"

        let testPort = freePort ()

        let applicationPort =
            Seq.initInfinite (fun _ -> freePort ()) |> Seq.find (fun port -> port <> testPort)

        let fixture =
            { Root = root
              TestPort = testPort
              ApplicationPort = applicationPort
              Browser = fakeBrowser root "1243" false }

        write root TestEnvironment.DefaultContractPath (contractText fixture (sha256Text "{\"state\":\"seeded\"}\n"))
        action fixture
    finally
        if Directory.Exists root then
            Directory.Delete(root, true)

let private noEnvironment (_: string) : string option = None

let private environmentOf (values: (string * string) list) =
    let map = Map.ofList values
    fun name -> map.TryFind name

/// Runs the preflight and measures its wall-clock time.
let private timed (label: string) (fixture: Fixture) environment =
    let stopwatch = Stopwatch.StartNew()
    let result = TestEnvironment.preflight (Path.Combine(fixture.Root, TestEnvironment.DefaultContractPath)) environment true
    stopwatch.Stop()
    Console.WriteLine $"TIMING test-env preflight [{label}] {stopwatch.Elapsed.TotalMilliseconds:F1} ms"
    result, stopwatch.Elapsed

let private errorsWith (code: string) (report: TestEnvironment.Report) =
    report.Findings
    |> List.filter (fun finding -> finding.Code = code && finding.Severity = TestEnvironment.Error)

let private hasError code (fragment: string) report =
    errorsWith code report |> List.exists (fun finding -> finding.What.Contains fragment)

let private actionable (report: TestEnvironment.Report) =
    report.Findings
    |> List.filter (fun finding -> finding.Severity <> TestEnvironment.Info)
    |> List.forall (fun finding -> finding.Fix.IsSome && finding.Where <> "" && finding.What <> "")

/// The seven intentional failures plus the controls. `validateReport`
/// checks the written diagnostics against the report schema.
let private scenarios (check: string -> bool -> unit) (validateReport: string -> Result<unit, string list>) =
    let fastEnough (elapsed: TimeSpan) = elapsed < TimeSpan.FromSeconds 15.0

    let expectFailure label code fragment (fixture: Fixture) environment =
        match timed label fixture environment with
        | Error errors, _ -> check $"{label}: the preflight runs ({joined errors})" false
        | Ok report, elapsed ->
            check $"{label}: the preflight fails" (not report.Passed)
            check $"{label}: {code} names the failure ('{fragment}')" (hasError code fragment report)
            check $"{label}: every finding says what, where and how to fix it" (actionable report)
            check $"{label}: the preflight fails fast ({elapsed.TotalMilliseconds:F0} ms)" (fastEnough elapsed)

            let diagnostics = Path.Combine(report.DiagnosticsPath, "preflight.json")

            check
                $"{label}: the diagnostics are written to the contract's location and conform to the report schema"
                (File.Exists diagnostics
                 && File.Exists(Path.Combine(report.DiagnosticsPath, "preflight.txt"))
                 && validateReport (File.ReadAllText diagnostics) = Ok())

    // Control: nothing listening, everything as declared.
    withFixture (fun fixture ->
        match timed "control: no server running" fixture noEnvironment with
        | Error errors, _ -> check $"control: the preflight runs ({joined errors})" false
        | Ok report, elapsed ->
            let errors = report.Findings |> List.filter (fun finding -> finding.Severity = TestEnvironment.Error)
            let details = errors |> List.map (fun finding -> finding.What) |> String.concat " | "
            check $"control: a matching environment passes ({details})" report.Passed
            check "control: the report records its own elapsed time" (report.ElapsedMilliseconds >= 0L)
            check $"control: the preflight is fast ({elapsed.TotalMilliseconds:F0} ms)" (fastEnough elapsed)

            check
                "control: the artifact is proved to be the expected candidate"
                (report.Findings |> List.exists (fun finding -> finding.Code = "COND-TENV-ARTIFACT" && finding.What.StartsWith "The artifact is the expected candidate"))

            check "control: the undecodable-path probe is skipped when no server runs" (report.Findings |> List.exists (fun finding -> finding.Code = "COND-TENV-SERVER" && finding.Severity = TestEnvironment.Info))

            let json = File.ReadAllText(Path.Combine(report.DiagnosticsPath, "preflight.json"))
            check "control: preflight.json conforms to conditor-test-environment-report.schema.json" (validateReport json = Ok())
            check "control: the JSON rendering conforms too" (validateReport (TestEnvironment.toJson report) = Ok()))

    // Control: a faithful server is already serving the exact artifact; the
    // exact candidate digest is supplied, and the runtime variable agrees.
    withFixture (fun fixture ->
        use _server = new TestServer(fixture.TestPort, Path.Combine(fixture.Root, "obj", "site-test"), Faithful)
        let digest = referenceDigest (Path.Combine(fixture.Root, "obj", "site-test"))

        match timed "control: faithful server reused" fixture (environmentOf [ "FIXTURE_CANDIDATE_SHA256", digest; "FIXTURE_MODE", "1" ]) with
        | Error errors, _ -> check $"control (server): the preflight runs ({joined errors})" false
        | Ok report, _ ->
            let details =
                report.Findings
                |> List.filter (fun finding -> finding.Severity = TestEnvironment.Error)
                |> List.map (fun finding -> finding.What)
                |> String.concat " | "

            check $"control (server): a server serving the expected artifact passes ({details})" report.Passed

            check
                "control (server): reuse of the running server is proved safe by artifact identity"
                (report.Findings |> List.exists (fun finding -> finding.Code = "COND-TENV-PORT" && finding.What.Contains "reusing it is safe"))

            check
                "control (server): the server survives the undecodable-path probe"
                (report.Findings |> List.exists (fun finding -> finding.Code = "COND-TENV-SERVER" && finding.What.Contains "stayed up")))

    // 1. Port occupied by a server not serving the expected artifact.
    withFixture (fun fixture ->
        use _server = new TestServer(fixture.TestPort, Path.Combine(fixture.Root, "obj", "site-test"), Foreign)
        expectFailure "1 port occupied by a foreign server" "COND-TENV-PORT" "not serving the expected artifact" fixture noEnvironment)

    // 1b. Port unavailable: something holds it that is not an HTTP server.
    withFixture (fun fixture ->
        let holder = new TcpListener(IPAddress.Loopback, fixture.TestPort)
        holder.Start()

        try
            expectFailure "1b port held by a non-HTTP listener" "COND-TENV-PORT" "occupied" fixture noEnvironment
        finally
            holder.Stop())

    // 2. Conflicting configuration: the base URL override disagrees with the
    // port override (R5: a config-only port move the specs did not follow).
    withFixture (fun fixture ->
        let other = fixture.TestPort + 1

        expectFailure
            "2 conflicting configuration (override disagreement)"
            "COND-TENV-CONFIG"
            "The overrides disagree"
            fixture
            (environmentOf [ "FIXTURE_BASE_URL", $"http://127.0.0.1:{fixture.TestPort - 1}/"; "FIXTURE_TEST_PORT", string other ]))

    // 2b. Conflicting configuration: application and test site share a port;
    // and the runtime variable is set to another value.
    withFixture (fun fixture ->
        expectFailure
            "2b conflicting configuration (duplicate port)"
            "COND-TENV-CONFIG"
            "Duplicate port"
            fixture
            (environmentOf [ "FIXTURE_TEST_PORT", string fixture.ApplicationPort ]))

    withFixture (fun fixture ->
        expectFailure
            "2c conflicting configuration (runtime variable)"
            "COND-TENV-CONFIG"
            "FIXTURE_MODE='1'"
            fixture
            (environmentOf [ "FIXTURE_MODE", "0" ]))

    // 3. Missing fixture, and a fixture whose digest changed.
    withFixture (fun fixture ->
        File.Delete(Path.Combine(fixture.Root, "fixtures", "state.json"))
        expectFailure "3 missing fixture" "COND-TENV-FIXTURE" "Missing fixture" fixture noEnvironment)

    withFixture (fun fixture ->
        write fixture.Root "fixtures/state.json" "{\"state\":\"edited\"}\n"
        expectFailure "3b fixture digest mismatch" "COND-TENV-FIXTURE" "Fixture digest mismatch" fixture noEnvironment)

    // 4. Wrong build configuration: a production build where the contract
    // requires the test build.
    withFixture (fun fixture ->
        let artifact = Path.Combine(fixture.Root, "obj", "site-test")
        write artifact "build-info.json" (buildInfo "production" (referenceDigest artifact))
        expectFailure "4 wrong build configuration" "COND-TENV-BUILD" "Wrong build configuration" fixture noEnvironment)

    // 5. Missing browser, missing shared libraries, wrong revision.
    withFixture (fun fixture ->
        expectFailure
            "5 missing browser"
            "COND-TENV-BROWSER"
            "Missing browser"
            fixture
            (environmentOf [ "FIXTURE_CHROMIUM_PATH", Path.Combine(fixture.Root, "pw-browsers", "absent", "chrome") ]))

    if OperatingSystem.IsLinux() then
        withFixture (fun fixture ->
            let broken = fakeBrowser (Path.Combine(fixture.Root, "broken")) "1243" true

            expectFailure
                "5b missing shared libraries"
                "COND-TENV-BROWSER"
                "missing shared libraries libq.so.6"
                fixture
                (environmentOf [ "FIXTURE_CHROMIUM_PATH", broken ]))
    else
        Console.WriteLine "SKIP 5b missing shared libraries: the ldd check runs on Linux only"

    withFixture (fun fixture ->
        let older = fakeBrowser (Path.Combine(fixture.Root, "older")) "1200" false

        expectFailure
            "5c wrong browser revision"
            "COND-TENV-BROWSER"
            "Wrong chromium revision"
            fixture
            (environmentOf [ "FIXTURE_CHROMIUM_PATH", older ]))

    // 6. Stale artifact: the bytes changed after the build recorded its
    // digest; and a candidate digest that is not the local artifact's.
    withFixture (fun fixture ->
        write (Path.Combine(fixture.Root, "obj", "site-test")) "app.js" "export const ready = false;\n"
        expectFailure "6 stale artifact (bytes changed after build)" "COND-TENV-ARTIFACT" "Stale artifact" fixture noEnvironment)

    withFixture (fun fixture ->
        expectFailure
            "6b stale artifact (not the exact candidate)"
            "COND-TENV-ARTIFACT"
            "environment variable FIXTURE_CANDIDATE_SHA256 expects"
            fixture
            (environmentOf [ "FIXTURE_CANDIDATE_SHA256", String('a', 64) ]))

    // 7. A running server that 5xx's, or crashes, on an undecodable URL path.
    withFixture (fun fixture ->
        use _server = new TestServer(fixture.TestPort, Path.Combine(fixture.Root, "obj", "site-test"), ServerError)
        expectFailure "7 server answers 5xx on an undecodable path" "COND-TENV-SERVER" "with 500" fixture noEnvironment)

    withFixture (fun fixture ->
        use _server = new TestServer(fixture.TestPort, Path.Combine(fixture.Root, "obj", "site-test"), Crashes)
        expectFailure "7b server crashes on an undecodable path" "COND-TENV-SERVER" "crashed or dropped the connection" fixture noEnvironment)

/// Pure-core rules: parsing, effective settings, ldd and revision parsing and
/// the reproducible content digest.
let private units (check: string -> bool -> unit) (validateContract: string -> Result<unit, string list>) =
    check
        "ldd output: 'not found' lines name the missing libraries"
        (TestEnvironment.missingLibraries "\tlinux-vdso.so.1 (0x0)\n\tlibnss3.so => not found\n\tlibc.so.6 => /lib/libc.so.6 (0x1)\n\tlibgbm.so.1 => not found\n" = [ "libnss3.so"; "libgbm.so.1" ])

    check "revision: read from the Playwright install directory" (TestEnvironment.revisionOf "/opt/pw-browsers/chromium-1243/chrome-linux64/chrome" = Some "1243")
    check "revision: headless shell directories count too" (TestEnvironment.revisionOf "/x/chromium_headless_shell-1187/chrome-headless-shell-linux64/chrome-headless-shell" = Some "1187")
    check "revision: unavailable, never invented, when the path records none" (TestEnvironment.revisionOf "/usr/bin/chromium" = None)

    check "a contract with the wrong schema id is refused" (TestEnvironment.parse """{"schema":"other"}""" |> Result.isError)
    check "a contract that is not JSON is refused" (TestEnvironment.parse "{" |> Result.isError)

    let sample =
        { Root = "/repo"
          TestPort = 4822
          ApplicationPort = 4821
          Browser = "/opt/pw-browsers/chromium" }

    let text = contractText sample (String('0', 64))
    check "the fixture contract conforms to conditor-test-environment.schema.json" (validateContract text = Ok())

    check
        "the schema rejects a contract without diagnostics"
        (validateContract (text.Replace("\"diagnostics\": { \"path\": \"TestResults/test-environment\" }", "\"unknown\": 1")) |> Result.isError)

    match TestEnvironment.parse text with
    | Error errors -> check $"the fixture contract parses ({joined errors})" false
    | Ok contract ->
        let resolved = TestEnvironment.effective contract (Map.ofList [ "FIXTURE_TEST_PORT", "4900" ])
        check "an environment port override is the effective port" (resolved.Port = Ok 4900)
        check "the effective base URL follows the port override" (resolved.BaseUrl |> Result.exists (fun endpoint -> endpoint.Uri.Port = 4900))
        check "a non-numeric override is refused" (TestEnvironment.effective contract (Map.ofList [ "FIXTURE_TEST_PORT", "x" ])).Port.IsError
        check "the identity file defaults to the build-info inside the artifact" (TestEnvironment.identityPath contract = "build-info.json")

        let selfConflict =
            TestEnvironment.parse (text.Replace("\"port\": 4822", "\"port\": 4823"))
            |> Result.map (fun contract ->
                TestEnvironment.evaluate
                    contract
                    "c.json"
                    { Environment = Map.empty
                      Fixtures = []
                      Artifact =
                        { Exists = true
                          ContentSha256 = Some(String('0', 64))
                          BuildInfo = TestEnvironment.BuildInfoRead(Some "test", Some(String('0', 64)))
                          IdentitySha256 = None }
                      Port = TestEnvironment.PortFree
                      Server = None
                      Browsers = [] })

        check
            "pure core: a base URL port that is not test.port is a conflict"
            (selfConflict
             |> Result.exists (List.exists (fun finding -> finding.Code = "COND-TENV-CONFIG" && finding.What.Contains "disagrees with itself")))

    let digestRoot = Path.Combine(Path.GetTempPath(), $"conditor-tenv-digest-{Guid.NewGuid():N}")

    try
        write digestRoot "b.txt" "b\n"
        write digestRoot "a/c.txt" "c\n"
        write digestRoot "build-info.json" "{}\n"
        check "the content digest matches the documented reference algorithm" (TestEnvironment.contentDigest [ "b.txt", sha256Text "b\n"; "a/c.txt", sha256Text "c\n" ] = referenceDigest digestRoot)

        // The documented shell recipe reproduces the digest without Conditor.
        if OperatingSystem.IsLinux() && File.Exists "/bin/bash" then
            let info = ProcessStartInfo("/bin/bash")
            info.ArgumentList.Add "-c"
            info.ArgumentList.Add "find . -type f ! -path ./build-info.json | sed 's|^\\./||' | LC_ALL=C sort | xargs -d '\\n' sha256sum | sha256sum"
            info.WorkingDirectory <- digestRoot
            info.RedirectStandardOutput <- true
            use proc = Process.Start info |> nonNull
            let output = proc.StandardOutput.ReadToEnd()
            proc.WaitForExit()
            let printed = output.Split(' ')
            check "the documented sha256sum recipe reproduces the content digest" (printed[0].Trim() = referenceDigest digestRoot)
    finally
        Directory.Delete(digestRoot, true)

/// Doctor aggregation (CON-374): nothing without a contract; the preflight's
/// findings, as COND-DOC-TENV-* codes, with one.
let private doctor (check: string -> bool -> unit) =
    let empty = Path.Combine(Path.GetTempPath(), $"conditor-tenv-none-{Guid.NewGuid():N}")
    Directory.CreateDirectory empty |> ignore

    try
        check "doctor adds no test-environment finding to a repository without a contract" (Doctor.testEnvironmentFindings empty noEnvironment = [])
    finally
        Directory.Delete(empty, true)

    withFixture (fun fixture ->
        File.Delete(Path.Combine(fixture.Root, "fixtures", "state.json"))
        let findings = Doctor.testEnvironmentFindings fixture.Root noEnvironment

        check
            "doctor reports a preflight failure as COND-DOC-TENV-FIXTURE with remediation"
            (findings
             |> List.exists (fun finding -> finding.Code = "COND-DOC-TENV-FIXTURE" && finding.Severity = Doctor.Error && finding.Remediation.IsSome))

        check "doctor's test-environment codes fit the doctor schema's code pattern" (findings |> List.forall (fun finding -> finding.Code.StartsWith "COND-DOC-TENV-"))

        check
            "doctor stays read-only: no diagnostics are written"
            (not (Directory.Exists(Path.Combine(fixture.Root, "TestResults")))))

/// The real Chromium in this environment, when present: proves the browser
/// check on an actual Playwright install. Unavailable elsewhere, and said so.
let private realBrowser (check: string -> bool -> unit) =
    let installed = "/opt/pw-browsers/chromium"

    if File.Exists installed && OperatingSystem.IsLinux() then
        withFixture (fun fixture ->
            match timed "control: real Playwright Chromium" fixture (environmentOf [ "FIXTURE_CHROMIUM_PATH", installed ]) with
            | Ok report, _ ->
                let browser = report.Findings |> List.filter (fun finding -> finding.Code = "COND-TENV-BROWSER")
                let details = browser |> List.map (fun finding -> finding.What) |> String.concat " | "
                check $"the real Chromium install resolves with its libraries and revision ({details})" (browser |> List.forall (fun finding -> finding.Severity = TestEnvironment.Info))
            | Error errors, _ -> check $"the real Chromium preflight runs ({joined errors})" false)
    else
        Console.WriteLine "SKIP real Chromium control: /opt/pw-browsers/chromium is not installed on this host"

let run (check: string -> bool -> unit) (validateContract: string -> Result<unit, string list>) (validateReport: string -> Result<unit, string list>) =
    units check validateContract
    scenarios check validateReport
    doctor check
    realBrowser check
