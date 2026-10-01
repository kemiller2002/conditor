open System
open System.IO
open System.Text.Json
open Conditor.Core
open Aegis

type private ManifestSelection =
    { ManifestPath: string
      Preset: ResolvedPreset option }

let private usage () =
    Console.WriteLine "Conditor"
    Console.WriteLine "  conditor presets"
    Console.WriteLine "  conditor components [--json]"
    Console.WriteLine "  conditor compatibility [--json]"
    Console.WriteLine "  conditor plan   [--preset NAME | --manifest PATH] [--target PATH]"
    Console.WriteLine "  conditor init   [--preset NAME | --manifest PATH] [--target PATH]"
    Console.WriteLine "  conditor verify [--manifest PATH] [--target PATH]"
    Console.WriteLine "  conditor doctor [--json] [--manifest PATH] [--target PATH]"
    Console.WriteLine "  conditor status [--json] [--manifest PATH] [--target PATH]"
    Console.WriteLine "  conditor repair [--manifest PATH] [--target PATH]"
    Console.WriteLine "  conditor upgrade [--check] [--manifest PATH] [--target PATH]"
    Console.WriteLine "  conditor resume [--launcher codex|claude] [--manifest PATH] [--target PATH]"
    Console.WriteLine "  conditor handoff [--resume] [--launcher codex|claude] --prompt-file ABSOLUTE_PATH [--manifest PATH] [--target PATH]"
    Console.WriteLine "  conditor start  [--preset NAME | --manifest PATH] [--check] [--launcher codex|claude] [--target PATH]"
    Console.WriteLine "  conditor workstation plan      [--profile NAME|PATH] [--with OPTIONAL]* [--home DIR] [--json]"
    Console.WriteLine "  conditor workstation apply     --authorize PLAN-DIGEST [--profile NAME|PATH] [--home DIR] [--artifact-mirror DIR] [--offline] [--praxis PATH] [--target-id ID] [--no-rollback]"
    Console.WriteLine "  conditor workstation status    [--home DIR] [--json]"
    Console.WriteLine "  conditor workstation reconcile --step ID [--profile NAME|PATH] [--home DIR]"
    Console.WriteLine "  conditor bundle create --resolved-set PATH --resolved-set-sha256 SHA256 --output DIR"
    Console.WriteLine "  conditor bundle verify --path DIR"
    Console.WriteLine "  conditor uninstall --plan [--home DIR] [--json]"
    Console.WriteLine "  conditor uninstall --authorize PLAN-DIGEST [--profile NAME|PATH] [--home DIR] [--praxis PATH] [--target-id ID]"

let private optionValue name (args: string array) =
    args
    |> Array.tryFindIndex ((=) name)
    |> Option.bind (fun index ->
        if index + 1 < args.Length then Some args[index + 1] else None)

let private hasFlag name (args: string array) =
    args |> Array.contains name

let private writeErrors (errors: string list) =
    errors |> List.iter (fun (error: string) -> Console.Error.WriteLine error)

let private resolveManifestSelection command target (args: string array) =
    let presetName = optionValue "--preset" args
    let manifestPath = optionValue "--manifest" args

    match presetName, manifestPath with
    | Some _, Some _ ->
        Error [ "Choose either --preset or --manifest, not both." ]
    | Some presetName, None ->
        if command <> "plan" && command <> "init" && command <> "start" then
            Error [ $"--preset is supported by plan, init, and start; '{command}' uses the repository's conditor.json." ]
        else
            Presets.resolve presetName
            |> Result.map (fun preset ->
                { ManifestPath = preset.ManifestPath
                  Preset = Some preset })
    | None, Some manifestPath ->
        Ok
            { ManifestPath = Path.GetFullPath manifestPath
              Preset = None }
    | None, None ->
        Ok
            { ManifestPath = Path.Combine(target, "conditor.json") |> Path.GetFullPath
              Preset = None }

let private createPlan operation target selection manifest =
    Planner.create target operation manifest
    |> Result.map (fun plan ->
        match operation, selection.Preset with
        | Init, Some preset -> Presets.bindToTarget preset plan
        | _ -> plan)

