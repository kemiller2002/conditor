namespace Conditor.Core

open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open System.Text.RegularExpressions
open Conditor.Core.Workstation

type AdoptionObservation =
    { ComponentId: string
      Status: string
      Detail: string
      Version: string option
      Command: string option }

type AdoptionComponent =
    { Id: string
      Version: string
      Distribution: Distribution
      Package: string
      SourceReference: string option
      AuthorityIdentity: string }

type AdoptionRegistryAuthority =
    { SourcePath: string
      Sha256: string
      TargetPath: string
      Profile: WorkstationProfile
      /// Every selection in the set (all roles), used to hold adopted
      /// components to the Registry version authority.
      Selections: RegistryAuthorityBinding.RegistryAuthoritySet }

type AdoptionPlan =
    { Target: string
      ProjectName: string
      Components: AdoptionComponent list
      Observations: AdoptionObservation list
      Refusals: string list
      RegistryAuthority: AdoptionRegistryAuthority option
      /// Structural review findings reported by integrity gates. Recorded in
      /// the lock; never a refusal.
      ReviewSignals: ReviewSignal list
      ManifestText: string
      Digest: string }

type AdoptionResult =
    { ManifestPath: string
      LockPath: string
      Components: AdoptionComponent list
      RegistryAuthorityPath: string option }

