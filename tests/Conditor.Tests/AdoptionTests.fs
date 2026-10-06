module AdoptionTests

open System
open System.IO
open System.Security.Cryptography
open System.Text.Json.Nodes
open Conditor.Core
open Conditor.Core.Workstation

let private result exitCode output error =
    { ExitCode = exitCode
      StandardOutput = output
      StandardError = error }

let private withTarget action =
    let target = Path.Combine(Path.GetTempPath(), $"conditor-adoption-{Guid.NewGuid():N}")
    Directory.CreateDirectory target |> ignore

    try
        action target
    finally
        if Directory.Exists target then
            Directory.Delete(target, true)

let private healthyPraxisRunner _ (executable: string) (arguments: string list) =
    let name = Path.GetFileName executable

    if name = "praxis" && arguments = [ "--version" ] then
        result 0 "ros-fs 3.6.0" ""
    elif name = "praxis" && arguments = [ "verify"; "--strict" ] then
        result 0 "healthy" ""
    else
        result -1 "" $"Unable to execute '{executable}'."


let private fileSha256 path =
    use stream = File.OpenRead path
    Convert.ToHexString(SHA256.HashData stream).ToLowerInvariant()

let private registryAuthorityFixture target =
    let rid = Platform.runtimeIdentifier ()
    let systemId = "registry-adopt-fixture"
    let version = "9.4.2"
    let repository = "example/registry-adopt-fixture"
    let commit = String.replicate 40 "c"
    let executable = Path.Combine(target, "registry-adopt-fixture")
    let log = Path.Combine(target, "registry-adopt.log")

    let identity = JsonObject()
    identity["SystemId"] <- JsonValue.Create systemId
    identity["Repository"] <- JsonValue.Create repository
    identity["Executable"] <- JsonValue.Create executable
    identity["ReleaseVersion"] <- JsonValue.Create version
    identity["SourceCommit"] <- JsonValue.Create commit

    let script =
        String.concat
            "\n"
            [ "#!/bin/sh"
              $"echo \"$*\" >> \"{log}\""
              "op=\"$1\"; root=\"$3\""
              "case \"$op\" in"
              $"  version) echo '{identity.ToJsonString()}' ;;"
              "  verify|doctor|status) [ \"$2\" = \"--root\" ] && [ -f \"$root/.registry-adopt/state\" ] || exit 3; echo '{\"healthy\":true}' ;;"
              "  init|upgrade) echo '{\"error\":\"mutation-not-expected-in-adoption-test\"}'; exit 7 ;;"
              "  *) exit 2 ;;"
              "esac"
              "" ]

    File.WriteAllText(executable, script)
    File.SetUnixFileMode(
        executable,
        UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute
    )
    Directory.CreateDirectory(Path.Combine(target, ".registry-adopt")) |> ignore
    File.WriteAllText(Path.Combine(target, ".registry-adopt", "state"), "existing\n")

    let release = JsonObject()
    release["systemId"] <- JsonValue.Create systemId
    release["role"] <- JsonValue.Create "repository-lifecycle"
    release["required"] <- JsonValue.Create true
    release["version"] <- JsonValue.Create version
    release["repository"] <- JsonValue.Create repository
    release["tag"] <- JsonValue.Create $"{systemId}-v{version}"
    release["commit"] <- JsonValue.Create commit
    release["releaseStage"] <- JsonValue.Create "stable"
    release["lifecycleState"] <- JsonValue.Create "active"
    release["distributionClass"] <- JsonValue.Create "self-contained-native-cli"
    release["executable"] <- JsonValue.Create executable

    let lifecycle = JsonObject()
    lifecycle["contract"] <- JsonValue.Create RepositoryLifecycleContract.Capability
    lifecycle["contractVersion"] <- JsonValue.Create 1
    release["repositoryLifecycle"] <- lifecycle

    let distribution = JsonObject()
    distribution["mechanism"] <- JsonValue.Create "github-release"
    distribution["package"] <- (null: JsonNode | null)
    distribution["url"] <- JsonValue.Create $"https://github.com/{repository}/releases/tag/{systemId}-v{version}"
    release["distribution"] <- distribution

    let artifact = JsonObject()
    artifact["name"] <- JsonValue.Create $"{systemId}-{rid}.tar.gz"
    artifact["purpose"] <- JsonValue.Create "executable"
    artifact["platform"] <- JsonValue.Create rid
    artifact["sha256"] <- JsonValue.Create(String.replicate 64 "d")
    let artifacts = JsonArray()
    artifacts.Add artifact
    release["artifacts"] <- artifacts

    let root = JsonObject()
    root["schema"] <- JsonValue.Create "echelon.resolved-release-set/v1"
    let profile = JsonObject()
    profile["id"] <- JsonValue.Create "registry-adopt-proof"
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

    let source = Path.Combine(Path.GetTempPath(), $"conditor-registry-authority-{Guid.NewGuid():N}.json")
    File.WriteAllText(source, root.ToJsonString())
    source, fileSha256 source, systemId, version, executable, log

