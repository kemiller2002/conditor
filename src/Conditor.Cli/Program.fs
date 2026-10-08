open System
open System.IO
open System.Text.Json
open Conditor.Core
open Conditor.Core.Workstation
open Aegis

type private ManifestSelection =
    { ManifestPath: string
      Preset: ResolvedPreset option }

let private usage () =
    Console.WriteLine "Conditor"
    Console.WriteLine "  conditor presets"
    Console.WriteLine "  conditor components [--json]"
    Console.WriteLine "  conditor compatibility [--json]"
    Console.WriteLine "  conditor adopt [--target PATH] [--name NAME] [--resolved-set PATH --resolved-set-sha256 SHA256] [--json] [--authorize PLAN-DIGEST]"
    Console.WriteLine "  conditor plan   [--preset NAME | --manifest PATH] [--target PATH] [--source-mirror DIR] [--offline]"
    Console.WriteLine "  conditor init   [--preset NAME | --manifest PATH] [--target PATH] [--source-mirror DIR] [--offline]"
    Console.WriteLine "  conditor verify [--manifest PATH] [--target PATH] [--source-mirror DIR] [--offline]"
    Console.WriteLine "  conditor doctor [--json] [--manifest PATH] [--target PATH]"
    Console.WriteLine "  conditor status [--json] [--manifest PATH] [--target PATH]"
    Console.WriteLine "  conditor repair [--manifest PATH] [--target PATH]"
    Console.WriteLine "  conditor upgrade [--check] [--manifest PATH] [--target PATH]"
    Console.WriteLine "  conditor upgrade --current --resolved-set PATH --resolved-set-sha256 SHA256 [--target PATH] [--home DIR] [--artifact-mirror DIR] [--offline] [--check | --authorize PLAN-DIGEST]"
    Console.WriteLine "  conditor resume [--launcher codex|claude] [--manifest PATH] [--target PATH]"
    Console.WriteLine "  conditor handoff [--resume] [--launcher codex|claude] --prompt-file ABSOLUTE_PATH [--manifest PATH] [--target PATH]"
    Console.WriteLine "  conditor start  [--preset NAME | --manifest PATH] [--check] [--launcher codex|claude] [--target PATH]"
    Console.WriteLine "  conditor supervise [--launcher claude|codex] [--permission-mode MODE] [--until ISO-8601] [--max-launches N] [--stop-file PATH] [--dry-run] [--manifest PATH] [--target PATH]"
    Console.WriteLine "  conditor repo create --repository OWNER/NAME [--target PATH] [--public] [--deploy github-pages] [--dry-run]"
    Console.WriteLine "  conditor requirements import [--target PATH] [--manifest PATH] [--check] [--authorize PLAN-DIGEST]"
    Console.WriteLine "  conditor workstation plan      [--profile NAME|PATH] [--with OPTIONAL]* [--home DIR] [--json]"
    Console.WriteLine "  conditor workstation apply     --authorize PLAN-DIGEST [--profile NAME|PATH] [--home DIR] [--artifact-mirror DIR] [--offline] [--praxis PATH] [--target-id ID] [--no-rollback]"
    Console.WriteLine "  conditor workstation status    [--home DIR] [--json]"
    Console.WriteLine "  conditor workstation reconcile --step ID [--profile NAME|PATH] [--home DIR]"
    Console.WriteLine "  conditor lifecycle plan  --operation init|status|verify|doctor|upgrade --root DIR --resolved-set PATH --resolved-set-sha256 SHA256 [--home DIR] [--json]"
    Console.WriteLine "  conditor lifecycle apply --authorize PLAN-DIGEST --operation ... --root DIR --resolved-set PATH --resolved-set-sha256 SHA256 [--home DIR] [--json]"
    Console.WriteLine "  conditor bundle create --resolved-set PATH --resolved-set-sha256 SHA256 --output DIR [--manifest PATH]"
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
                match Installer.executeWithSignals target selection.ManifestPath plan with
                | Ok(lockPath, signals) when not signals.IsEmpty ->
                    Console.WriteLine
                        $"Structural review signals ({signals.Length}; reported by integrity gates, not blocking):"

                    for signal in signals do
                        Console.WriteLine $"  {VerificationGate.describe signal}"

                    match lockPath with
                    | Some path ->
                        Console.WriteLine $"Conditor completed successfully. Lock file: {path}"
                        registerRepositoryComponents target plan
                    | None -> Console.WriteLine "Conditor completed successfully."

                    0
                | Ok(Some lockPath, _) ->
                    Console.WriteLine $"Conditor completed successfully. Lock file: {lockPath}"
                    registerRepositoryComponents target plan
                    0
                | Ok(None, _) ->
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