/// After a successful repository install (every action succeeded and the
/// lock was written), register each installed component with Project
/// Administration through `praxis installation register`, when a Praxis with
/// that capability is configured (`CONDITOR_PRAXIS`). The Conditor lock stays
/// local installation evidence; it is not the central inventory. Optional:
/// an unavailable integration never fails the install.
let private registerRepositoryComponents (target: string) (plan: InstallationPlan) =
    match Environment.GetEnvironmentVariable "CONDITOR_PRAXIS" |> Option.ofObj with
    | None -> ()
    | Some praxis ->
        for comp in plan.Components do
            let distribution =
                match comp.Distribution with
                | HostTool -> "github-release"
                | LifecycleNpm
                | NpmPackage -> "npm"
                | NugetPackage -> "nuget"

            let result =
                ProcessRunner.runProcess
                    target
                    praxis
                    [ "installation"; "register"; "--system"; comp.Id; "--version"; comp.Version
                      "--target-kind"; "repository"; "--distribution"; distribution; "--artifact"; $"{comp.Package}@{comp.Version}"
                      "--evidence"; "lock=.conditor/lock.json" ]

            let summary = result.StandardOutput.Trim().Split('\n') |> Array.tryLast |> Option.defaultValue ""
            Console.WriteLine $"  registration {comp.Id} {comp.Version}: {summary}"

let private run operation shouldExecute target selection =
    match Manifest.load selection.ManifestPath with
    | Error errors ->
        writeErrors errors
        2
    | Ok manifest ->
        match createPlan operation target selection manifest with
        | Error errors ->
            writeErrors errors
            3
        | Ok plan ->
            Console.WriteLine $"Conditor plan for '{plan.ProjectName}'"
            Installer.describe plan |> List.iter (fun (line: string) -> Console.WriteLine line)

            if not shouldExecute then
                0
            else
                match Installer.execute target selection.ManifestPath plan with
                | Ok(Some lockPath) ->
                    Console.WriteLine $"Conditor completed successfully. Lock file: {lockPath}"
                    registerRepositoryComponents target plan
                    0
                | Ok None ->
                    Console.WriteLine "Conditor completed successfully."
                    0
                | Error errors ->
                    writeErrors errors
                    4

let private withLauncherOverride launcherOverride (manifest: ProjectManifest) =
    match launcherOverride with
    | None -> Ok manifest
    | Some launcher when launcher <> "codex" && launcher <> "claude" ->
        Error [ $"Unsupported launcher override '{launcher}'. Supported launchers: codex, claude." ]
    | Some launcher ->
        match manifest.Execution with
        | None -> Error [ "--launcher requires an execution section in the Conditor manifest." ]
        | Some execution when not execution.Enabled ->
            Error [ "--launcher cannot enable execution that the Conditor manifest has disabled." ]
        | Some execution ->
            Ok { manifest with Execution = Some { execution with Launcher = Some launcher } }

let private runEstablishedStart checkOnly launcherOverride target manifestPath =
    match Manifest.load manifestPath with
    | Error errors ->
        writeErrors errors
        2
    | Ok loadedManifest ->
        match withLauncherOverride launcherOverride loadedManifest with
        | Error errors ->
            writeErrors errors
            5
        | Ok manifest when checkOnly ->
            match Execution.check target manifestPath manifest with
            | Error errors ->
                writeErrors errors
                5
            | Ok ready ->
                Console.WriteLine $"Execution ready: launcher={ready.Launcher}; mission={ready.Mission.Id}; state={ready.MissionState}; contract={ready.ContractPath}"
                0
        | Ok manifest ->
            match Execution.start target manifestPath manifest with
            | Error errors ->
                writeErrors errors
                6
            | Ok result ->
                if not (String.IsNullOrWhiteSpace result.StandardOutput) then
                    Console.WriteLine(result.StandardOutput.Trim())

                Console.WriteLine "Launcher exited successfully. Praxis remains authoritative for mission completion."
                0

let private runResume launcherOverride target manifestPath =
    match Manifest.load manifestPath with
    | Error errors ->
        writeErrors errors
        2
    | Ok loadedManifest ->
        match withLauncherOverride launcherOverride loadedManifest with
        | Error errors ->
            writeErrors errors
            5
        | Ok manifest ->
            match Execution.resume target manifestPath manifest with
            | Error errors ->
                writeErrors errors
                10
            | Ok result ->
                if not (String.IsNullOrWhiteSpace result.StandardOutput) then
                    Console.WriteLine(result.StandardOutput.Trim())

                Console.WriteLine "Launcher resume exited successfully. Praxis remains authoritative for mission completion."
                0

