/// Workstation installation registration (conditor#95, CON-364..366,
/// DF-CON-2026-A006): every outcome carries its reason; no Praxis is
/// `unavailable` and an unusable one is `misconfigured` (the Registry's
/// integration vocabulary); the workstation's own Conditor-installed,
/// ledger-owned `~/.local/bin/praxis` is the default registrar, while an
/// explicit --praxis/CONDITOR_PRAXIS still wins; and installs whose
/// registration never succeeded are backfilled through a planned,
/// authorized command. Sacrificial homes only; nothing touches the real home.
module RegistrationBackfillTests

open System
open System.Formats.Tar
open System.IO
open System.IO.Compression
open System.Security.Cryptography
open Conditor.Core
open Conditor.Core.Workstation

let private temp label =
    let dir = Path.Combine(Path.GetTempPath(), $"conditor-reg-{label}-{Guid.NewGuid():N}")
    Directory.CreateDirectory dir |> ignore
    dir

let private probe exe args = ProcessRunner.runProcess (Path.GetTempPath()) exe args

let private executable (path: string) (body: string) =
    File.WriteAllText(path, body)
    File.SetUnixFileMode(path, UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute)

/// A release bundle `<id>-<rid>.tar.gz` holding `script` as its executable.
let private bundle (mirror: string) (id: string) (script: string) =
    let rid = Platform.runtimeIdentifier ()
    let staging = temp "bundle"
    let root = Path.Combine(staging, $"{id}-{rid}")
    Directory.CreateDirectory root |> ignore
    executable (Path.Combine(root, id)) script
    let name = $"{id}-{rid}.tar.gz"
    let archive = Path.Combine(mirror, name)

    do
        use file = File.Create archive
        use gzip = new GZipStream(file, CompressionLevel.Fastest)
        TarFile.CreateFromDirectory(staging, gzip, false)

    use stream = File.OpenRead archive
    name, Convert.ToHexString(SHA256.HashData stream).ToLowerInvariant()

/// A tool that only reports its version.
let private tool id version = $"#!/bin/sh\necho \"{id} {version}\"\n"

/// A Praxis that reports `version`, logs every registration request to
/// `log` and answers with `answer` (a Praxis installation outcome JSON).
let private praxisScript version (log: string) (answer: string) =
    $"#!/bin/sh\nif [ \"$1\" = \"--version\" ]; then echo \"praxis {version}\"; exit 0; fi\necho \"$@\" >> \"{log}\"\nprintf '%%s' '{answer}'\n"

let private profileFile (dir: string) (components: (string * string * string * string) list) =
    let rid = Platform.runtimeIdentifier ()

    let entries =
        components
        |> List.map (fun (id, version, name, sha) ->
            $$"""{ "id": "{{id}}", "version": "{{version}}", "executable": "{{id}}", "versionProbe": ["--version"],
                  "source": { "kind": "github-release", "repository": "example/{{id}}", "tag": "v{{version}}" },
                  "assets": { "{{rid}}": { "name": "{{name}}", "sha256": "{{sha}}" } } }""")
        |> String.concat ","

    let path = Path.Combine(dir, "test.profile.json")
    File.WriteAllText(path, $$"""{ "schema": "conditor.workstation-profile/v1", "id": "test", "version": 1, "extends": "minimal", "components": [ {{entries}} ] }""")
    path

let private resolve path =
    match Profiles.resolve path with
    | Ok p -> p
    | Error e -> failwith e

let private context home mirror praxis : WorkstationContext =
    { Home = home; ArtifactMirror = Some mirror; Offline = false; Praxis = praxis; TargetId = Some "ws-test" }

let private install ctx profile =
    let plan = Engine.plan ctx profile (Platform.runtimeIdentifier ()) (Engine.discover probe profile.Prerequisites) []

    match Engine.apply ctx probe plan plan.Digest true with
    | Ok r when r.Failed.IsNone -> r
    | Ok r -> failwith $"install failed: {r.Failed}"
    | Error e -> failwith e

let private recorded = """{"schema":"praxis.installation.outcome/v1","outcome":"recorded","eventId":"evt-1"}"""

let private calls log = if File.Exists log then File.ReadAllText log else ""

let private registrationOf id (results: (string * Engine.Registration) list) =
    results |> List.tryFind (fun (c, _) -> c = id) |> Option.map snd

/// A home with strata 1.0.0, praxis 9.9.0 (a fake that records) and ordo
/// 2.0.0 installed by Conditor, with no Praxis configured at install time.
let private installedHome () =
    let home = temp "home"
    let mirror = temp "mirror"
    let log = Path.Combine(mirror, "praxis-calls.log")
    let strata = bundle mirror "strata" (tool "strata" "1.0.0")
    let praxis = bundle mirror "praxis" (praxisScript "9.9.0" log recorded)
    let ordo = bundle mirror "ordo" (tool "ordo" "2.0.0")

    let profile =
        profileFile
            mirror
            [ "strata", "1.0.0", fst strata, snd strata
              "praxis", "9.9.0", fst praxis, snd praxis
              "ordo", "2.0.0", fst ordo, snd ordo ]
        |> resolve

    home, mirror, log, profile

