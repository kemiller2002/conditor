namespace Conditor.Core.Workstation

open System
open System.Formats.Tar
open System.IO
open System.IO.Compression
open System.Net.Http
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open Conditor.Core

/// What a plan step does to the host (CON-181).
[<RequireQualifiedAccess>]
type StepOperation =
    | Create
    | Modify
    | Download
    | AdoptReuse
    | ExternalTrust

[<RequireQualifiedAccess>]
module StepOperation =
    let toWire o =
        match o with
        | StepOperation.Create -> "CREATE"
        | StepOperation.Modify -> "MODIFY"
        | StepOperation.Download -> "DOWNLOAD"
        | StepOperation.AdoptReuse -> "ADOPT/REUSE"
        | StepOperation.ExternalTrust -> "EXTERNAL TRUST"

/// A typed expected receipt (CON-140; Ordo ORD-EXEC-100/101).
[<RequireQualifiedAccess>]
type Expected =
    | FileSha256 of path: string * sha256: string
    | DirectoryWithFile of path: string * file: string
    | CommandReports of executable: string * arguments: string list * contains: string
    | ManagedBlock of file: string * block: string
    | PrerequisitePresent of id: string

[<RequireQualifiedAccess>]
module Expected =
    let describe e =
        match e with
        | Expected.FileSha256(p, sha) -> $"file {p} has sha256:{sha}"
        | Expected.DirectoryWithFile(p, f) -> $"directory {p} contains {f}"
        | Expected.CommandReports(exe, args, contains) ->
            let joined = String.concat " " args
            $"`{exe} {joined}` exits 0 and reports {contains}"
        | Expected.ManagedBlock(f, b) -> $"{f} holds exactly one current conditor:{b} block"
        | Expected.PrerequisitePresent id -> $"{id} is present and adopted, never modified"

[<RequireQualifiedAccess>]
type Outcome =
    | Match
    | Mismatch
    | Indeterminate

[<RequireQualifiedAccess>]
module Outcome =
    let toWire o =
        match o with
        | Outcome.Match -> "match"
        | Outcome.Mismatch -> "mismatch"
        | Outcome.Indeterminate -> "indeterminate"

    let fromWire raw =
        match raw with
        | "match" -> Some Outcome.Match
        | "mismatch" -> Some Outcome.Mismatch
        | "indeterminate" -> Some Outcome.Indeterminate
        | _ -> None

/// A trust class for an external artifact (CON-182).
[<RequireQualifiedAccess>]
type Trust =
    | IntegrityVerified
    | TransportOnly
    | Unknown

type PlanStep =
    { Id: string
      Sequence: int
      Operation: StepOperation
      Component: string option
      /// Resource shown to the operator, relative to the workstation home
      /// (`~/...`), so a plan never leaks an absolute path.
      Resource: string
      /// Artifact identity for downloads: URL and pinned digest.
      Artifact: (string * string) option
      Trust: Trust
      Command: string option
      Expected: Expected
      Ownership: Ownership
      RequiresAuthorization: bool
      DependsOn: string list
      Reversible: bool }

type WorkstationPlan =
    { Profile: WorkstationProfile
      RuntimeIdentifier: string
      Prerequisites: (Prerequisite * PrerequisiteState) list
      Steps: PlanStep list
      Refusals: string list
      Digest: string }

/// Where the workstation's Conditor-owned state lives. `Home` is the user
/// home to act on (a temporary directory in rehearsals).
type WorkstationContext =
    { Home: string
      /// A local mirror of release assets (`<dir>/<asset-name>`), used
      /// instead of downloading when present (offline rehearsals, tests).
      ArtifactMirror: string option
      /// Refuse all network artifact fallback. Every required release asset
      /// must be present in ArtifactMirror when true.
      Offline: bool
      /// A Praxis executable that supports `installation register`, used to
      /// register installations when Project Administration is configured.
      Praxis: string option
      /// The explicit logical workstation identity for registration.
      TargetId: string option }

module WorkstationPaths =
    let conditorDir (ctx: WorkstationContext) = Path.Combine(ctx.Home, ".conditor", "workstation")
    let ledger ctx = Path.Combine(conditorDir ctx, "ledger.jsonl")
    let cache (ctx: WorkstationContext) = Path.Combine(ctx.Home, ".conditor", "cache")
    let installRoot (ctx: WorkstationContext) = Path.Combine(ctx.Home, ".local", "share", "echelon")
    let binDir (ctx: WorkstationContext) = Path.Combine(ctx.Home, ".local", "bin")
    let shellProfile (ctx: WorkstationContext) = Path.Combine(ctx.Home, ".profile")

    let display (ctx: WorkstationContext) (path: string) =
        let rel = Path.GetRelativePath(ctx.Home, path).Replace('\\', '/')
        if rel.StartsWith("..", StringComparison.Ordinal) then rel else "~/" + rel

    let resolve (ctx: WorkstationContext) (display: string) =
        if display.StartsWith("~/", StringComparison.Ordinal) then Path.Combine(ctx.Home, display.Substring 2) else display