let private writeExternalPrompt (target: string) (promptPath: string) (promptText: string) =
    if not (Path.IsPathRooted promptPath) then
        Error [ "--prompt-file must be an absolute path outside the target repository." ]
    else
        let root = Path.GetFullPath target
        let fullPath = Path.GetFullPath promptPath
        let rootPrefix =
            root.TrimEnd(Path.DirectorySeparatorChar) + string Path.DirectorySeparatorChar

        let comparison =
            if OperatingSystem.IsWindows() then StringComparison.OrdinalIgnoreCase else StringComparison.Ordinal

        if fullPath = root || fullPath.StartsWith(rootPrefix, comparison) then
            Error [ "--prompt-file must be outside the target repository." ]
        else
            try
                match Path.GetDirectoryName fullPath with
                | null -> ()
                | parent when String.IsNullOrWhiteSpace parent -> ()
                | parent -> Directory.CreateDirectory parent |> ignore

                let temporary = $"{fullPath}.conditor-{Guid.NewGuid():N}.tmp"

                try
                    File.WriteAllText(temporary, promptText)
                    File.Move(temporary, fullPath, true)
                    Ok fullPath
                finally
                    if File.Exists temporary then
                        File.Delete temporary
            with ex ->
                Error [ $"Unable to write external provider prompt: {ex.Message}" ]

let private runHandoff resumeOnly launcherOverride promptPath target manifestPath =
    match promptPath with
    | None ->
        writeErrors [ "handoff requires --prompt-file with an absolute path outside the target repository." ]
        11
    | Some requestedPath ->
        match Manifest.load manifestPath with
        | Error errors ->
            writeErrors errors
            2
        | Ok loadedManifest ->
            match withLauncherOverride launcherOverride loadedManifest with
            | Error errors ->
                writeErrors errors
                5
            | Ok manifest ->
                match Execution.handoff resumeOnly target manifestPath manifest with
                | Error errors ->
                    writeErrors errors
                    11
                | Ok(ready, promptText) ->
                    match writeExternalPrompt target requestedPath promptText with
                    | Error errors ->
                        writeErrors errors
                        11
                    | Ok fullPath ->
                        Console.WriteLine $"External provider handoff ready: launcher={ready.Launcher}; mission={ready.Mission.Id}; state=active; prompt={fullPath}"
                        0

let private presetTargetState target (preset: ResolvedPreset) =
    let targetManifest = Path.Combine(target, "conditor.json")
    let lockPath = Path.Combine(target, ".conditor", "lock.json")

    if File.Exists targetManifest then
        let existing = File.ReadAllText targetManifest

        if existing <> preset.Content then
            Error
                [ $"Target conditor.json does not match built-in preset '{preset.Id}'."
                  "Conditor will not replace a different project declaration during start." ]
        else
            Ok(File.Exists lockPath, targetManifest)
    else
        Ok(false, targetManifest)

let private runStart checkOnly launcherOverride target selection =
    match selection.Preset with
    | None ->
        runEstablishedStart checkOnly launcherOverride target selection.ManifestPath
    | Some preset ->
        match presetTargetState target preset with
        | Error errors ->
            writeErrors errors
            5
        | Ok(true, targetManifest) ->
            runEstablishedStart checkOnly launcherOverride target targetManifest
        | Ok(false, _) when checkOnly ->
            writeErrors
                [ $"Preset '{preset.Id}' has not established this repository yet."
                  "start --check is read-only. Run 'conditor init --preset <name>' first, or run 'conditor start --preset <name>' to initialize and launch." ]
            5
        | Ok(false, targetManifest) ->
            let initialization = run Init true target selection

            if initialization <> 0 then
                initialization
            else
                runEstablishedStart false launcherOverride target targetManifest

let private runUpgrade checkOnly target manifestPath =
    match Manifest.load manifestPath with
    | Error errors ->
        writeErrors errors
        2
    | Ok manifest when checkOnly ->
        match Upgrade.preview target manifest with
        | Error errors ->
            writeErrors errors
            9
        | Ok preview ->
            if preview.ChangedComponents.IsEmpty then
                Console.WriteLine "Upgrade preview: no lifecycle version changes are required."
            else
                let changed = String.Join(", ", preview.ChangedComponents)
                Console.WriteLine $"Upgrade preview: {changed}"

            Installer.describe preview.Plan
            |> List.iter (fun line -> Console.WriteLine $"  {line}")

            0
    | Ok manifest ->
        match Upgrade.apply target manifestPath manifest with
        | Error errors ->
            writeErrors errors
            9
        | Ok result ->
            if result.ChangedComponents.IsEmpty then
                Console.WriteLine "Conditor upgrade completed: no lifecycle version changes were required."
            else
                let changed = String.Join(", ", result.ChangedComponents)
                Console.WriteLine $"Conditor upgraded lifecycle components: {changed}"

            Console.WriteLine $"Updated lock: {result.LockPath}"
            0

