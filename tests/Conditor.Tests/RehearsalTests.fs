module RehearsalTests

open System
open System.IO
open System.Text.Json
open Conditor.Core

let private ids =
    [ "c6b26614-fc43-5013-a15c-cf95942ee07b"
      "e3adbee5-5096-5a10-bbfb-cecda86a154b"
      "1784da3c-7141-59ee-a495-1d5d44bf2284" ]

let private idsJson = ids |> List.map (sprintf "\"%s\"") |> String.concat ","
let private demonstrationIds = ids @ [ "fc3f71a9-4c14-5a31-8196-7ce17a7041c1" ]
let private demonstrationIdsJson = demonstrationIds |> List.map (sprintf "\"%s\"") |> String.concat ","
let private digest = String.replicate 64 "a"
let private commit = String.replicate 40 "b"
let private sourceCommit = String.replicate 40 "c"

let private outputForTemplate templateIdentifier investigationIdsJson = function
    | Rehearsal.Stage.VerifyTemplate ->
        $"{{\"templateIdentifier\":\"{templateIdentifier}\",\"templateVersion\":\"1.0.0\",\"templateDigest\":\"{digest}\",\"sourceRepository\":\"kemiller2002/causa-data\",\"sourceCommit\":\"{sourceCommit}\",\"investigationIdentifiers\":[{investigationIdsJson}],\"result\":\"VERIFIED\"}}"
    | Rehearsal.Stage.CheckLocalDestination ->
        "{\"workspaceIdentifier\":\"rehearsal-006\",\"localDestinationResult\":\"ABSENT\"}"
    | Rehearsal.Stage.CheckRemoteDestination ->
        "{\"workspaceIdentifier\":\"rehearsal-006\",\"repository\":\"kemiller2002/causa-data\",\"branch\":\"main\",\"remoteDestinationResult\":\"ABSENT\"}"
    | Rehearsal.Stage.CreateWorkspace
    | Rehearsal.Stage.ValidateWorkspace ->
        $"{{\"workspaceIdentifier\":\"rehearsal-006\",\"templateDigest\":\"{digest}\",\"investigationIdentifiers\":[{investigationIdsJson}],\"verificationResult\":\"VERIFIED\"}}"
    | Rehearsal.Stage.PublishWorkspace ->
        $"{{\"workspaceIdentifier\":\"rehearsal-006\",\"repository\":\"kemiller2002/causa-data\",\"branch\":\"main\",\"publishedCommit\":\"{commit}\",\"templateDigest\":\"{digest}\",\"investigationIdentifiers\":[{investigationIdsJson}],\"publicationResult\":\"PUBLISHED\"}}"
    | Rehearsal.Stage.VerifyRemotePublication ->
        $"{{\"workspaceIdentifier\":\"rehearsal-006\",\"repository\":\"kemiller2002/causa-data\",\"branch\":\"main\",\"publishedCommit\":\"{commit}\",\"templateDigest\":\"{digest}\",\"investigationIdentifiers\":[{investigationIdsJson}],\"remoteVerificationResult\":\"REMOTE_VERIFIED\"}}"
    | Rehearsal.Stage.VerifyApplicationCompatibility ->
        $"{{\"workspaceIdentifier\":\"rehearsal-006\",\"investigationIdentifiers\":[{investigationIdsJson}],\"applicationCompatibilityResult\":\"APPLICATION_VERIFIED\"}}"
    | Rehearsal.Stage.ResolveConfiguration -> "{}"

let private outputFor = outputForTemplate "historical-v1" idsJson

let private withFixture (action: string -> Rehearsal.Config -> unit) =
    let root = Path.Combine(Path.GetTempPath(), $"conditor-rehearsal-{Guid.NewGuid():N}")
    let causa = Path.Combine(root, "causa-data")
    let project = Path.Combine(causa, "tools", "src", "Causa.Tool", "Causa.Tool.fsproj")
    Directory.CreateDirectory(Path.GetDirectoryName project |> nonNull) |> ignore
    File.WriteAllText(project, "<Project />")
    let config: Rehearsal.Config =
        { Id = "006"
          Application = "causa"
          Workspace =
            { Repository = "kemiller2002/causa-data"
              Branch = "main"
              Template = "historical-v1"
              Name = "rehearsal-006"
              Checkout = causa
              ToolProject = "tools/src/Causa.Tool/Causa.Tool.fsproj"
              DotnetExecutable = "dotnet"
              Create = true
              Publish = true
              Verify = true }
          ReceiptPath = Path.Combine(root, ".conditor", "evidence", "rehearsal-006-readiness.json") }
    try action root config
    finally if Directory.Exists root then Directory.Delete(root, true)

