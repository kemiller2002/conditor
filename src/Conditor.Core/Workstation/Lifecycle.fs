namespace Conditor.Core.Workstation

open System
open System.IO
open System.Security.Cryptography
open System.Text
open Conditor.Core

// Generic repository lifecycle (echelon-registry
// spec/repository-lifecycle-contract.md). Every fact Conditor uses here —
// system id, exact version, executable, platform artifact and digest, source
// commit, contract version — comes from the verified Registry resolved
// release set. Conditor knows only the contract's operations; the component
// owns how it changes the repository. There is deliberately no per-system
// branching in this module.

/// A contract-level lifecycle operation.
[<RequireQualifiedAccess>]
type LifecycleOperation =
    | Init
    | Status
    | Verify
    | Doctor
    | Upgrade

[<RequireQualifiedAccess>]
module LifecycleOperation =
    let all =
        [ LifecycleOperation.Init
          LifecycleOperation.Status
          LifecycleOperation.Verify
          LifecycleOperation.Doctor
          LifecycleOperation.Upgrade ]

    let toWire operation =
        match operation with
        | LifecycleOperation.Init -> "init"
        | LifecycleOperation.Status -> "status"
        | LifecycleOperation.Verify -> "verify"
        | LifecycleOperation.Doctor -> "doctor"
        | LifecycleOperation.Upgrade -> "upgrade"

    let fromWire raw =
        all |> List.tryFind (fun operation -> toWire operation = raw)

    let mutates operation =
        match operation with
        | LifecycleOperation.Init
        | LifecycleOperation.Upgrade -> true
        | LifecycleOperation.Status
        | LifecycleOperation.Verify
        | LifecycleOperation.Doctor -> false

    /// The contract invocations a requested operation discloses, in order.
    /// Mutating operations are always followed by verification.
    let phases operation =
        match operation with
        | LifecycleOperation.Init -> [ LifecycleOperation.Init; LifecycleOperation.Verify ]
        | LifecycleOperation.Upgrade -> [ LifecycleOperation.Upgrade; LifecycleOperation.Verify ]
        | other -> [ other ]

/// One disclosed component invocation.
type LifecycleStep =
    { Sequence: int
      Id: string
      Component: string
      Version: string
      Operation: LifecycleOperation
      /// The installed executable, shown relative to the workstation home.
      Executable: string
      Arguments: string list }

/// A disclosed precondition: the installed executable is the exact
/// Registry-selected release, revalidated before any repository effect.
type LifecyclePrecondition =
    { Component: string
      Artifact: Expected
      Identity: Expected }

type LifecyclePlan =
    { Profile: WorkstationProfile
      Root: string
      Operation: LifecycleOperation
      Preconditions: LifecyclePrecondition list
      Steps: LifecycleStep list
      Refusals: string list
      Digest: string }

type LifecycleStepResult =
    { Step: LifecycleStep
      ExitCode: int
      StandardOutput: string
      StandardError: string }

type LifecycleResult =
    { Results: LifecycleStepResult list
      Failed: (string * string) option }