let private writeDoctorJson (report: Doctor.Report) =
    use stream = Console.OpenStandardOutput()
    let mutable options = JsonWriterOptions()
    options.Indented <- true
    use writer = new Utf8JsonWriter(stream, options)
    writer.WriteStartObject()
    writer.WriteNumber("schemaVersion", 1)
    writer.WriteString("project", report.Project)
    writer.WriteBoolean("healthy", report.Healthy)
    writer.WriteStartArray("findings")

    for finding in report.Findings do
        writer.WriteStartObject()
        writer.WriteString("code", finding.Code)
        writer.WriteString("severity", Doctor.severityText finding.Severity)
        writer.WriteString("area", finding.Area)
        writer.WriteString("detail", finding.Detail)

        match finding.Remediation with
        | Some remediation -> writer.WriteString("remediation", remediation)
        | None -> ()

        writer.WriteEndObject()

    writer.WriteEndArray()
    writer.WriteEndObject()
    writer.Flush()

let private runDoctor json target manifestPath =
    match Manifest.load manifestPath with
    | Error errors ->
        writeErrors errors
        2
    | Ok manifest ->
        let report = Doctor.inspect target manifestPath manifest

        if json then
            writeDoctorJson report
        else
            Console.WriteLine $"Project: {report.Project}"
            let healthText = if report.Healthy then "healthy" else "attention required"
            Console.WriteLine $"Doctor: {healthText}"

            for finding in report.Findings do
                Console.WriteLine $"  [{Doctor.severityText finding.Severity}] {finding.Code} {finding.Area}: {finding.Detail}"

                match finding.Remediation with
                | Some remediation -> Console.WriteLine $"    remediation: {remediation}"
                | None -> ()

        if report.Healthy then 0 else 12

let private runRepair target manifestPath =
    match Manifest.load manifestPath with
    | Error errors ->
        writeErrors errors
        2
    | Ok manifest ->
        match Repair.reconcile target manifestPath manifest with
        | Ok() ->
            Console.WriteLine "Conditor repair completed successfully."
            0
        | Error errors ->
            writeErrors errors
            8

let private writeStatusJson (report: Status.ProjectStatus) =
    use stream = Console.OpenStandardOutput()
    let mutable options = JsonWriterOptions()
    options.Indented <- true
    use writer = new Utf8JsonWriter(stream, options)
    writer.WriteStartObject()
    writer.WriteNumber("schemaVersion", 1)
    writer.WriteString("project", report.Project)
    writer.WriteBoolean("healthy", Status.isHealthy report)
    writer.WriteStartArray("components")

    for componentText in report.Components do
        writer.WriteStringValue componentText

    writer.WriteEndArray()
    writer.WriteStartArray("checks")

    for check in report.Checks do
        writer.WriteStartObject()
        writer.WriteString("name", check.Name)
        writer.WriteString("state", Status.stateText check.State)
        writer.WriteString("detail", check.Detail)
        writer.WriteEndObject()

    writer.WriteEndArray()
    writer.WriteEndObject()
    writer.Flush()

let private runStatus json target manifestPath =
    match Manifest.load manifestPath with
    | Error errors ->
        writeErrors errors
        2
    | Ok manifest ->
        let report = Status.inspect target manifestPath manifest

        if json then
            writeStatusJson report
        else
            Console.WriteLine $"Project: {report.Project}"

            if report.Components.IsEmpty then
                Console.WriteLine "Components: none"
            else
                Console.WriteLine "Components:"

                for componentText in report.Components do
                    Console.WriteLine $"  {componentText}"

            Console.WriteLine "Checks:"

            for check in report.Checks do
                Console.WriteLine $"  [{Status.stateText check.State}] {check.Name}: {check.Detail}"

        if Status.isHealthy report then 0 else 7

let private distributionText =
    function
    | HostTool -> "host-tool"
    | LifecycleNpm -> "lifecycle-npm"
    | NpmPackage -> "npm"
    | NugetPackage -> "nuget"

let private bindingText =
    function
    | NpmDependency -> "npm"
    | NugetReference -> "nuget"