let private runSupervise (args: string array) target manifestPath =
    let permissionOverride = optionValue "--permission-mode" args

    let until =
        match optionValue "--until" args with
        | None -> Ok None
        | Some value ->
            match DateTimeOffset.TryParse(value, Globalization.CultureInfo.InvariantCulture, Globalization.DateTimeStyles.None) with
            | true, parsed when value.Contains('T') && (value.EndsWith "Z" || value.LastIndexOfAny([| '+'; '-' |]) > value.IndexOf('T')) -> Ok(Some parsed)
            | _ -> Error [ $"--until '{value}' must be an ISO-8601 time with an explicit offset, such as 2026-11-07T16:40:00-05:00." ]

    let maxLaunches =
        match optionValue "--max-launches" args with
        | None -> Ok None
        | Some value ->
            match Int32.TryParse value with
            | true, number when number > 0 -> Ok(Some number)
            | _ -> Error [ "--max-launches must be a positive integer." ]

    let manifest =
        Manifest.load manifestPath
        |> Result.bind (withLauncherOverride (optionValue "--launcher" args))
        |> Result.bind (fun manifest ->
            match permissionOverride, manifest.Execution with
            | None, _ -> Ok manifest
            | Some mode, _ when not (List.contains mode [ "acceptEdits"; "auto"; "bypassPermissions"; "dontAsk" ]) ->
                Error [ $"Unsupported --permission-mode '{mode}'. Supported: acceptEdits, auto, bypassPermissions, dontAsk." ]
            | Some mode, Some execution -> Ok { manifest with Execution = Some { execution with PermissionMode = Some mode } }
            | Some _, None -> Error [ "--permission-mode requires an execution section in the Conditor manifest." ])

    match manifest, until, maxLaunches with
    | Error errors, _, _
    | _, Error errors, _
    | _, _, Error errors ->
        writeErrors errors
        2
    | Ok manifest, Ok until, Ok maxLaunches ->
        match Execution.check target manifestPath manifest with
        | Error errors ->
            writeErrors errors
            10
        | Ok ready ->
            let policy =
                let defaults = Supervisor.defaultPolicy until
                { defaults with MaxLaunches = maxLaunches |> Option.defaultValue defaults.MaxLaunches }

            let importedQueue = Supervisor.importedTrace target ready.Mission.Id

            let stopFile =
                optionValue "--stop-file" args
                |> Option.map Path.GetFullPath
                |> Option.defaultValue (
                    let gitDirectory = Path.Combine(target, ".git")

                    if Directory.Exists gitDirectory then
                        Path.Combine(gitDirectory, "conditor-supervisor.stop")
                    else
                        Path.Combine(Path.GetTempPath(), "conditor-supervisor.stop")
                )

            let commandFor kind attempt =
                let context: Launcher.InstructionContext =
                    { Kind = kind
                      ImportedQueue = importedQueue
                      Until = until
                      Attempt = attempt }

                Launcher.command ready (Launcher.instructionFor context ready)

            let describeCommand (executable: string, arguments: string list) =
                let shown =
                    arguments
                    |> List.map (fun argument -> if argument.Contains '\n' || argument.Contains ' ' then "\"" + argument.Replace("\"", "\\\"") + "\"" else argument)

                String.Join(" ", executable :: shown)

            let untilText = until |> Option.map (fun value -> value.ToString("o")) |> Option.defaultValue "none"
            Console.WriteLine $"Supervising {ready.Mission.Id} ({ready.MissionState}) with {ready.Launcher}, permission mode {ready.PermissionMode}; deadline {untilText}; at most {policy.MaxLaunches} launches."
            Console.WriteLine $"Stop after the current session with: touch {stopFile}"

            if hasFlag "--dry-run" args then
                let firstKind = if ready.MissionState = "ready" then Launcher.StartRun else Launcher.ResumeRun

                match commandFor firstKind 1, commandFor Launcher.ResumeRun 2 with
                | Ok first, Ok resume ->
                    Console.WriteLine "Dry run: nothing was activated or launched."
                    Console.WriteLine $"First launch ({firstKind}):"
                    Console.WriteLine $"  {describeCommand first}"
                    Console.WriteLine "Every later launch (a fresh session that resumes from Praxis state):"
                    Console.WriteLine $"  {describeCommand resume}"
                    Console.WriteLine $"A session shorter than {policy.FastExit.TotalMinutes:F0} min backs off from {policy.BaseBackoff.TotalSeconds:F0}s, doubling to at most {policy.MaxBackoff.TotalMinutes:F0} min; otherwise the next launch follows after {policy.Pause.TotalSeconds:F0}s."
                    0
                | Error error, _
                | _, Error error ->
                    Console.Error.WriteLine error
                    10
            else
                let effects: SupervisorEffects =
                    { Now = fun () -> DateTimeOffset.Now
                      Sleep = fun span -> Threading.Thread.Sleep span
                      Observe =
                        fun () ->
                            Supervisor.observeState target ready.Mission.Id
                            |> Result.map (fun (missionState, openSlices) ->
                                { Now = DateTimeOffset.Now
                                  MissionState = missionState
                                  OpenSlices = openSlices
                                  StopRequested = File.Exists stopFile })
                      Activate = fun () -> Mission.activate target ready.Mission
                      Run =
                        fun kind attempt ->
                            commandFor kind attempt
                            |> Result.bind (fun (executable, arguments) -> ProcessRunner.runAttached target executable arguments)
                      Log =
                        fun line ->
                            let stamp = DateTimeOffset.Now.ToString("HH:mm:ss")
                            Console.WriteLine $"[conditor {stamp}] {line}" }

                match Supervisor.run policy effects with
                | Error errors ->
                    writeErrors errors
                    10
                | Ok reason ->
                    Console.WriteLine $"Supervisor finished: {reason}"
                    0

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

let private currentUpgradeContext (args: string array) : WorkstationContext =
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

let private currentUpgradeProbe (executable: string) (arguments: string list) =
    ProcessRunner.runProcess (Directory.GetCurrentDirectory()) executable arguments

