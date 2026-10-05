namespace Conditor.Core

open System
open System.Text.Json

/// Registry is the single version authority when a manifest declares
/// `registryAuthority` (docs/decisions/0001-registry-version-authority.md).
///
/// Every requested component — whether or not Conditor also embeds a
/// descriptor for it — must be selected by the declared resolved release set,
/// and is bound to exactly the version that set selects. The decision is a
/// pure function of the authority and the request so it can be tested over the
/// whole embedded catalog and reused by every planner-like entry point.
module RegistryAuthorityBinding =

    /// The exact Registry decision a refusal or binding refers to.
    type AuthoritySource =
        { Path: string
          Sha256: string
          Profile: string }

    /// One component selection from the resolved release set, regardless of
    /// its environment role (host-tool, repository-lifecycle, project-binding).
    type AuthoritySelection =
        { Id: string
          Version: string
          Role: string }

    type RegistryAuthoritySet =
        { Source: AuthoritySource
          Selections: Map<string, AuthoritySelection> }

    type AuthorityViolation =
        /// conditor.json pins a version the authority did not select.
        | VersionConflict of
            componentId: string *
            requestedVersion: string *
            authorityVersion: string *
            source: AuthoritySource
        /// The component is requested but the authority selects no release for it.
        /// Conditor does not fall back to its embedded catalog once an authority
        /// is declared; there is no escape hatch for this in the current design.
        | AbsentFromAuthority of componentId: string * requestedVersion: string option * source: AuthoritySource

    type AuthorityDecision =
        /// No registryAuthority is declared; the embedded catalog governs.
        | NoAuthorityDeclared of ComponentRequest
        /// The request, with its version set to the authority's selection.
        | BoundToAuthority of ComponentRequest * AuthoritySelection
        | Refused of AuthorityViolation

    type BindingOutcome =
        { Requests: ComponentRequest list
          Violations: AuthorityViolation list }

    let describeSource (source: AuthoritySource) =
        $"resolved-release-set '{source.Path}' (sha256:{source.Sha256}; profile {source.Profile})"

    let describe (violation: AuthorityViolation) =
        match violation with
        | VersionConflict(id, requested, selected, source) ->
            $"Registry authority conflict for component '{id}': conditor.json requests version '{requested}', but the Registry authority {describeSource source} selects version '{selected}'. Registry is the single version authority when declared; replace the resolved-set authority instead of silently substituting a version."
        | AbsentFromAuthority(id, requested, source) ->
            let version =
                requested
                |> Option.map (fun value -> $" version '{value}'")
                |> Option.defaultValue String.Empty

            $"Component '{id}'{version} is not selected by the declared Registry authority {describeSource source}. When registryAuthority is declared, Conditor will not fall back to its embedded catalog; select the component in the Registry profile or remove it from conditor.json."

    /// The binding rule. Pure: identical inputs always yield the same decision.
    let decide (authority: RegistryAuthoritySet option) (request: ComponentRequest) : AuthorityDecision =
        match authority with
        | None -> NoAuthorityDeclared request
        | Some set ->
            match set.Selections |> Map.tryFind request.Id, request.Version with
            | None, requested -> Refused(AbsentFromAuthority(request.Id, requested, set.Source))
            | Some selection, Some requested when requested <> selection.Version ->
                Refused(VersionConflict(request.Id, requested, selection.Version, set.Source))
            | Some selection, _ ->
                BoundToAuthority({ request with Version = Some selection.Version }, selection)

    /// Applies the rule to a whole manifest: bound/ungoverned requests are
    /// kept (in order, with authority versions), refusals are collected.
    let bindAll (authority: RegistryAuthoritySet option) (requests: ComponentRequest list) : BindingOutcome =
        let decisions = requests |> List.map (decide authority)

        { Requests =
            decisions
            |> List.choose (function
                | NoAuthorityDeclared request
                | BoundToAuthority(request, _) -> Some request
                | Refused _ -> None)
          Violations =
            decisions
            |> List.choose (function
                | Refused violation -> Some violation
                | _ -> None) }

    let private tryProperty (name: string) (element: JsonElement) =
        let mutable value = Unchecked.defaultof<JsonElement>

        if element.ValueKind = JsonValueKind.Object && element.TryGetProperty(name, &value) then
            Some value
        else
            None

    let private str name element =
        match tryProperty name element with
        | Some value when value.ValueKind = JsonValueKind.String -> value.GetString() |> Option.ofObj
        | _ -> None

    let private parseSelection (element: JsonElement) : Result<AuthoritySelection, string> =
        match str "systemId" element, str "version" element, str "role" element with
        | Some id, Some version, Some role -> Ok { Id = id; Version = version; Role = role }
        | id, _, _ ->
            let label = id |> Option.defaultValue "<unknown>"
            Error $"resolved release set component '{label}' is missing systemId, version or role"

    let private addSelection (selections: Map<string, AuthoritySelection>) (selection: AuthoritySelection) =
        if selections.ContainsKey selection.Id then
            Error $"resolved release set selects '{selection.Id}' more than once"
        else
            Ok(selections |> Map.add selection.Id selection)

    /// Reads every component selection (all roles) from resolved-release-set
    /// bytes. Callers must verify the bytes' digest first (ResolvedReleaseSets.parseVerified).
    let parseSelections (path: string) (sha256: string) (bytes: byte array) : Result<RegistryAuthoritySet, string> =
        try
            use document = JsonDocument.Parse bytes
            let root = document.RootElement

            let profile =
                tryProperty "profile" root
                |> Option.map (fun value ->
                    let id = str "id" value |> Option.defaultValue "<unknown>"
                    let version = str "version" value |> Option.defaultValue "<unknown>"
                    $"{id}@{version}")
                |> Option.defaultValue "<unknown>"

            let components =
                match tryProperty "components" root with
                | Some value when value.ValueKind = JsonValueKind.Array -> value.EnumerateArray() |> Seq.toList
                | _ -> []

            components
            |> List.fold
                (fun state element -> state |> Result.bind (fun selections -> parseSelection element |> Result.bind (addSelection selections)))
                (Ok Map.empty)
            |> Result.map (fun selections ->
                { Source =
                    { Path = path
                      Sha256 = sha256
                      Profile = profile }
                  Selections = selections })
        with :? JsonException as ex ->
            Error $"resolved release set is not valid JSON: {ex.Message}"