let private sourceText =
    function
    | RegistryPackage -> "registry"
    | GitHubSource source ->
        let entrypoint =
            match source.Entrypoint with
            | NodeScript path -> $"node-script:{path}"
            | FileArtifact path -> $"file-artifact:{path}"

        $"github:{source.Repository}#{source.Commit}:{entrypoint}"

let private printComponents json =
    let descriptors = Registry.descriptors |> List.sortBy (fun descriptor -> descriptor.Definition.Id)

    if json then
        use stream = Console.OpenStandardOutput()
        let mutable options = JsonWriterOptions()
        options.Indented <- true
        use writer = new Utf8JsonWriter(stream, options)
        writer.WriteStartObject()
        writer.WriteNumber("schemaVersion", 1)
        writer.WriteStartArray("components")

        for descriptor in descriptors do
            let definition = descriptor.Definition
            writer.WriteStartObject()
            writer.WriteString("id", definition.Id)
            writer.WriteString("displayName", definition.DisplayName)
            writer.WriteString("distribution", distributionText definition.Distribution)
            writer.WriteString("package", definition.Package)
            writer.WriteString("defaultVersion", definition.DefaultVersion)
            writer.WriteString("descriptorSha256", descriptor.Sha256)
            writer.WriteStartArray("qualifiedVersions")

            for version in descriptor.QualifiedVersions |> Seq.sort do
                writer.WriteStringValue version

            writer.WriteEndArray()

            match definition.LifecycleSource with
            | Some source -> writer.WriteString("lifecycleSource", sourceText source)
            | None -> ()

            match definition.ApplicationBinding with
            | Some binding -> writer.WriteString("applicationBinding", bindingText binding)
            | None -> ()

            match definition.Command with
            | Some command -> writer.WriteString("command", command)
            | None -> ()

            writer.WriteEndObject()

        writer.WriteEndArray()
        writer.WriteEndObject()
        writer.Flush()
    else
        Console.WriteLine "Built-in Conditor components:"

        for descriptor in descriptors do
            let definition = descriptor.Definition
            let versions = descriptor.QualifiedVersions |> Seq.sort |> String.concat ", "
            let source =
                definition.LifecycleSource
                |> Option.map sourceText
                |> Option.defaultValue "application-binding-only"

            Console.WriteLine
                $"  {definition.Id}@{definition.DefaultVersion} [{distributionText definition.Distribution}] qualified={versions} descriptor={descriptor.Sha256} source={source}"

    0

let private printCompatibility json =
    if json then
        use stream = Console.OpenStandardOutput()
        let mutable options = JsonWriterOptions()
        options.Indented <- true
        use writer = new Utf8JsonWriter(stream, options)
        writer.WriteStartObject()
        writer.WriteNumber("schemaVersion", 1)
        writer.WriteStartArray("components")

        for item in Compatibility.supported |> List.sortBy _.Id do
            writer.WriteStartObject()
            writer.WriteString("id", item.Id)
            writer.WriteStartArray("versions")

            for version in item.Versions |> Seq.sort do
                writer.WriteStringValue version

            writer.WriteEndArray()
            writer.WriteString("notes", item.Notes)
            writer.WriteEndObject()

        writer.WriteEndArray()
        writer.WriteStartArray("requirements")

        for requirement in Compatibility.requirements do
            writer.WriteStartObject()
            writer.WriteString("subject", requirement.Subject)
            writer.WriteString("requires", requirement.Requires)
            writer.WriteString("reason", requirement.Reason)
            writer.WriteEndObject()

        writer.WriteEndArray()
        writer.WriteEndObject()
        writer.Flush()
    else
        Console.WriteLine "Qualified component versions:"

        for line in Compatibility.describe () do
            Console.WriteLine $"  {line}"

        Console.WriteLine "Compatibility requirements:"

        for requirement in Compatibility.requirements do
            Console.WriteLine $"  {requirement.Subject} -> {requirement.Requires}: {requirement.Reason}"

    0

let private printPresets () =
    Console.WriteLine "Built-in Conditor presets:"

    for name in Presets.names do
        Console.WriteLine $"  {name}"

    0

// ------------------------------------------------------------ workstation

open Conditor.Core.Workstation

let private printBundleSummary (verb: string) (summary: OfflineBundleSummary) =
    Console.WriteLine $"Offline bundle {verb}: {summary.Root}"
    Console.WriteLine $"  profile:      {summary.ProfileId}@{summary.ProfileVersion}"
    Console.WriteLine $"  platform:     {summary.Platform}"
    Console.WriteLine $"  resolved set: sha256:{summary.ResolvedSetSha256}"
    Console.WriteLine $"  artifacts:    {summary.ArtifactCount}"
    Console.WriteLine $"  native mirror:{summary.NativeMirror}"
    Console.WriteLine $"  packages:     {summary.PackageMirror}"