let private printCurrentUpgradePlan (plan: CurrentUpgradePlan) =
    Console.WriteLine $"Current upgrade plan: {plan.TargetProfile.Id}@{plan.TargetProfile.Version}"
    Console.WriteLine $"  target set: sha256:{plan.TargetSetSha256}"
    Console.WriteLine $"  manifest:   sha256:{plan.CurrentManifestSha256}"

    if plan.Transitions.IsEmpty then
        Console.WriteLine "  version transitions: none"
    else
        Console.WriteLine "  version transitions:"

        for transition in plan.Transitions do
            Console.WriteLine
                $"    {transition.Id}: {transition.FromVersion} -> {transition.ToVersion} [{transition.Mode}]"

    match plan.WorkstationPlan with
    | Some workstation ->
        Console.WriteLine $"  workstation effects: {workstation.Steps.Length} (nested plan {workstation.Digest})"

        for step in workstation.Steps do
            Console.WriteLine
                $"    {step.Sequence,2}. {StepOperation.toWire step.Operation,-12} {step.Resource} :: {Expected.describe step.Expected}"
    | None ->
        Console.WriteLine "  workstation effects: none"

    match plan.GenericInitPlan with
    | Some lifecycle ->
        let optIns =
            plan.OptIns
            |> List.map (fun request -> $"{request.Id}@{request.Version |> Option.defaultValue String.Empty}")
            |> String.concat ", "

        Console.WriteLine $"  repository lifecycle opt-ins: {optIns} (nested plan {lifecycle.Digest})"

        for step in lifecycle.Steps do
            let arguments = String.concat " " step.Arguments
            Console.WriteLine $"    {step.Component}@{step.Version}: {step.Executable} {arguments}"
    | None -> ()

    match plan.GenericUpgradePlan with
    | Some lifecycle ->
        Console.WriteLine $"  repository lifecycle upgrades: {lifecycle.Steps.Length} (nested plan {lifecycle.Digest})"

        for step in lifecycle.Steps do
            let arguments = String.concat " " step.Arguments
            Console.WriteLine $"    {step.Component}@{step.Version}: {step.Executable} {arguments}"
    | None ->
        Console.WriteLine "  repository lifecycle upgrades: none"

    if not plan.EmbeddedTransitions.IsEmpty then
        Console.WriteLine "  qualified legacy lifecycle upgrades:"

        for transition, _, release in plan.EmbeddedTransitions do
            Console.WriteLine $"    {transition.Id}: {transition.FromVersion} -> {transition.ToVersion} via {release.Executable}"

    if not plan.PackageLifecycleTransitions.IsEmpty then
        Console.WriteLine "  qualified package lifecycle upgrades:"

        for item in plan.PackageLifecycleTransitions do
            let transition = item.Transition
            let upgrade = String.concat " " item.UpgradeArguments
            let verify = String.concat " " item.VerifyArguments

            Console.WriteLine
                $"    {transition.Id}: {transition.FromVersion} -> {transition.ToVersion} via {item.Package}@{transition.ToVersion} (package sha256:{item.ArtifactSha256}): {upgrade}, then {verify}"

    if not plan.WebPackageTransitions.IsEmpty then
        Console.WriteLine "  web-package bindings:"

        for transition in plan.WebPackageTransitions do
            let target = transition.Target

            Console.WriteLine
                $"    {transition.Id}: {transition.FromVersion} -> {target.Version} [{WebPackageTransition.mode transition}] {target.Package}@{target.Specifier} (package sha256:{target.ArtifactSha256})"

            for directory, arguments in WebPackageTransition.steps transition do
                let rendered = String.concat " " arguments
                Console.WriteLine $"      {directory}: npm {rendered}"

    if not plan.NugetFeedChanges.IsEmpty then
        Console.WriteLine $"  NuGet release-asset feeds ({NugetFeed.FeedDirectory}):"

        for release, previous in plan.NugetFeedChanges do
            let change =
                match previous with
                | Some fromVersion -> $"{fromVersion} -> {release.Version}"
                | None -> $"opt in at {release.Version}"

            Console.WriteLine $"    {release.Id}: {change} from {release.Repository}@{release.Tag}"

            for package in release.Packages do
                Console.WriteLine $"      {package.PackageId} {package.Version} (sha256:{package.Sha256})"

    Console.WriteLine "  final verification: complete repository verify, then authority + lock commit"

    for refusal in plan.Refusals do
        Console.WriteLine $"  REFUSED {refusal}"

    Console.WriteLine $"Plan digest: {plan.Digest}"