module Adoption =
    type Runner = string -> string -> string list -> ProcessResult

    [<Literal>]
    let private AuthorityTargetPath = ".conditor/authority/resolved-release-set.json"

    let private distributionText =
        function
        | HostTool -> "host-tool"
        | LifecycleNpm -> "lifecycle-npm"
        | NpmPackage -> "npm"
        | NugetPackage -> "nuget"

    let private sha256Text (value: string) =
        value
        |> Encoding.UTF8.GetBytes
        |> SHA256.HashData
        |> Convert.ToHexString
        |> fun value -> value.ToLowerInvariant()

    let private sha256Bytes (value: byte array) =
        value
        |> SHA256.HashData
        |> Convert.ToHexString
        |> fun digest -> digest.ToLowerInvariant()

    let private normalizeSha256 (value: string) =
        if value.StartsWith("sha256:", StringComparison.Ordinal) then
            value.Substring("sha256:".Length)
        else
            value

    let private localExecutable target command =
        let suffix = if OperatingSystem.IsWindows() then ".cmd" else String.Empty
        let candidate = Path.Combine(target, "node_modules", ".bin", command + suffix)
        if File.Exists candidate then candidate else command

    let private versionMatches (qualified: Set<string>) (output: string) =
        qualified
        |> Set.toList
        |> List.filter (fun version ->
            let pattern = $"(?<![0-9A-Za-z]){Regex.Escape version}(?![0-9A-Za-z])"
            Regex.IsMatch(output, pattern, RegexOptions.CultureInvariant))
        |> List.sort

    let private embeddedSourceReference version (definition: ComponentDefinition) =
        match definition.Distribution with
        | HostTool -> Ok(Some $"host:{definition.Id}@{version}")
        | LifecycleNpm ->
            match definition.LifecycleSource with
            | Some RegistryPackage -> Ok(Some $"{ComponentDefinition.packageFor version definition}@{version}")
            | Some(GitHubSource source) when version = definition.DefaultVersion ->
                Ok(Some(SourceCache.sourceReference source))
            | Some(GitHubSource _) ->
                Error
                    $"Component '{definition.Id}' version '{version}' is installed, but its descriptor has no immutable source mapping for that version."
            | None ->
                Error $"Lifecycle component '{definition.Id}' has no immutable distribution source."
        | NpmPackage
        | NugetPackage -> Ok None

    let private parseIdentity (text: string) : Map<string, string> option =
        try
            match JsonNode.Parse text with
            | :? JsonObject as root ->
                root
                |> Seq.choose (fun entry ->
                    match entry.Value with
                    | :? JsonValue as value ->
                        match value.TryGetValue<string>() with
                        | true, actual ->
                            actual
                            |> Option.ofObj
                            |> Option.map (fun present -> entry.Key.ToLowerInvariant(), present)
                        | _ -> None
                    | _ -> None)
                |> Map.ofSeq
                |> Some
            | _ -> None
        with :? JsonException ->
            None

    let private identityDifferences (expected: (string * string) list) output =
        match parseIdentity output with
        | None -> [ "identity output is not a JSON object" ]
        | Some actual ->
            expected
            |> List.choose (fun (key, expectedValue) ->
                match actual |> Map.tryFind (key.ToLowerInvariant()) with
                | Some actualValue when actualValue = expectedValue -> None
                | actualValue ->
                    let shown = actualValue |> Option.defaultValue "<absent>"
                    Some $"{key}: expected {expectedValue}, reported {shown}")

    let private renderManifest
        (projectName: string)
        (registryAuthority: AdoptionRegistryAuthority option)
        (components: AdoptionComponent list)
        =
        let root = JsonObject()
        root["schemaVersion"] <- JsonValue.Create 1
        root["name"] <- JsonValue.Create projectName

        registryAuthority
        |> Option.iter (fun authority ->
            let item = JsonObject()
            item["kind"] <- JsonValue.Create "resolved-release-set"
            item["path"] <- JsonValue.Create authority.TargetPath
            item["sha256"] <- JsonValue.Create authority.Sha256
            root["registryAuthority"] <- item)

        let componentArray = JsonArray()

        for adoptedEntry in components |> List.sortBy _.Id do
            let item = JsonObject()
            item["id"] <- JsonValue.Create adoptedEntry.Id
            item["version"] <- JsonValue.Create adoptedEntry.Version
            item["required"] <- JsonValue.Create true
            componentArray.Add item

        root["components"] <- componentArray
        root["requirements"] <- JsonArray()
        let execution = JsonObject()
        execution["enabled"] <- JsonValue.Create false
        root["execution"] <- execution
        root.ToJsonString(JsonSerializerOptions(WriteIndented = true)) + Environment.NewLine

    let private resolved (adoptedEntry: AdoptionComponent) =
        { Id = adoptedEntry.Id
          Version = adoptedEntry.Version
          Distribution = adoptedEntry.Distribution
          Package = adoptedEntry.Package
          SourceReference = adoptedEntry.SourceReference }

    let private projectManifest
        (projectName: string)
        (registryAuthority: AdoptionRegistryAuthority option)
        (components: AdoptionComponent list)
        =
        { SchemaVersion = 1
          Name = projectName
          Components =
            components
            |> List.sortBy _.Id
            |> List.map (fun adoptedEntry ->
                { Id = adoptedEntry.Id
                  Version = Some adoptedEntry.Version
                  Required = true })
          RegistryAuthority =
            registryAuthority
            |> Option.map (fun authority ->
                { Kind = "resolved-release-set"
                  Path = authority.TargetPath
                  Sha256 = authority.Sha256 })
          Scaffold = None
          Requirements = []
          Execution =
            Some
                { Enabled = false
                  Launcher = None
                  Mission = None
                  ContractPath = None } }

    /// Whether the repository shows any trace of the component's installation.
    /// A component that declares no markers can only be judged by its command.
    let private installedInRepository target (definition: ComponentDefinition) =
        definition.InstallationMarkers.IsEmpty
        || definition.InstallationMarkers
           |> List.exists (fun marker ->
               let path = Path.Combine(target, marker)
               File.Exists path || Directory.Exists path)

    let private observeEmbeddedLifecycle
        (runner: Runner)
        target
        (descriptor: ComponentDescriptor)
        =
        let definition = descriptor.Definition

        match definition.Command with
        // A lifecycle CLI installed on the machine says nothing about this
        // repository. Without any installation marker here the component is
        // absent, whatever the CLI would report, so it is not probed at all.
        | Some command when not (installedInRepository target definition) ->
            let markers = String.Join(", ", definition.InstallationMarkers)

            None,
            { ComponentId = definition.Id
              Status = "not-found"
              Detail = $"Not installed in this repository: none of its installation markers ({markers}) exists."
              Version = None
              Command = Some(localExecutable target command) },
            [],
            []
        | None ->
            None,
            { ComponentId = definition.Id
              Status = "skipped"
              Detail = "Component has no lifecycle command and cannot be adopted automatically."
              Version = None
              Command = None },
            [],
            []
        | Some command ->
            let executable = localExecutable target command
            let versionResult = runner target executable definition.VersionArguments

            if versionResult.ExitCode = -1 then
                None,
                { ComponentId = definition.Id
                  Status = "not-found"
                  Detail = "Lifecycle command is not available on PATH or in node_modules/.bin."
                  Version = None
                  Command = Some executable },
                [],
                []
            elif versionResult.ExitCode <> 0 then
                let detail =
                    let error = versionResult.StandardError.Trim()
                    if String.IsNullOrWhiteSpace error then
                        $"Version probe exited {versionResult.ExitCode}."
                    else
                        $"Version probe exited {versionResult.ExitCode}: {error}"

                None,
                { ComponentId = definition.Id
                  Status = "refused"
                  Detail = detail
                  Version = None
                  Command = Some executable },
                [ $"Component '{definition.Id}' appears present but its read-only version probe failed; adoption will not guess." ],
                []
            else
                let output = versionResult.StandardOutput + Environment.NewLine + versionResult.StandardError
                let qualified = Registry.qualifiedVersions definition.Id |> Option.defaultValue Set.empty
                let matches = versionMatches qualified output

                match matches with
                | [] ->
                    None,
                    { ComponentId = definition.Id
                      Status = "refused"
                      Detail = $"Version output did not identify one of Conditor's qualified versions: {output.Trim()}"
                      Version = None
                      Command = Some executable },
                    [ $"Component '{definition.Id}' is present but its version is not unambiguously qualified by this Conditor build." ],
                    []
                | [ version ] ->
                    let verifyResult =
                        runner target executable (VerificationGate.argumentsFor version definition)

                    match VerificationGate.interpret definition.Id version definition verifyResult with
                    | Error reasons ->
                        None,
                        { ComponentId = definition.Id
                          Status = "refused"
                          Detail = String.concat " | " reasons
                          Version = Some version
                          Command = Some executable },
                        [ $"Component '{definition.Id}' version '{version}' was detected but did not verify successfully; adoption will not record unhealthy state." ],
                        []
                    | Ok signals ->
                        match embeddedSourceReference version definition with
                        | Error error ->
                            None,
                            { ComponentId = definition.Id
                              Status = "refused"
                              Detail = error
                              Version = Some version
                              Command = Some executable },
                            [ error ],
                            []
                        | Ok source ->
                            let adoptedEntry =
                                { Id = definition.Id
                                  Version = version
                                  Distribution = definition.Distribution
                                  Package = ComponentDefinition.packageFor version definition
                                  SourceReference = source
                                  AuthorityIdentity = $"embedded-descriptor=sha256:{descriptor.Sha256}" }

                            let detail =
                                match VerificationGate.gateFor version definition, signals with
                                | None, _ -> "Qualified version detected and the component's read-only verify contract passed."
                                | Some _, [] -> "Qualified version detected and the component's integrity gate passed with no structural review signals."
                                | Some _, reported ->
                                    $"Qualified version detected and the component's integrity gate passed; {reported.Length} structural review signal(s) recorded, not blocking."

                            Some adoptedEntry,
                            { ComponentId = definition.Id
                              Status = "verified"
                              Detail = detail
                              Version = Some version
                              Command = Some executable },
                            [],
                            signals
                | versions ->
                    let rendered = String.Join(", ", versions)

                    None,
                    { ComponentId = definition.Id
                      Status = "refused"
                      Detail = $"Version output ambiguously matched multiple qualified versions: {rendered}."
                      Version = None
                      Command = Some executable },
                    [ $"Component '{definition.Id}' version identity is ambiguous; adoption will not choose one." ],
                    []

    let private observeRegistryLifecycle
        (runner: Runner)
        target
        (authority: AdoptionRegistryAuthority)
        (release: ProfileComponent)
        =
        match release.Lifecycle with
        | None ->
            // Without a contract Conditor has no lifecycle semantics for the
            // release, so it can never adopt it. That blocks adoption only when
            // the release is actually installed here: an absent component is
            // reported exactly as an absent contracted one is.
            let executable = localExecutable target release.Executable

            if (runner target executable release.VersionProbe).ExitCode = -1 then
                None,
                { ComponentId = release.Id
                  Status = "not-found"
                  Detail =
                    $"Registry-selected executable is not installed; its release declares no supported {RepositoryLifecycleContract.Capability} contract, so it could not be adopted if it were."
                  Version = None
                  Command = Some executable },
                []
            else
                None,
                { ComponentId = release.Id
                  Status = "refused"
                  Detail =
                    $"Registry release has repository-lifecycle role but no supported {RepositoryLifecycleContract.Capability} contract."
                  Version = Some release.Version
                  Command = Some executable },
                [ $"Registry component '{release.Id}' cannot be adopted because its lifecycle contract is unsupported or missing." ]
        | Some lifecycle ->
            let executable = localExecutable target release.Executable
            let identityResult = runner target executable release.VersionProbe

            if identityResult.ExitCode = -1 then
                None,
                { ComponentId = release.Id
                  Status = "not-found"
                  Detail = "Registry-selected lifecycle executable is not installed on PATH or in node_modules/.bin."
                  Version = None
                  Command = Some executable },
                []
            elif identityResult.ExitCode <> 0 then
                None,
                { ComponentId = release.Id
                  Status = "refused"
                  Detail = $"Registry lifecycle identity probe exited {identityResult.ExitCode}."
                  Version = None
                  Command = Some executable },
                [ $"Registry component '{release.Id}' is present but its lifecycle identity probe failed." ]
            else
                let expected = RepositoryLifecycleContract.identity release lifecycle
                let differences = identityDifferences expected identityResult.StandardOutput

                if not differences.IsEmpty then
                    None,
                    { ComponentId = release.Id
                      Status = "refused"
                      Detail = "Registry identity mismatch: " + String.concat "; " differences
                      Version = Some release.Version
                      Command = Some executable },
                    [ $"Registry component '{release.Id}' does not match the exact release selected by the resolved set." ]
                else
                    let verifyResult =
                        runner target executable [ "verify"; "--root"; Path.GetFullPath target ]

                    if verifyResult.ExitCode <> 0 then
                        let diagnostic =
                            [ verifyResult.StandardOutput.Trim(); verifyResult.StandardError.Trim() ]
                            |> List.filter (String.IsNullOrWhiteSpace >> not)
                            |> String.concat " | "

                        None,
                        { ComponentId = release.Id
                          Status = "refused"
                          Detail =
                            if String.IsNullOrWhiteSpace diagnostic then
                                $"Verification exited {verifyResult.ExitCode}."
                            else
                                $"Verification exited {verifyResult.ExitCode}: {diagnostic}"
                          Version = Some release.Version
                          Command = Some executable },
                        [ $"Registry component '{release.Id}' matched the selected release but did not verify successfully in this repository." ]
                    else
                        let source =
                            RepositoryLifecycleContract.sourceReference
                                authority.Profile.SourceIdentity
                                release
                                lifecycle

                        let adoptedEntry =
                            { Id = release.Id
                              Version = release.Version
                              Distribution = HostTool
                              Package = release.Executable
                              SourceReference = Some source
                              AuthorityIdentity =
                                authority.Profile.SourceIdentity
                                |> Option.defaultValue $"resolved-set=sha256:{authority.Sha256}" }

                        Some adoptedEntry,
                        { ComponentId = release.Id
                          Status = "verified"
                          Detail =
                            $"Registry-selected {RepositoryLifecycleContract.Capability}/v{lifecycle.ContractVersion} identity and repository verification both passed."
                          Version = Some release.Version
                          Command = Some executable },
                        []

    let loadRegistryAuthority _ sourcePath expectedSha256 =
        let fullSource = Path.GetFullPath sourcePath
        let normalized = normalizeSha256 expectedSha256

        if not (File.Exists fullSource) then
            Error $"resolved release set not found: {fullSource}"
        else
            let bytes = File.ReadAllBytes fullSource

            ResolvedReleaseSets.parseVerified (Platform.runtimeIdentifier ()) normalized bytes
            |> Result.bind (fun profile ->
                RegistryAuthorityBinding.parseSelections AuthorityTargetPath normalized bytes
                |> Result.map (fun selections ->
                    { SourcePath = fullSource
                      Sha256 = normalized
                      TargetPath = AuthorityTargetPath
                      Profile = profile
                      Selections = selections }))

    let planWithAuthority
        (runner: Runner)
        target
        requestedName
        (registryAuthority: AdoptionRegistryAuthority option)
        =
        let fullTarget = Path.GetFullPath target
        let observations = ResizeArray<AdoptionObservation>()
        let components = ResizeArray<AdoptionComponent>()
        let refusals = ResizeArray<string>()
        let reviewSignals = ResizeArray<ReviewSignal>()

        if not (Directory.Exists fullTarget) then
            refusals.Add $"Target repository does not exist: {fullTarget}"

        let manifestPath = Path.Combine(fullTarget, "conditor.json")
        let lockPath = Path.Combine(fullTarget, ".conditor", "lock.json")

        if File.Exists manifestPath || File.Exists lockPath then
            refusals.Add "Repository already contains Conditor governance. Use status, repair, or upgrade instead of adopt."

        let registryLifecycle =
            registryAuthority
            |> Option.map (fun authority ->
                authority.Profile.Components
                |> List.filter (fun release -> release.Role = Some "repository-lifecycle")
                |> List.sortBy _.Id)
            |> Option.defaultValue []

        let registryIds = registryLifecycle |> List.map _.Id |> Set.ofList

        if Directory.Exists fullTarget then
            for release in registryLifecycle do
                let adoptedEntry, observation, entryRefusals =
                    observeRegistryLifecycle runner fullTarget registryAuthority.Value release

                observations.Add observation
                adoptedEntry |> Option.iter components.Add
                entryRefusals |> List.iter refusals.Add

            for descriptor in Registry.descriptors |> List.sortBy (fun item -> item.Definition.Id) do
                if not (registryIds.Contains descriptor.Definition.Id) then
                    match descriptor.Definition.Distribution with
                    | HostTool
                    | LifecycleNpm ->
                        let adoptedEntry, observation, entryRefusals, entrySignals =
                            observeEmbeddedLifecycle runner fullTarget descriptor

                        observations.Add observation
                        adoptedEntry |> Option.iter components.Add
                        entryRefusals |> List.iter refusals.Add
                        entrySignals |> List.iter reviewSignals.Add
                    | NpmPackage
                    | NugetPackage ->
                        observations.Add
                            { ComponentId = descriptor.Definition.Id
                              Status = "skipped"
                              Detail = "Application-package binding requires an explicit project target; Conditor will not infer or adopt it automatically."
                              Version = None
                              Command = None }

        if components.Count = 0 && refusals.Count = 0 then
            refusals.Add "No verifiable lifecycle components were found to adopt."

        let name =
            requestedName
            |> Option.filter (String.IsNullOrWhiteSpace >> not)
            |> Option.defaultWith (fun () ->
                let inferred = DirectoryInfo(fullTarget).Name
                if String.IsNullOrWhiteSpace inferred then "adopted-repository" else inferred)

        let componentList = components |> Seq.toList |> List.sortBy _.Id
        let manifestText = renderManifest name registryAuthority componentList
        let manifest = projectManifest name registryAuthority componentList
        let externalIds = registryIds

        Compatibility.validateWithExternalIds externalIds manifest
        |> List.iter (fun error -> refusals.Add $"Compatibility refusal: {error}")

        // An adopted manifest must replan: hold every observed component to the
        // same Registry version authority the planner enforces (CON-F1).
        (RegistryAuthorityBinding.bindAll
            (registryAuthority |> Option.map _.Selections)
            manifest.Components)
            .Violations
        |> List.iter (RegistryAuthorityBinding.describe >> refusals.Add)

        let refusalList = refusals |> Seq.distinct |> Seq.toList
        let authorityDigest =
            registryAuthority
            |> Option.map (fun authority ->
                let sourceIdentity = authority.Profile.SourceIdentity |> Option.defaultValue "-"
                $"{authority.TargetPath}|sha256:{authority.Sha256}|{sourceIdentity}")
            |> Option.defaultValue "-"

        let signalList = reviewSignals |> Seq.toList

        // Review signals are recorded in the lock, so the authorization binds
        // them too: a changed finding needs a fresh review of the proposal.
        let digestMaterial =
            String.concat
                "\n"
                ([ "conditor.adoption-plan/v2"
                   fullTarget
                   authorityDigest
                   manifestText ]
                 @ (componentList
                    |> List.map (fun adoptedEntry ->
                        let source = adoptedEntry.SourceReference |> Option.defaultValue "-"
                        $"{adoptedEntry.Id}|{adoptedEntry.Version}|{distributionText adoptedEntry.Distribution}|{adoptedEntry.AuthorityIdentity}|{source}"))
                 @ (signalList
                    |> List.map (fun signal ->
                        $"review|{signal.ComponentId}|{signal.Code}|{signal.Band}|{signal.Path}|{signal.LineCount}")))

        { Target = fullTarget
          ProjectName = name
          Components = componentList
          Observations = observations |> Seq.toList
          Refusals = refusalList
          RegistryAuthority = registryAuthority
          ReviewSignals = signalList
          ManifestText = manifestText
          Digest = sha256Text digestMaterial }

    let planWith (runner: Runner) target requestedName =
        planWithAuthority runner target requestedName None

    let plan target requestedName =
        planWith ProcessRunner.runProcess target requestedName

    let planWithRegistryAuthority target requestedName registryAuthority =
        planWithAuthority ProcessRunner.runProcess target requestedName (Some registryAuthority)

    let private materializeRegistryAuthority target (authority: AdoptionRegistryAuthority) =
        let bytes = File.ReadAllBytes authority.SourcePath
        let actual = sha256Bytes bytes

        if actual <> authority.Sha256 then
            Error
                $"Registry authority changed after planning: expected sha256:{authority.Sha256}, observed sha256:{actual}."
        else
            let destination = Path.GetFullPath(Path.Combine(target, authority.TargetPath))
            let parent = Path.GetDirectoryName destination |> Option.ofObj |> Option.defaultValue target
            Directory.CreateDirectory parent |> ignore

            if File.Exists destination then
                let existing = File.ReadAllBytes destination |> sha256Bytes

                if existing = authority.Sha256 then
                    Ok(destination, false)
                else
                    Error
                        $"Registry authority target already exists with different content: {destination}"
            else
                File.WriteAllBytes(destination, bytes)
                Ok(destination, true)

    let applyWithAuthority
        (runner: Runner)
        target
        requestedName
        registryAuthority
        authorization
        =
        let plan = planWithAuthority runner target requestedName registryAuthority

        if not (String.Equals(plan.Digest, authorization, StringComparison.OrdinalIgnoreCase)) then
            Error
                [ "Adoption authorization digest does not match the current repository observation."
                  $"Current plan digest: {plan.Digest}"
                  "Run conditor adopt again and review the current proposal before authorizing." ]
        elif not plan.Refusals.IsEmpty then
            Error plan.Refusals
        else
            let manifestPath = Path.Combine(plan.Target, "conditor.json")
            let lockPath = Path.Combine(plan.Target, ".conditor", "lock.json")
            let temporary = $"{manifestPath}.conditor-adopt-{Guid.NewGuid():N}.tmp"
            let mutable authorityCreated: string option = None

            try
                let authorityPath =
                    match plan.RegistryAuthority with
                    | None -> None
                    | Some authority ->
                        match materializeRegistryAuthority plan.Target authority with
                        | Error error -> raise (InvalidOperationException error)
                        | Ok(path, created) ->
                            if created then authorityCreated <- Some path
                            Some path

                File.WriteAllText(temporary, plan.ManifestText)
                File.Move(temporary, manifestPath, false)

                let installationPlan =
                    { ProjectName = plan.ProjectName
                      Operation = Init
                      Components = plan.Components |> List.map resolved
                      Actions = [] }

                let writtenLock = LockFile.writeWith plan.Target manifestPath installationPlan plan.ReviewSignals

                Ok
                    { ManifestPath = manifestPath
                      LockPath = writtenLock
                      Components = plan.Components
                      RegistryAuthorityPath = authorityPath }
            with ex ->
                if File.Exists temporary then File.Delete temporary
                if File.Exists manifestPath then File.Delete manifestPath
                if File.Exists lockPath then File.Delete lockPath
                authorityCreated
                |> Option.iter (fun path -> if File.Exists path then File.Delete path)
                Error [ $"Unable to adopt repository atomically: {ex.Message}" ]

    let applyWith (runner: Runner) target requestedName authorization =
        applyWithAuthority runner target requestedName None authorization

    let apply target requestedName authorization =
        applyWith ProcessRunner.runProcess target requestedName authorization

    let applyWithRegistryAuthority target requestedName registryAuthority authorization =
        applyWithAuthority
            ProcessRunner.runProcess
            target
            requestedName
            (Some registryAuthority)
            authorization