/// The durable, append-only workstation ledger (CON-190..198).
module Ledger =
    let private options = JsonSerializerOptions(WriteIndented = false)

    let append (ctx: WorkstationContext) (fields: (string * string) list) =
        let node = JsonObject()
        fields |> List.iter (fun (k, v) -> node[k] <- JsonValue.Create v)
        node["at"] <- JsonValue.Create(DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"))
        let path = WorkstationPaths.ledger ctx
        Directory.CreateDirectory(Path.GetDirectoryName path |> Option.ofObj |> Option.defaultValue ".") |> ignore
        File.AppendAllText(path, node.ToJsonString options + "\n")

    let read (ctx: WorkstationContext) : Map<string, string> list =
        let path = WorkstationPaths.ledger ctx

        if not (File.Exists path) then
            []
        else
            File.ReadAllLines path
            |> Array.filter (fun l -> l.Trim().Length > 0)
            |> Array.choose (fun l ->
                match JsonNode.Parse l with
                | :? JsonObject as o ->
                    o
                    |> Seq.choose (fun kv ->
                        match kv.Value with
                        | :? JsonValue as v ->
                            match v.TryGetValue<string>() with
                            | true, s -> s |> Option.ofObj |> Option.map (fun text -> kv.Key, text)
                            | _ -> None
                        | _ -> None)
                    |> Map.ofSeq
                    |> Some
                | _ -> None)
            |> Array.toList

    let private get key (entry: Map<string, string>) = entry |> Map.tryFind key

    /// The latest status of each step: its last observation, reconciliation,
    /// or an attempt with no observation (an unknown effect).
    let stepStatus (entries: Map<string, string> list) (stepId: string) =
        entries
        |> List.filter (fun e -> get "step" e = Some stepId && List.contains (get "entry" e |> Option.defaultValue "") [ "started"; "observed"; "reconciled"; "rolled-back" ])
        |> List.tryLast
        |> Option.map (fun e ->
            match get "entry" e with
            | Some "started" -> "unknown"
            | Some "observed" -> get "outcome" e |> Option.defaultValue "indeterminate"
            | Some "reconciled" -> "reconciled-" + (get "finding" e |> Option.defaultValue "unknown")
            | Some "rolled-back" -> "rolled-back"
            | _ -> "unknown")

    /// Resources Conditor recorded ownership for, with the latest record
    /// per resource.
    let ownership (entries: Map<string, string> list) =
        entries
        |> List.filter (fun e -> get "entry" e = Some "ownership")
        |> List.groupBy (fun e -> get "resource" e |> Option.defaultValue "")
        |> List.map (fun (resource, es) -> resource, List.last es)
        |> List.filter (fun (resource, e) -> resource <> "" && get "state" e <> Some "released")

module Engine =
    let private sha256File (path: string) =
        use stream = File.OpenRead path
        Convert.ToHexString(SHA256.HashData stream).ToLowerInvariant()

    let private sha256Text (text: string) =
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes text)).ToLowerInvariant()

    let blockBegin id = $"# >>> conditor:{id} >>>"
    let blockEnd id = $"# <<< conditor:{id} <<<"

    let private pathBlockId = "workstation-path"

    let private pathBlock (ctx: WorkstationContext) =
        let bin = WorkstationPaths.display ctx (WorkstationPaths.binDir ctx)
        let shellBin = "$HOME/" + bin.Substring 2
        String.concat "\n" [ blockBegin pathBlockId; $"case \":$PATH:\" in *\":{shellBin}:\"*) ;; *) PATH=\"{shellBin}:$PATH\" ;; esac"; "export PATH"; blockEnd pathBlockId ]

    // ------------------------------------------------------------ discovery

    let private parseVersion (text: string) =
        text.Split([| ' '; '\n'; '\r'; '\t' |], StringSplitOptions.RemoveEmptyEntries)
        |> Array.tryFind (fun token -> token.Length > 0 && Char.IsAsciiDigit token[0] && token.Contains '.')
        |> Option.map (fun token -> token.TrimEnd('.', ','))

    let private versionAtLeast (minimum: string) (actual: string) =
        let parts (v: string) =
            v.Split('.') |> Array.truncate 3 |> Array.map (fun p -> p |> Seq.takeWhile Char.IsAsciiDigit |> Seq.toArray |> String |> fun s -> if s = "" then 0 else int s)

        compare (parts actual |> Array.toList) (parts minimum |> Array.toList) >= 0

    /// Classify each prerequisite without mutating the host (CON-170..175).
    /// `probe` runs a command and returns its result; injected for tests.
    let discover (probe: string -> string list -> ProcessResult) (prerequisites: Prerequisite list) =
        prerequisites
        |> List.map (fun p ->
            let state =
                match p.Id with
                | "git" ->
                    let r = probe "git" [ "--version" ]

                    match r.ExitCode, parseVersion r.StandardOutput with
                    | 0, Some v when p.MinimumVersion |> Option.forall (fun m -> versionAtLeast m v) -> PrerequisiteState.Satisfied v
                    | 0, Some v -> PrerequisiteState.ExternalOnly $"git {v} is older than {p.MinimumVersion |> Option.defaultValue String.Empty}; upgrade it with the host package manager"
                    | 0, None -> PrerequisiteState.Unknown "git reported no version"
                    | _ -> PrerequisiteState.ExternalOnly "git is not installed; install it with the host package manager"
                | "gh" ->
                    let installed = probe "gh" [ "--version" ]

                    if installed.ExitCode <> 0 then
                        PrerequisiteState.ExternalOnly "GitHub CLI is not installed"
                    else
                        // Authentication is checked separately and never persisted.
                        let auth = probe "gh" [ "auth"; "status" ]
                        let version = parseVersion installed.StandardOutput |> Option.defaultValue "unknown"

                        if auth.ExitCode = 0 then PrerequisiteState.Satisfied version
                        else PrerequisiteState.ExternalOnly $"GitHub CLI {version} is installed but not authenticated (run gh auth login)"
                | other -> PrerequisiteState.Unknown $"no discovery rule for '{other}'"

            p, state)

    // -------------------------------------------------------------- planning

    let private releaseUrl (c: ProfileComponent) (asset: ReleaseAsset) =
        $"https://github.com/{c.Repository}/releases/download/{c.Tag}/{asset.Name}"

    /// Build the complete, deterministic effect plan before any mutation
    /// (CON-180..186). The digest scopes authorization to exactly these
    /// disclosed effects.
    let plan (ctx: WorkstationContext) (profile: WorkstationProfile) (rid: string) (prerequisites: (Prerequisite * PrerequisiteState) list) (selectedOptional: string list) : WorkstationPlan =
        let ownedByLedger =
            Ledger.read ctx |> Ledger.ownership |> List.map fst |> Set.ofList

        let prerequisiteSteps =
            prerequisites
            |> List.mapi (fun i (p, state) ->
                { Id = $"prerequisite-{p.Id}"
                  Sequence = i + 1
                  Operation = StepOperation.AdoptReuse
                  Component = None
                  Resource = p.Id
                  Artifact = None
                  Trust = Trust.Unknown
                  Command = None
                  Expected = Expected.PrerequisitePresent p.Id
                  Ownership = Ownership.Adopted
                  RequiresAuthorization = false
                  DependsOn = []
                  Reversible = false })
            |> List.filter (fun s ->
                prerequisites
                |> List.exists (fun (p, st) -> $"prerequisite-{p.Id}" = s.Id && (match st with PrerequisiteState.Satisfied _ -> true | _ -> false)))

        let componentSteps =
            profile.Components
            |> List.collect (fun c ->
                match c.Assets |> Map.tryFind rid with
                | None -> []
                | Some asset ->
                    let cached = Path.Combine(WorkstationPaths.cache ctx, c.Id, c.Version, asset.Name)
                    let installDir = Path.Combine(WorkstationPaths.installRoot ctx, c.Id, c.Version)
                    let shim = Path.Combine(WorkstationPaths.binDir ctx, c.Executable)
                    let display = WorkstationPaths.display ctx
                    let owned path = if ownedByLedger.Contains(display path) || not (File.Exists path || Directory.Exists path) then Ownership.ConditorCreated else Ownership.UserOwned

                    [ { Id = $"{c.Id}-download"
                        Sequence = 0
                        Operation = StepOperation.Download
                        Component = Some c.Id
                        Resource = display cached
                        Artifact = Some(releaseUrl c asset, "sha256:" + asset.Sha256)
                        Trust = Trust.IntegrityVerified
                        Command = None
                        Expected = Expected.FileSha256(display cached, asset.Sha256)
                        Ownership = owned cached
                        RequiresAuthorization = true
                        DependsOn = []
                        Reversible = true }
                      { Id = $"{c.Id}-extract"
                        Sequence = 0
                        Operation = StepOperation.Create
                        Component = Some c.Id
                        Resource = display installDir
                        Artifact = None
                        Trust = Trust.IntegrityVerified
                        Command = None
                        Expected = Expected.DirectoryWithFile(display installDir, c.Executable)
                        Ownership = owned installDir
                        RequiresAuthorization = true
                        DependsOn = [ $"{c.Id}-download" ]
                        Reversible = true }
                      { Id = $"{c.Id}-shim"
                        Sequence = 0
                        Operation = StepOperation.Create
                        Component = Some c.Id
                        Resource = display shim
                        Artifact = None
                        Trust = Trust.IntegrityVerified
                        Command = Some(String.concat " " (c.Executable :: c.VersionProbe))
                        Expected = Expected.CommandReports(display shim, c.VersionProbe, c.Version)
                        Ownership = owned shim
                        RequiresAuthorization = true
                        DependsOn = [ $"{c.Id}-extract" ]
                        Reversible = true } ])

        let profileFile = WorkstationPaths.shellProfile ctx

        let pathStep =
            if componentSteps.IsEmpty then
                []
            else
                [ { Id = "shell-path"
                    Sequence = 0
                    Operation = StepOperation.Modify
                    Component = None
                    Resource = WorkstationPaths.display ctx profileFile
                    Artifact = None
                    Trust = Trust.IntegrityVerified
                    Command = None
                    Expected = Expected.ManagedBlock(WorkstationPaths.display ctx profileFile, pathBlockId)
                    // The file is the user's; only the managed block is Conditor's.
                    Ownership = Ownership.Shared
                    RequiresAuthorization = true
                    DependsOn = []
                    Reversible = true } ]

        let refusals =
            [ for c in profile.Components do
                  if not (c.Assets.ContainsKey rid) then
                      yield $"{c.Id} {c.Version} publishes no asset for {rid}"
              for (p, st) in prerequisites do
                  match st with
                  | PrerequisiteState.Satisfied _ -> ()
                  | PrerequisiteState.ExternalOnly why
                  | PrerequisiteState.Unsupported why
                  | PrerequisiteState.Unknown why when p.Required -> yield $"required prerequisite {p.Id}: {why}"
                  | _ -> ()
              for o in selectedOptional do
                  match profile.Optional |> List.tryFind (fun (id, _) -> id = o) with
                  | Some(id, reason) -> yield $"optional capability {id} is not installable by this profile version ({reason})"
                  | None -> yield $"'{o}' is not an optional capability of profile {profile.Id}" ]

        let steps =
            prerequisiteSteps @ pathStep @ componentSteps
            |> List.mapi (fun i s -> { s with Sequence = i + 1 })

        let canonical =
            steps
            |> List.map (fun s ->
                let artifact = s.Artifact |> Option.map (fun (u, d) -> u + "@" + d) |> Option.defaultValue "-"
                $"{s.Sequence}|{s.Id}|{StepOperation.toWire s.Operation}|{s.Resource}|{artifact}|{Ownership.toWire s.Ownership}|{Expected.describe s.Expected}")
            |> String.concat "\n"

        { Profile = profile
          RuntimeIdentifier = rid
          Prerequisites = prerequisites
          Steps = steps
          Refusals = refusals
          Digest =
            let sourceIdentity = profile.SourceIdentity |> Option.defaultValue "-"
            "sha256:" + sha256Text ($"{profile.Id}@{profile.Version}\n{sourceIdentity}\n{canonical}") }

    // -------------------------------------------------------------- receipts

    /// Observe a step's expected receipt on the host.
    let observe (ctx: WorkstationContext) (probe: string -> string list -> ProcessResult) (expected: Expected) : Outcome * string =
        match expected with
        | Expected.FileSha256(display, sha) ->
            let path = WorkstationPaths.resolve ctx display

            if not (File.Exists path) then Outcome.Mismatch, "absent"
            else
                let actual = sha256File path
                (if actual = sha then Outcome.Match else Outcome.Mismatch), "sha256:" + actual
        | Expected.DirectoryWithFile(display, file) ->
            let dir = WorkstationPaths.resolve ctx display

            if File.Exists(Path.Combine(dir, file)) then Outcome.Match, "present"
            elif Directory.Exists dir then Outcome.Indeterminate, "directory present without its executable (partial extraction?)"
            else Outcome.Mismatch, "absent"
        | Expected.CommandReports(display, args, contains) ->
            let exe = WorkstationPaths.resolve ctx display

            if not (File.Exists exe) then
                Outcome.Mismatch, "absent"
            else
                let r = probe exe args

                if r.ExitCode = -1 then Outcome.Indeterminate, r.StandardError.Trim()
                elif r.ExitCode = 0 && r.StandardOutput.Contains contains then Outcome.Match, r.StandardOutput.Trim()
                else Outcome.Mismatch, $"exit {r.ExitCode}: {r.StandardOutput.Trim()}"
        | Expected.ManagedBlock(display, block) ->
            let file = WorkstationPaths.resolve ctx display

            if not (File.Exists file) then
                Outcome.Mismatch, "absent"
            else
                let text = File.ReadAllText file
                let occurrences = text.Split(blockBegin block).Length - 1

                match occurrences with
                | 1 when text.Contains(pathBlock ctx) -> Outcome.Match, "one managed block"
                | 0 -> Outcome.Mismatch, "no managed block"
                | 1 -> Outcome.Mismatch, "managed block content differs (edited?)"
                | n -> Outcome.Mismatch, $"{n} managed blocks (duplicates)"
        | Expected.PrerequisitePresent _ -> Outcome.Match, "discovered"

    // ------------------------------------------------------------- effects

    let private fetch (ctx: WorkstationContext) (url: string) (tag: string) (assetName: string) (destination: string) =
        Directory.CreateDirectory(Path.GetDirectoryName destination |> Option.ofObj |> Option.defaultValue ".") |> ignore
        let temp = destination + ".partial"

        // A mirror may be laid out by tag (`<mirror>/<tag>/<asset>`) or flat.
        let mirrored =
            ctx.ArtifactMirror
            |> Option.bind (fun m -> [ Path.Combine(m, tag, assetName); Path.Combine(m, assetName) ] |> List.tryFind File.Exists)

        match mirrored with
        | Some local -> File.Copy(local, temp, true)
        | None when ctx.Offline ->
            invalidOp $"Offline workstation mode refused network fallback for {assetName}. Expected the artifact in --artifact-mirror."
        | None ->
            use client = new HttpClient()
            use response = client.GetAsync(url).GetAwaiter().GetResult()
            response.EnsureSuccessStatusCode() |> ignore
            use output = File.Create temp
            response.Content.CopyToAsync(output).GetAwaiter().GetResult()

        File.Move(temp, destination, true)

    let private extract (archive: string) (destination: string) =
        let staging = destination + ".staging"
        if Directory.Exists staging then Directory.Delete(staging, true)
        Directory.CreateDirectory staging |> ignore

        if archive.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) then
            ZipFile.ExtractToDirectory(archive, staging)
        else
            use file = File.OpenRead archive
            use gzip = new GZipStream(file, CompressionMode.Decompress)
            TarFile.ExtractToDirectory(gzip, staging, true)

        // Release bundles wrap their content in one top-level directory.
        let content =
            match Directory.GetDirectories staging, Directory.GetFiles staging with
            | [| single |], [||] -> single
            | _ -> staging

        if Directory.Exists destination then Directory.Delete(destination, true)
        Directory.CreateDirectory(Path.GetDirectoryName destination |> Option.ofObj |> Option.defaultValue ".") |> ignore
        Directory.Move(content, destination)
        if Directory.Exists staging then Directory.Delete(staging, true)

    let private installNativeAsset (artifact: string) (destination: string) (executable: string) =
        if artifact.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
           || artifact.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) then
            extract artifact destination
        else
            let staging = destination + ".staging"
            if Directory.Exists staging then Directory.Delete(staging, true)
            Directory.CreateDirectory staging |> ignore
            let target = Path.Combine(staging, executable)
            File.Copy(artifact, target, true)

            if not (OperatingSystem.IsWindows()) then
                File.SetUnixFileMode(
                    target,
                    UnixFileMode.UserRead
                    ||| UnixFileMode.UserWrite
                    ||| UnixFileMode.UserExecute
                    ||| UnixFileMode.GroupRead
                    ||| UnixFileMode.GroupExecute
                    ||| UnixFileMode.OtherRead
                    ||| UnixFileMode.OtherExecute
                )

            if Directory.Exists destination then Directory.Delete(destination, true)
            Directory.CreateDirectory(Path.GetDirectoryName destination |> Option.ofObj |> Option.defaultValue ".") |> ignore
            Directory.Move(staging, destination)

    let private writeShim (shim: string) (target: string) =
        Directory.CreateDirectory(Path.GetDirectoryName shim |> Option.ofObj |> Option.defaultValue ".") |> ignore
        File.WriteAllText(shim, $"#!/bin/sh\n# Managed by Conditor; see ~/.conditor/workstation/ledger.jsonl\nexec \"{target}\" \"$@\"\n")

        if not (OperatingSystem.IsWindows()) then
            File.SetUnixFileMode(shim, UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute ||| UnixFileMode.GroupRead ||| UnixFileMode.GroupExecute ||| UnixFileMode.OtherRead ||| UnixFileMode.OtherExecute)

    let private ensureBlock (ctx: WorkstationContext) (file: string) =
        let block = pathBlock ctx
        let existing = if File.Exists file then File.ReadAllText file else ""

        if existing.Contains(blockBegin pathBlockId) then
            // Replace the existing managed block; never append a duplicate.
            let start = existing.IndexOf(blockBegin pathBlockId)
            let finish = existing.IndexOf(blockEnd pathBlockId, start)
            let tail = if finish < 0 then "" else existing.Substring(finish + (blockEnd pathBlockId).Length)
            File.WriteAllText(file, existing.Substring(0, start) + block + tail)
        else
            let separator = if existing = "" || existing.EndsWith "\n" then "" else "\n"
            File.WriteAllText(file, existing + separator + block + "\n")

    let private removeBlock (file: string) =
        if File.Exists file then
            let existing = File.ReadAllText file
            let start = existing.IndexOf(blockBegin pathBlockId)

            if start >= 0 then
                let finish = existing.IndexOf(blockEnd pathBlockId, start)
                let tail = if finish < 0 then "" else existing.Substring(finish + (blockEnd pathBlockId).Length).TrimStart('\n')
                File.WriteAllText(file, existing.Substring(0, start) + tail)

    /// Perform one step's effect.
    let private perform (ctx: WorkstationContext) (profile: WorkstationProfile) (rid: string) (step: PlanStep) =
        let comp = step.Component |> Option.bind (fun id -> profile.Components |> List.tryFind (fun c -> c.Id = id))

        match step.Id, comp with
        | id, Some c when id = $"{c.Id}-download" ->
            let asset = c.Assets[rid]
            fetch ctx (releaseUrl c asset) c.Tag asset.Name (WorkstationPaths.resolve ctx step.Resource)
        | id, Some c when id = $"{c.Id}-extract" ->
            let artifact = Path.Combine(WorkstationPaths.cache ctx, c.Id, c.Version, c.Assets[rid].Name)
            installNativeAsset artifact (WorkstationPaths.resolve ctx step.Resource) c.Executable
        | id, Some c when id = $"{c.Id}-shim" ->
            writeShim (WorkstationPaths.resolve ctx step.Resource) (Path.Combine(WorkstationPaths.installRoot ctx, c.Id, c.Version, c.Executable))
        | "shell-path", _ -> ensureBlock ctx (WorkstationPaths.resolve ctx step.Resource)
        | _ -> ()

    /// Undo one Conditor-owned effect (rollback and uninstall).
    let undo (ctx: WorkstationContext) (resource: string) =
        let path = WorkstationPaths.resolve ctx resource

        if resource = WorkstationPaths.display ctx (WorkstationPaths.shellProfile ctx) then removeBlock path
        elif Directory.Exists path then Directory.Delete(path, true)
        elif File.Exists path then File.Delete path

    // -------------------------------------------------------- registration

    /// Register an installed component through Praxis's
    /// `praxis installation register` (Project Administration's
    /// capability). Called only after the component's receipt matched.
    let register (ctx: WorkstationContext) (probe: string -> string list -> ProcessResult) (verb: string) (c: ProfileComponent) (rid: string) (stepId: string) =
        match ctx.Praxis with
        | None -> "unavailable", "no Praxis with installation registration configured (--praxis)"
        | Some praxis ->
            let asset = c.Assets |> Map.tryFind rid

            let target =
                match ctx.TargetId with
                | Some id -> [ "--target-kind"; "environment"; "--target-id"; id ]
                | None -> [ "--target-kind"; "environment" ]

            let args =
                [ "installation"; verb; "--system"; c.Id; "--version"; c.Version ]
                @ target
                @ [ "--source-repository"; c.Repository; "--distribution"; "github-release"; "--release"; c.Tag ]
                @ (asset |> Option.map (fun a -> [ "--artifact"; a.Name; "--digest"; "sha256:" + a.Sha256 ]) |> Option.defaultValue [])
                @ [ "--evidence"; $"receipt=conditor-workstation-ledger#{stepId}"; "--json" ]

            let r = probe praxis args

            let outcome =
                try
                    match JsonNode.Parse r.StandardOutput with
                    | null -> "unavailable"
                    | node ->
                        match node["outcome"] with
                        | :? JsonValue as v ->
                            match v.TryGetValue<string>() with
                            | true, s -> s |> Option.ofObj |> Option.defaultValue "unavailable"
                            | _ -> "unavailable"
                        | _ -> "unavailable"
                with _ ->
                    "unavailable"

            outcome, (r.StandardError + r.StandardOutput).Trim()

    // ---------------------------------------------------------------- apply

    type ApplyResult =
        { Completed: string list
          Reused: string list
          Failed: (string * string) option
          RolledBack: (string * string) list
          Registrations: (string * string) list }

    let private retrySafe (step: PlanStep) =
        match step.Operation with
        | StepOperation.Download -> true // verified by digest after the fact
        | _ -> false

    /// Apply an authorized plan. Refuses a digest that does not match the
    /// disclosed plan, refuses to retry a step whose effect is unknown until
    /// it is reconciled, stops at the first mismatch, and rolls back the
    /// Conditor-owned effects of this run from the ledger (CON-220..225).
    let apply (ctx: WorkstationContext) (probe: string -> string list -> ProcessResult) (plan: WorkstationPlan) (authorizedDigest: string) (rollbackOnFailure: bool) : Result<ApplyResult, string> =
        if not plan.Refusals.IsEmpty then
            Error("plan has refusals: " + String.concat "; " plan.Refusals)
        elif authorizedDigest <> plan.Digest then
            Error $"authorization {authorizedDigest} does not match the disclosed plan {plan.Digest}; review the plan and authorize its digest"
        else
            let before = Ledger.read ctx

            let unknown =
                plan.Steps
                |> List.filter (fun s -> Ledger.stepStatus before s.Id = Some "unknown" && not (retrySafe s))

            if not unknown.IsEmpty then
                let names = unknown |> List.map (fun s -> s.Id) |> String.concat ", "
                Error $"steps with unknown effects must be reconciled before retry: {names} (conditor workstation reconcile)"
            else
                Ledger.append ctx (
                    [ "entry", "plan"
                      "digest", plan.Digest
                      "profile", $"{plan.Profile.Id}@{plan.Profile.Version}"
                      "rid", plan.RuntimeIdentifier ]
                    @ (plan.Profile.SourceIdentity |> Option.map (fun source -> [ "sourceIdentity", source ]) |> Option.defaultValue []))

                let stop (acc: ApplyResult) (step: PlanStep) (outcome: Outcome) (observed: string) =
                    let failed = { acc with Failed = Some(step.Id, observed) }

                    if not rollbackOnFailure then
                        failed
                    else
                        let reversible =
                            plan.Steps
                            |> List.filter (fun s -> List.contains s.Id (acc.Completed @ [ step.Id ]) && s.Reversible && s.Ownership <> Ownership.UserOwned)
                            |> List.rev

                        let rolled =
                            reversible
                            |> List.map (fun s ->
                                // Only effects this run created are reversed; a failed step whose
                                // effect is indeterminate is left for reconciliation.
                                if s.Id = step.Id && outcome = Outcome.Indeterminate then
                                    s.Id, "retained-indeterminate"
                                else
                                    try
                                        undo ctx s.Resource
                                        let path = WorkstationPaths.resolve ctx s.Resource
                                        let gone = s.Operation = StepOperation.Modify || not (File.Exists path || Directory.Exists path)
                                        let result = if gone then "match" else "mismatch"
                                        Ledger.append ctx [ "entry", "rolled-back"; "step", s.Id; "resource", s.Resource; "outcome", result ]
                                        if gone then Ledger.append ctx [ "entry", "ownership"; "resource", s.Resource; "class", "conditor-created"; "state", "released" ]
                                        s.Id, result
                                    with ex ->
                                        Ledger.append ctx [ "entry", "rolled-back"; "step", s.Id; "resource", s.Resource; "outcome", "indeterminate"; "detail", ex.Message ]
                                        s.Id, "indeterminate")

                        { failed with RolledBack = rolled }

                let rec loop (remaining: PlanStep list) (acc: ApplyResult) =
                    match remaining with
                    | [] -> acc
                    | step :: rest ->
                        let pre, _ = observe ctx probe step.Expected

                        if pre = Outcome.Match then
                            // Already satisfied: reuse; never claim ownership of something we did not create.
                            let owned = Ledger.ownership (Ledger.read ctx) |> List.exists (fun (r, _) -> r = step.Resource)
                            Ledger.append ctx [ "entry", "observed"; "step", step.Id; "outcome", "match"; "observed", "pre-existing"; "reused", "true" ]

                            // Validating or reusing an existing tool never claims it (CON-205).
                            if not owned && step.Ownership <> Ownership.Shared then
                                Ledger.append ctx [ "entry", "ownership"; "resource", step.Resource; "class", Ownership.toWire Ownership.Adopted; "component", defaultArg step.Component "" ]

                            loop rest { acc with Reused = acc.Reused @ [ step.Id ] }
                        elif step.Ownership = Ownership.UserOwned then
                            // Never overwrite something Conditor did not create (CON-011, CON-202).
                            Ledger.append ctx [ "entry", "observed"; "step", step.Id; "outcome", "mismatch"; "observed", "refused: resource exists and is user-owned" ]
                            stop acc step Outcome.Mismatch $"{step.Resource} exists and is not Conditor-owned; move it aside or remove it, then plan again"
                        else
                            let priorExisted = File.Exists(WorkstationPaths.resolve ctx step.Resource) || Directory.Exists(WorkstationPaths.resolve ctx step.Resource)
                            Ledger.append ctx [ "entry", "started"; "step", step.Id; "operation", StepOperation.toWire step.Operation; "resource", step.Resource ]

                            let performed =
                                try
                                    perform ctx plan.Profile plan.RuntimeIdentifier step
                                    Ok()
                                with ex ->
                                    Error ex.Message

                            let outcome, observed =
                                match performed with
                                | Error message -> (if priorExisted then Outcome.Indeterminate else Outcome.Mismatch), message
                                | Ok() -> observe ctx probe step.Expected

                            Ledger.append ctx [ "entry", "observed"; "step", step.Id; "outcome", Outcome.toWire outcome; "observed", observed ]

                            match outcome with
                            | Outcome.Match ->
                                let ownershipClass = if step.Ownership = Ownership.Shared then Ownership.Shared else Ownership.ConditorCreated

                                Ledger.append
                                    ctx
                                    [ "entry", "ownership"
                                      "resource", step.Resource
                                      "class", Ownership.toWire ownershipClass
                                      "component", defaultArg step.Component ""
                                      "priorExisted", string priorExisted ]

                                let registrations =
                                    match step.Component, step.Id.EndsWith "-shim" with
                                    | Some id, true ->
                                        let c = plan.Profile.Components |> List.find (fun c -> c.Id = id)
                                        let result, detail = register ctx probe "register" c plan.RuntimeIdentifier step.Id
                                        Ledger.append ctx [ "entry", "registration"; "component", id; "version", c.Version; "outcome", result; "detail", detail ]
                                        [ id, result ]
                                    | _ -> []

                                loop rest { acc with Completed = acc.Completed @ [ step.Id ]; Registrations = acc.Registrations @ registrations }
                            | _ -> stop acc step outcome observed

                Ok(loop plan.Steps { Completed = []; Reused = []; Failed = None; RolledBack = []; Registrations = [] })

    /// Reconcile an unknown effect by observation, never by assumption
    /// (CON-194; ORD-EXEC-113).
    let reconcile (ctx: WorkstationContext) (probe: string -> string list -> ProcessResult) (plan: WorkstationPlan) (stepId: string) =
        match plan.Steps |> List.tryFind (fun s -> s.Id = stepId) with
        | None -> Error $"step {stepId} is not in the plan"
        | Some step when Ledger.stepStatus (Ledger.read ctx) stepId <> Some "unknown" && Ledger.stepStatus (Ledger.read ctx) stepId <> Some "indeterminate" ->
            Error $"step {stepId} has no unknown effect to reconcile"
        | Some step ->
            let outcome, observed = observe ctx probe step.Expected
            let path = WorkstationPaths.resolve ctx step.Resource

            let finding =
                match outcome with
                | Outcome.Match -> "occurred"
                | _ when not (File.Exists path || Directory.Exists path) -> "did-not-occur"
                | _ -> "unknown"

            Ledger.append ctx [ "entry", "reconciled"; "step", stepId; "finding", finding; "observed", observed ]

            if finding = "occurred" then
                Ledger.append ctx [ "entry", "observed"; "step", stepId; "outcome", "match"; "observed", observed ]
                Ledger.append ctx [ "entry", "ownership"; "resource", step.Resource; "class", Ownership.toWire (if step.Ownership = Ownership.Shared then Ownership.Shared else Ownership.ConditorCreated); "component", defaultArg step.Component "" ]

            Ok finding

    // ------------------------------------------------------------ uninstall

    [<RequireQualifiedAccess>]
    type UninstallAction =
        | Remove
        | Restore
        | RetainShared
        | RetainAdopted
        | RetainUser
        | Unresolved of reason: string

    let uninstallActionToWire a =
        match a with
        | UninstallAction.Remove -> "remove"
        | UninstallAction.Restore -> "restore"
        | UninstallAction.RetainShared -> "retain-shared"
        | UninstallAction.RetainAdopted -> "retain-adopted"
        | UninstallAction.RetainUser -> "retain-user"
        | UninstallAction.Unresolved _ -> "unresolved"

    type UninstallPlan =
        { Actions: (string * string * UninstallAction) list
          Components: string list
          Digest: string }

    /// The deterministic uninstall plan, derived from recorded ownership
    /// only (CON-210..217). Adopted tools are never removed; project source
    /// and user work are never in scope.
    let uninstallPlan (ctx: WorkstationContext) =
        let ownership = Ledger.read ctx |> Ledger.ownership

        let actions =
            ownership
            |> List.map (fun (resource, record) ->
                let klass = record |> Map.tryFind "class" |> Option.bind Ownership.fromWire |> Option.defaultValue Ownership.UserOwned
                let path = WorkstationPaths.resolve ctx resource

                let action =
                    match klass with
                    | Ownership.ConditorCreated when resource.EndsWith("/bin/" + Path.GetFileName path) && File.Exists path && not ((File.ReadAllText path).Contains "Managed by Conditor") ->
                        UninstallAction.Unresolved "the shim was replaced by something Conditor did not write"
                    | Ownership.ConditorCreated -> UninstallAction.Remove
                    | Ownership.Shared -> UninstallAction.Restore
                    | Ownership.Adopted
                    | Ownership.External -> UninstallAction.RetainAdopted
                    | Ownership.OtherEchelon -> UninstallAction.RetainShared
                    | Ownership.UserOwned -> UninstallAction.RetainUser

                resource, (record |> Map.tryFind "component" |> Option.defaultValue ""), action)
            |> List.sortBy (fun (r, _, _) -> r)

        let components =
            actions
            |> List.choose (fun (_, c, a) -> if c <> "" && a = UninstallAction.Remove then Some c else None)
            |> List.distinct

        let canonical = actions |> List.map (fun (r, c, a) -> $"{r}|{c}|{uninstallActionToWire a}") |> String.concat "\n"
        { Actions = actions; Components = components; Digest = "sha256:" + sha256Text canonical }

    /// Apply an authorized uninstall plan. Unresolved actions block
    /// destruction entirely. Removal is recorded per resource with a
    /// receipt; installation removal is registered only after the component's
    /// removal receipts matched.
    let uninstall (ctx: WorkstationContext) (probe: string -> string list -> ProcessResult) (profile: WorkstationProfile option) (plan: UninstallPlan) (authorizedDigest: string) =
        if authorizedDigest <> plan.Digest then
            Error $"authorization {authorizedDigest} does not match the uninstall plan {plan.Digest}"
        elif plan.Actions |> List.exists (fun (_, _, a) -> match a with UninstallAction.Unresolved _ -> true | _ -> false) then
            Error "the uninstall plan has unresolved resources; nothing was removed"
        else
            let results =
                plan.Actions
                |> List.choose (fun (resource, comp, action) ->
                    match action with
                    | UninstallAction.Remove
                    | UninstallAction.Restore ->
                        undo ctx resource
                        let path = WorkstationPaths.resolve ctx resource

                        let removed =
                            match action with
                            | UninstallAction.Restore -> not (File.Exists path) || not ((File.ReadAllText path).Contains(blockBegin pathBlockId))
                            | _ -> not (File.Exists path || Directory.Exists path)

                        let outcome = if removed then "match" else "mismatch"
                        Ledger.append ctx [ "entry", "removed"; "resource", resource; "action", uninstallActionToWire action; "outcome", outcome; "component", comp ]
                        if removed then Ledger.append ctx [ "entry", "ownership"; "resource", resource; "class", "conditor-created"; "state", "released" ]
                        Some(resource, comp, outcome)
                    | _ -> None)

            let registrations =
                plan.Components
                |> List.choose (fun id ->
                    let all = results |> List.filter (fun (_, c, _) -> c = id)

                    match profile |> Option.bind (fun p -> p.Components |> List.tryFind (fun c -> c.Id = id)) with
                    | Some c when all |> List.forall (fun (_, _, o) -> o = "match") ->
                        let result, detail = register ctx probe "remove" c (Platform.runtimeIdentifier ()) $"{id}-uninstall"
                        Ledger.append ctx [ "entry", "registration"; "component", id; "version", c.Version; "operation", "remove"; "outcome", result; "detail", detail ]
                        Some(id, result)
                    | _ -> None)

            Ok(results, registrations)