/// A governed repository carries its own ./praxis launcher. Probing the host
/// tool from inside such a repository must not run that launcher instead.
let private localLauncherDoesNotShadowHostTool check =
    withTarget (fun target ->
        if OperatingSystem.IsWindows() then
            check "local launcher shadowing check is skipped on Windows" true
        else
            let name = $"conditor-shadow-probe-{Guid.NewGuid():N}"
            let launcher = Path.Combine(target, name)
            File.WriteAllText(launcher, "#!/bin/sh\necho shadowed\n")
            File.SetUnixFileMode(launcher, UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute)
            let previous = Directory.GetCurrentDirectory()

            try
                Directory.SetCurrentDirectory target
                let probed = ProcessRunner.runProcess target name [ "--version" ]

                check
                    "a bare command name never resolves to a launcher in the working directory"
                    (ProcessRunner.resolveExecutable name = None
                     && probed.ExitCode = -1
                     && not (probed.StandardOutput.Contains "shadowed"))

                check
                    "a command with a directory part is run as given"
                    ((ProcessRunner.runProcess target launcher []).StandardOutput.Trim() = "shadowed")
            finally
                Directory.SetCurrentDirectory previous)


/// Communication and Visual Engineering CLIs installed on the machine, as on a
/// workstation that has used them in another repository. Their verify fails
/// exactly as the real ones do in a repository where they were never installed.
let private machineWideEngineeringRunner (installedInRepository: string list) target (executable: string) (arguments: string list) =
    let name = Path.GetFileName executable |> Option.ofObj |> Option.defaultValue ""

    match name, arguments with
    | "praxis", _ -> healthyPraxisRunner target executable arguments
    | ("communication-engineering" | "visual-engineering"), [ "--version" ] -> result 0 "1.0.0" ""
    | ("communication-engineering" | "visual-engineering"), [ "verify"; "--strict" ] ->
        if installedInRepository |> List.contains name then
            result 0 "verification passed" ""
        else
            result 3 "Verification failed.\n  FAIL installation: not installed in this repository" ""
    | _ -> result -1 "" $"Unable to execute '{executable}'."

let private observationFor componentId (plan: AdoptionPlan) =
    plan.Observations |> List.tryFind (fun observation -> observation.ComponentId = componentId)

/// A lifecycle CLI on the machine says nothing about the repository: a
/// component Conditor cannot find installed here is not-found, not refused.
let private machineCliDoesNotImplyRepositoryInstallation check =
    withTarget (fun target ->
        let plan = Adoption.planWith (machineWideEngineeringRunner []) target None

        for id in [ "communication-engineering"; "visual-engineering" ] do
            check
                $"{id} with a machine CLI but no repository installation is not-found"
                (observationFor id plan |> Option.exists (fun observation -> observation.Status = "not-found"))

        check
            "machine-only engineering CLIs do not block adoption"
            (plan.Refusals
             |> List.forall (fun refusal ->
                 not (refusal.Contains "communication-engineering" || refusal.Contains "visual-engineering")))

        check "adoption still proceeds with the installed Praxis" (plan.Components |> List.map _.Id = [ "praxis" ]))

    withTarget (fun target ->
        Directory.CreateDirectory(Path.Combine(target, ".echelon")) |> ignore
        File.WriteAllText(Path.Combine(target, ".echelon", "visual-engineering.json"), "{}")
        let plan = Adoption.planWith (machineWideEngineeringRunner []) target None

        check
            "a component installed in the repository that fails verify is still refused"
            (observationFor "visual-engineering" plan |> Option.exists (fun observation -> observation.Status = "refused")
             && plan.Refusals |> List.exists (fun refusal -> refusal.Contains "visual-engineering"))

        check
            "the other machine-only component stays not-found"
            (observationFor "communication-engineering" plan
             |> Option.exists (fun observation -> observation.Status = "not-found")))

    withTarget (fun target ->
        Directory.CreateDirectory(Path.Combine(target, ".communication-engineering")) |> ignore
        let plan = Adoption.planWith (machineWideEngineeringRunner [ "communication-engineering" ]) target None

        check
            "a component installed in the repository that verifies is adopted"
            (observationFor "communication-engineering" plan
             |> Option.exists (fun observation -> observation.Status = "verified")
             && plan.Components |> List.exists (fun entry -> entry.Id = "communication-engineering")))

