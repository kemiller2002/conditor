namespace Conditor.Core

open System
open System.IO
open System.Text.Json
open System.Text.Json.Nodes
open System.Text.RegularExpressions

/// Fail-closed orchestration for Causa rehearsal workspaces. Conditor owns the
/// ordered preflight; all storage, replay, publication and resolver rules stay
/// in the Causa F# tool invoked by this module.
[<RequireQualifiedAccess>]
module Rehearsal =
    [<Literal>]
    let ReceiptSchema = "conditor.causa-rehearsal-readiness/v1"

    type WorkspaceRequest =
        { Repository: string
          Branch: string
          Template: string
          Name: string
          Checkout: string
          ToolProject: string
          DotnetExecutable: string
          Create: bool
          Publish: bool
          Verify: bool }

    type Config =
        { Id: string
          Application: string
          Workspace: WorkspaceRequest
          ReceiptPath: string }

    [<RequireQualifiedAccess>]
    type Stage =
        | ResolveConfiguration
        | VerifyTemplate
        | CheckLocalDestination
        | CheckRemoteDestination
        | CreateWorkspace
        | ValidateWorkspace
        | PublishWorkspace
        | VerifyRemotePublication
        | VerifyApplicationCompatibility

    type ToolInvocation =
        { Stage: Stage
          WorkingDirectory: string
          Executable: string
          Arguments: string list }

    type ToolRunner = ToolInvocation -> ProcessResult

    type StepEvidence =
        { Stage: Stage
          Passed: bool
          At: DateTimeOffset
          Output: string option }

    type Transition =
        { State: string
          At: DateTimeOffset
          EvidenceStage: Stage }

    type Receipt =
        { Path: string
          RehearsalId: string
          TemplateId: string
          TemplateVersion: string option
          TemplateDigest: string option
          SourceRepository: string option
          SourceCommit: string option
          DestinationRepository: string
          DestinationBranch: string
          WorkspaceId: string
          PublishedCommit: string option
          InvestigationIds: string list
          WorkspaceValidation: string
          RemoteVerification: string
          ApplicationCompatibility: string
          InitializedAt: DateTimeOffset
          Verdict: string
          FailureStage: Stage option
          Failure: string option
          Recovery: string option
          Evidence: StepEvidence list
          Transitions: Transition list }

    let private stageText = function
        | Stage.ResolveConfiguration -> "RESOLVE_CONFIGURATION"
        | Stage.VerifyTemplate -> "VERIFY_TEMPLATE"
        | Stage.CheckLocalDestination -> "CHECK_LOCAL_DESTINATION"
        | Stage.CheckRemoteDestination -> "CHECK_REMOTE_DESTINATION"
        | Stage.CreateWorkspace -> "CREATE_WORKSPACE"
        | Stage.ValidateWorkspace -> "VALIDATE_WORKSPACE"
        | Stage.PublishWorkspace -> "PUBLISH_WORKSPACE"
        | Stage.VerifyRemotePublication -> "VERIFY_REMOTE_PUBLICATION"
        | Stage.VerifyApplicationCompatibility -> "VERIFY_APPLICATION_COMPATIBILITY"

    let private property (name: string) (element: JsonElement) =
        let mutable value = Unchecked.defaultof<JsonElement>
        if element.ValueKind = JsonValueKind.Object && element.TryGetProperty(name, &value) then Some value else None

    let private stringProperty (location: string) (name: string) (element: JsonElement) (errors: ResizeArray<string>) =
        match property name element with
        | Some value when value.ValueKind = JsonValueKind.String && not (String.IsNullOrWhiteSpace(value.GetString())) -> value.GetString() |> nonNull
        | _ ->
            errors.Add $"{location}/{name} must be a non-empty string."
            ""

    let private boolProperty (location: string) (name: string) (element: JsonElement) (errors: ResizeArray<string>) =
        match property name element with
        | Some value when value.ValueKind = JsonValueKind.True -> true
        | Some value when value.ValueKind = JsonValueKind.False -> false
        | _ ->
            errors.Add $"{location}/{name} must be a boolean."
            false

    let private optionalString (name: string) (element: JsonElement) =
        match property name element with
        | Some value when value.ValueKind = JsonValueKind.String && not (String.IsNullOrWhiteSpace(value.GetString())) -> Some(value.GetString() |> nonNull)
        | _ -> None

    let private isUnsafeRelativePath (value: string) =
        Path.IsPathRooted value
        || value.Contains '\\'
        || (value.Split('/') |> Array.exists (fun segment -> segment = "" || segment = "." || segment = ".."))

    let private idPattern = Regex("^[a-z0-9][a-z0-9-]{0,63}$", RegexOptions.CultureInvariant)
    let private repositoryPattern = Regex("^[A-Za-z0-9][A-Za-z0-9-]{0,38}/[A-Za-z0-9._-]{1,100}$", RegexOptions.CultureInvariant)

    let private derivedWorkspace (id: string) =
        match Int32.TryParse id with
        | true, number when number > 0 && number <= 999999 -> Some(sprintf "rehearsal-%03d" number)
        | _ -> None

    /// Reads the optional rehearsal extension from the same versioned
    /// `conditor.json` as every other project declaration.
    let parse (manifestDirectory: string) (target: string) (text: string) : Result<Config, string list> =
        let errors = ResizeArray<string>()
        try
            use document = JsonDocument.Parse text
            let root = document.RootElement
            match property "rehearsal" root with
            | Some rehearsal when rehearsal.ValueKind = JsonValueKind.Object ->
                let id = stringProperty "#/rehearsal" "id" rehearsal errors
                let application = stringProperty "#/rehearsal" "application" rehearsal errors
                let workspace =
                    match property "workspace" rehearsal with
                    | Some value when value.ValueKind = JsonValueKind.Object -> value
                    | _ ->
                        errors.Add "#/rehearsal/workspace must be an object."
                        JsonDocument.Parse("{}").RootElement
                let repository = stringProperty "#/rehearsal/workspace" "repository" workspace errors
                let branch = stringProperty "#/rehearsal/workspace" "branch" workspace errors
                let template = stringProperty "#/rehearsal/workspace" "template" workspace errors
                let name = stringProperty "#/rehearsal/workspace" "name" workspace errors
                let checkoutText = stringProperty "#/rehearsal/workspace" "checkout" workspace errors
                let create = boolProperty "#/rehearsal/workspace" "create" workspace errors
                let publish = boolProperty "#/rehearsal/workspace" "publish" workspace errors
                let verify = boolProperty "#/rehearsal/workspace" "verify" workspace errors
                let toolProject = optionalString "toolProject" workspace |> Option.defaultValue "tools/src/Causa.Tool/Causa.Tool.fsproj"
                let dotnet = optionalString "dotnetExecutable" workspace |> Option.defaultValue "dotnet"
                let receipt = optionalString "receipt" rehearsal |> Option.defaultValue $".conditor/evidence/rehearsal-{id}-readiness.json"

                if application <> "causa" then errors.Add "#/rehearsal/application must be 'causa'."
                if not (repositoryPattern.IsMatch repository) then errors.Add "#/rehearsal/workspace/repository must be OWNER/NAME."
                if String.IsNullOrWhiteSpace branch || branch.Contains ".." || branch.StartsWith "/" || branch.EndsWith "/" then errors.Add "#/rehearsal/workspace/branch is invalid."
                if not (idPattern.IsMatch template) then errors.Add "#/rehearsal/workspace/template is invalid."
                if not (idPattern.IsMatch name) then errors.Add "#/rehearsal/workspace/name is invalid."
                match derivedWorkspace id with
                | Some expected when name <> expected -> errors.Add $"#/rehearsal/workspace/name must be '{expected}' for rehearsal id '{id}'."
                | None -> errors.Add "#/rehearsal/id must be a positive integer from 1 through 999999."
                | _ -> ()
                if not create || not publish || not verify then errors.Add "Causa rehearsal readiness requires create, publish, and verify to all be true."
                if isUnsafeRelativePath toolProject then errors.Add "#/rehearsal/workspace/toolProject must be a canonical relative path within checkout."
                if isUnsafeRelativePath receipt then errors.Add "#/rehearsal/receipt must be a canonical relative path within the Conditor target."

                if errors.Count > 0 then Error(List.ofSeq errors)
                else
                    let checkout = if Path.IsPathRooted checkoutText then Path.GetFullPath checkoutText else Path.GetFullPath(checkoutText, manifestDirectory)
                    Ok
                        { Id = id
                          Application = application
                          Workspace =
                            { Repository = repository
                              Branch = branch
                              Template = template
                              Name = name
                              Checkout = checkout
                              ToolProject = toolProject.Replace('\\', '/')
                              DotnetExecutable = dotnet
                              Create = create
                              Publish = publish
                              Verify = verify }
                          ReceiptPath = Path.GetFullPath(receipt, target) }
            | _ -> Error [ "#/rehearsal must be an object." ]
        with :? JsonException as ex -> Error [ $"Manifest is not valid JSON: {ex.Message}" ]

    let load (manifestPath: string) (target: string) =
        if not (File.Exists manifestPath) then Error [ $"Manifest not found: {manifestPath}" ]
        else parse (Path.GetDirectoryName(Path.GetFullPath manifestPath) |> nonNull) (Path.GetFullPath target) (File.ReadAllText manifestPath)

    let private finalJsonLine (output: string) =
        output.Split('\n', StringSplitOptions.RemoveEmptyEntries ||| StringSplitOptions.TrimEntries)
        |> Array.rev
        |> Array.tryFind (fun line -> line.StartsWith "{" && line.EndsWith "}")

    let private parseOutput (stage: Stage) (result: ProcessResult) =
        if result.ExitCode <> 0 then
            Error $"{stageText stage} failed with exit code {result.ExitCode}. Resolve the reported Causa tool condition and retry with the same explicit configuration."
        else
            match finalJsonLine result.StandardOutput with
            | None -> Error $"{stageText stage} returned no machine-readable evidence. Repair the Causa tool invocation; readiness cannot use textual or inferred evidence."
            | Some line ->
                try
                    match JsonNode.Parse(line: string) with
                    | null -> Error $"{stageText stage} returned null machine-readable evidence. Repair the Causa tool output and retry."
                    | node -> Ok(node, line)
                with :? JsonException -> Error $"{stageText stage} returned malformed machine-readable evidence. Repair the Causa tool output and retry."

    let private nodeString (name: string) (node: JsonNode) =
        match node[name] with
        | null -> None
        | value ->
            try value.GetValue<string>() |> Some
            with _ -> None

    let private nodeStrings (name: string) (node: JsonNode) =
        match node[name] with
        | :? JsonArray as values ->
            values
            |> Seq.choose (fun value ->
                match Option.ofObj value with
                | None -> None
                | Some node ->
                    try Some(node.GetValue<string>())
                    with _ -> None)
            |> Seq.toList
        | _ -> []

    let private requireEvidence (stage: Stage) (expected: string list) (node: JsonNode) =
        let missing = expected |> List.filter (fun name -> isNull node[name])
        if missing.IsEmpty then Ok()
        else
            let names = String.Join(", ", missing)
            Error $"{stageText stage} evidence is incomplete; missing {names}. Fix the Causa tool/Conditor contract and retry."

    let private invocation (config: Config) (stage: Stage) (command: string) (arguments: string list) =
        let project = Path.Combine(config.Workspace.Checkout, config.Workspace.ToolProject.Replace('/', Path.DirectorySeparatorChar))
        { Stage = stage
          WorkingDirectory = config.Workspace.Checkout
          Executable = config.Workspace.DotnetExecutable
          Arguments =
            [ "run"; "--project"; project; "--configuration"; "Release"; "--"; command ]
            @ arguments
            @ [ "--root"; config.Workspace.Checkout ] }

    let private jsonTimestamp (value: DateTimeOffset) = value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'")

    let private receiptJson (receipt: Receipt) =
        let root = JsonObject()
        let stringNode (text: string) = JsonValue.Create<string>(text) |> nonNull :> JsonNode
        let optional (name: string) (value: string option) =
            match value with
            | Some text -> root[name] <- stringNode text
            | None -> root[name] <- null
        root["schema"] <- JsonValue.Create ReceiptSchema
        root["rehearsalIdentifier"] <- JsonValue.Create receipt.RehearsalId
        root["templateIdentifier"] <- JsonValue.Create receipt.TemplateId
        optional "templateVersion" receipt.TemplateVersion
        optional "templateDigest" receipt.TemplateDigest
        optional "sourceRepository" receipt.SourceRepository
        optional "sourceCommit" receipt.SourceCommit
        root["destinationRepository"] <- JsonValue.Create receipt.DestinationRepository
        root["destinationBranch"] <- JsonValue.Create receipt.DestinationBranch
        root["workspaceIdentifier"] <- JsonValue.Create receipt.WorkspaceId
        optional "publishedCommit" receipt.PublishedCommit
        let ids = JsonArray()
        receipt.InvestigationIds |> List.iter ids.Add
        root["investigationIdentifiers"] <- ids
        root["investigationCount"] <- JsonValue.Create receipt.InvestigationIds.Length
        root["workspaceValidationResult"] <- JsonValue.Create receipt.WorkspaceValidation
        root["remoteVerificationResult"] <- JsonValue.Create receipt.RemoteVerification
        root["applicationCompatibilityResult"] <- JsonValue.Create receipt.ApplicationCompatibility
        root["initializationTimestamp"] <- JsonValue.Create(jsonTimestamp receipt.InitializedAt)
        root["overallReadinessVerdict"] <- JsonValue.Create receipt.Verdict
        optional "failureStage" (receipt.FailureStage |> Option.map stageText)
        optional "failure" receipt.Failure
        optional "permittedRecoveryAction" receipt.Recovery
        root["evidence"] <-
            JsonArray(
                receipt.Evidence
                |> List.map (fun evidence ->
                    let item = JsonObject()
                    item["stage"] <- JsonValue.Create(stageText evidence.Stage)
                    item["result"] <- JsonValue.Create(if evidence.Passed then "PASSED" else "FAILED")
                    item["at"] <- JsonValue.Create(jsonTimestamp evidence.At)
                    match evidence.Output with
                    | Some output -> item["toolEvidence"] <- JsonValue.Create output
                    | None -> item["toolEvidence"] <- null
                    item :> JsonNode)
                |> List.toArray)
        root["transitions"] <-
            JsonArray(
                receipt.Transitions
                |> List.map (fun transition ->
                    let item = JsonObject()
                    item["state"] <- JsonValue.Create transition.State
                    item["at"] <- JsonValue.Create(jsonTimestamp transition.At)
                    item["evidenceStage"] <- JsonValue.Create(stageText transition.EvidenceStage)
                    item :> JsonNode)
                |> List.toArray)
        root.ToJsonString(JsonSerializerOptions(WriteIndented = true)) + Environment.NewLine

    let private writeReceipt receipt =
        let directory = Path.GetDirectoryName receipt.Path |> nonNull
        Directory.CreateDirectory directory |> ignore
        let temporary = Path.Combine(directory, $".{Path.GetFileName receipt.Path}.{Guid.NewGuid():N}.tmp")
        try
            File.WriteAllText(temporary, receiptJson receipt)
            File.Move(temporary, receipt.Path, true)
            Ok receipt
        with ex ->
            if File.Exists temporary then File.Delete temporary
            Error $"Unable to write rehearsal readiness evidence: {ex.Message}"

    let private recovery = function
        | Stage.ResolveConfiguration -> "Correct the rehearsal configuration and tool paths; do not create or select a workspace manually."
        | Stage.VerifyTemplate -> "Restore or repair the immutable template in Causa Data, then rerun initialization."
        | Stage.CheckLocalDestination
        | Stage.CheckRemoteDestination -> "Choose a new explicit rehearsal identifier/workspace, or investigate the existing destination; never overwrite or reuse it."
        | Stage.CreateWorkspace -> "Resolve the Causa creation error and retry only after confirming no destination was published."
        | Stage.ValidateWorkspace -> "Preserve the failed local workspace for diagnosis and choose a new explicit workspace after correcting the cause."
        | Stage.PublishWorkspace -> "Keep the receipt blocked, resolve authentication/branch/concurrency, and retry publication without falling back to another workspace."
        | Stage.VerifyRemotePublication -> "Investigate the published commit and branch; do not declare readiness or republish over an existing workspace."
        | Stage.VerifyApplicationCompatibility -> "Correct the application resolver/provider compatibility and rerun the full preflight; do not start timed execution."

    /// Executes the mandatory preflight in order and writes READY only when
    /// every Causa-produced evidence document is present and internally bound
    /// to the selected workspace, repository, branch and commit.
    let initialize (clock: unit -> DateTimeOffset) (runner: ToolRunner) (target: string) (config: Config) : Result<Receipt, string> =
        let started = clock ()
        let evidence = ResizeArray<StepEvidence>()
        let transitions = ResizeArray<Transition>()
        let mutable templateVersion = None
        let mutable templateDigest = None
        let mutable sourceRepository = None
        let mutable sourceCommit = None
        let mutable investigationIds: string list = []
        let mutable publishedCommit = None
        let mutable workspaceValidation = "NOT_RUN"
        let mutable remoteVerification = "NOT_RUN"
        let mutable applicationCompatibility = "NOT_RUN"

        let receipt verdict failedStage failure =
            { Path = config.ReceiptPath
              RehearsalId = config.Id
              TemplateId = config.Workspace.Template
              TemplateVersion = templateVersion
              TemplateDigest = templateDigest
              SourceRepository = sourceRepository
              SourceCommit = sourceCommit
              DestinationRepository = config.Workspace.Repository
              DestinationBranch = config.Workspace.Branch
              WorkspaceId = config.Workspace.Name
              PublishedCommit = publishedCommit
              InvestigationIds = investigationIds
              WorkspaceValidation = workspaceValidation
              RemoteVerification = remoteVerification
              ApplicationCompatibility = applicationCompatibility
              InitializedAt = started
              Verdict = verdict
              FailureStage = failedStage
              Failure = failure
              Recovery = failedStage |> Option.map recovery
              Evidence = List.ofSeq evidence
              Transitions = List.ofSeq transitions }

        let block stage detail =
            let now = clock ()
            evidence.Add { Stage = stage; Passed = false; At = now; Output = None }
            transitions.Add { State = "BLOCKED"; At = now; EvidenceStage = stage }
            let blocked = receipt "BLOCKED" (Some stage) (Some detail)
            match writeReceipt blocked with
            | Ok saved -> Error $"{detail} Readiness is BLOCKED; receipt: {saved.Path}"
            | Error writeError -> Error $"{detail} Readiness is BLOCKED, and its receipt could not be written: {writeError}"

        let project = Path.Combine(config.Workspace.Checkout, config.Workspace.ToolProject.Replace('/', Path.DirectorySeparatorChar))
        if not (Directory.Exists config.Workspace.Checkout) then block Stage.ResolveConfiguration $"Causa checkout does not exist: {config.Workspace.Checkout}."
        elif not (File.Exists project) then block Stage.ResolveConfiguration $"Causa tool project does not exist: {project}."
        elif ProcessRunner.resolveExecutable config.Workspace.DotnetExecutable |> Option.isNone then block Stage.ResolveConfiguration $"Causa tool runtime is unavailable: {config.Workspace.DotnetExecutable}."
        else
            evidence.Add { Stage = Stage.ResolveConfiguration; Passed = true; At = clock (); Output = None }

            let run stage command arguments expected =
                let result = runner (invocation config stage command arguments)
                match parseOutput stage result with
                | Error detail -> Error detail
                | Ok(node, output) ->
                    match requireEvidence stage expected node with
                    | Error detail -> Error detail
                    | Ok () ->
                        evidence.Add { Stage = stage; Passed = true; At = clock (); Output = Some output }
                        Ok node

            let workspaceArgs = [ "--workspace"; config.Workspace.Name; "--template"; config.Workspace.Template ]
            match run Stage.VerifyTemplate "template-verify" [ "--template"; config.Workspace.Template ] [ "templateIdentifier"; "templateVersion"; "templateDigest"; "sourceRepository"; "sourceCommit"; "investigationIdentifiers"; "result" ] with
            | Error detail -> block Stage.VerifyTemplate detail
            | Ok template ->
                templateVersion <- nodeString "templateVersion" template
                templateDigest <- nodeString "templateDigest" template
                sourceRepository <- nodeString "sourceRepository" template
                sourceCommit <- nodeString "sourceCommit" template
                investigationIds <- nodeStrings "investigationIdentifiers" template
                if nodeString "templateIdentifier" template <> Some config.Workspace.Template || nodeString "result" template <> Some "VERIFIED" || sourceRepository <> Some config.Workspace.Repository || investigationIds.IsEmpty || investigationIds.Length <> (Set.ofList investigationIds).Count then
                    block Stage.VerifyTemplate "Template evidence does not identify the configured repository and a non-empty, unique investigation set."
                else
                    match run Stage.CheckLocalDestination "workspace-local-check" [ "--workspace"; config.Workspace.Name ] [ "workspaceIdentifier"; "localDestinationResult" ] with
                    | Error detail -> block Stage.CheckLocalDestination detail
                    | Ok local when nodeString "workspaceIdentifier" local <> Some config.Workspace.Name || nodeString "localDestinationResult" local <> Some "ABSENT" ->
                        block Stage.CheckLocalDestination "Local destination evidence does not prove the selected workspace is absent."
                    | Ok _ ->
                        let remoteArgs = [ "--workspace"; config.Workspace.Name; "--repository"; config.Workspace.Repository; "--branch"; config.Workspace.Branch ]
                        match run Stage.CheckRemoteDestination "workspace-remote-check" remoteArgs [ "workspaceIdentifier"; "repository"; "branch"; "remoteDestinationResult" ] with
                        | Error detail -> block Stage.CheckRemoteDestination detail
                        | Ok remote when nodeString "workspaceIdentifier" remote <> Some config.Workspace.Name || nodeString "repository" remote <> Some config.Workspace.Repository || nodeString "branch" remote <> Some config.Workspace.Branch || nodeString "remoteDestinationResult" remote <> Some "ABSENT" ->
                            block Stage.CheckRemoteDestination "Remote destination evidence does not prove the configured repository, branch, and workspace are absent."
                        | Ok _ ->
                            match run Stage.CreateWorkspace "workspace-create" workspaceArgs [ "workspaceIdentifier"; "templateDigest"; "investigationIdentifiers"; "verificationResult" ] with
                            | Error detail -> block Stage.CreateWorkspace detail
                            | Ok created when nodeString "workspaceIdentifier" created <> Some config.Workspace.Name || nodeString "templateDigest" created <> templateDigest ->
                                block Stage.CreateWorkspace "Workspace creation evidence does not match the selected workspace and template."
                            | Ok _ ->
                                transitions.Add { State = "LOCAL_CREATED"; At = clock (); EvidenceStage = Stage.CreateWorkspace }
                                match run Stage.ValidateWorkspace "workspace-verify" workspaceArgs [ "workspaceIdentifier"; "templateDigest"; "investigationIdentifiers"; "verificationResult" ] with
                                | Error detail -> block Stage.ValidateWorkspace detail
                                | Ok verified when nodeString "workspaceIdentifier" verified <> Some config.Workspace.Name || nodeString "templateDigest" verified <> templateDigest || nodeString "verificationResult" verified <> Some "VERIFIED" || nodeStrings "investigationIdentifiers" verified <> investigationIds ->
                                    block Stage.ValidateWorkspace "Workspace validation evidence is incomplete or does not match the initialization receipt."
                                | Ok _ ->
                                    workspaceValidation <- "LOCAL_VERIFIED"
                                    transitions.Add { State = "LOCAL_VERIFIED"; At = clock (); EvidenceStage = Stage.ValidateWorkspace }
                                    let publishArgs = workspaceArgs @ [ "--repository"; config.Workspace.Repository; "--branch"; config.Workspace.Branch ]
                                    match run Stage.PublishWorkspace "workspace-publish" publishArgs [ "workspaceIdentifier"; "repository"; "branch"; "publishedCommit"; "templateDigest"; "investigationIdentifiers"; "publicationResult" ] with
                                    | Error detail -> block Stage.PublishWorkspace detail
                                    | Ok published ->
                                        publishedCommit <- nodeString "publishedCommit" published
                                        if nodeString "workspaceIdentifier" published <> Some config.Workspace.Name || nodeString "repository" published <> Some config.Workspace.Repository || nodeString "branch" published <> Some config.Workspace.Branch || nodeString "templateDigest" published <> templateDigest || nodeStrings "investigationIdentifiers" published <> investigationIds || nodeString "publicationResult" published <> Some "PUBLISHED" || publishedCommit.IsNone then
                                            block Stage.PublishWorkspace "Publication evidence does not bind the configured destination and template to a commit."
                                        else
                                            transitions.Add { State = "PUBLISHED"; At = clock (); EvidenceStage = Stage.PublishWorkspace }
                                            let verifyRemoteArgs = publishArgs @ [ "--commit"; publishedCommit.Value ]
                                            match run Stage.VerifyRemotePublication "workspace-remote-verify" verifyRemoteArgs [ "workspaceIdentifier"; "repository"; "branch"; "publishedCommit"; "templateDigest"; "investigationIdentifiers"; "remoteVerificationResult" ] with
                                            | Error detail -> block Stage.VerifyRemotePublication detail
                                            | Ok remoteVerified when nodeString "workspaceIdentifier" remoteVerified <> Some config.Workspace.Name || nodeString "repository" remoteVerified <> Some config.Workspace.Repository || nodeString "branch" remoteVerified <> Some config.Workspace.Branch || nodeString "publishedCommit" remoteVerified <> publishedCommit || nodeString "templateDigest" remoteVerified <> templateDigest || nodeStrings "investigationIdentifiers" remoteVerified <> investigationIds || nodeString "remoteVerificationResult" remoteVerified <> Some "REMOTE_VERIFIED" ->
                                                block Stage.VerifyRemotePublication "Remote verification evidence does not match the selected workspace and published commit."
                                            | Ok _ ->
                                                remoteVerification <- "REMOTE_VERIFIED"
                                                transitions.Add { State = "REMOTE_VERIFIED"; At = clock (); EvidenceStage = Stage.VerifyRemotePublication }
                                                match run Stage.VerifyApplicationCompatibility "workspace-application-verify" workspaceArgs [ "workspaceIdentifier"; "investigationIdentifiers"; "applicationCompatibilityResult" ] with
                                                | Error detail -> block Stage.VerifyApplicationCompatibility detail
                                                | Ok application when nodeString "workspaceIdentifier" application <> Some config.Workspace.Name || nodeStrings "investigationIdentifiers" application <> investigationIds || nodeString "applicationCompatibilityResult" application <> Some "APPLICATION_VERIFIED" ->
                                                    block Stage.VerifyApplicationCompatibility "Application compatibility evidence does not load the selected template's investigation set."
                                                | Ok _ ->
                                                    applicationCompatibility <- "APPLICATION_VERIFIED"
                                                    transitions.Add { State = "APPLICATION_VERIFIED"; At = clock (); EvidenceStage = Stage.VerifyApplicationCompatibility }
                                                    transitions.Add { State = "READY"; At = clock (); EvidenceStage = Stage.VerifyApplicationCompatibility }
                                                    let ready = receipt "READY" None None
                                                    writeReceipt ready
                                                    |> Result.mapError id

    let runWithProcess target config =
        initialize (fun () -> DateTimeOffset.UtcNow) (fun command -> ProcessRunner.runProcess command.WorkingDirectory command.Executable command.Arguments) target config

    let toJson receipt = receiptJson receipt