let private runBundle (args: string array) =
    match args |> Array.tryItem 1 with
    | Some "create" ->
        match optionValue "--resolved-set" args, optionValue "--resolved-set-sha256" args, optionValue "--output" args with
        | Some resolvedSet, Some digest, Some output ->
            match OfflineBundle.create (Path.GetFullPath resolvedSet) digest (Path.GetFullPath output) with
            | Ok summary ->
                printBundleSummary "created" summary
                0
            | Error error ->
                Console.Error.WriteLine error
                3
        | _ ->
            Console.Error.WriteLine "bundle create requires --resolved-set PATH --resolved-set-sha256 SHA256 --output DIR"
            2
    | Some "verify" ->
        match optionValue "--path" args with
        | Some path ->
            match OfflineBundle.verify (Path.GetFullPath path) with
            | Ok summary ->
                printBundleSummary "verified" summary
                0
            | Error error ->
                Console.Error.WriteLine error
                3
        | None ->
            Console.Error.WriteLine "bundle verify requires --path DIR"
            2
    | _ ->
        usage ()
        2

let private workstationContext (args: string array) : WorkstationContext =
    let home =
        optionValue "--home" args
        |> Option.orElse (Environment.GetEnvironmentVariable "HOME" |> Option.ofObj)
        |> Option.defaultValue (Environment.GetFolderPath Environment.SpecialFolder.UserProfile)
        |> Path.GetFullPath

    { Home = home
      ArtifactMirror = optionValue "--artifact-mirror" args |> Option.map Path.GetFullPath
      Offline = hasFlag "--offline" args
      Praxis = optionValue "--praxis" args |> Option.orElse (Environment.GetEnvironmentVariable "CONDITOR_PRAXIS" |> Option.ofObj)
      TargetId = optionValue "--target-id" args }

let private probe (executable: string) (arguments: string list) =
    ProcessRunner.runProcess (Directory.GetCurrentDirectory()) executable arguments

let private optionValues name (args: string array) =
    args |> Array.toList |> List.pairwise |> List.choose (fun (flag, value) -> if flag = name then Some value else None)

let private workstationProfile (args: string array) =
    let runtimeIdentifier = Platform.runtimeIdentifier ()

    match optionValue "--resolved-set" args with
    | Some path ->
        match optionValue "--resolved-set-sha256" args with
        | None ->
            Error "--resolved-set requires --resolved-set-sha256 SHA256 so Registry selection is integrity-bound"
        | Some digest ->
            ResolvedReleaseSets.loadFile runtimeIdentifier (Path.GetFullPath path) digest
    | None ->
        Profiles.resolve (optionValue "--profile" args |> Option.defaultValue "echelon-engineering")

let private workstationPlan (args: string array) =
    let ctx = workstationContext args

    workstationProfile args
    |> Result.map (fun profile ->
        let prerequisites = Engine.discover probe profile.Prerequisites
        ctx, Engine.plan ctx profile (Platform.runtimeIdentifier ()) prerequisites (optionValues "--with" args))

