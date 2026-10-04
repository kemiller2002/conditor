namespace Conditor.Core

open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open Conditor.Core.Workstation

type CurrentUpgradeTransition =
    { Id: string
      FromVersion: string
      ToVersion: string
      Role: string
      Mode: string }

type CurrentUpgradeFileChange =
    { Path: string
      BeforeSha256: string
      AfterSha256: string
      Content: string }

type CurrentUpgradePlan =
    { Target: string
      ManifestPath: string
      CurrentManifestSha256: string
      TargetSetPath: string
      TargetSetSha256: string
      TargetProfile: WorkstationProfile
      TargetManifestText: string
      Transitions: CurrentUpgradeTransition list
      WorkstationPlan: WorkstationPlan option
      GenericUpgradePlan: LifecyclePlan option
      GenericVerifyPlan: LifecyclePlan option
      EmbeddedTransitions: (CurrentUpgradeTransition * ComponentDefinition * ProfileComponent) list
      FileChanges: CurrentUpgradeFileChange list
      Refusals: string list
      Digest: string }

type CurrentUpgradeResult =
    { ChangedComponents: string list
      UpdatedFiles: string list
      LockPath: string
      AuthorityPath: string
      NoRemainingVersionChanges: bool }

module CurrentUpgrade =
    [<Literal>]
    let private AuthorityPath = ".conditor/authority/resolved-release-set.json"

    type private TargetEntry =
        { Id: string
          Version: string
          Role: string
          DistributionClass: string
          Repository: string
          Tag: string
          DistributionMechanism: string option
          DistributionPackage: string option
          PrimaryArtifactName: string option
          HasLifecycle: bool }

    let private normalizeSha256 (value: string) =
        if value.StartsWith("sha256:", StringComparison.Ordinal) then
            value.Substring("sha256:".Length)
        else
            value

    let private sha256Bytes (bytes: byte array) =
        bytes
        |> SHA256.HashData
        |> Convert.ToHexString
        |> fun value -> value.ToLowerInvariant()

    let private sha256Text (value: string) =
        value |> Encoding.UTF8.GetBytes |> sha256Bytes

    let private sha256File (path: string) = File.ReadAllBytes path |> sha256Bytes

    let private tryProperty (name: string) (element: JsonElement) =
        let mutable value = Unchecked.defaultof<JsonElement>
        if element.ValueKind = JsonValueKind.Object && element.TryGetProperty(name, &value) then Some value else None

    let private str name element =
        tryProperty name element
        |> Option.bind (fun value ->
            if value.ValueKind = JsonValueKind.String then value.GetString() |> Option.ofObj else None)

    let private objects name element =
        match tryProperty name element with
        | Some value when value.ValueKind = JsonValueKind.Array -> value.EnumerateArray() |> Seq.toList
        | _ -> []

    let private targetEntries (path: string) : Result<TargetEntry list, string> =
        try
            use document = JsonDocument.Parse(File.ReadAllBytes path)

            let components =
                match tryProperty "components" document.RootElement with
                | Some value when value.ValueKind = JsonValueKind.Array -> value.EnumerateArray() |> Seq.toList
                | _ -> []

            Ok(
                components
                |> List.choose (fun item ->
                    match
                        str "systemId" item,
                        str "version" item,
                        str "role" item,
                        str "distributionClass" item,
                        str "repository" item,
                        str "tag" item
                    with
                    | Some id, Some version, Some role, Some distributionClass, Some repository, Some tag ->
                        let distribution = tryProperty "distribution" item

                        let primaryArtifact =
                            objects "artifacts" item
                            |> List.tryPick (fun artifact ->
                                match str "purpose" artifact, str "name" artifact with
                                | Some ("package" | "executable"), Some name -> Some name
                                | _ -> None)

                        Some
                            { Id = id
                              Version = version
                              Role = role
                              DistributionClass = distributionClass
                              Repository = repository
                              Tag = tag
                              DistributionMechanism = distribution |> Option.bind (str "mechanism")
                              DistributionPackage = distribution |> Option.bind (str "package")
                              PrimaryArtifactName = primaryArtifact
                              HasLifecycle = (tryProperty "repositoryLifecycle" item).IsSome }
                    | _ -> None)
            )
        with
        | :? JsonException as ex -> Error $"target resolved set is not valid JSON: {ex.Message}"
        | ex -> Error $"unable to read target resolved set: {ex.Message}"

    let private semverCore (value: string) =
        let core = value.Split([| '-'; '+' |], 2)[0]
        let parts = core.Split('.')

        if parts.Length = 3 then
            match Int32.TryParse parts[0], Int32.TryParse parts[1], Int32.TryParse parts[2] with
            | (true, major), (true, minor), (true, patch) -> Some(major, minor, patch)
            | _ -> None
        else
            None

    let private isDowngrade fromVersion toVersion =
        match semverCore fromVersion, semverCore toVersion with
        | Some fromValue, Some toValue -> toValue < fromValue
        | _ -> false

    let private renderTargetManifest (manifestPath: string) (targetDigest: string) (transitions: CurrentUpgradeTransition list) =
        try
            match JsonNode.Parse(File.ReadAllText manifestPath) with
            | :? JsonObject as root ->
                let transitionMap = transitions |> List.map (fun t -> t.Id, t.ToVersion) |> Map.ofList

                match root["components"] with
                | :? JsonArray as components ->
                    for item in components do
                        match item with
                        | :? JsonObject as entry ->
                            match entry["id"] with
                            | :? JsonValue as idValue ->
                                match idValue.TryGetValue<string>() with
                                | true, id ->
                                    match id |> Option.ofObj with
                                    | Some nonNullId ->
                                        match transitionMap |> Map.tryFind (nonNullId.ToLowerInvariant()) with
                                        | Some version -> entry["version"] <- JsonValue.Create<string>(version)
                                        | None -> ()
                                    | None -> ()
                                | _ -> ()
                            | _ -> ()
                        | _ -> ()
                | _ -> ()

                let authority = JsonObject()
                authority["kind"] <- JsonValue.Create "resolved-release-set"
                authority["path"] <- JsonValue.Create AuthorityPath
                authority["sha256"] <- JsonValue.Create<string>(targetDigest)
                root["registryAuthority"] <- authority
                Ok(root.ToJsonString(JsonSerializerOptions(WriteIndented = true)) + Environment.NewLine)
            | _ -> Error "conditor.json must be a JSON object"
        with :? JsonException as ex ->
            Error $"conditor.json is not valid JSON: {ex.Message}"

    let private installedExecutable (ctx: WorkstationContext) (release: ProfileComponent) =
        Path.Combine(WorkstationPaths.installRoot ctx, release.Id, release.Version, release.Executable)

    let private qualifiedEmbeddedTarget (id: string) (version: string) =
        match Registry.tryFind id, Registry.qualifiedVersions id with
        | Some definition, Some versions when versions.Contains version ->
            match definition.Distribution with
            | HostTool -> Some definition
            | _ -> None
        | _ -> None


    let private packageSpecifier (entry: TargetEntry) =
        match entry.DistributionMechanism with
        | Some "npm" -> Ok entry.Version
        | Some "github-release" ->
            match entry.PrimaryArtifactName with
            | Some artifact when entry.DistributionClass = "web-package" ->
                Ok $"https://github.com/{entry.Repository}/releases/download/{entry.Tag}/{artifact}"
            | _ ->
                Error
                    $"Project binding '{entry.Id}' uses github-release but exposes no package artifact."
        | Some mechanism ->
            Error $"Project binding '{entry.Id}' uses unsupported package mechanism '{mechanism}'."
        | None ->
            Error $"Project binding '{entry.Id}' is missing a distribution mechanism."

    let private npmBindingChange target (definition: ComponentDefinition) (entry: TargetEntry) =
        let path = Path.Combine(Path.GetFullPath target, "src", "kernel", "package.json")

        if not (File.Exists path) then
            Error
                $"Project binding '{definition.Id}' expects src/kernel/package.json; Conditor will not guess another npm target."
        else
            packageSpecifier entry
            |> Result.bind (fun specifier ->
                try
                    match JsonNode.Parse(File.ReadAllText path) with
                    | :? JsonObject as root ->
                        match root["dependencies"] with
                        | :? JsonObject as dependencies when dependencies.ContainsKey definition.Package ->
                            dependencies[definition.Package] <- JsonValue.Create specifier
                            let content = root.ToJsonString(JsonSerializerOptions(WriteIndented = true)) + Environment.NewLine
                            Ok
                                { Path = path
                                  BeforeSha256 = sha256File path
                                  AfterSha256 = sha256Text content
                                  Content = content }
                        | _ ->
                            Error
                                $"Project binding '{definition.Id}' expects dependency '{definition.Package}' in src/kernel/package.json; Conditor will not add an unproven binding."
                    | _ -> Error "src/kernel/package.json must contain a JSON object."
                with :? JsonException as ex ->
                    Error $"Unable to parse src/kernel/package.json: {ex.Message}")

    let private nugetBindingChange target (definition: ComponentDefinition) (entry: TargetEntry) =
        let path = Path.Combine(Path.GetFullPath target, "src", "engine", "App.Engine.fsproj")

        if not (File.Exists path) then
            Error
                $"Project binding '{definition.Id}' expects src/engine/App.Engine.fsproj; Conditor will not guess another NuGet target."
        else
            let before = File.ReadAllText path
            let escaped = Regex.Escape definition.Package
            let pattern =
                $"(<PackageReference\\s+[^>]*Include=\\\"{escaped}\\\"[^>]*Version=\\\")[^\\\"]+(\\\")"
            let matches = Regex.Matches(before, pattern, RegexOptions.CultureInvariant)

            if matches.Count <> 1 then
                Error
                    $"Project binding '{definition.Id}' expected exactly one Version attribute for '{definition.Package}'; found {matches.Count}."
            else
                let content = Regex.Replace(before, pattern, $"$1{entry.Version}$2", RegexOptions.CultureInvariant)
                Ok
                    { Path = path
                      BeforeSha256 = sha256File path
                      AfterSha256 = sha256Text content
                      Content = content }

    let private foundationsChange target transitions =
        let path = Path.Combine(Path.GetFullPath target, ".echelon", "foundations.json")

        if not (File.Exists path) then
            Ok None
        else
            try
                match JsonNode.Parse(File.ReadAllText path) with
                | :? JsonObject as root ->
                    match root["capabilities"] with
                    | :? JsonObject as capabilities ->
                        for transition in transitions do
                            match capabilities[transition.Id] with
                            | :? JsonObject as capability ->
                                capability["version"] <- JsonValue.Create transition.ToVersion
                            | _ -> ()

                        let content = root.ToJsonString(JsonSerializerOptions(WriteIndented = true)) + Environment.NewLine

                        if sha256Text content = sha256File path then Ok None
                        else
                            Ok(
                                Some
                                    { Path = path
                                      BeforeSha256 = sha256File path
                                      AfterSha256 = sha256Text content
                                      Content = content }
                            )
                    | _ -> Ok None
                | _ -> Ok None
            with :? JsonException as ex ->
                Error $"Unable to parse .echelon/foundations.json: {ex.Message}"

    let private writeTextAtomically path content =
        let bytes = Encoding.UTF8.GetBytes content
        let parent = Path.GetDirectoryName path |> Option.ofObj |> Option.defaultValue "."
        Directory.CreateDirectory parent |> ignore
        let temporary = $"{path}.conditor-current-{Guid.NewGuid():N}.tmp"

        try
            File.WriteAllBytes(temporary, bytes)
            File.Move(temporary, path, true)
        finally
            if File.Exists temporary then File.Delete temporary

    let private applyFileChanges changes =
        let backups = ResizeArray<string * string>()

        try
            for change in changes do
                if sha256File change.Path <> change.BeforeSha256 then
                    raise (
                        InvalidOperationException(
                            $"Project binding '{change.Path}' changed after planning."
                        )
                    )

                backups.Add(change.Path, File.ReadAllText change.Path)
                writeTextAtomically change.Path change.Content

            Ok(List.ofSeq backups)
        with ex ->
            for path, content in backups do
                writeTextAtomically path content

            Error [ $"Unable to apply planned project-binding changes: {ex.Message}" ]

    let private restoreFileChanges backups =
        for path, content in backups do
            writeTextAtomically path content

    let private prepare
        target
        manifestPath
        (manifest: ProjectManifest)
        targetSetPath
        targetDigest
        (ctx: WorkstationContext)
        (probe: string -> string list -> ProcessResult)
        =
        let errors = ResizeArray<string>()
        let normalizedDigest = normalizeSha256 targetDigest
        let rid = Platform.runtimeIdentifier ()

        LockFile.verifyManifest target manifestPath
        |> Result.mapError (List.iter errors.Add)
        |> ignore

        let targetProfile =
            match ResolvedReleaseSets.loadFile rid targetSetPath normalizedDigest with
            | Ok profile -> Some profile
            | Error error ->
                errors.Add $"Target Registry release set: {error}"
                None

        let entries =
            match targetEntries targetSetPath with
            | Ok values -> values
            | Error error ->
                errors.Add error
                []

        let entryMap = entries |> List.map (fun entry -> entry.Id, entry) |> Map.ofList
        let profileMap =
            targetProfile
            |> Option.map (fun profile -> profile.Components |> List.map (fun release -> release.Id, release) |> Map.ofList)
            |> Option.defaultValue Map.empty

        let transitions = ResizeArray<CurrentUpgradeTransition>()
        let embeddedTransitions = ResizeArray<CurrentUpgradeTransition * ComponentDefinition * ProfileComponent>()

        for request in manifest.Components do
            match request.Version, entryMap |> Map.tryFind request.Id with
            | Some fromVersion, Some targetEntry when fromVersion <> targetEntry.Version ->
                if isDowngrade fromVersion targetEntry.Version then
                    errors.Add
                        $"Current Registry selection would downgrade '{request.Id}' from {fromVersion} to {targetEntry.Version}; --current never downgrades."
                else
                    let mutable mode = "refused"

                    match profileMap |> Map.tryFind request.Id with
                    | Some release when release.Lifecycle.IsSome ->
                        mode <- "repository-lifecycle"
                    | Some release ->
                        match qualifiedEmbeddedTarget request.Id targetEntry.Version with
                        | Some definition ->
                            mode <- "embedded-qualified-lifecycle"
                            embeddedTransitions.Add(
                                ({ Id = request.Id
                                   FromVersion = fromVersion
                                   ToVersion = targetEntry.Version
                                   Role = targetEntry.Role
                                   Mode = mode },
                                 definition,
                                 release)
                            )
                        | None ->
                            errors.Add
                                $"'{request.Id}' {fromVersion} -> {targetEntry.Version} is a native change, but the target release declares no {RepositoryLifecycleContract.Capability} contract and this Conditor build has not qualified that exact lifecycle version."
                    | None ->
                        match Registry.tryFind request.Id with
                        | Some definition when targetEntry.Role = "project-binding" && definition.ApplicationBinding.IsSome ->
                            mode <- "project-binding"
                        | _ ->
                            errors.Add
                                $"'{request.Id}' {fromVersion} -> {targetEntry.Version} is selected by Registry as {targetEntry.Role}/{targetEntry.DistributionClass}, but Conditor has no safe repository upgrade contract for that distribution."

                    transitions.Add
                        { Id = request.Id
                          FromVersion = fromVersion
                          ToVersion = targetEntry.Version
                          Role = targetEntry.Role
                          Mode = mode }
            | Some _, None when Registry.tryFind request.Id |> Option.isNone ->
                errors.Add
                    $"Registry-only component '{request.Id}' is absent from the target current release set; Conditor will not discard its authority."
            | None, Some targetEntry ->
                errors.Add
                    $"Component '{request.Id}' has no explicit installed version; current upgrade requires an exact old version before selecting {targetEntry.Version}."
            | _ -> ()

        let transitionList : CurrentUpgradeTransition list =
            transitions
            |> Seq.toList
            |> List.filter (fun transition -> transition.Mode <> "refused")

        let fileChanges = ResizeArray<CurrentUpgradeFileChange>()

        for transition in transitionList |> List.filter (fun item -> item.Mode = "project-binding") do
            match Registry.tryFind transition.Id, entryMap |> Map.tryFind transition.Id with
            | Some definition, Some entry ->
                match definition.ApplicationBinding with
                | Some NpmDependency ->
                    match npmBindingChange target definition entry with
                    | Ok change -> fileChanges.Add change
                    | Error error -> errors.Add error
                | Some NugetReference ->
                    match nugetBindingChange target definition entry with
                    | Ok change -> fileChanges.Add change
                    | Error error -> errors.Add error
                | None ->
                    errors.Add $"Project binding '{transition.Id}' has no embedded target-binding declaration."
            | _ ->
                errors.Add $"Project binding '{transition.Id}' could not be resolved for a safe file edit."

        match foundationsChange target transitionList with
        | Ok(Some change) -> fileChanges.Add change
        | Ok None -> ()
        | Error error -> errors.Add error

        let manifestIds = manifest.Components |> List.map _.Id |> Set.ofList

        let workstationProfile =
            targetProfile
            |> Option.map (fun profile ->
                { profile with
                    Components =
                        profile.Components
                        |> List.filter (fun release -> manifestIds.Contains release.Id) })

        let workstationPlan =
            workstationProfile
            |> Option.bind (fun profile ->
                if profile.Components.IsEmpty then
                    None
                else
                    let prerequisites = Engine.discover probe profile.Prerequisites
                    let plan = Engine.plan ctx profile rid prerequisites []
                    plan.Refusals |> List.iter errors.Add
                    Some plan)

        let changedIds = transitionList |> List.map _.Id |> Set.ofList

        let genericChangedProfile =
            targetProfile
            |> Option.map (fun profile ->
                { profile with
                    Components =
                        profile.Components
                        |> List.filter (fun release ->
                            changedIds.Contains release.Id && release.Lifecycle.IsSome)
                        |> List.map (fun release -> { release with Role = Some "repository-lifecycle" }) })

        let genericUpgradePlan =
            genericChangedProfile
            |> Option.bind (fun profile ->
                if profile.Components.IsEmpty then None
                else
                    let plan = Lifecycle.plan ctx profile rid (Path.GetFullPath target) LifecycleOperation.Upgrade
                    plan.Refusals |> List.iter errors.Add
                    Some plan)

        let genericVerifyProfile =
            targetProfile
            |> Option.map (fun profile ->
                { profile with
                    Components =
                        profile.Components
                        |> List.filter (fun release ->
                            manifestIds.Contains release.Id && release.Lifecycle.IsSome)
                        |> List.map (fun release -> { release with Role = Some "repository-lifecycle" }) })

        let genericVerifyPlan =
            genericVerifyProfile
            |> Option.bind (fun profile ->
                if profile.Components.IsEmpty then None
                else
                    let plan = Lifecycle.plan ctx profile rid (Path.GetFullPath target) LifecycleOperation.Verify
                    plan.Refusals |> List.iter errors.Add
                    Some plan)

        let targetManifestText =
            match renderTargetManifest manifestPath normalizedDigest transitionList with
            | Ok text -> text
            | Error error ->
                errors.Add error
                File.ReadAllText manifestPath

        let currentManifestSha = sha256File manifestPath
        let transitionText =
            transitionList
            |> List.sortBy (fun (transition: CurrentUpgradeTransition) -> transition.Id)
            |> List.map (fun (transition: CurrentUpgradeTransition) ->
                $"{transition.Id}|{transition.FromVersion}|{transition.ToVersion}|{transition.Role}|{transition.Mode}")
            |> String.concat "\n"

        let fileChangeText =
            fileChanges
            |> Seq.sortBy (fun change -> change.Path)
            |> Seq.map (fun change ->
                let relative = Path.GetRelativePath(Path.GetFullPath target, change.Path).Replace('\\', '/')
                $"{relative}|{change.BeforeSha256}|{change.AfterSha256}")
            |> String.concat "\n"

        let profileIdentity =
            targetProfile
            |> Option.map (fun profile ->
                let sourceIdentity = profile.SourceIdentity |> Option.defaultValue "-"
                $"profile={profile.Id}@{profile.Version}|{sourceIdentity}")
            |> Option.defaultValue "profile=<invalid>"

        let digestMaterial =
            String.concat
                "\n"
                [ "conditor.current-upgrade-plan/v1"
                  $"target={Path.GetFullPath target}"
                  $"manifest=sha256:{currentManifestSha}"
                  $"targetSet=sha256:{normalizedDigest}"
                  profileIdentity
                  workstationPlan |> Option.map (fun plan -> $"workstation={plan.Digest}") |> Option.defaultValue "workstation=-"
                  genericUpgradePlan |> Option.map (fun plan -> $"lifecycleUpgrade={plan.Digest}") |> Option.defaultValue "lifecycleUpgrade=-"
                  genericVerifyPlan |> Option.map (fun plan -> $"lifecycleVerify={plan.Digest}") |> Option.defaultValue "lifecycleVerify=-"
                  transitionText
                  fileChangeText ]

        targetProfile
        |> Option.map (fun profile ->
            { Target = Path.GetFullPath target
              ManifestPath = Path.GetFullPath manifestPath
              CurrentManifestSha256 = currentManifestSha
              TargetSetPath = Path.GetFullPath targetSetPath
              TargetSetSha256 = normalizedDigest
              TargetProfile = profile
              TargetManifestText = targetManifestText
              Transitions = transitionList
              WorkstationPlan = workstationPlan
              GenericUpgradePlan = genericUpgradePlan
              GenericVerifyPlan = genericVerifyPlan
              EmbeddedTransitions = embeddedTransitions |> Seq.toList
              FileChanges = fileChanges |> Seq.toList
              Refusals = errors |> Seq.distinct |> Seq.toList
              Digest = "sha256:" + sha256Text digestMaterial })

    let preview target manifestPath manifest targetSetPath targetDigest ctx probe =
        if not (File.Exists manifestPath) then
            Error [ $"Manifest not found: {manifestPath}" ]
        elif not (File.Exists targetSetPath) then
            Error [ $"Target resolved set not found: {targetSetPath}" ]
        else
            match prepare target manifestPath manifest targetSetPath targetDigest ctx probe with
            | None -> Error [ "Unable to construct a current-upgrade plan from the target Registry release set." ]
            | Some plan -> Ok plan

    let private writeAtomically (path: string) (bytes: byte array) =
        let parent = Path.GetDirectoryName path |> Option.ofObj |> Option.defaultValue "."
        Directory.CreateDirectory parent |> ignore
        let temporary = $"{path}.conditor-current-{Guid.NewGuid():N}.tmp"

        try
            File.WriteAllBytes(temporary, bytes)
            File.Move(temporary, path, true)
        finally
            if File.Exists temporary then File.Delete temporary

    let private executeEmbedded
        (target: string)
        (ctx: WorkstationContext)
        ((transition, definition, release): CurrentUpgradeTransition * ComponentDefinition * ProfileComponent)
        =
        let executable = installedExecutable ctx release
        let upgrade = ProcessRunner.runProcess target executable definition.UpgradeArguments

        if upgrade.ExitCode <> 0 then
            Error
                [ $"Current upgrade stopped at {transition.Id}@{transition.ToVersion} upgrade."
                  $"Executable: {executable}"
                  upgrade.StandardOutput.Trim()
                  upgrade.StandardError.Trim() ]
            |> Result.mapError (List.filter (String.IsNullOrWhiteSpace >> not))
        else
            let verify = ProcessRunner.runProcess target executable definition.VerifyArguments

            if verify.ExitCode <> 0 then
                Error
                    [ $"Current upgrade installed {transition.Id}@{transition.ToVersion}, but its repository verification failed."
                      verify.StandardOutput.Trim()
                      verify.StandardError.Trim() ]
                |> Result.mapError (List.filter (String.IsNullOrWhiteSpace >> not))
            else
                Ok()

    let apply
        target
        manifestPath
        manifest
        targetSetPath
        targetDigest
        ctx
        probe
        authorization
        =
        match preview target manifestPath manifest targetSetPath targetDigest ctx probe with
        | Error errors -> Error errors
        | Ok plan when plan.Digest <> authorization ->
            Error
                [ $"Authorization {authorization} does not match the current-upgrade plan {plan.Digest}."
                  "Run conditor upgrade --current --check again and review the exact current plan." ]
        | Ok plan when not plan.Refusals.IsEmpty ->
            Error plan.Refusals
        | Ok plan ->
            let workstationResult =
                match plan.WorkstationPlan with
                | None -> Ok()
                | Some workstation ->
                    match Engine.apply ctx probe workstation workstation.Digest true with
                    | Error error -> Error [ $"Workstation current-upgrade failed: {error}" ]
                    | Ok result ->
                        match result.Failed with
                        | Some(step, detail) -> Error [ $"Workstation current-upgrade failed at {step}: {detail}" ]
                        | None -> Ok()

            workstationResult
            |> Result.bind (fun () ->
                match plan.GenericUpgradePlan with
                | None -> Ok()
                | Some lifecycle ->
                    match Lifecycle.apply ctx probe lifecycle lifecycle.Digest with
                    | Error error -> Error [ $"Repository lifecycle current-upgrade failed: {error}" ]
                    | Ok result ->
                        match result.Failed with
                        | Some(step, detail) -> Error [ $"Repository lifecycle current-upgrade failed at {step}: {detail}" ]
                        | None -> Ok())
            |> Result.bind (fun () ->
                plan.EmbeddedTransitions
                |> List.fold
                    (fun state transition ->
                        state |> Result.bind (fun () -> executeEmbedded target ctx transition))
                    (Ok()))
            |> Result.bind (fun () ->
                match plan.GenericVerifyPlan with
                | None -> Ok()
                | Some lifecycle ->
                    match Lifecycle.apply ctx probe lifecycle lifecycle.Digest with
                    | Error error -> Error [ $"Post-upgrade generic verification failed: {error}" ]
                    | Ok result ->
                        match result.Failed with
                        | Some(step, detail) -> Error [ $"Post-upgrade generic verification failed at {step}: {detail}" ]
                        | None -> Ok())
            |> Result.bind (fun () ->
                let authorityBytes = File.ReadAllBytes plan.TargetSetPath
                let observed = sha256Bytes authorityBytes

                if observed <> plan.TargetSetSha256 then
                    Error
                        [ $"Target Registry authority changed after authorization: expected sha256:{plan.TargetSetSha256}, observed sha256:{observed}." ]
                elif sha256File manifestPath <> plan.CurrentManifestSha256 then
                    Error [ "conditor.json changed after current-upgrade authorization; governance was not modified." ]
                else
                    let pendingRelative = $".conditor/authority/current-upgrade-{Guid.NewGuid():N}.json"
                    let pendingPath = Path.Combine(Path.GetFullPath target, pendingRelative)
                    Directory.CreateDirectory(Path.GetDirectoryName pendingPath |> Option.ofObj |> Option.defaultValue target) |> ignore
                    File.WriteAllBytes(pendingPath, authorityBytes)

                    match Manifest.parseText plan.TargetManifestText with
                    | Error errors ->
                        File.Delete pendingPath
                        Error errors
                    | Ok targetManifest ->
                        let verificationManifest =
                            { targetManifest with
                                RegistryAuthority =
                                    Some
                                        { Kind = "resolved-release-set"
                                          Path = pendingRelative.Replace('\\', '/')
                                          Sha256 = plan.TargetSetSha256 } }

                        let verification =
                            Requirements.verify target verificationManifest
                            |> Result.bind (fun () ->
                                Planner.create target Verify verificationManifest
                                |> Result.bind (fun verifyPlan ->
                                    Installer.execute target String.Empty verifyPlan |> Result.map ignore))

                        match verification with
                        | Error errors ->
                            File.Delete pendingPath
                            Error(
                                "Full repository verification failed after component upgrades; existing Conditor governance was left unchanged."
                                :: errors
                            )
                        | Ok() ->
                            let finalAuthorityPath = Path.Combine(Path.GetFullPath target, AuthorityPath)
                            let oldAuthority =
                                if File.Exists finalAuthorityPath then Some(File.ReadAllBytes finalAuthorityPath) else None
                            let oldManifest = File.ReadAllBytes manifestPath

                            let commitResult =
                                try
                                    writeAtomically finalAuthorityPath authorityBytes
                                    writeAtomically manifestPath (Encoding.UTF8.GetBytes plan.TargetManifestText)

                                    match Manifest.load manifestPath with
                                    | Error errors -> Error errors
                                    | Ok committedManifest ->
                                        match Planner.create target Init committedManifest with
                                        | Error errors -> Error errors
                                        | Ok lockPlan ->
                                            let lockPath = LockFile.write target manifestPath lockPlan
                                            let remaining =
                                                match preview target manifestPath committedManifest targetSetPath targetDigest ctx probe with
                                                | Ok second -> second.Transitions.IsEmpty
                                                | Error _ -> false

                                            Ok
                                                { ChangedComponents = plan.Transitions |> List.map _.Id
                                                  LockPath = lockPath
                                                  AuthorityPath = finalAuthorityPath
                                                  NoRemainingVersionChanges = remaining }
                                with ex ->
                                    writeAtomically manifestPath oldManifest

                                    match oldAuthority with
                                    | Some bytes -> writeAtomically finalAuthorityPath bytes
                                    | None -> if File.Exists finalAuthorityPath then File.Delete finalAuthorityPath

                                    Error [ $"Unable to commit current-upgrade governance atomically: {ex.Message}" ]

                            if File.Exists pendingPath then File.Delete pendingPath
                            commitResult)
