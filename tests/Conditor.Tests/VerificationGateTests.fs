module VerificationGateTests

open System
open System.IO
open System.Security.Cryptography
open System.Text.Json.Nodes
open Conditor.Core
open Conditor.Core.Workstation

// The split adoption gate (user decision "split the gate"): Conditor fails
// closed on Ordo installation-integrity failures and records SDE-STRUCT-001
// structural review findings instead of refusing on them.

let private result exitCode output error =
    { ExitCode = exitCode
      StandardOutput = output
      StandardError = error }

let private withTarget action =
    let target = Path.Combine(Path.GetTempPath(), $"conditor-gate-{Guid.NewGuid():N}")
    Directory.CreateDirectory target |> ignore

    try
        action target
    finally
        if Directory.Exists target then
            Directory.Delete(target, true)

let private joined (errors: string list) = String.concat "; " errors

let private gateArguments = [ "verify"; "--integrity-only"; "--json" ]

/// `ordo verify --integrity-only --json` as Ordo 1.4.2 emits it.
let private ordoReport (passed: bool) (failures: (string * string * string) list) (signals: (string * int) list) =
    let root = JsonObject()
    root["schemaVersion"] <- JsonValue.Create 1
    root["command"] <- JsonValue.Create "verify"
    root["mode"] <- JsonValue.Create "integrity-only"
    root["passed"] <- JsonValue.Create passed
    let failureArray = JsonArray()

    for category, code, detail in failures do
        let item = JsonObject()
        item["category"] <- JsonValue.Create category
        item["code"] <- JsonValue.Create code
        item["detail"] <- JsonValue.Create detail
        failureArray.Add item

    root["failures"] <- failureArray
    let signalArray = JsonArray()

    for path, lines in signals do
        let item = JsonObject()
        item["code"] <- JsonValue.Create "SDE-STRUCT-001"
        item["band"] <- JsonValue.Create(if lines > 2000 then "justification-required" else "review")
        item["path"] <- JsonValue.Create path
        item["lineCount"] <- JsonValue.Create lines
        signalArray.Add item

    root["reviewSignals"] <- signalArray
    root["exitCode"] <- JsonValue.Create(if passed then 0 else 1)
    root.ToJsonString()

let private healthyWithSignals =
    ordoReport true [] [ "src/Big.fs", 2957; "src/Medium.fs", 812 ]

let private ordoRunner (version: string) (verify: string list -> ProcessResult) _ (executable: string) (arguments: string list) =
    match Path.GetFileName executable, arguments with
    | "ordo", [ "--version" ] -> result 0 version ""
    | "ordo", arguments -> verify arguments
    | _ -> result -1 "" $"Unable to execute '{executable}'."

let private ordoDefinition () =
    Registry.tryFind "ordo" |> Option.defaultWith (fun () -> invalidOp "ordo descriptor missing")

let private fileSha256 path =
    use stream = File.OpenRead path
    Convert.ToHexString(SHA256.HashData stream).ToLowerInvariant()