let private runCurrentUpgrade (args: string array) checkOnly target manifestPath =
    match optionValue "--resolved-set" args, optionValue "--resolved-set-sha256" args with
    | None, _ ->
        writeErrors [ "--current requires --resolved-set PATH --resolved-set-sha256 SHA256 from the Registry current selection." ]
        2
    | _, None ->
        writeErrors [ "--current requires --resolved-set-sha256 SHA256 so the target Registry selection is immutable." ]
        2
    | Some resolvedSet, Some resolvedSetDigest ->
        match Manifest.load manifestPath with
        | Error errors ->
            writeErrors errors
            2
        | Ok manifest ->
            let ctx = currentUpgradeContext args
            let resolvedPath = Path.GetFullPath resolvedSet

            match
                CurrentUpgrade.preview
                    target
                    manifestPath
                    manifest
                    resolvedPath
                    resolvedSetDigest
                    ctx
                    currentUpgradeProbe
            with
            | Error errors ->
                writeErrors errors
                9
            | Ok plan ->
                printCurrentUpgradePlan plan

                if not plan.Refusals.IsEmpty then
                    9
                else
                    match optionValue "--authorize" args with
                    | None ->
                        Console.WriteLine
                            $"Authorize exactly this current selection with: conditor upgrade --current --target \"{target}\" --resolved-set \"{resolvedPath}\" --resolved-set-sha256 {plan.TargetSetSha256} --authorize {plan.Digest}"
                        0
                    | Some _ when checkOnly ->
                        Console.WriteLine "--check is read-only; authorization was not used."
                        0
                    | Some authorization ->
                        match
                            CurrentUpgrade.apply
                                target
                                manifestPath
                                manifest
                                resolvedPath
                                resolvedSetDigest
                                ctx
                                currentUpgradeProbe
                                authorization
                        with
                        | Error errors ->
                            writeErrors errors
                            9
                        | Ok result ->
                            if result.ChangedComponents.IsEmpty then
                                Console.WriteLine "Current upgrade completed: all Registry-governed component versions were already current."
                            else
                                let changed = String.Join(", ", result.ChangedComponents)
                                Console.WriteLine $"Current upgrade completed: {changed}"

                            if not result.OptedIn.IsEmpty then
                                let optedIn = String.Join(", ", result.OptedIn)
                                Console.WriteLine $"  opted in: {optedIn}"

                            Console.WriteLine $"  authority: {result.AuthorityPath}"
                            Console.WriteLine $"  lock:      {result.LockPath}"
                            let driftText =
                                if result.NoRemainingVersionChanges then "none" else "still present"

                            Console.WriteLine $"  second-plan version drift: {driftText}"

                            if result.NoRemainingVersionChanges then 0 else 9

let private runUpgrade (args: string array) checkOnly target manifestPath =
    if hasFlag "--current" args then
        runCurrentUpgrade args checkOnly target manifestPath
    else
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

let private printComponents json =
    if json then
        use stream = Console.OpenStandardOutput()
        ComponentInventory.writeJson stream Registry.descriptors
    else
        Console.WriteLine "Built-in Conditor components:"

        for descriptor in Registry.descriptors |> List.sortBy _.Definition.Id do
            let definition = descriptor.Definition
            let versions = descriptor.QualifiedVersions |> Seq.sort |> String.concat ", "
            let source =
                definition.LifecycleSource
                |> Option.map ComponentInventory.sourceText
                |> Option.defaultValue "application-binding-only"

            let historical =
                definition.HistoricalPackages
                |> List.map (fun entry ->
                    let historicalVersions = entry.Versions |> Seq.sort |> String.concat ","
                    $" historical={entry.Package}@{historicalVersions}")
                |> String.concat String.Empty

            Console.WriteLine
                $"  {definition.Id}@{definition.DefaultVersion} [{ComponentInventory.distributionText definition.Distribution}] package={definition.Package} qualified={versions}{historical} descriptor={descriptor.Sha256} source={source}"

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
    Console.WriteLine $"  sources:      {summary.SourceMirror}"
    Console.WriteLine $"  source files: {summary.SourceFileCount}"
    summary.ProjectManifestPath |> Option.iter (fun path -> Console.WriteLine $"  project:      {path}")

let private runBundle (args: string array) =
    match args |> Array.tryItem 1 with
    | Some "create" ->
        match optionValue "--resolved-set" args, optionValue "--resolved-set-sha256" args, optionValue "--output" args with
        | Some resolvedSet, Some digest, Some output ->
            let projectManifest = optionValue "--manifest" args |> Option.map Path.GetFullPath

            match OfflineBundle.create (Path.GetFullPath resolvedSet) digest (Path.GetFullPath output) projectManifest with
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

/// Generic repository lifecycle for Registry-resolved components that declare
/// echelon.repository-lifecycle. Only a verified resolved release set can
/// select them; embedded profiles carry no lifecycle contract.
let private lifecyclePlan (args: string array) =
    let ctx = workstationContext args

    match optionValue "--resolved-set" args, optionValue "--operation" args |> Option.bind LifecycleOperation.fromWire, optionValue "--root" args with
    | None, _, _ -> Error "lifecycle requires --resolved-set PATH --resolved-set-sha256 SHA256; lifecycle components are selected only by Registry"
    | _, None, _ -> Error "lifecycle requires --operation init|status|verify|doctor|upgrade"
    | _, _, None -> Error "lifecycle requires --root DIR"
    | Some _, Some operation, Some root ->
        workstationProfile args
        |> Result.map (fun profile -> ctx, Lifecycle.plan ctx profile (Platform.runtimeIdentifier ()) (Path.GetFullPath root) operation)