let run check =
    localLauncherDoesNotShadowHostTool check
    machineCliDoesNotImplyRepositoryInstallation check

    withTarget (fun target ->
        let plan = Adoption.planWith healthyPraxisRunner target None

        check "adoption discovers verified Praxis" (plan.Components |> List.map _.Id = [ "praxis" ])
        check "adoption records exact qualified version" (plan.Components.Head.Version = "3.6.0")
        check "adoption proposal is mutation-free" (not (File.Exists(Path.Combine(target, "conditor.json"))))
        check "adoption proposal has no refusal for healthy component" plan.Refusals.IsEmpty
        check
            "application bindings are explicitly skipped rather than guessed"
            (plan.Observations
             |> List.exists (fun observation ->
                 observation.ComponentId = "forma"
                 && observation.Status = "skipped"
                 && observation.Detail.Contains("explicit project target")))

        match Adoption.applyWith healthyPraxisRunner target None "bad-digest" with
        | Ok _ -> check "adoption rejects stale authorization digest" false
        | Error errors ->
            check
                "adoption rejects stale authorization digest"
                (errors |> List.exists (fun error -> error.Contains("does not match")))
            check
                "stale adoption authorization performs no mutation"
                (not (File.Exists(Path.Combine(target, "conditor.json"))))

        match Adoption.applyWith healthyPraxisRunner target None plan.Digest with
        | Error errors ->
            let details = String.concat "; " errors
            check $"authorized adoption succeeds: {details}" false
        | Ok adopted ->
            check "authorized adoption writes manifest" (File.Exists adopted.ManifestPath)
            check "authorized adoption writes lock" (File.Exists adopted.LockPath)

            match Manifest.load adopted.ManifestPath with
            | Error errors ->
                let details = String.concat "; " errors
                check $"adopted manifest parses: {details}" false
            | Ok manifest ->
                check
                    "adopted manifest records only proven lifecycle component"
                    (manifest.Components
                     |> List.exists (fun entry ->
                         entry.Id = "praxis"
                         && entry.Version = Some "3.6.0"
                         && entry.Required))

            let second = Adoption.planWith healthyPraxisRunner target None

            check
                "adoption refuses repository already governed by Conditor"
                (second.Refusals
                 |> List.exists (fun refusal -> refusal.Contains("already contains Conditor governance"))))

    withTarget (fun target ->
        let unknownVersionRunner _ (executable: string) (arguments: string list) =
            let name = Path.GetFileName executable

            if name = "praxis" && arguments = [ "--version" ] then
                result 0 "ros-fs 99.0.0" ""
            else
                result -1 "" "missing"

        let plan = Adoption.planWith unknownVersionRunner target None

        check
            "adoption refuses installed but unqualified version"
            (plan.Refusals
             |> List.exists (fun refusal -> refusal.Contains("not unambiguously qualified")))
        check "unqualified component is not adopted" (plan.Components |> List.forall (fun item -> item.Id <> "praxis")))

    withTarget (fun target ->
        let unhealthyRunner _ (executable: string) (arguments: string list) =
            let name = Path.GetFileName executable

            if name = "praxis" && arguments = [ "--version" ] then
                result 0 "ros-fs 3.6.0" ""
            elif name = "praxis" && arguments = [ "verify"; "--strict" ] then
                result 4 "" "verification failed"
            else
                result -1 "" "missing"

        let plan = Adoption.planWith unhealthyRunner target None

        check
            "adoption refuses detected component that fails verify"
            (plan.Refusals
             |> List.exists (fun refusal -> refusal.Contains("did not verify successfully")))
        check
            "failed verification does not create Conditor governance"
            (not (File.Exists(Path.Combine(target, "conditor.json")))))

    withTarget (fun target ->
        let ambiguousRunner _ (executable: string) (arguments: string list) =
            let name = Path.GetFileName executable

            if name = "praxis" && arguments = [ "--version" ] then
                result 0 "compatibility: 3.1.4 current: 3.6.0" ""
            else
                result -1 "" "missing"

        let plan = Adoption.planWith ambiguousRunner target None

        check
            "adoption refuses ambiguous version identity"
            (plan.Refusals
             |> List.exists (fun refusal -> refusal.Contains("version identity is ambiguous"))))


    withTarget (fun target ->
        if OperatingSystem.IsWindows() then
            check "Registry adoption fixture is skipped on Windows" true
        else
            let source, digest, systemId, version, executable, log =
                registryAuthorityFixture target

            try
                match Adoption.loadRegistryAuthority target source digest with
                | Error error ->
                    check $"Registry adoption authority loads: {error}" false
                | Ok authority ->
                    let plan =
                        Adoption.planWithAuthority
                            ProcessRunner.runProcess
                            target
                            None
                            (Some authority)

                    check
                        "Registry authority discovers a lifecycle component absent from the embedded catalog"
                        (plan.Components
                         |> List.exists (fun entry ->
                             entry.Id = systemId
                             && entry.Version = version
                             && entry.Package = executable))

                    check
                        "Registry adoption plan is bound to the resolved set"
                        (plan.RegistryAuthority
                         |> Option.exists (fun selected ->
                             selected.Sha256 = digest
                             && selected.Profile.SourceIdentity
                                |> Option.exists (fun identity ->
                                    identity.Contains $"resolved-set=sha256:{digest}")))

                    check
                        "Registry adoption verifies identity and repository state without initialization"
                        (File.ReadAllLines(log)
                         |> Array.forall (fun invocation ->
                             invocation = "version"
                             || invocation = $"verify --root {target}"))

                    match
                        Adoption.applyWithAuthority
                            ProcessRunner.runProcess
                            target
                            None
                            (Some authority)
                            plan.Digest
                    with
                    | Error errors ->
                        let details = String.concat "; " errors
                        check $"Registry-authorized adoption succeeds: {details}" false
                    | Ok adopted ->
                        let authorityPath =
                            Path.Combine(target, ".conditor", "authority", "resolved-release-set.json")

                        check "Registry authority is materialized into the governed repository" (File.Exists authorityPath)
                        check "materialized Registry authority preserves exact digest" (fileSha256 authorityPath = digest)

                        match Manifest.load adopted.ManifestPath with
                        | Error errors ->
                            let details = String.concat "; " errors
                            check $"Registry-adopted manifest parses: {details}" false
                        | Ok manifest ->
                            check
                                "Registry-adopted manifest records durable authority"
                                (manifest.RegistryAuthority
                                 |> Option.exists (fun authorityRef ->
                                     authorityRef.Kind = "resolved-release-set"
                                     && authorityRef.Path = ".conditor/authority/resolved-release-set.json"
                                     && authorityRef.Sha256 = digest))

                            match Planner.create target Verify manifest with
                            | Error errors ->
                                let details = String.concat "; " errors
                                check $"Registry-adopted component replans after adoption: {details}" false
                            | Ok verifyPlan ->
                                check
                                    "future verify resolves Registry-only component generically"
                                    (verifyPlan.Actions
                                     |> List.exists (fun action ->
                                         action.ComponentId = systemId
                                         && action.ComponentVersion = version
                                         && (match action.Execution with
                                             | ExternalProcess(command, arguments) ->
                                                 command = executable
                                                 && arguments = [ "verify"; "--root"; target ]
                                             | _ -> false)))

                                check
                                    "Registry-adopted lock revalidates against durable authority"
                                    (LockFile.verifyResolvedComponents target verifyPlan.Components
                                     |> Result.isOk)

                                check
                                    "future verify executes component-owned lifecycle without reinitialization"
                                    (Installer.execute target adopted.ManifestPath verifyPlan
                                     |> Result.isOk)

                            File.AppendAllText(authorityPath, Environment.NewLine)

                            check
                                "future planning refuses tampered persisted Registry authority"
                                (match Planner.create target Verify manifest with
                                 | Error errors ->
                                     errors
                                     |> List.exists (fun error ->
                                         error.Contains("resolved release set digest mismatch"))
                                 | Ok _ -> false)

                    let wrongIdentityRunner workingDirectory command arguments =
                        if command = executable && arguments = [ "version" ] then
                            result
                                0
                                """{"SystemId":"registry-adopt-fixture","Repository":"example/registry-adopt-fixture","Executable":"WRONG","ReleaseVersion":"9.4.2","SourceCommit":"cccccccccccccccccccccccccccccccccccccccc"}"""
                                ""
                        else
                            ProcessRunner.runProcess workingDirectory command arguments

                    let mismatch =
                        Adoption.planWithAuthority
                            wrongIdentityRunner
                            target
                            None
                            (Some authority)

                    check
                        "Registry adoption refuses an installed executable with mismatched release identity"
                        (mismatch.Refusals
                         |> List.exists (fun refusal ->
                             refusal.Contains("does not match the exact release selected")))
            finally
                if File.Exists source then
                    File.Delete source)