/// A one-component resolved release set whose repository-lifecycle release
/// declares no lifecycle contract, as the Registry's percepta 0.1.0 does.
let private contractlessLifecycleSet target =
    let rid = Platform.runtimeIdentifier ()
    let systemId = "contractless-lifecycle"
    let version = "0.1.0"
    let repository = "example/contractless-lifecycle"
    let executable = $"contractless-lifecycle-{Guid.NewGuid():N}"
    let release = JsonObject()
    release["systemId"] <- JsonValue.Create systemId
    release["role"] <- JsonValue.Create "repository-lifecycle"
    release["required"] <- JsonValue.Create true
    release["version"] <- JsonValue.Create version
    release["repository"] <- JsonValue.Create repository
    release["tag"] <- JsonValue.Create $"{systemId}-v{version}"
    release["commit"] <- JsonValue.Create(String.replicate 40 "c")
    release["releaseStage"] <- JsonValue.Create "stable"
    release["lifecycleState"] <- JsonValue.Create "active"
    release["distributionClass"] <- JsonValue.Create "self-contained-native-cli"
    release["executable"] <- JsonValue.Create executable
    let distribution = JsonObject()
    distribution["mechanism"] <- JsonValue.Create "github-release"
    distribution["package"] <- (null: JsonNode | null)
    distribution["url"] <- JsonValue.Create $"https://github.com/{repository}/releases/tag/{systemId}-v{version}"
    release["distribution"] <- distribution
    let artifact = JsonObject()
    artifact["name"] <- JsonValue.Create $"{systemId}-{rid}"
    artifact["purpose"] <- JsonValue.Create "executable"
    artifact["platform"] <- JsonValue.Create rid
    artifact["sha256"] <- JsonValue.Create(String.replicate 64 "d")
    let artifacts = JsonArray()
    artifacts.Add artifact
    release["artifacts"] <- artifacts
    let root = JsonObject()
    root["schema"] <- JsonValue.Create "echelon.resolved-release-set/v1"
    let profile = JsonObject()
    profile["id"] <- JsonValue.Create "contractless-proof"
    profile["version"] <- JsonValue.Create "0.1.0"
    profile["sha256"] <- JsonValue.Create(String.replicate 64 "a")
    root["profile"] <- profile
    root["platform"] <- JsonValue.Create rid
    let catalog = JsonObject()
    catalog["sha256"] <- JsonValue.Create(String.replicate 64 "b")
    root["catalogSnapshot"] <- catalog
    let components = JsonArray()
    components.Add release
    root["components"] <- components
    let source = Path.Combine(target, "..", $"conditor-contractless-{Guid.NewGuid():N}.json") |> Path.GetFullPath
    File.WriteAllText(source, root.ToJsonString())
    source, fileSha256 source, systemId, executable

let private gateSelection check =
    let ordo = ordoDefinition ()

    check
        "Ordo 1.4.2 is qualified and verified through the integrity-only gate"
        (Registry.qualifiedVersions "ordo" |> Option.exists (Set.contains "1.4.2")
         && VerificationGate.argumentsFor "1.4.2" ordo = gateArguments)

    check
        "Ordo releases without the integrity gate keep the fail-closed strict verify"
        (VerificationGate.argumentsFor "1.4.1" ordo = [ "verify"; "--strict" ]
         && VerificationGate.argumentsFor "1.4.0" ordo = [ "verify"; "--strict" ])

    check
        "components without an integrity gate keep their verify arguments"
        (match Registry.tryFind "praxis" with
         | Some praxis -> VerificationGate.argumentsFor praxis.DefaultVersion praxis = praxis.VerifyArguments
         | None -> false)

let private interpretation check =
    let ordo = ordoDefinition ()
    let interpret = VerificationGate.interpret "ordo" "1.4.2" ordo

    match interpret (result 0 healthyWithSignals "") with
    | Ok signals ->
        check
            "a passing integrity gate yields its structural review signals"
            (signals
             |> List.map (fun signal -> signal.ComponentId, signal.Code, signal.Path, signal.LineCount)
             |> (=) [ ("ordo", "SDE-STRUCT-001", "src/Big.fs", 2957); ("ordo", "SDE-STRUCT-001", "src/Medium.fs", 812) ])
    | Error errors -> check $"a passing integrity gate yields its structural review signals: {joined errors}" false

    let damaged =
        ordoReport false [ "integrity", "managed-file-modified", "reference/GLOSSARY.md has been modified locally" ] [ "src/Big.fs", 2957 ]

    check
        "an integrity failure is refused with its code"
        (match interpret (result 1 damaged "") with
         | Error errors -> errors |> List.exists (fun error -> error.Contains "managed-file-modified")
         | Ok _ -> false)

    check
        "a version or pin mismatch is refused"
        (match interpret (result 1 (ordoReport false [ "integrity", "toolchain-pin-mismatch", "pin differs" ] []) "") with
         | Error errors -> errors |> List.exists (fun error -> error.Contains "toolchain-pin-mismatch")
         | Ok _ -> false)

    check
        "a zero exit whose report is not JSON fails closed"
        (interpret (result 0 "SDE v1.4.2 verified." "") |> Result.isError)

    check
        "a zero exit whose report lists an integrity failure fails closed"
        (interpret (result 0 (ordoReport true [ "integrity", "version-mismatch", "x" ] []) "") |> Result.isError)

    check
        "a zero exit whose report does not claim to pass fails closed"
        (interpret (result 0 (ordoReport false [] []) "") |> Result.isError)

    check
        "a failing exit fails closed even if the report claims to pass"
        (interpret (result 1 healthyWithSignals "") |> Result.isError)

    let strictOnly = VerificationGate.interpret "ordo" "1.4.1" ordo

    check
        "a release without the gate passes only on exit zero and records no signals"
        (strictOnly (result 0 "SDE v1.4.1 verified." "") = Ok []
         && strictOnly (result 1 "Strict verification failed" "") |> Result.isError)