let private lifecycleJson (plan: LifecyclePlan) (result: LifecycleResult option) =
    let o = System.Text.Json.Nodes.JsonObject()
    o["schema"] <- System.Text.Json.Nodes.JsonValue.Create(if result.IsSome then "conditor.lifecycle-result/v1" else "conditor.lifecycle-plan/v1")
    o["profile"] <- System.Text.Json.Nodes.JsonValue.Create $"{plan.Profile.Id}@{plan.Profile.Version}"
    o["sourceIdentity"] <- System.Text.Json.Nodes.JsonValue.Create(plan.Profile.SourceIdentity |> Option.defaultValue "")
    o["root"] <- System.Text.Json.Nodes.JsonValue.Create plan.Root
    o["operation"] <- System.Text.Json.Nodes.JsonValue.Create(LifecycleOperation.toWire plan.Operation)
    o["digest"] <- System.Text.Json.Nodes.JsonValue.Create plan.Digest
    let components = System.Text.Json.Nodes.JsonArray()

    for c in plan.Profile.Components |> List.filter (fun c -> c.Role = Some "repository-lifecycle") do
        let n = System.Text.Json.Nodes.JsonObject()
        n["systemId"] <- System.Text.Json.Nodes.JsonValue.Create c.Id
        n["version"] <- System.Text.Json.Nodes.JsonValue.Create c.Version
        n["repository"] <- System.Text.Json.Nodes.JsonValue.Create c.Repository
        n["tag"] <- System.Text.Json.Nodes.JsonValue.Create c.Tag
        n["executable"] <- System.Text.Json.Nodes.JsonValue.Create c.Executable
        c.Lifecycle |> Option.iter (fun l ->
            n["sourceCommit"] <- System.Text.Json.Nodes.JsonValue.Create l.SourceCommit
            n["lifecycleContract"] <- System.Text.Json.Nodes.JsonValue.Create $"{RepositoryLifecycleContract.Capability}/v{l.ContractVersion}")
        c.Assets |> Map.iter (fun rid a ->
            n["platform"] <- System.Text.Json.Nodes.JsonValue.Create rid
            n["artifact"] <- System.Text.Json.Nodes.JsonValue.Create a.Name
            n["sha256"] <- System.Text.Json.Nodes.JsonValue.Create a.Sha256)
        components.Add n

    o["components"] <- components
    let steps = System.Text.Json.Nodes.JsonArray()

    for s in plan.Steps do
        let n = System.Text.Json.Nodes.JsonObject()
        n["sequence"] <- System.Text.Json.Nodes.JsonValue.Create s.Sequence
        n["id"] <- System.Text.Json.Nodes.JsonValue.Create s.Id
        n["component"] <- System.Text.Json.Nodes.JsonValue.Create s.Component
        n["command"] <- System.Text.Json.Nodes.JsonValue.Create(String.concat " " (s.Executable :: s.Arguments))

        result
        |> Option.bind (fun r -> r.Results |> List.tryFind (fun x -> x.Step.Id = s.Id))
        |> Option.iter (fun r ->
            n["exitCode"] <- System.Text.Json.Nodes.JsonValue.Create r.ExitCode
            n["output"] <-
                try
                    System.Text.Json.Nodes.JsonNode.Parse r.StandardOutput
                with :? JsonException ->
                    System.Text.Json.Nodes.JsonValue.Create r.StandardOutput)

        steps.Add n

    o["steps"] <- steps
    let refusals = System.Text.Json.Nodes.JsonArray()
    plan.Refusals |> List.iter (fun r -> refusals.Add(System.Text.Json.Nodes.JsonValue.Create r))
    o["refusals"] <- refusals
    result |> Option.iter (fun r -> o["failed"] <- System.Text.Json.Nodes.JsonValue.Create(r.Failed |> Option.map fst |> Option.defaultValue ""))
    o.ToJsonString(JsonSerializerOptions(WriteIndented = true))

let private runLifecycle (args: string array) =
    match args |> Array.tryItem 1 with
    | Some "plan" ->
        match lifecyclePlan args with
        | Error e ->
            Console.Error.WriteLine e
            2
        | Ok(_, plan) ->
            if hasFlag "--json" args then
                Console.WriteLine(lifecycleJson plan None)
            else
                Console.WriteLine $"Lifecycle plan: {LifecycleOperation.toWire plan.Operation} {plan.Root} with {plan.Profile.Id}@{plan.Profile.Version}"

                for p in plan.Preconditions do
                    Console.WriteLine $"  precondition {p.Component}: {Expected.describe p.Artifact}"
                    Console.WriteLine $"  precondition {p.Component}: {Expected.describe p.Identity}"

                for s in plan.Steps do
                    let command = String.concat " " (s.Executable :: s.Arguments)
                    Console.WriteLine $"  {s.Sequence,2}. {s.Component}@{s.Version}: {command}"

                for r in plan.Refusals do
                    Console.WriteLine $"  REFUSED {r}"

                Console.WriteLine $"Authorize exactly these invocations with: conditor lifecycle apply --authorize {plan.Digest}"

            if plan.Refusals.IsEmpty then 0 else 3
    | Some "apply" ->
        match lifecyclePlan args, optionValue "--authorize" args with
        | Error e, _ ->
            Console.Error.WriteLine e
            2
        | _, None ->
            Console.Error.WriteLine "lifecycle apply requires --authorize PLAN-DIGEST; run `conditor lifecycle plan` and review it first"
            2
        | Ok(ctx, plan), Some digest ->
            match Lifecycle.apply ctx probe plan digest with
            | Error e ->
                Console.Error.WriteLine $"REFUSED {e}"
                3
            | Ok result ->
                if hasFlag "--json" args then
                    Console.WriteLine(lifecycleJson plan (Some result))
                else
                    for r in result.Results do
                        Console.WriteLine $"  {r.Step.Id}: exit {r.ExitCode}"

                match result.Failed with
                | Some(step, why) ->
                    Console.Error.WriteLine $"  FAILED {step}: {why}"
                    4
                | None -> 0
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

