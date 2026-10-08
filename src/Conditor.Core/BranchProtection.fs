namespace Conditor.Core

open System
open System.Text.Json
open System.Text.Json.Nodes

/// What a protected branch requires, as a scaffold declares it in
/// `.github/branch-protection.json` and the repository-creation step applies it.
type BranchProtectionPolicy =
    { Branch: string
      /// Check-run names that must pass before a pull request merges.
      RequiredChecks: string list
      /// Require the pull request branch to be up to date with the base.
      StrictStatusChecks: bool
      /// Apply the rules to administrators too, so no one merges around CI.
      EnforceAdmins: bool
      RequirePullRequest: bool
      RequiredApprovingReviewCount: int
      AllowForcePushes: bool
      AllowDeletions: bool }

/// The branch-protection declaration: a pure parser that fails closed, a
/// canonical renderer, and the GitHub REST payload that applies it.
module BranchProtection =
    [<Literal>]
    let Schema = "conditor.branch-protection/v1"

    /// Where a scaffold declares the protection for its repository.
    [<Literal>]
    let RelativePath = ".github/branch-protection.json"

    let private fields =
        set
            [ "schema"; "branch"; "requiredStatusChecks"; "strictStatusChecks"; "enforceAdmins"
              "requirePullRequest"; "requiredApprovingReviewCount"; "allowForcePushes"; "allowDeletions" ]

    let private property (root: JsonElement) (name: string) =
        let mutable value = Unchecked.defaultof<JsonElement>
        if root.TryGetProperty(name, &value) then Some value else None

    let private boolean root name =
        match property root name with
        | Some value when value.ValueKind = JsonValueKind.True -> Ok true
        | Some value when value.ValueKind = JsonValueKind.False -> Ok false
        | _ -> Error [ $"'{name}' must be true or false." ]

    let private nonBlank (element: JsonElement) =
        if element.ValueKind = JsonValueKind.String then
            element.GetString() |> Option.ofObj |> Option.filter (String.IsNullOrWhiteSpace >> not)
        else
            None

    let private text root name =
        match property root name |> Option.bind nonBlank with
        | Some value -> Ok value
        | None -> Error [ $"'{name}' must be a non-empty string." ]

    let private checks root =
        match property root "requiredStatusChecks" with
        | Some value when value.ValueKind = JsonValueKind.Array ->
            let items = value.EnumerateArray() |> List.ofSeq

            let names = items |> List.choose nonBlank

            if names.Length <> items.Length || names.IsEmpty then
                Error [ "'requiredStatusChecks' must be a non-empty array of non-empty check names." ]
            elif (List.distinct names).Length <> names.Length then
                Error [ "'requiredStatusChecks' must not repeat a check." ]
            else
                Ok names
        | _ -> Error [ "'requiredStatusChecks' must be a non-empty array of non-empty check names." ]

    let private reviewCount root =
        match property root "requiredApprovingReviewCount" with
        | Some value when value.ValueKind = JsonValueKind.Number ->
            match value.TryGetInt32() with
            | true, count when count >= 0 && count <= 6 -> Ok count
            | _ -> Error [ "'requiredApprovingReviewCount' must be an integer from 0 to 6." ]
        | _ -> Error [ "'requiredApprovingReviewCount' must be an integer from 0 to 6." ]

    let private errorsOf result =
        match result with
        | Ok _ -> []
        | Error errors -> errors

    /// Parses a declaration. Any unknown field, wrong type or missing value is
    /// an error: protection is never applied from a guess.
    let parse (json: string) : Result<BranchProtectionPolicy, string list> =
        try
            use document = JsonDocument.Parse json
            let root = document.RootElement

            if root.ValueKind <> JsonValueKind.Object then
                Error [ "A branch-protection declaration must be a JSON object." ]
            else
                let unknown =
                    root.EnumerateObject()
                    |> Seq.map _.Name
                    |> Seq.filter (fields.Contains >> not)
                    |> Seq.map (fun name -> $"Unknown branch-protection field '{name}'.")
                    |> List.ofSeq

                let schema =
                    match text root "schema" with
                    | Ok value when value = Schema -> Ok value
                    | Ok value -> Error [ $"Unsupported branch-protection schema '{value}'; expected '{Schema}'." ]
                    | Error errors -> Error errors

                let branch = text root "branch"
                let required = checks root
                let strict = boolean root "strictStatusChecks"
                let admins = boolean root "enforceAdmins"
                let pullRequest = boolean root "requirePullRequest"
                let reviews = reviewCount root
                let forcePushes = boolean root "allowForcePushes"
                let deletions = boolean root "allowDeletions"

                match schema, branch, required, strict, admins, pullRequest, reviews, forcePushes, deletions with
                | Ok _, Ok branch, Ok required, Ok strict, Ok admins, Ok pullRequest, Ok reviews, Ok forcePushes, Ok deletions when unknown.IsEmpty ->
                    Ok
                        { Branch = branch
                          RequiredChecks = required
                          StrictStatusChecks = strict
                          EnforceAdmins = admins
                          RequirePullRequest = pullRequest
                          RequiredApprovingReviewCount = reviews
                          AllowForcePushes = forcePushes
                          AllowDeletions = deletions }
                | _ ->
                    Error(
                        unknown
                        @ errorsOf schema
                        @ errorsOf branch
                        @ errorsOf required
                        @ errorsOf strict
                        @ errorsOf admins
                        @ errorsOf pullRequest
                        @ errorsOf reviews
                        @ errorsOf forcePushes
                        @ errorsOf deletions
                    )
        with :? JsonException as ex ->
            Error [ $"The branch-protection declaration is not valid JSON: {ex.Message}" ]

    let private stringArray (values: string list) =
        let array = JsonArray()
        values |> List.iter (fun value -> array.Add(JsonValue.Create value))
        array

    let private serialize (node: JsonNode) =
        node.ToJsonString(JsonSerializerOptions(WriteIndented = true, IndentSize = 2)) + "\n"

    /// The canonical declaration text for a policy.
    let render (policy: BranchProtectionPolicy) =
        let root = JsonObject()
        root["schema"] <- JsonValue.Create Schema
        root["branch"] <- JsonValue.Create policy.Branch
        root["requiredStatusChecks"] <- stringArray policy.RequiredChecks
        root["strictStatusChecks"] <- JsonValue.Create policy.StrictStatusChecks
        root["enforceAdmins"] <- JsonValue.Create policy.EnforceAdmins
        root["requirePullRequest"] <- JsonValue.Create policy.RequirePullRequest
        root["requiredApprovingReviewCount"] <- JsonValue.Create policy.RequiredApprovingReviewCount
        root["allowForcePushes"] <- JsonValue.Create policy.AllowForcePushes
        root["allowDeletions"] <- JsonValue.Create policy.AllowDeletions
        serialize root

    /// The body of `PUT /repos/{owner}/{repo}/branches/{branch}/protection`.
    let toGitHubRequest (policy: BranchProtectionPolicy) =
        let root = JsonObject()
        let statusChecks = JsonObject()
        statusChecks["strict"] <- JsonValue.Create policy.StrictStatusChecks

        let checks = JsonArray()

        for check in policy.RequiredChecks do
            let entry = JsonObject()
            entry["context"] <- JsonValue.Create check
            checks.Add entry

        statusChecks["checks"] <- checks

        root["required_status_checks"] <- statusChecks
        root["enforce_admins"] <- JsonValue.Create policy.EnforceAdmins

        let reviews: JsonNode | null =
            if policy.RequirePullRequest then
                let reviews = JsonObject()
                reviews["required_approving_review_count"] <- JsonValue.Create policy.RequiredApprovingReviewCount
                reviews["dismiss_stale_reviews"] <- JsonValue.Create false
                reviews["require_code_owner_reviews"] <- JsonValue.Create false
                reviews
            else
                null

        // GitHub requires both keys; null means "no review rule" and
        // "no push restriction".
        root["required_pull_request_reviews"] <- reviews
        root["restrictions"] <- (null: JsonNode | null)
        root["allow_force_pushes"] <- JsonValue.Create policy.AllowForcePushes
        root["allow_deletions"] <- JsonValue.Create policy.AllowDeletions
        serialize root