let private printPlan (json: bool) (ctx: WorkstationContext) (plan: WorkstationPlan) =
    if json then
        let o = System.Text.Json.Nodes.JsonObject()
        o["schema"] <- System.Text.Json.Nodes.JsonValue.Create "conditor.workstation-plan/v1"
        o["profile"] <- System.Text.Json.Nodes.JsonValue.Create $"{plan.Profile.Id}@{plan.Profile.Version}"
        o["runtimeIdentifier"] <- System.Text.Json.Nodes.JsonValue.Create plan.RuntimeIdentifier
        o["digest"] <- System.Text.Json.Nodes.JsonValue.Create plan.Digest
        let steps = System.Text.Json.Nodes.JsonArray()

        for s in plan.Steps do
            let n = System.Text.Json.Nodes.JsonObject()
            n["sequence"] <- System.Text.Json.Nodes.JsonValue.Create s.Sequence
            n["id"] <- System.Text.Json.Nodes.JsonValue.Create s.Id
            n["operation"] <- System.Text.Json.Nodes.JsonValue.Create(StepOperation.toWire s.Operation)
            n["resource"] <- System.Text.Json.Nodes.JsonValue.Create s.Resource
            s.Artifact |> Option.iter (fun (u, d) -> n["artifact"] <- System.Text.Json.Nodes.JsonValue.Create u; n["digest"] <- System.Text.Json.Nodes.JsonValue.Create d)
            s.Command |> Option.iter (fun c -> n["command"] <- System.Text.Json.Nodes.JsonValue.Create c)
            n["expectedReceipt"] <- System.Text.Json.Nodes.JsonValue.Create(Expected.describe s.Expected)
            n["ownership"] <- System.Text.Json.Nodes.JsonValue.Create(Ownership.toWire s.Ownership)
            n["requiresAuthorization"] <- System.Text.Json.Nodes.JsonValue.Create s.RequiresAuthorization
            steps.Add n

        o["steps"] <- steps
        let refusals = System.Text.Json.Nodes.JsonArray()
        plan.Refusals |> List.iter (fun r -> refusals.Add(System.Text.Json.Nodes.JsonValue.Create r))
        o["refusals"] <- refusals
        Console.WriteLine(o.ToJsonString(JsonSerializerOptions(WriteIndented = true)))
    else
        Console.WriteLine $"Workstation plan: profile {plan.Profile.Id}@{plan.Profile.Version} for {plan.RuntimeIdentifier} (home {ctx.Home})"

        for (p, state) in plan.Prerequisites do
            Console.WriteLine $"  prerequisite {p.Id}: %A{state}"

        for s in plan.Steps do
            let artifact = s.Artifact |> Option.map (fun (u, d) -> $"\n        artifact: {u}\n        digest:   {d} (trust: %A{s.Trust})") |> Option.defaultValue ""
            let command = s.Command |> Option.map (fun c -> $"\n        command:  {c}") |> Option.defaultValue ""
            Console.WriteLine $"  {s.Sequence,2}. {StepOperation.toWire s.Operation,-15} {s.Resource}  [{Ownership.toWire s.Ownership}]{artifact}{command}\n        expect:   {Expected.describe s.Expected}"

        for r in plan.Refusals do
            Console.WriteLine $"  REFUSED {r}"

        Console.WriteLine $"Authorize exactly these effects with: conditor workstation apply --authorize {plan.Digest}"

let private runWorkstation (args: string array) =
    match args |> Array.tryItem 1 with
    | Some "plan" ->
        match workstationPlan args with
        | Error e ->
            Console.Error.WriteLine e
            1
        | Ok(ctx, plan) ->
            printPlan (hasFlag "--json" args) ctx plan
            if plan.Refusals.IsEmpty then 0 else 3
    | Some "apply" ->
        match workstationPlan args, optionValue "--authorize" args with
        | Error e, _ ->
            Console.Error.WriteLine e
            1
        | _, None ->
            Console.Error.WriteLine "apply requires --authorize PLAN-DIGEST; run `conditor workstation plan` and review it first"
            2
        | Ok(ctx, plan), Some digest ->
            match Engine.apply ctx probe plan digest (not (hasFlag "--no-rollback" args)) with
            | Error e ->
                Console.Error.WriteLine $"REFUSED {e}"
                3
            | Ok result ->
                result.Reused |> List.iter (fun s -> Console.WriteLine $"  reused    {s} (receipt already matched)")
                result.Completed |> List.iter (fun s -> Console.WriteLine $"  completed {s} (receipt matched)")
                result.Registrations |> List.iter (fun (c, o) -> Console.WriteLine $"  registration {c}: {o}")

                match result.Failed with
                | Some(step, why) ->
                    Console.Error.WriteLine $"  FAILED {step}: {why}"
                    result.RolledBack |> List.iter (fun (s, o) -> Console.Error.WriteLine $"  rolled back {s}: {o}")
                    4
                | None ->
                    Console.WriteLine "Workstation ready."
                    0
    | Some "status" ->
        let ctx = workstationContext args
        let entries = Ledger.read ctx

        for (resource, record) in Ledger.ownership entries do
            let klass = record |> Map.tryFind "class" |> Option.defaultValue "?"
            let comp = record |> Map.tryFind "component" |> Option.defaultValue ""
            Console.WriteLine $"  {klass,-17} {resource} {comp}"

        0
    | Some "reconcile" ->
        match workstationPlan args, optionValue "--step" args with
        | Error e, _ ->
            Console.Error.WriteLine e
            1
        | _, None ->
            Console.Error.WriteLine "reconcile requires --step ID"
            2
        | Ok(ctx, plan), Some step ->
            match Engine.reconcile ctx probe plan step with
            | Ok finding ->
                Console.WriteLine $"{step}: effect {finding}"
                0
            | Error e ->
                Console.Error.WriteLine $"REFUSED {e}"
                3
    | _ ->
        usage ()
        2