let private writeAdoptionJson (plan: AdoptionPlan) =
    let root = System.Text.Json.Nodes.JsonObject()
    root["schema"] <- System.Text.Json.Nodes.JsonValue.Create "conditor.adoption-plan/v2"
    root["target"] <- System.Text.Json.Nodes.JsonValue.Create plan.Target
    root["project"] <- System.Text.Json.Nodes.JsonValue.Create plan.ProjectName
    root["digest"] <- System.Text.Json.Nodes.JsonValue.Create plan.Digest

    plan.RegistryAuthority
    |> Option.iter (fun authority ->
        let item = System.Text.Json.Nodes.JsonObject()
        item["kind"] <- System.Text.Json.Nodes.JsonValue.Create "resolved-release-set"
        item["sourcePath"] <- System.Text.Json.Nodes.JsonValue.Create authority.SourcePath
        item["targetPath"] <- System.Text.Json.Nodes.JsonValue.Create authority.TargetPath
        item["sha256"] <- System.Text.Json.Nodes.JsonValue.Create authority.Sha256
        item["profile"] <- System.Text.Json.Nodes.JsonValue.Create $"{authority.Profile.Id}@{authority.Profile.Version}"
        item["sourceIdentity"] <-
            System.Text.Json.Nodes.JsonValue.Create(authority.Profile.SourceIdentity |> Option.defaultValue "")
        root["registryAuthority"] <- item)

    let observations = System.Text.Json.Nodes.JsonArray()

    for observation in plan.Observations do
        let item = System.Text.Json.Nodes.JsonObject()
        item["component"] <- System.Text.Json.Nodes.JsonValue.Create observation.ComponentId
        item["status"] <- System.Text.Json.Nodes.JsonValue.Create observation.Status
        item["detail"] <- System.Text.Json.Nodes.JsonValue.Create observation.Detail
        observation.Version
        |> Option.iter (fun version -> item["version"] <- System.Text.Json.Nodes.JsonValue.Create version)
        observation.Command
        |> Option.iter (fun command -> item["command"] <- System.Text.Json.Nodes.JsonValue.Create command)
        observations.Add item

    root["observations"] <- observations

    let components = System.Text.Json.Nodes.JsonArray()

    for adoptedComponent in plan.Components do
        let item = System.Text.Json.Nodes.JsonObject()
        item["id"] <- System.Text.Json.Nodes.JsonValue.Create adoptedComponent.Id
        item["version"] <- System.Text.Json.Nodes.JsonValue.Create adoptedComponent.Version
        components.Add item

    root["components"] <- components

    let signals = System.Text.Json.Nodes.JsonArray()

    for signal in plan.ReviewSignals do
        let item = System.Text.Json.Nodes.JsonObject()
        item["component"] <- System.Text.Json.Nodes.JsonValue.Create signal.ComponentId
        item["code"] <- System.Text.Json.Nodes.JsonValue.Create signal.Code
        item["band"] <- System.Text.Json.Nodes.JsonValue.Create signal.Band
        item["path"] <- System.Text.Json.Nodes.JsonValue.Create signal.Path
        item["lineCount"] <- System.Text.Json.Nodes.JsonValue.Create signal.LineCount
        signals.Add item

    root["reviewSignals"] <- signals

    let refusals = System.Text.Json.Nodes.JsonArray()
    plan.Refusals
    |> List.iter (fun refusal -> refusals.Add(System.Text.Json.Nodes.JsonValue.Create refusal))
    root["refusals"] <- refusals
    root["manifest"] <- System.Text.Json.Nodes.JsonNode.Parse plan.ManifestText
    Console.WriteLine(root.ToJsonString(JsonSerializerOptions(WriteIndented = true)))

let private adoptionRegistryAuthority target (args: string array) =
    match optionValue "--resolved-set" args, optionValue "--resolved-set-sha256" args with
    | None, None -> Ok None
    | Some _, None ->
        Error "--resolved-set requires --resolved-set-sha256 SHA256 so adoption authority is integrity-bound."
    | None, Some _ ->
        Error "--resolved-set-sha256 requires --resolved-set PATH."
    | Some path, Some digest ->
        Adoption.loadRegistryAuthority target path digest
        |> Result.map Some

