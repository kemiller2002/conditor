module BranchProtectionTests

open System.Text.Json
open Conditor.Core

let private policy = Scaffolding.webBranchProtection

let private isError result =
    match result with
    | Error _ -> true
    | Ok _ -> false

let private mutate (edit: string -> string) =
    BranchProtection.render policy |> edit |> BranchProtection.parse

let run (check: string -> bool -> unit) =
    check "a rendered declaration parses back to the same policy"
        (BranchProtection.parse (BranchProtection.render policy) = Ok policy)

    check "rendering is deterministic"
        (BranchProtection.render policy = BranchProtection.render policy)

    check "an unknown field is refused"
        (mutate (fun text -> text.Replace("\"branch\"", "\"bypass\": true,\n  \"branch\"")) |> isError)

    check "another schema is refused"
        (mutate (fun text -> text.Replace(BranchProtection.Schema, "conditor.branch-protection/v2")) |> isError)

    check "an empty check list is refused"
        (BranchProtection.parse
            """{"schema":"conditor.branch-protection/v1","branch":"main","requiredStatusChecks":[],"strictStatusChecks":false,"enforceAdmins":true,"requirePullRequest":true,"requiredApprovingReviewCount":0,"allowForcePushes":false,"allowDeletions":false}"""
         |> isError)

    check "a repeated check is refused"
        (BranchProtection.parse
            """{"schema":"conditor.branch-protection/v1","branch":"main","requiredStatusChecks":["a","a"],"strictStatusChecks":false,"enforceAdmins":true,"requirePullRequest":true,"requiredApprovingReviewCount":0,"allowForcePushes":false,"allowDeletions":false}"""
         |> isError)

    check "a blank check name is refused"
        (BranchProtection.parse
            """{"schema":"conditor.branch-protection/v1","branch":"main","requiredStatusChecks":[" "],"strictStatusChecks":false,"enforceAdmins":true,"requirePullRequest":true,"requiredApprovingReviewCount":0,"allowForcePushes":false,"allowDeletions":false}"""
         |> isError)

    check "a mistyped field is refused"
        (mutate (fun text -> text.Replace("\"allowDeletions\": false", "\"allowDeletions\": \"no\"")) |> isError)

    check "a missing field is refused"
        (mutate (fun text -> text.Replace(",\n  \"allowDeletions\": false", "")) |> isError)

    check "a review count GitHub cannot apply is refused"
        (mutate (fun text -> text.Replace("\"requiredApprovingReviewCount\": 0", "\"requiredApprovingReviewCount\": 7")) |> isError)

    check "invalid JSON is refused" (BranchProtection.parse "{" |> isError)

    use request = JsonDocument.Parse(BranchProtection.toGitHubRequest policy)
    let root = request.RootElement
    let statusChecks = root.GetProperty "required_status_checks"

    check "the GitHub request requires every declared check by context"
        (statusChecks.GetProperty("strict").GetBoolean() = false
         && (statusChecks.GetProperty("checks").EnumerateArray()
             |> Seq.map (fun entry -> entry.GetProperty("context").GetString())
             |> List.ofSeq) = (policy.RequiredChecks |> List.map Some |> List.map Option.toObj))

    check "the GitHub request binds administrators and requires a pull request without review"
        (root.GetProperty("enforce_admins").GetBoolean()
         && root.GetProperty("required_pull_request_reviews").GetProperty("required_approving_review_count").GetInt32() = 0
         && root.GetProperty("restrictions").ValueKind = JsonValueKind.Null
         && not (root.GetProperty("allow_force_pushes").GetBoolean())
         && not (root.GetProperty("allow_deletions").GetBoolean()))

    let withoutPullRequests = { policy with RequirePullRequest = false }
    use direct = JsonDocument.Parse(BranchProtection.toGitHubRequest withoutPullRequests)

    check "a policy without pull requests sends an explicit null review rule"
        (direct.RootElement.GetProperty("required_pull_request_reviews").ValueKind = JsonValueKind.Null)
