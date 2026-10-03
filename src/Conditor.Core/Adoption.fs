namespace Conditor.Core

open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open System.Text.RegularExpressions

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
      DescriptorSha256: string }

type AdoptionPlan =
    { Target: string
      ProjectName: string
      Components: AdoptionComponent list
      Observations: AdoptionObservation list
      Refusals: string list
      ManifestText: string
      Digest: string }

type AdoptionResult =
    { ManifestPath: string
      LockPath: string
      Components: AdoptionComponent list }

module Adoption =
    type Runner = string -> string -> string list -> ProcessResult

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

    let private sourceReference version (definition: ComponentDefinition) =
        match definition.Distribution with
        | HostTool -> Ok(Some $"host:{definition.Id}@{version}")
        | LifecycleNpm ->
            match definition.LifecycleSource with
            | Some RegistryPackage -> Ok(Some $"{definition.Package}@{version}")
            | Some(GitHubSource source) when version = definition.DefaultVersion ->
                Ok(Some(SourceCache.sourceReference source))
            | Some(GitHubSource _) ->
                Error
                    $"Component '{definition.Id}' version '{version}' is installed, but its descriptor has no immutable source mapping for that version."
            | None ->
                Error $"Lifecycle adoptedComponent '{definition.Id}' has no immutable distribution source."
        | NpmPackage
        | NugetPackage -> Ok None

    let private renderManifest (projectName: string) (components: AdoptionComponent list) =
        let root = JsonObject()
        root["schemaVersion"] <- JsonValue.Create 1
        root["name"] <- JsonValue.Create projectName

        let componentArray = JsonArray()

        for adoptedComponent in components |> List.sortBy _.Id do
            let item = JsonObject()
            item["id"] <- JsonValue.Create adoptedComponent.Id
            item["version"] <- JsonValue.Create adoptedComponent.Version
            item["required"] <- JsonValue.Create true
            componentArray.Add item

        root["components"] <- componentArray
        root["requirements"] <- JsonArray()
        let execution = JsonObject()
        execution["enabled"] <- JsonValue.Create false
        root["execution"] <- execution
        root.ToJsonString(JsonSerializerOptions(WriteIndented = true)) + Environment.NewLine

    let private resolved (adoptedComponent: AdoptionComponent) =
        { Id = adoptedComponent.Id
          Version = adoptedComponent.Version
          Distribution = adoptedComponent.Distribution
          Package = adoptedComponent.Package
          SourceReference = adoptedComponent.SourceReference }

    let private projectManifest (projectName: string) (components: AdoptionComponent list) =
        { SchemaVersion = 1
          Name = projectName
          Components =
            components
            |> List.sortBy _.Id
            |> List.map (fun adoptedComponent ->
                { Id = adoptedComponent.Id
                  Version = Some adoptedComponent.Version
                  Required = true })
          Scaffold = None
          Requirements = []
          Execution =
            Some
                { Enabled = false
                  Launcher = None
                  Mission = None
                  ContractPath = None } }

    let private observeLifecycle
        (runner: Runner)
        target
        (descriptor: ComponentDescriptor)
        =
        let definition = descriptor.Definition

        match definition.Command with
        | None ->
            None,
            { ComponentId = definition.Id
              Status = "skipped"
              Detail = "Component has no lifecycle command and cannot be adopted automatically."
              Version = None
              Command = None },
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
                [ $"Component '{definition.Id}' appears present but its read-only version probe failed; adoption will not guess." ]
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
                    [ $"Component '{definition.Id}' is present but its version is not unambiguously qualified by this Conditor build." ]
                | [ version ] ->
                    let verifyResult = runner target executable definition.VerifyArguments

                    if verifyResult.ExitCode <> 0 then
                        let diagnostic =
                            [ verifyResult.StandardOutput.Trim(); verifyResult.StandardError.Trim() ]
                            |> List.filter (String.IsNullOrWhiteSpace >> not)
                            |> String.concat " | "

                        None,
                        { ComponentId = definition.Id
                          Status = "refused"
                          Detail =
                            if String.IsNullOrWhiteSpace diagnostic then
                                $"Verification exited {verifyResult.ExitCode}."
                            else
                                $"Verification exited {verifyResult.ExitCode}: {diagnostic}"
                          Version = Some version
                          Command = Some executable },
                        [ $"Component '{definition.Id}' version '{version}' was detected but did not verify successfully; adoption will not record unhealthy state." ]
                    else
                        match sourceReference version definition with
                        | Error error ->
                            None,
                            { ComponentId = definition.Id
                              Status = "refused"
                              Detail = error
                              Version = Some version
                              Command = Some executable },
                            [ error ]
                        | Ok source ->
                            let adopted =
                                { Id = definition.Id
                                  Version = version
                                  Distribution = definition.Distribution
                                  Package = definition.Package
                                  SourceReference = source
                                  DescriptorSha256 = descriptor.Sha256 }

                            Some adopted,
                            { ComponentId = definition.Id
                              Status = "verified"
                              Detail = "Qualified version detected and the component's read-only verify contract passed."
                              Version = Some version
                              Command = Some executable },
                            []
                | versions ->
                    let rendered = String.Join(", ", versions)

                    None,
                    { ComponentId = definition.Id
                      Status = "refused"
                      Detail = $"Version output ambiguously matched multiple qualified versions: {rendered}."
                      Version = None
                      Command = Some executable },
                    [ $"Component '{definition.Id}' version identity is ambiguous; adoption will not choose one." ]

    let planWith (runner: Runner) target requestedName =
        let fullTarget = Path.GetFullPath target
        let observations = ResizeArray<AdoptionObservation>()
        let components = ResizeArray<AdoptionComponent>()
        let refusals = ResizeArray<string>()

        if not (Directory.Exists fullTarget) then
            refusals.Add $"Target repository does not exist: {fullTarget}"

        let manifestPath = Path.Combine(fullTarget, "conditor.json")
        let lockPath = Path.Combine(fullTarget, ".conditor", "lock.json")

        if File.Exists manifestPath || File.Exists lockPath then
            refusals.Add "Repository already contains Conditor governance. Use status, repair, or upgrade instead of adopt."

        if Directory.Exists fullTarget then
            for descriptor in Registry.descriptors |> List.sortBy (fun item -> item.Definition.Id) do
                match descriptor.Definition.Distribution with
                | HostTool
                | LifecycleNpm ->
                    let adoptedComponent, observation, componentRefusals =
                        observeLifecycle runner fullTarget descriptor

                    observations.Add observation
                    adoptedComponent |> Option.iter components.Add
                    componentRefusals |> List.iter refusals.Add
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
        let manifestText = renderManifest name componentList
        let manifest = projectManifest name componentList

        Compatibility.validate manifest
        |> List.iter (fun error -> refusals.Add $"Compatibility refusal: {error}")

        let refusalList = refusals |> Seq.distinct |> Seq.toList
        let digestMaterial =
            String.concat
                "\n"
                ([ "conditor.adoption-plan/v1"
                   fullTarget
                   manifestText ]
                 @ (componentList
                    |> List.map (fun adoptedComponent ->
                        $"{adoptedComponent.Id}|{adoptedComponent.Version}|{distributionText adoptedComponent.Distribution}|{adoptedComponent.DescriptorSha256}")))

        { Target = fullTarget
          ProjectName = name
          Components = componentList
          Observations = observations |> Seq.toList
          Refusals = refusalList
          ManifestText = manifestText
          Digest = sha256Text digestMaterial }

    let plan target requestedName =
        planWith ProcessRunner.runProcess target requestedName

    let applyWith (runner: Runner) target requestedName authorization =
        let plan = planWith runner target requestedName

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

            try
                File.WriteAllText(temporary, plan.ManifestText)
                File.Move(temporary, manifestPath, false)

                let installationPlan =
                    { ProjectName = plan.ProjectName
                      Operation = Init
                      Components = plan.Components |> List.map resolved
                      Actions = [] }

                let writtenLock = LockFile.write plan.Target manifestPath installationPlan

                Ok
                    { ManifestPath = manifestPath
                      LockPath = writtenLock
                      Components = plan.Components }
            with ex ->
                if File.Exists temporary then File.Delete temporary
                if File.Exists manifestPath then File.Delete manifestPath
                if File.Exists lockPath then File.Delete lockPath
                Error [ $"Unable to adopt repository atomically: {ex.Message}" ]

    let apply target requestedName authorization =
        applyWith ProcessRunner.runProcess target requestedName authorization