let private runAdopt (args: string array) =
    let target =
        optionValue "--target" args
        |> Option.defaultValue (Directory.GetCurrentDirectory())
        |> Path.GetFullPath

    let requestedName = optionValue "--name" args

    match adoptionRegistryAuthority target args with
    | Error error ->
        Console.Error.WriteLine error
        2
    | Ok registryAuthority ->
        let plan =
            Adoption.planWithAuthority
                ProcessRunner.runProcess
                target
                requestedName
                registryAuthority

        if hasFlag "--json" args then
            writeAdoptionJson plan
        else
            Console.WriteLine $"Adoption plan for '{plan.ProjectName}' at {plan.Target}"

            plan.RegistryAuthority
            |> Option.iter (fun authority ->
                Console.WriteLine $"  authority  {authority.Profile.Id}@{authority.Profile.Version}: sha256:{authority.Sha256}")

            for observation in plan.Observations do
                let version = observation.Version |> Option.map (fun value -> $" @{value}") |> Option.defaultValue ""
                Console.WriteLine $"  {observation.Status,-10} {observation.ComponentId}{version}: {observation.Detail}"

            if not plan.ReviewSignals.IsEmpty then
                Console.WriteLine ""
                Console.WriteLine
                    $"Structural review signals ({plan.ReviewSignals.Length}; recorded in the lock, not blocking):"

                for signal in plan.ReviewSignals do
                    Console.WriteLine $"  {VerificationGate.describe signal}"

            Console.WriteLine ""
            Console.WriteLine "Proposed conditor.json:"
            Console.WriteLine plan.ManifestText

            for refusal in plan.Refusals do
                Console.WriteLine $"  REFUSED {refusal}"

            if plan.Refusals.IsEmpty then
                let authorityArgs =
                    plan.RegistryAuthority
                    |> Option.map (fun authority ->
                        $" --resolved-set \"{authority.SourcePath}\" --resolved-set-sha256 {authority.Sha256}")
                    |> Option.defaultValue ""

                Console.WriteLine
                    $"Authorize exactly this observed state with: conditor adopt --target \"{plan.Target}\"{authorityArgs} --authorize {plan.Digest}"

        match optionValue "--authorize" args with
        | None ->
            if plan.Refusals.IsEmpty then 0 else 3
        | Some digest ->
            match
                Adoption.applyWithAuthority
                    ProcessRunner.runProcess
                    target
                    requestedName
                    registryAuthority
                    digest
            with
            | Error errors ->
                writeErrors errors
                3
            | Ok result ->
                Console.WriteLine "Repository adopted without reinitializing component-owned state."
                Console.WriteLine $"  manifest: {result.ManifestPath}"
                Console.WriteLine $"  lock:     {result.LockPath}"
                result.RegistryAuthorityPath
                |> Option.iter (fun path -> Console.WriteLine $"  authority:{path}")

                for adoptedEntry in result.Components do
                    Console.WriteLine $"  adopted:  {adoptedEntry.Id}@{adoptedEntry.Version}"

                0

let private runRepository (args: string array) =
    match args |> Array.tryItem 1 with
    | Some "create" ->
        let target =
            optionValue "--target" args
            |> Option.defaultValue (Directory.GetCurrentDirectory())
            |> Path.GetFullPath

        let deploy =
            match optionValue "--deploy" args with
            | None -> Ok NoDeployment
            | Some "github-pages" -> Ok GitHubPages
            | Some other -> Error [ $"Unsupported deployment target '{other}'. Supported: github-pages." ]

        let repository =
            match optionValue "--repository" args with
            | None -> Error [ "conditor repo create requires --repository OWNER/NAME." ]
            | Some value -> RepositoryCreation.parseRepository value

        match repository, deploy with
        | Error errors, _
        | _, Error errors ->
            writeErrors errors
            2
        | Ok(owner, name), Ok deploy ->
            let request =
                { Owner = owner
                  Name = name
                  Visibility = if hasFlag "--public" args then PublicRepository else PrivateRepository
                  Deploy = deploy }

            let run executable arguments = ProcessRunner.runProcess target executable arguments

            let planned =
                RepositoryCreation.readProtection target
                |> Result.bind (fun protection ->
                    let local = RepositoryCreation.observeLocal run
                    let remote = RepositoryCreation.observeRemote run request
                    RepositoryCreation.plan request local remote protection)

            match planned with
            | Error errors ->
                Console.Error.WriteLine $"Conditor will not create {RepositoryCreation.fullName request}:"
                errors |> List.iter (fun error -> Console.Error.WriteLine $"  - {error}")
                3
            | Ok steps when hasFlag "--dry-run" args ->
                Console.WriteLine $"Dry run: nothing was created or changed. Creating {RepositoryCreation.fullName request} from {target} would run:"

                steps
                |> List.iteri (fun index step ->
                    Console.WriteLine $"  {index + 1}. {step.Description}"
                    Console.WriteLine $"     {RepositoryCreation.commandLine step}"

                    match step.Command with
                    | GitHubWithBody(_, body) ->
                        body.TrimEnd().Split('\n') |> Array.iter (fun line -> Console.WriteLine $"       {line}")
                    | _ -> ())

                0
            | Ok steps ->
                let withBody (body: string) (action: string -> ProcessResult) =
                    let path = Path.Combine(Path.GetTempPath(), $"conditor-github-{Guid.NewGuid():N}.json")

                    try
                        File.WriteAllText(path, body)
                        action path
                    finally
                        if File.Exists path then
                            File.Delete path

                let outcome = RepositoryCreation.execute run withBody steps

                for step in outcome.Completed do
                    Console.WriteLine $"  done     {step.Description}"

                match outcome.Failed with
                | None ->
                    Console.WriteLine $"Created https://github.com/{RepositoryCreation.fullName request} and pushed {RepositoryCreation.Branch}."
                    0
                | Some(step, result) ->
                    Console.Error.WriteLine $"  FAILED   {step.Description}"
                    Console.Error.WriteLine $"           {RepositoryCreation.commandLine step} exited with {result.ExitCode}"

                    [ result.StandardError.Trim(); result.StandardOutput.Trim() ]
                    |> List.filter (String.IsNullOrWhiteSpace >> not)
                    |> List.iter (fun line -> Console.Error.WriteLine $"           {line}")

                    for remaining in step :: outcome.Remaining do
                        Console.Error.WriteLine $"  to do    {remaining.Description}: {RepositoryCreation.commandLine remaining}"

                        match remaining.Command with
                        | GitHubWithBody(_, body) ->
                            body.TrimEnd().Split('\n') |> Array.iter (fun line -> Console.Error.WriteLine $"             {line}")
                        | _ -> ()

                    Console.Error.WriteLine "Nothing was rolled back. Fix the cause, then run the remaining steps by hand."
                    1
    | _ ->
        usage ()
        2