let private fixedClock () = DateTimeOffset(2026, 10, 28, 18, 0, 0, TimeSpan.Zero)

let private jsonAt (path: string) (name: string) =
    use document = JsonDocument.Parse(File.ReadAllText path)
    document.RootElement.GetProperty(name).GetString()

let run check validateReceipt =
    withFixture (fun _ config ->
        let calls = ResizeArray<Rehearsal.ToolInvocation>()
        let runner (command: Rehearsal.ToolInvocation) =
            calls.Add command
            { ExitCode = 0; StandardOutput = outputFor command.Stage; StandardError = "" }
        match Rehearsal.initialize fixedClock runner config.Workspace.Checkout config with
        | Error error -> check $"successful Causa rehearsal preflight: {error}" false
        | Ok receipt ->
            check "successful preflight returns READY" (receipt.Verdict = "READY")
            check "successful preflight records six evidence-backed readiness transitions" (receipt.Transitions |> List.map _.State = [ "LOCAL_CREATED"; "LOCAL_VERIFIED"; "PUBLISHED"; "REMOTE_VERIFIED"; "APPLICATION_VERIFIED"; "READY" ])
            check "successful preflight binds the published commit" (receipt.PublishedCommit = Some commit)
            check "successful preflight writes its machine-readable receipt" (File.Exists receipt.Path && jsonAt receipt.Path "overallReadinessVerdict" = "READY")
            check "successful preflight receipt conforms to its schema" (validateReceipt (File.ReadAllText receipt.Path) = Ok())
            check
                "Causa commands run in mandatory order"
                (calls |> Seq.map _.Stage |> Seq.toList =
                    [ Rehearsal.Stage.VerifyTemplate
                      Rehearsal.Stage.CheckLocalDestination
                      Rehearsal.Stage.CheckRemoteDestination
                      Rehearsal.Stage.CreateWorkspace
                      Rehearsal.Stage.ValidateWorkspace
                      Rehearsal.Stage.PublishWorkspace
                      Rehearsal.Stage.VerifyRemotePublication
                      Rehearsal.Stage.VerifyApplicationCompatibility ]))

    let failingStages =
        [ Rehearsal.Stage.VerifyTemplate
          Rehearsal.Stage.CheckLocalDestination
          Rehearsal.Stage.CheckRemoteDestination
          Rehearsal.Stage.CreateWorkspace
          Rehearsal.Stage.ValidateWorkspace
          Rehearsal.Stage.PublishWorkspace
          Rehearsal.Stage.VerifyRemotePublication
          Rehearsal.Stage.VerifyApplicationCompatibility ]

    for failing in failingStages do
        withFixture (fun _ config ->
            let calls = ResizeArray<Rehearsal.ToolInvocation>()
            let runner (command: Rehearsal.ToolInvocation) =
                calls.Add command
                if command.Stage = failing then { ExitCode = 1; StandardOutput = ""; StandardError = "github_pat_must_not_escape" }
                else { ExitCode = 0; StandardOutput = outputFor command.Stage; StandardError = "" }
            let result = Rehearsal.initialize fixedClock runner config.Workspace.Checkout config
            check $"{failing} blocks readiness" (Result.isError result && jsonAt config.ReceiptPath "overallReadinessVerdict" = "BLOCKED")
            check $"{failing} BLOCKED receipt conforms to its schema" (validateReceipt (File.ReadAllText config.ReceiptPath) = Ok())
            check $"{failing} stops later stages" (calls[calls.Count - 1].Stage = failing)
            check $"{failing} does not persist provider credential output" (not (File.ReadAllText(config.ReceiptPath).Contains "github_pat_must_not_escape")))

    withFixture (fun _ config ->
        let runner (command: Rehearsal.ToolInvocation) =
            if command.Stage = Rehearsal.Stage.ValidateWorkspace then { ExitCode = 0; StandardOutput = "{}"; StandardError = "" }
            else { ExitCode = 0; StandardOutput = outputFor command.Stage; StandardError = "" }
        let result = Rehearsal.initialize fixedClock runner config.Workspace.Checkout config
        check "missing evidence blocks readiness" (Result.isError result && jsonAt config.ReceiptPath "overallReadinessVerdict" = "BLOCKED"))

    withFixture (fun _ config ->
        let runner (command: Rehearsal.ToolInvocation) =
            let output = outputFor command.Stage
            let mismatched = if command.Stage = Rehearsal.Stage.VerifyTemplate then output.Replace("kemiller2002/causa-data", "another-owner/causa-data") else output
            { ExitCode = 0; StandardOutput = mismatched; StandardError = "" }
        let result = Rehearsal.initialize fixedClock runner config.Workspace.Checkout config
        check "wrong source repository blocks readiness" (Result.isError result && jsonAt config.ReceiptPath "failureStage" = "VERIFY_TEMPLATE"))

    withFixture (fun _ config ->
        let configured = { config with Workspace = { config.Workspace with Template = "demonstration-v1" } }
        let runner (command: Rehearsal.ToolInvocation) =
            { ExitCode = 0
              StandardOutput = outputForTemplate "demonstration-v1" demonstrationIdsJson command.Stage
              StandardError = "" }
        match Rehearsal.initialize fixedClock runner configured.Workspace.Checkout configured with
        | Error error -> check $"combined demonstration template reaches READY: {error}" false
        | Ok receipt ->
            check "combined demonstration template reaches READY" (receipt.Verdict = "READY")
            check "combined demonstration receipt binds all four investigations" (receipt.InvestigationIds = demonstrationIds && validateReceipt (File.ReadAllText receipt.Path) = Ok()))

    withFixture (fun _ config ->
        let runner (command: Rehearsal.ToolInvocation) =
            let output = outputFor command.Stage
            let mismatched =
                if command.Stage = Rehearsal.Stage.VerifyRemotePublication then
                    output.Replace(digest, String.replicate 64 "d")
                else output
            { ExitCode = 0; StandardOutput = mismatched; StandardError = "" }
        let result = Rehearsal.initialize fixedClock runner config.Workspace.Checkout config
        check "remote template digest mismatch blocks readiness" (Result.isError result && jsonAt config.ReceiptPath "failureStage" = "VERIFY_REMOTE_PUBLICATION"))

    withFixture (fun root config ->
        let mutable escaped = false
        let runner (command: Rehearsal.ToolInvocation) =
            let arguments = String.Join(" ", command.Arguments)
            if arguments.Contains "rehearsal-005" then escaped <- true
            if command.Stage = Rehearsal.Stage.PublishWorkspace then { ExitCode = 1; StandardOutput = ""; StandardError = "" }
            else { ExitCode = 0; StandardOutput = outputFor command.Stage; StandardError = "" }
        Rehearsal.initialize fixedClock runner root config |> ignore
        check "publication failure never falls back to a prior workspace" (not escaped))

    let manifest =
        """{"schemaVersion":1,"name":"rehearsal","components":[],"rehearsal":{"id":"006","application":"causa","workspace":{"repository":"kemiller2002/causa-data","branch":"main","template":"historical-v1","name":"rehearsal-006","checkout":"../causa-data","create":true,"publish":true,"verify":true}}}"""
    match Rehearsal.parse "/tmp/conditor" "/tmp/target" manifest with
    | Error errors ->
        let details = String.Join("; ", errors)
        check $"valid rehearsal configuration parses: {details}" false
    | Ok parsed -> check "rehearsal configuration derives the selected workspace exactly" (parsed.Workspace.Name = "rehearsal-006")

    let wrongName = manifest.Replace("rehearsal-006", "rehearsal-005")
    match Rehearsal.parse "/tmp/conditor" "/tmp/target" wrongName with
    | Error errors -> check "configuration rejects workspace/rehearsal mismatch" (errors |> List.exists (fun error -> error.Contains "must be 'rehearsal-006'"))
    | Ok _ -> check "configuration rejects workspace/rehearsal mismatch" false

    let escapingReceipt = manifest.Replace("\"application\":\"causa\",", "\"application\":\"causa\",\"receipt\":\"../escape.json\",")
    match Rehearsal.parse "/tmp/conditor" "/tmp/target" escapingReceipt with
    | Error errors -> check "configuration rejects a traversing receipt path" (errors |> List.exists (fun error -> error.Contains "receipt"))
    | Ok _ -> check "configuration rejects a traversing receipt path" false

    let escapingProject = manifest.Replace("\"checkout\":\"../causa-data\",", "\"checkout\":\"../causa-data\",\"toolProject\":\"../outside.fsproj\",")
    match Rehearsal.parse "/tmp/conditor" "/tmp/target" escapingProject with
    | Error errors -> check "configuration rejects a traversing tool project path" (errors |> List.exists (fun error -> error.Contains "toolProject"))
    | Ok _ -> check "configuration rejects a traversing tool project path" false
