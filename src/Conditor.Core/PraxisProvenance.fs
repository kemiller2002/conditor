namespace Conditor.Core

open System
open System.IO
open System.Text.Json
open System.Text.RegularExpressions

/// Post-install readiness of the Praxis agent identity/provenance contract
/// (Praxis RQ-ROS-2026-A007, A012, A016; DF-ROS-2026-A037) in a target
/// repository. Praxis owns the policy, guidance, and documentation; Conditor
/// only checks that the installed Praxis release delivered them (CON-128..CON-131).
module PraxisProvenance =
    /// Readiness of the provenance contract in an established repository.
    type ReadinessStatus =
        /// ros.json enforces the provenance policy and the agent guidance and documentation are installed.
        | Enabled
        /// The installed Praxis version predates provenance support; nothing is expected yet.
        | NotSupportedByInstalledVersion
        /// The installed Praxis version provides provenance but the repository lacks part of it.
        | MissingWhenExpected

    /// The `ros.json` `provenance` policy as observed in the target.
    type PolicyState =
        | PolicyAbsent
        | PolicyInvalid of reason: string
        | PolicyNotEnforced
        | PolicyEnforced of requiredFrom: string

    /// Raw observations of the target repository. Built by `observe`; pure checks consume it.
    type Observation =
        { RosJson: string option
          AgentsMarkdown: string option
          ProvenanceDocumentExists: bool }

    type Readiness =
        { Status: ReadinessStatus
          InstalledVersion: string option
          MinimumVersion: string option
          Policy: PolicyState
          AgentGuidance: bool
          ProvenanceDocument: bool
          Missing: string list
          Detail: string }

    [<Literal>]
    let CapabilityName = "provenance"

    [<Literal>]
    let AgentGuidanceHeading = "Agent Identity and Provenance"

    [<Literal>]
    let ProvenanceDocumentPath = "docs/agent-provenance.md"

    let statusText =
        function
        | Enabled -> "enabled"
        | NotSupportedByInstalledVersion -> "not-supported-by-installed-version"
        | MissingWhenExpected -> "missing-when-expected"

    // --- Capability table (data-driven; components/praxis.capabilities.json) ---

    let private tryProperty (name: string) (element: JsonElement) =
        let mutable value = Unchecked.defaultof<JsonElement>
        if element.TryGetProperty(name, &value) then Some value else None

    let private versionPattern = Regex("^[0-9]+(\\.[0-9]+)*\\z", RegexOptions.CultureInvariant)

    /// Parses a capability table and returns each capability's minimum version
    /// (`None` when no qualified release provides it yet).
    let parseCapabilities (text: string) : Result<Map<string, string option>, string list> =
        try
            use document = JsonDocument.Parse text
            let root = document.RootElement

            match tryProperty "schemaVersion" root, tryProperty "capabilities" root with
            | Some version, _ when version.ValueKind <> JsonValueKind.Number || version.GetInt32() <> 1 ->
                Error [ "Capability table schemaVersion must be 1." ]
            | None, _ -> Error [ "Capability table schemaVersion must be 1." ]
            | _, Some capabilities when capabilities.ValueKind = JsonValueKind.Object ->
                let entries =
                    capabilities.EnumerateObject()
                    |> Seq.map (fun capability ->
                        match tryProperty "minimumVersion" capability.Value with
                        | Some value when value.ValueKind = JsonValueKind.Null -> Ok(capability.Name, None)
                        | Some value when value.ValueKind = JsonValueKind.String ->
                            match value.GetString() with
                            | null -> Error $"Capability '{capability.Name}' minimumVersion must not be null text."
                            | text when versionPattern.IsMatch text -> Ok(capability.Name, Some text)
                            | _ ->
                                Error
                                    $"Capability '{capability.Name}' minimumVersion must be null or a numeric dotted version."
                        | _ ->
                            Error
                                $"Capability '{capability.Name}' minimumVersion must be null or a numeric dotted version.")
                    |> List.ofSeq

                let errors =
                    entries
                    |> List.choose (function
                        | Error error -> Some error
                        | Ok _ -> None)

                if errors.IsEmpty then
                    entries
                    |> List.choose (function
                        | Ok entry -> Some entry
                        | Error _ -> None)
                    |> Map.ofList
                    |> Ok
                else
                    Error errors
            | _ -> Error [ "Capability table must contain a 'capabilities' object." ]
        with :? JsonException as ex ->
            Error [ $"Capability table is invalid JSON: {ex.Message}" ]

    let private capabilityResource = "Conditor.Capabilities.praxis.json"

    /// The embedded Praxis capability table.
    let praxisCapabilities: Lazy<Result<Map<string, string option>, string list>> =
        lazy
            (let assembly = typeof<ComponentDefinition>.Assembly

             match assembly.GetManifestResourceStream capabilityResource |> Option.ofObj with
             | None -> Error [ $"Embedded capability table is missing: {capabilityResource}" ]
             | Some stream ->
                 use value = stream
                 use reader = new StreamReader(value)
                 parseCapabilities (reader.ReadToEnd()))

    /// The first Praxis version providing provenance, or `None` while no qualified release does.
    let provenanceMinimumVersion () =
        match praxisCapabilities.Force() with
        | Ok table -> table |> Map.tryFind CapabilityName |> Option.flatten
        | Error _ -> None

    let private parseVersion (text: string) =
        if versionPattern.IsMatch text then
            Some(text.Split('.') |> Array.map int |> List.ofArray)
        else
            None

    /// True when `installed` is at or above `minimum`. A missing threshold or an
    /// unparseable (e.g. prerelease) version never counts as supporting.
    let supports (minimum: string option) (installed: string option) =
        match minimum |> Option.bind parseVersion, installed |> Option.bind parseVersion with
        | Some required, Some actual ->
            let width = max required.Length actual.Length
            let pad (parts: int list) = parts @ List.replicate (width - parts.Length) 0
            compare (pad actual) (pad required) >= 0
        | _ -> false

    // --- Pure evaluation of the target's Praxis-owned artifacts ---

    /// Reads the `provenance` policy from ros.json text. Absent file and absent
    /// policy are equivalent: Praxis validates such a repository as before (A007).
    let evaluatePolicy (rosJson: string option) =
        match rosJson with
        | None -> PolicyAbsent
        | Some text ->
            try
                use document = JsonDocument.Parse text
                let root = document.RootElement

                if root.ValueKind <> JsonValueKind.Object then
                    PolicyInvalid "ros.json is not a JSON object"
                else
                    match tryProperty "provenance" root with
                    | None -> PolicyAbsent
                    | Some policy when policy.ValueKind <> JsonValueKind.Object ->
                        PolicyInvalid "ros.json 'provenance' is not an object"
                    | Some policy ->
                        let enforced =
                            match tryProperty "enforce" policy with
                            | Some value -> value.ValueKind = JsonValueKind.True
                            | None -> false

                        let requiredFrom =
                            match tryProperty "requiredFrom" policy with
                            | Some value when value.ValueKind = JsonValueKind.String ->
                                value.GetString() |> Option.ofObj |> Option.filter (String.IsNullOrWhiteSpace >> not)
                            | _ -> None

                        match enforced, requiredFrom with
                        | true, Some date -> PolicyEnforced date
                        | true, None -> PolicyInvalid "ros.json 'provenance.enforce' is true but 'requiredFrom' is missing"
                        | false, _ -> PolicyNotEnforced
            with :? JsonException as ex ->
                PolicyInvalid $"ros.json is invalid JSON: {ex.Message}"

    /// Stable marker a Praxis release may place in AGENTS.md to identify the section.
    [<Literal>]
    let AgentGuidanceMarker = "praxis:agent-identity-provenance"

    let private headingPattern =
        Regex("^#{1,6}[ \t]+(?<title>[^\n]*)$", RegexOptions.Multiline ||| RegexOptions.CultureInvariant)

    let private contains (text: string) (phrase: string) =
        text.Contains(phrase, StringComparison.OrdinalIgnoreCase)

    /// True when AGENTS.md carries the Praxis agent identity/provenance guidance.
    /// Detection is deliberately tolerant of wording and heading changes across
    /// Praxis releases (it gates verification once a threshold is set): any of
    /// a stable marker, a heading naming both identity and provenance, or the
    /// key phrases (a reference to the provenance document together with the
    /// identity declaration variable `ROS_ACTOR_KIND`).
    let hasAgentGuidance (agentsMarkdown: string option) =
        agentsMarkdown
        |> Option.exists (fun raw ->
            let text = raw.Replace("\r\n", "\n")

            let heading =
                headingPattern.Matches text
                |> Seq.exists (fun found ->
                    let title = found.Groups["title"].Value
                    contains title "identity" && contains title "provenance")

            contains text AgentGuidanceMarker
            || heading
            || (contains text "agent-provenance.md" && text.Contains "ROS_ACTOR_KIND"))

    /// Pure readiness decision. The installed repository content wins over the
    /// version table: a repository that already carries the complete contract is
    /// Enabled; a version the table says supports provenance must carry it.
    let assess (minimumVersion: string option) (installedVersion: string option) (observation: Observation) =
        let policy = evaluatePolicy observation.RosJson
        let guidance = hasAgentGuidance observation.AgentsMarkdown
        let documentation = observation.ProvenanceDocumentExists

        let missing =
            [ match policy with
              | PolicyEnforced _ -> ()
              | PolicyAbsent -> "ros.json 'provenance' policy (enforce: true, requiredFrom)"
              | PolicyNotEnforced -> "ros.json 'provenance.enforce' is not true"
              | PolicyInvalid reason -> reason
              if not guidance then
                  $"AGENTS.md '{AgentGuidanceHeading}' guidance"
              if not documentation then
                  ProvenanceDocumentPath ]

        let installedText = installedVersion |> Option.defaultValue "unknown"
        let supported = supports minimumVersion installedVersion

        let status, detail =
            match missing, supported with
            | [], _ ->
                let requiredFrom =
                    match policy with
                    | PolicyEnforced date -> date
                    | _ -> "unknown"

                Enabled,
                $"Praxis provenance enabled (installed Praxis {installedText}; policy enforced from {requiredFrom}; agent guidance and {ProvenanceDocumentPath} present)."
            | gaps, true ->
                let listed = String.Join("; ", gaps)
                let minimum = minimumVersion |> Option.defaultValue "?"

                MissingWhenExpected,
                $"Praxis {installedText} provides provenance (from {minimum}) but the repository is missing: {listed}. Run the Praxis lifecycle upgrade/doctor; Conditor does not write Praxis-owned files."
            | _, false ->
                let threshold =
                    match minimumVersion with
                    | None -> "no qualified Praxis release provides provenance yet"
                    | Some version -> $"provenance requires Praxis {version} or later"

                NotSupportedByInstalledVersion,
                $"Praxis provenance is not available: installed Praxis {installedText} predates it ({threshold}). New work in this repository is not provenance-validated; qualify and upgrade to a Praxis release with provenance when one is published."

        { Status = status
          InstalledVersion = installedVersion
          MinimumVersion = minimumVersion
          Policy = policy
          AgentGuidance = guidance
          ProvenanceDocument = documentation
          Missing = (if status = Enabled then [] else missing)
          Detail = detail }

    // --- Effectful edges ---

    /// Reads the three Praxis-owned artifacts from the target repository.
    let observe (target: string) =
        let read relative =
            let path = Path.Combine(target, relative)

            try
                if File.Exists path then Some(File.ReadAllText path) else None
            with _ ->
                None

        { RosJson = read "ros.json"
          AgentsMarkdown = read "AGENTS.md"
          ProvenanceDocumentExists = File.Exists(Path.Combine(target, ProvenanceDocumentPath)) }

    /// The resolved Praxis version of a plan, if the plan includes Praxis.
    let praxisVersion (components: ResolvedComponent list) =
        components
        |> List.tryFind (fun resolved -> resolved.Id = "praxis")
        |> Option.map _.Version

    /// Readiness of the target for the plan's resolved Praxis version; `None`
    /// when the plan does not include Praxis.
    let inspect (target: string) (components: ResolvedComponent list) =
        praxisVersion components
        |> Option.map (fun version -> assess (provenanceMinimumVersion ()) (Some version) (observe target))