let private runRequirements (args: string array) =
    match args |> Array.tryItem 1 with
    | Some "import" ->
        let target =
            optionValue "--target" args
            |> Option.defaultValue (Directory.GetCurrentDirectory())
            |> Path.GetFullPath

        let manifestPath =
            optionValue "--manifest" args
            |> Option.map Path.GetFullPath
            |> Option.defaultValue (Path.Combine(target, "conditor.json"))

        match RequirementsImportRun.observe target manifestPath with
        | Error errors ->
            Console.Error.WriteLine "Conditor will not import the requirements; nothing was changed:"
            errors |> List.iter (fun error -> Console.Error.WriteLine $"  - {error}")
            3
        | Ok observation ->
            let plan = observation.Plan

            Console.WriteLine
                $"Requirements import plan sha256:{plan.Digest}: {plan.Items.Length} slice work items, {plan.RequirementCount} source requirements traced to {RequirementsImportRun.Umbrella} ({observation.Spec.TraceAttachment})."

            for item in plan.Items do
                let dependency = item.DependsOn |> Option.map (fun id -> $" after {id}") |> Option.defaultValue ""
                Console.WriteLine $"  {item.Order,2}. {item.Id,-30} {item.Priority,-6} {item.Requirements.Length,4} requirements{dependency}"

            let check = hasFlag "--check" args
            let authorized = optionValue "--authorize" args

            match authorized with
            | Some digest when digest.Replace("sha256:", "") <> plan.Digest ->
                Console.Error.WriteLine $"Refusing: --authorize {digest} does not match the plan sha256:{plan.Digest}. Nothing was changed."
                3
            | _ when observation.Actions.IsEmpty ->
                Console.WriteLine "Already imported: every work item and the trace match the plan. Nothing to change."
                0
            | _ when check ->
                Console.WriteLine $"Check only; nothing was changed. The import would apply {observation.Actions.Length} Praxis changes:"
                observation.Actions |> List.iter (fun action -> Console.WriteLine $"  {RequirementsImportRun.describe action}")
                Console.WriteLine $"Apply exactly this plan with: conditor requirements import --target \"{target}\" --authorize sha256:{plan.Digest}"
                0
            | _ ->
                let run executable arguments = ProcessRunner.runProcess target executable arguments

                match RequirementsImportRun.commitPreconditions run with
                | errors when not errors.IsEmpty ->
                    Console.Error.WriteLine "Conditor will not import the requirements; nothing was changed:"
                    errors |> List.iter (fun error -> Console.Error.WriteLine $"  - {error}")
                    3
                | _ ->
                    let now () = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ")

                    let withTrace (content: string) (action: string -> ProcessResult) =
                        let path = Path.Combine(Path.GetTempPath(), $"conditor-trace-{Guid.NewGuid():N}.json")

                        try
                            File.WriteAllText(path, content)
                            action path
                        finally
                            if File.Exists path then
                                File.Delete path

                    let report (outcome: RequirementsImportOutcome) =
                        for action in outcome.Applied do
                            Console.WriteLine $"  done     {RequirementsImportRun.describe action}"

                        match outcome.Failed with
                        | Some(action, result) ->
                            Console.Error.WriteLine $"  FAILED   {RequirementsImportRun.describe action} (exit {result.ExitCode})"

                            [ result.StandardError.Trim(); result.StandardOutput.Trim() ]
                            |> List.filter (String.IsNullOrWhiteSpace >> not)
                            |> List.iter (fun line -> Console.Error.WriteLine $"           {line}")

                            for remaining in outcome.Remaining do
                                Console.Error.WriteLine $"  not run  {RequirementsImportRun.describe remaining}"

                            Console.Error.WriteLine "Re-run the same command after fixing the cause: the import resumes where it stopped."
                        | None -> ()

                    match RequirementsImportRun.apply run now withTrace true observation with
                    | Error(error, outcome) ->
                        report outcome
                        Console.Error.WriteLine error
                        1
                    | Ok outcome ->
                        report outcome
                        outcome.Commit |> Option.iter (fun commit -> Console.WriteLine $"Recorded the Praxis state as commit {commit}.")
                        if outcome.Failed.IsSome then 1 else 0
    | _ ->
        usage ()
        2

let private configureSourcePolicy (args: string array) =
    optionValue "--source-mirror" args
    |> Option.iter (fun path ->
        Environment.SetEnvironmentVariable("CONDITOR_SOURCE_MIRROR", Path.GetFullPath path))

    if hasFlag "--offline" args then
        Environment.SetEnvironmentVariable("CONDITOR_OFFLINE", "1")

let private execute (args: string array) =
    configureSourcePolicy args

    if args.Length = 0 then
        usage ()
        1
    else
        let command = args[0].Trim().ToLowerInvariant()

        if command = "workstation" then
            runWorkstation args
        elif command = "lifecycle" then
            runLifecycle args
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
        elif command = "adopt" then
            runAdopt args
        elif command = "repo" then
            runRepository args
        elif command = "requirements" then
            runRequirements args
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
                | "upgrade" -> runUpgrade args (hasFlag "--check" args) target selection.ManifestPath
                | "resume" -> runResume (optionValue "--launcher" args) target selection.ManifestPath
                | "handoff" ->
                    runHandoff
                        (hasFlag "--resume" args)
                        (optionValue "--launcher" args)
                        (optionValue "--prompt-file" args)
                        target
                        selection.ManifestPath
                | "supervise" -> runSupervise args target selection.ManifestPath
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