let private runUninstall (args: string array) =
    let ctx = workstationContext args
    let plan = Engine.uninstallPlan ctx

    if hasFlag "--plan" args then
        for (resource, comp, action) in plan.Actions do
            let reason = match action with Engine.UninstallAction.Unresolved why -> $" ({why})" | _ -> ""
            Console.WriteLine $"  {Engine.uninstallActionToWire action,-15} {resource} {comp}{reason}"

        Console.WriteLine "  (source repositories and user work are never in scope)"
        Console.WriteLine $"Authorize exactly these actions with: conditor uninstall --authorize {plan.Digest}"
        0
    else
        match optionValue "--authorize" args with
        | None ->
            Console.Error.WriteLine "uninstall requires --plan, or --authorize PLAN-DIGEST after reviewing the plan"
            2
        | Some digest ->
            let profile = workstationProfile args |> Result.toOption

            match Engine.uninstall ctx probe profile plan digest with
            | Error e ->
                Console.Error.WriteLine $"REFUSED {e}"
                3
            | Ok(results, registrations) ->
                results |> List.iter (fun (r, _, o) -> Console.WriteLine $"  removed {r}: {o}")
                registrations |> List.iter (fun (c, o) -> Console.WriteLine $"  removal registration {c}: {o}")
                if results |> List.forall (fun (_, _, o) -> o = "match") then 0 else 4

let private execute (args: string array) =
    if args.Length = 0 then
        usage ()
        1
    else
        let command = args[0].Trim().ToLowerInvariant()

        if command = "workstation" then
            runWorkstation args
        elif command = "bundle" then
            runBundle args
        elif command = "uninstall" then
            runUninstall args
        elif command = "presets" then
            printPresets ()
        elif command = "components" then
            printComponents (hasFlag "--json" args)
        elif command = "compatibility" then
            printCompatibility (hasFlag "--json" args)
        else
            let target =
                optionValue "--target" args
                |> Option.defaultValue (Directory.GetCurrentDirectory())
                |> Path.GetFullPath

            match resolveManifestSelection command target args with
            | Error errors ->
                writeErrors errors
                1
            | Ok selection ->
                match command with
                | "plan" -> run Init false target selection
                | "init" -> run Init true target selection
                | "verify" -> run Verify true target selection
                | "doctor" -> runDoctor (hasFlag "--json" args) target selection.ManifestPath
                | "status" -> runStatus (hasFlag "--json" args) target selection.ManifestPath
                | "repair" -> runRepair target selection.ManifestPath
                | "upgrade" -> runUpgrade (hasFlag "--check" args) target selection.ManifestPath
                | "resume" -> runResume (optionValue "--launcher" args) target selection.ManifestPath
                | "handoff" ->
                    runHandoff
                        (hasFlag "--resume" args)
                        (optionValue "--launcher" args)
                        (optionValue "--prompt-file" args)
                        target
                        selection.ManifestPath
                | "start" ->
                    runStart
                        (hasFlag "--check" args)
                        (optionValue "--launcher" args)
                        target
                        selection
                | _ ->
                    usage ()
                    1


[<EntryPoint>]
let main (args: string array) =
    let version =
        System.Reflection.Assembly.GetExecutingAssembly().GetName().Version
        |> Option.ofObj
        |> Option.map string

    let config = Aegis.configure "Conditor.Cli" version [ Sinks.console ]

    match Bootstrap.validate None config with
    | Result.Error problems ->
        for problem in problems do
            let _, message = Bootstrap.describe problem
            Console.Error.WriteLine $"Aegis configuration error: {message}"

        1
    | Ok validated ->
        let scope = Aegis.scope validated "Conditor.Cli.Main" Map.empty

        let classify scope ex =
            Aegis.faultOf
                validated
                scope
                (FaultCode "CONDITOR.CLI.UNHANDLED")
                UnknownFailure
                FaultSeverity.Error
                DegradedApplication
                RequiresIntervention
                ManualIntervention
                "Conditor encountered an unexpected operational failure."
                ex

        match Aegis.capture validated scope classify (fun () -> execute args) with
        | Ok exitCode -> exitCode
        | Result.Error fault ->
            Console.Error.WriteLine $"{fault.UserMessage} Reference {fault.Id.Value}"
            1
