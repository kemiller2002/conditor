namespace Conditor.Core

open System
open System.IO
open System.Text.Json

/// Praxis agent-provenance gate (CON-068, CON-069). Praxis owns the provenance
/// policy and the agent guidance; Conditor only reports whether the selected
/// Praxis version provides them and, when it does, verifies that the installed
/// repository actually received them. Conditor never writes either file.
module PraxisProvenance =
    [<Literal>]
    let CapabilityName = "provenance"

    /// Heading Praxis installs into AGENTS.md for provenance-capable versions.
    [<Literal>]
    let AgentsSection = "Agent Identity and Provenance"

    type Gate =
        /// The selected version is at or after the first provenance-capable version.
        | Capable of since: string
        /// The selected version predates the first provenance-capable version.
        | Predates of since: string * status: CapabilityStatus
        /// The selected or declared version is not a comparable semantic version.
        | Incomparable of since: string
        /// The component descriptor declares no provenance capability.
        | NotDeclared

    let gate (capabilities: ComponentCapability list) (version: string) =
        match capabilities |> List.tryFind (fun capability -> capability.Name = CapabilityName) with
        | None -> NotDeclared
        | Some capability ->
            match SemanticVersion.isAtLeast capability.Since version with
            | Some true -> Capable capability.Since
            | Some false -> Predates(capability.Since, capability.Status)
            | None -> Incomparable capability.Since

    let gateFor (componentId: string) (version: string) =
        Registry.descriptors
        |> List.tryFind (fun descriptor -> descriptor.Definition.Id = componentId)
        |> Option.map (fun descriptor -> gate descriptor.Capabilities version)
        |> Option.defaultValue NotDeclared

    /// Planning diagnostic (a warning, never a failure) for a Praxis version that
    /// cannot install the provenance policy, so a greenfield install is not
    /// silently un-governed.
    let diagnostic (version: string) (selected: Gate) =
        let consequence =
            $"The initialized repository will not receive the Praxis provenance policy (ros.json \"provenance\" block) or the AGENTS.md \"{AgentsSection}\" guidance, so agent contributions will not be attributed."

        match selected with
        | Capable _
        | NotDeclared -> None
        | Predates(since, CapabilityUnreleased) ->
            Some
                $"WARNING Praxis {version} predates agent provenance; the first provenance-capable Praxis ({since}) is not yet released or qualified by Conditor. {consequence} Upgrade Praxis once Conditor qualifies {since} or later."
        | Predates(since, CapabilityReleased) ->
            Some
                $"WARNING Praxis {version} predates agent provenance (available from {since}). {consequence} Select a qualified Praxis version at or after {since}."
        | Incomparable since ->
            Some
                $"WARNING Unable to compare Praxis version '{version}' with the first provenance-capable version '{since}'; provenance installation cannot be confirmed."

    let planDiagnostics (plan: InstallationPlan) =
        plan.Components
        |> List.filter (fun resolved -> resolved.Id = "praxis")
        |> List.choose (fun resolved -> diagnostic resolved.Version (gateFor resolved.Id resolved.Version))

    let private tryProperty (name: string) (element: JsonElement) =
        let mutable value = Unchecked.defaultof<JsonElement>
        if element.TryGetProperty(name, &value) then Some value else None

    let private rosPolicyErrors (rosJson: string option) =
        match rosJson with
        | None -> [ "Praxis ros.json is missing; the provenance policy cannot be verified." ]
        | Some text ->
            try
                use document = JsonDocument.Parse text

                match tryProperty "provenance" document.RootElement with
                | Some policy when policy.ValueKind = JsonValueKind.Object ->
                    match tryProperty "enforce" policy with
                    | Some enforce when enforce.ValueKind = JsonValueKind.True -> []
                    | _ -> [ "Praxis ros.json \"provenance\" policy does not set \"enforce\": true." ]
                | _ -> [ "Praxis ros.json has no \"provenance\" policy object." ]
            with ex ->
                [ $"Praxis ros.json is not valid JSON: {ex.Message}" ]

    let private agentGuidanceErrors (agents: string option) =
        match agents with
        | None -> [ "AGENTS.md is missing; the Praxis agent provenance guidance cannot be verified." ]
        | Some text when text.Contains(AgentsSection, StringComparison.Ordinal) -> []
        | Some _ -> [ $"AGENTS.md does not contain the Praxis \"{AgentsSection}\" section." ]

    /// Pure check over the contents of the target's ros.json and AGENTS.md.
    let checkContents (rosJson: string option) (agents: string option) =
        rosPolicyErrors rosJson @ agentGuidanceErrors agents

    let private readIfPresent path =
        if File.Exists path then Some(File.ReadAllText path) else None

    /// Verifies that Praxis installed its provenance policy and agent guidance
    /// into the target repository. Returns the list of failures (empty = verified).
    let verifyTarget (target: string) =
        checkContents
            (readIfPresent (Path.Combine(target, "ros.json")))
            (readIfPresent (Path.Combine(target, "AGENTS.md")))