let private adoption check =
    withTarget (fun target ->
        let runner =
            ordoRunner "1.4.2" (fun arguments ->
                if arguments = gateArguments then result 0 healthyWithSignals ""
                else result 9 "" $"unexpected arguments {arguments}")

        let plan = Adoption.planWith runner target None

        check "adoption accepts intact Ordo 1.4.2 despite structural findings" (plan.Components |> List.exists (fun c -> c.Id = "ordo" && c.Version = "1.4.2"))
        check "adoption with only review signals has no refusal" plan.Refusals.IsEmpty
        check "adoption plan surfaces the structural review signals" (plan.ReviewSignals |> List.map _.Path = [ "src/Big.fs"; "src/Medium.fs" ])

        check
            "the Ordo observation reports the review signal count"
            (plan.Observations
             |> List.exists (fun o -> o.ComponentId = "ordo" && o.Status = "verified" && o.Detail.Contains "2 structural review signal"))

        match Adoption.applyWith runner target None plan.Digest with
        | Error errors -> check $"adoption with review signals applies: {joined errors}" false
        | Ok adopted ->
            let lock = JsonNode.Parse(File.ReadAllText adopted.LockPath)

            let recorded =
                match lock with
                | null -> []
                | node ->
                    match node["reviewSignals"] with
                    | :? JsonArray as signals ->
                        signals
                        |> Seq.choose (fun signal ->
                            match signal with
                            | null -> None
                            | item ->
                                match item["lineCount"] with
                                | null -> None
                                | lines -> Some(string item["component"], string item["path"], lines.GetValue<int>()))
                        |> List.ofSeq
                    | _ -> []

            check
                "the adoption lock records the structural review signals"
                (recorded = [ "ordo", "src/Big.fs", 2957; "ordo", "src/Medium.fs", 812 ]))

    withTarget (fun target ->
        let runner =
            ordoRunner "1.4.2" (fun _ ->
                result 1 (ordoReport false [ "integrity", "managed-file-missing", "reference/GLOSSARY.md is missing" ] []) "")

        let plan = Adoption.planWith runner target None

        check "adoption refuses an Ordo integrity failure" (plan.Refusals |> List.exists (fun r -> r.Contains "'ordo'"))
        check "an integrity failure is not adopted" (plan.Components |> List.forall (fun c -> c.Id <> "ordo"))
        check "the refusal names the integrity failure" (plan.Observations |> List.exists (fun o -> o.ComponentId = "ordo" && o.Detail.Contains "managed-file-missing")))

    withTarget (fun target ->
        let runner =
            ordoRunner "1.4.1" (fun arguments ->
                if arguments = [ "verify"; "--strict" ] then result 1 "Strict verification failed" ""
                else result 9 "" "unexpected")

        let plan = Adoption.planWith runner target None

        check "an Ordo release without the gate still fails closed on strict verify" (plan.Refusals |> List.exists (fun r -> r.Contains "'ordo'")))

    withTarget (fun target ->
        let runner =
            ordoRunner "1.4.2" (fun arguments ->
                if arguments = gateArguments then result 0 healthyWithSignals ""
                else result 9 "" "unexpected")

        let first = Adoption.planWith runner target None

        let changed =
            ordoRunner "1.4.2" (fun _ -> result 0 (ordoReport true [] [ "src/Big.fs", 3001 ]) "")

        let second = Adoption.planWith changed target None

        check "the adoption digest binds the observed review signals" (first.Digest <> second.Digest))