let run (check: string -> bool -> unit) =
    let rid = Platform.runtimeIdentifier ()

    // Fixes 1 and 2: every outcome has a reason, in the Registry vocabulary.
    let home, mirror, log, profile = installedHome ()
    let emptyHome = temp "empty"
    let notConfigured = Engine.register (context emptyHome mirror None) probe "register" profile.Components.Head rid "strata-shim"
    check "registration without any Praxis is unavailable" (notConfigured.Outcome = "unavailable")
    check "the unavailable reason names --praxis, CONDITOR_PRAXIS and why the workstation shim was not used" (notConfigured.Reason.Contains "--praxis" && notConfigured.Reason.Contains "CONDITOR_PRAXIS" && notConfigured.Reason.Contains "~/.local/bin/praxis")

    let falsePraxis = Engine.register (context emptyHome mirror (Some "/bin/false")) probe "register" profile.Components.Head rid "strata-shim"
    check "a configured Praxis that produces no installation result is misconfigured, not unavailable" (falsePraxis.Outcome = "misconfigured")
    check "the misconfigured reason names the executable and its exit code" (falsePraxis.Reason.Contains "/bin/false" && falsePraxis.Reason.Contains "exited 1")

    let missing = Path.Combine(emptyHome, "no-such-praxis")
    let absent = Engine.register (context emptyHome mirror (Some missing)) probe "register" profile.Components.Head rid "strata-shim"
    check "a configured Praxis that cannot be run is misconfigured" (absent.Outcome = "misconfigured" && absent.Reason.Contains "could not be run")

    let interpreted outcome stdout =
        Engine.interpretRegistration "praxis" { ExitCode = 0; StandardOutput = stdout; StandardError = "" }

    let praxisUnavailable = interpreted "unavailable" """{"outcome":"unavailable","message":"no Project Administration provider is configured"}"""
    check "Praxis's own unavailable outcome passes through with its message" (praxisUnavailable = { Outcome = "unavailable"; Reason = "no Project Administration provider is configured" })
    let refused = interpreted "refused" """{"outcome":"refused","code":"target-unknown","message":"no such target"}"""
    check "a refusal carries its code and message" (refused = { Outcome = "refused"; Reason = "target-unknown: no such target" })
    check "a recorded outcome carries its event" ((interpreted "recorded" recorded).Reason = "event evt-1")
    check "JSON without an outcome is misconfigured" ((interpreted "" """{"schema":"x"}""").Outcome = "misconfigured")

    // Install with no Praxis configured: Praxis registers itself through its
    // own new, ledger-owned shim; strata (installed before it) cannot.
    let installed = install (context home mirror None) profile
    check "an install before any Praxis exists records unavailable with its reason" (registrationOf "strata" installed.Registrations |> Option.exists (fun r -> r.Outcome = "unavailable" && r.Reason <> ""))
    check "the Conditor-installed Praxis registers through its own ledger-owned shim" (registrationOf "praxis" installed.Registrations |> Option.exists (fun r -> r.Outcome = "recorded"))
    check "later components register through the ledger-owned shim by default" (registrationOf "ordo" installed.Registrations |> Option.exists (fun r -> r.Outcome = "recorded"))
    check "the ledger keeps the reason for every registration" ((File.ReadAllText(Path.Combine(home, ".conditor", "workstation", "ledger.jsonl"))).Contains "\"detail\":\"no Praxis configured")

    // Fix 3: the ledger-owned default, and what never qualifies.
    let ctx = context home mirror None
    check "the default registrar is the ledger-owned, verified ~/.local/bin/praxis" (Engine.praxisSource ctx probe = Engine.PraxisSource.LedgerOwned(Path.Combine(home, ".local", "bin", "praxis"), "9.9.0"))
    check "an explicit --praxis or CONDITOR_PRAXIS wins over the ledger-owned shim" (Engine.praxisSource (context home mirror (Some "/opt/praxis")) probe = Engine.PraxisSource.Explicit "/opt/praxis")

    let handHome = temp "hand"
    let handShim = Path.Combine(handHome, ".local", "bin", "praxis")
    Directory.CreateDirectory(Path.GetDirectoryName handShim |> nonNull) |> ignore
    executable handShim (praxisScript "9.9.0" (Path.Combine(handHome, "log")) recorded)
    check "a praxis Conditor did not install is never the default" (match Engine.praxisSource (context handHome mirror None) probe with Engine.PraxisSource.NotConfigured why -> why.Contains "not in the workstation ledger" | _ -> false)

    let shim = Path.Combine(home, ".local", "bin", "praxis")
    let original = File.ReadAllText shim
    File.WriteAllText(shim, original.Replace("/praxis/9.9.0/praxis", "/elsewhere/praxis"))
    check "a ledger-owned shim that was rewritten is never the default" (match Engine.praxisSource ctx probe with Engine.PraxisSource.NotConfigured why -> why.Contains "not the shim Conditor writes" || why.Contains "outside" | _ -> false)
    File.WriteAllText(shim, original)

    let target = Path.Combine(home, ".local", "share", "echelon", "praxis", "9.9.0", "praxis")
    let targetText = File.ReadAllText target
    File.WriteAllText(target, "#!/bin/sh\necho \"praxis 1.0.0\"\n")
    check "a ledger-owned shim that no longer reports its version is never the default" (match Engine.praxisSource ctx probe with Engine.PraxisSource.NotConfigured why -> why.Contains "did not report 9.9.0" | _ -> false)
    File.WriteAllText(target, targetText)
    check "the restored shim verifies again" (Engine.praxisSource ctx probe = Engine.PraxisSource.LedgerOwned(shim, "9.9.0"))

    // Fix 4: backfill is planned and authorized; it only proposes installs
    // Conditor made whose registration never succeeded.
    let plan = Engine.backfillPlan ctx probe profile rid
    check "the backfill plan proposes exactly the unregistered install" (plan.Items |> List.map (fun i -> i.Component.Id) = [ "strata" ])
    check "the backfill plan carries the last outcome it is repairing" (plan.Items |> List.exists (fun i -> i.LastOutcome = Some "unavailable"))
    check "the backfill plan skips components already registered, with the reason" (plan.Skipped |> List.exists (fun (id, why) -> id = "praxis" && why.Contains "already registered (recorded)"))
    check "the backfill plan has no refusals with a verified registrar" plan.Refusals.IsEmpty
    check "the backfill plan digest is deterministic" (plan.Digest = (Engine.backfillPlan ctx probe profile rid).Digest)
    check "the backfill plan is a dry run" (not ((calls log).Contains "--system strata"))
    check "backfill refuses an authorization that is not the plan" (Result.isError (Engine.backfillApply ctx probe plan rid "sha256:other"))
    check "a different registrar is a different authorization" (plan.Digest <> (Engine.backfillPlan (context home mirror (Some "/bin/false")) probe profile rid).Digest)

    match Engine.backfillApply ctx probe plan rid plan.Digest with
    | Error e -> check $"backfill applies the authorized plan ({e})" false
    | Ok results ->
        check "backfill registers the unregistered install" (results = [ "strata", { Outcome = "recorded"; Reason = "event evt-1" } ])
        check "backfill cites the original install receipt as evidence" ((calls log).Contains "--system strata" && (calls log).Contains "receipt=conditor-workstation-ledger#strata-shim")
        check "backfill records its outcome in the ledger" ((File.ReadAllText(Path.Combine(home, ".conditor", "workstation", "ledger.jsonl"))).Contains "\"trigger\":\"backfill\"")

    let after = Engine.backfillPlan ctx probe profile rid
    check "after a backfill there is nothing left to register" after.Items.IsEmpty

    // Without any registrar, a backfill with work to do is refused.
    let home2 = temp "home"
    let mirror2 = temp "mirror"
    let strata2 = bundle mirror2 "strata" (tool "strata" "1.0.0")
    let strataProfile = profileFile mirror2 [ "strata", "1.0.0", fst strata2, snd strata2 ] |> resolve
    let strataMirror = mirror2
    let log2 = Path.Combine(mirror2, "praxis-calls.log")
    install (context home2 strataMirror None) strataProfile |> ignore
    let refusedPlan = Engine.backfillPlan (context home2 strataMirror None) probe strataProfile rid
    check "backfill without any registrar is refused as unavailable" (refusedPlan.Refusals |> List.exists (fun r -> r.StartsWith "unavailable:"))
    check "a refused backfill plan cannot be applied" (Result.isError (Engine.backfillApply (context home2 strataMirror None) probe refusedPlan rid refusedPlan.Digest))
    check "a refused backfill registers nothing" (not ((calls log2).Contains "--system"))

    // A version Conditor did not install is not backfilled.
    let other = { profile with Components = profile.Components |> List.map (fun c -> if c.Id = "ordo" then { c with Version = "3.0.0" } else c) }
    let otherPlan = Engine.backfillPlan ctx probe other rid
    check "a version the shim does not report now is skipped, not registered" (otherPlan.Skipped |> List.exists (fun (id, why) -> id = "ordo" && why.Contains "does not report ordo 3.0.0"))