module Lifecycle =
    let private sha256Text (text: string) =
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes text)).ToLowerInvariant()

    let private installedExecutable (ctx: WorkstationContext) (c: ProfileComponent) =
        Path.Combine(WorkstationPaths.installRoot ctx, c.Id, c.Version, c.Executable)

    let private refusalsFor (rid: string) (c: ProfileComponent) =
        [ match c.Lifecycle with
          | None ->
              $"{c.Id} {c.Version} has role repository-lifecycle but its release declares no supported {RepositoryLifecycleContract.Capability} contract; Conditor has no generic lifecycle semantics for it"
          | Some _ -> ()
          if not (c.Assets.ContainsKey rid) then
              $"{c.Id} {c.Version} publishes no executable artifact for {rid}" ]

    /// Plan contract invocations for every repository-lifecycle component of
    /// a Registry-resolved profile against one repository. Never mutates.
    let plan (ctx: WorkstationContext) (profile: WorkstationProfile) (rid: string) (root: string) (operation: LifecycleOperation) : LifecyclePlan =
        let display = WorkstationPaths.display ctx

        let lifecycleComponents =
            profile.Components |> List.filter (fun c -> c.Role = Some "repository-lifecycle")

        let rootRefusals =
            [ if not (Path.IsPathRooted root) then $"repository root '{root}' must be an absolute path"
              elif not (Directory.Exists root) then $"repository root '{root}' does not exist"
              if lifecycleComponents.IsEmpty then $"profile {profile.Id}@{profile.Version} resolves no repository-lifecycle component" ]

        let refusals = rootRefusals @ (lifecycleComponents |> List.collect (refusalsFor rid))

        let conforming =
            lifecycleComponents |> List.filter (fun c -> c.Lifecycle.IsSome && c.Assets.ContainsKey rid)

        let preconditions =
            conforming
            |> List.map (fun c ->
                let cached = Path.Combine(WorkstationPaths.cache ctx, c.Id, c.Version, c.Assets[rid].Name)

                { Component = c.Id
                  Artifact = Expected.FileSha256(display cached, c.Assets[rid].Sha256)
                  Identity = Engine.installedReceipt (display (installedExecutable ctx c)) c })

        let steps =
            conforming
            |> List.collect (fun c ->
                LifecycleOperation.phases operation
                |> List.map (fun phase ->
                    { Sequence = 0
                      Id = $"{c.Id}-{LifecycleOperation.toWire phase}"
                      Component = c.Id
                      Version = c.Version
                      Operation = phase
                      Executable = display (installedExecutable ctx c)
                      Arguments = [ LifecycleOperation.toWire phase; "--root"; root ] }))
            |> List.mapi (fun i s -> { s with Sequence = i + 1 })

        let canonical =
            [ $"{profile.Id}@{profile.Version}"
              profile.SourceIdentity |> Option.defaultValue "-"
              $"rid={rid}"
              $"root={root}"
              $"operation={LifecycleOperation.toWire operation}" ]
            @ (preconditions |> List.map (fun p -> $"pre|{p.Component}|{Expected.describe p.Artifact}|{Expected.describe p.Identity}"))
            @ (steps
               |> List.map (fun s ->
                   let arguments = String.concat " " s.Arguments
                   $"{s.Sequence}|{s.Id}|{s.Executable}|{arguments}"))
            |> String.concat "\n"

        { Profile = profile
          Root = root
          Operation = operation
          Preconditions = preconditions
          Steps = steps
          Refusals = refusals
          Digest = "sha256:" + sha256Text canonical }

    /// Run exactly the authorized plan. Preconditions are revalidated first;
    /// execution stops at the first invocation that does not exit 0.
    let apply (ctx: WorkstationContext) (probe: string -> string list -> ProcessResult) (plan: LifecyclePlan) (authorizedDigest: string) : Result<LifecycleResult, string> =
        if not plan.Refusals.IsEmpty then
            Error("plan has refusals: " + String.concat "; " plan.Refusals)
        elif authorizedDigest <> plan.Digest then
            Error $"authorization {authorizedDigest} does not match the disclosed lifecycle plan {plan.Digest}; review the plan and authorize its digest"
        else
            let failedPreconditions =
                plan.Preconditions
                |> List.collect (fun p ->
                    [ p.Artifact; p.Identity ]
                    |> List.choose (fun expected ->
                        match Engine.observe ctx probe expected with
                        | Outcome.Match, _ -> None
                        | _, observed -> Some $"{p.Component}: {Expected.describe expected} ({observed})"))

            if not failedPreconditions.IsEmpty then
                Error(
                    "installed components do not match the Registry selection; run `conditor workstation apply` for this resolved set first: "
                    + String.concat "; " failedPreconditions
                )
            else
                Ledger.append ctx (
                    [ "entry", "lifecycle-plan"
                      "digest", plan.Digest
                      "profile", $"{plan.Profile.Id}@{plan.Profile.Version}"
                      "operation", LifecycleOperation.toWire plan.Operation
                      "root", plan.Root ]
                    @ (plan.Profile.SourceIdentity |> Option.map (fun source -> [ "sourceIdentity", source ]) |> Option.defaultValue []))

                let rec loop (remaining: LifecycleStep list) (results: LifecycleStepResult list) =
                    match remaining with
                    | [] -> { Results = List.rev results; Failed = None }
                    | step :: rest ->
                        let r = probe (WorkstationPaths.resolve ctx step.Executable) step.Arguments
                        let result = { Step = step; ExitCode = r.ExitCode; StandardOutput = r.StandardOutput; StandardError = r.StandardError }

                        Ledger.append
                            ctx
                            [ "entry", "lifecycle"
                              "step", step.Id
                              "component", step.Component
                              "version", step.Version
                              "operation", LifecycleOperation.toWire step.Operation
                              "root", plan.Root
                              "exitCode", string r.ExitCode
                              "outputSha256", "sha256:" + sha256Text r.StandardOutput ]

                        if r.ExitCode = 0 then
                            loop rest (result :: results)
                        else
                            let detail = (r.StandardError + r.StandardOutput).Trim()
                            { Results = List.rev (result :: results); Failed = Some(step.Id, $"exit {r.ExitCode}: {detail}") }

                Ok(loop plan.Steps [])