let private contractlessLifecycle check =
    withTarget (fun target ->
        let source, digest, systemId, executable = contractlessLifecycleSet target

        try
            match Adoption.loadRegistryAuthority target source digest with
            | Error error -> check $"contractless lifecycle set loads: {error}" false
            | Ok authority ->
                let absent _ _ _ = result -1 "" "not found"
                let plan = Adoption.planWithAuthority absent target None (Some authority)

                check
                    "an uninstalled lifecycle release without a contract is reported, not refused"
                    (plan.Observations |> List.exists (fun o -> o.ComponentId = systemId && o.Status = "not-found")
                     && plan.Refusals |> List.forall (fun r -> not (r.Contains systemId)))

                let present _ (command: string) (arguments: string list) =
                    if Path.GetFileName command = executable then result 0 "contractless-lifecycle 0.1.0" ""
                    else result -1 "" "not found"

                let installed = Adoption.planWithAuthority present target None (Some authority)

                check
                    "an installed lifecycle release without a contract is still refused"
                    (installed.Refusals |> List.exists (fun r -> r.Contains systemId && r.Contains "lifecycle contract"))
        finally
            if File.Exists source then File.Delete source)

/// `conditor init` runs the same gate and records its review signals.
let private initialization check =
    withTarget (fun target ->
        if OperatingSystem.IsWindows() then
            check "init integrity gate fixture is skipped on Windows" true
        else
            let bin = Path.Combine(target, ".fake-bin")
            Directory.CreateDirectory bin |> ignore
            let ordo = Path.Combine(bin, "ordo")
            let report = healthyWithSignals.Replace("'", "'\\''")

            File.WriteAllText(
                ordo,
                String.concat
                    "\n"
                    [ "#!/bin/sh"
                      "case \"$*\" in"
                      "  --version) echo 1.4.2 ;;"
                      "  init) exit 0 ;;"
                      $"  'verify --integrity-only --json') echo '{report}' ;;"
                      "  *) echo \"unexpected: $*\" >&2; exit 9 ;;"
                      "esac"
                      "" ]
            )

            File.SetUnixFileMode(ordo, UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute)
            let manifestPath = Path.Combine(target, "conditor.json")
            File.WriteAllText(manifestPath, """{"schemaVersion":1,"name":"gate","components":[{"id":"ordo","version":"1.4.2"}]}""")
            let previous = Environment.GetEnvironmentVariable "PATH"

            try
                Environment.SetEnvironmentVariable("PATH", bin + string Path.PathSeparator + previous)

                match Manifest.load manifestPath |> Result.bind (Planner.create target Init) with
                | Error errors -> check $"init plan for Ordo 1.4.2 succeeds: {joined errors}" false
                | Ok plan ->
                    check
                        "init verifies Ordo 1.4.2 through the integrity gate"
                        (plan.Actions
                         |> List.exists (fun action ->
                             action.ComponentId = "ordo"
                             && action.Kind = IntegrityVerifyLifecycle
                             && action.Execution = ExternalProcess("ordo", gateArguments)))

                    match Installer.execute target manifestPath plan with
                    | Error errors -> check $"init passes the integrity gate despite review signals: {joined errors}" false
                    | Ok lockPath ->
                        let text = lockPath |> Option.map File.ReadAllText |> Option.defaultValue ""
                        check "init records review signals in the lock" (text.Contains "reviewSignals" && text.Contains "src/Big.fs")
            finally
                Environment.SetEnvironmentVariable("PATH", previous))

let run check =
    gateSelection check
    interpretation check
    adoption check
    contractlessLifecycle check
    initialization check
