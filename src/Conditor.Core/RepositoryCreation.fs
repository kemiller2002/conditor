namespace Conditor.Core

open System
open System.IO
open System.Text.RegularExpressions

type RepositoryVisibility =
    | PrivateRepository
    | PublicRepository

type DeployTarget =
    | NoDeployment
    | GitHubPages

type RepositoryRequest =
    { Owner: string
      Name: string
      Visibility: RepositoryVisibility
      Deploy: DeployTarget }

/// What the local repository looks like before anything is published.
type LocalRepositoryState =
    { IsRepository: bool
      HasCommits: bool
      Branch: string option
      HasChanges: bool
      Origin: string option }

/// Whether the GitHub repository already exists. `RemoteUnknown` is any
/// answer other than "found" or "not found" (no auth, no network).
type RemoteRepositoryState =
    | RemoteAbsent
    | RemotePresent
    | RemoteUnknown of reason: string

type RepositoryCommand =
    | Git of arguments: string list
    | GitHub of arguments: string list
    /// A `gh api` call whose JSON body is sent with `--input <file>`.
    | GitHubWithBody of arguments: string list * body: string

type RepositoryStep =
    { Description: string
      Command: RepositoryCommand }

type RepositoryCreationOutcome =
    { Completed: RepositoryStep list
      Failed: (RepositoryStep * ProcessResult) option
      Remaining: RepositoryStep list }

/// Creates the GitHub repository for a Conditor-initialized local repository
/// and configures it for agent work: a pushed main, merge-commit-only pull
/// requests with auto-merge, read-only default workflow tokens and the
/// branch protection the scaffold declares. The plan is a pure function of
/// the request and the observed state; execution takes an injected runner,
/// so tests and `--dry-run` never touch GitHub.
module RepositoryCreation =
    [<Literal>]
    let Branch = "main"

    let private segment = Regex("^[A-Za-z0-9][A-Za-z0-9._-]{0,99}$")

    /// Parses `OWNER/NAME`.
    let parseRepository (value: string) =
        match value.Trim().Split('/') with
        | [| owner; name |] when segment.IsMatch owner && segment.IsMatch name && not (name.EndsWith ".git") ->
            Ok(owner, name)
        | _ -> Error [ $"Repository '{value}' must be OWNER/NAME using letters, digits, '.', '-' or '_'." ]

    let fullName (request: RepositoryRequest) = $"{request.Owner}/{request.Name}"

    let remoteUrl (request: RepositoryRequest) = $"https://github.com/{fullName request}.git"

    let private preconditions (request: RepositoryRequest) (local: LocalRepositoryState) (remote: RemoteRepositoryState) =
        [ if not local.IsRepository then
              yield "The target is not a Git repository. Run 'git init -b main' and 'conditor init' first."
          elif local.Branch <> Some Branch then
              let branch = local.Branch |> Option.defaultValue "a detached HEAD"
              yield $"The target is on {branch}; the repository is published from '{Branch}' (git init -b {Branch})."
          elif local.HasCommits && local.HasChanges then
              yield "The target has uncommitted changes. Commit or discard them before publishing; Conditor never publishes a half-recorded state."
          elif not local.HasCommits && not local.HasChanges then
              yield "The target has nothing to publish. Run 'conditor init' first."

          match local.Origin with
          | Some origin -> yield $"The target already has an 'origin' remote ({origin}); Conditor creates repositories only for unpublished targets."
          | None -> ()

          match remote with
          | RemoteAbsent -> ()
          | RemotePresent -> yield $"GitHub repository {fullName request} already exists; Conditor never adopts or overwrites an existing repository."
          | RemoteUnknown reason -> yield $"Cannot tell whether {fullName request} exists, so nothing was created: {reason}" ]

    let private commitSteps (request: RepositoryRequest) (local: LocalRepositoryState) =
        if local.HasCommits then
            []
        else
            [ { Description = "Stage the Conditor bootstrap"
                Command = Git [ "add"; "--all" ] }
              { Description = "Record the first commit"
                Command = Git [ "commit"; "--quiet"; "-m"; $"Bootstrap {request.Name} with Conditor" ] } ]

    let private publishSteps (request: RepositoryRequest) =
        let visibility =
            match request.Visibility with
            | PrivateRepository -> "--private"
            | PublicRepository -> "--public"

        [ { Description = $"Create the GitHub repository {fullName request}"
            Command = GitHub [ "repo"; "create"; fullName request; visibility; "--disable-wiki" ] }
          { Description = "Add the GitHub repository as origin"
            Command = Git [ "remote"; "add"; "origin"; remoteUrl request ] }
          { Description = $"Push {Branch}"
            Command = Git [ "push"; "--set-upstream"; "origin"; Branch ] } ]

    /// Pull requests merge with merge commits only: Praxis checkpoints are
    /// commits, and a squash or rebase merge would orphan them from main.
    let private settingsSteps (request: RepositoryRequest) =
        let repository = $"repos/{fullName request}"

        [ { Description = "Allow auto-merge with merge commits only; delete merged branches"
            Command =
              GitHub
                  [ "api"; "--method"; "PATCH"; repository
                    "-F"; "allow_auto_merge=true"
                    "-F"; "allow_merge_commit=true"
                    "-F"; "allow_squash_merge=false"
                    "-F"; "allow_rebase_merge=false"
                    "-F"; "delete_branch_on_merge=true" ] }
          { Description = "Give workflows a read-only token that cannot approve pull requests"
            Command =
              GitHub
                  [ "api"; "--method"; "PUT"; $"{repository}/actions/permissions/workflow"
                    "-f"; "default_workflow_permissions=read"
                    "-F"; "can_approve_pull_request_reviews=false" ] } ]

    let private protectionSteps (request: RepositoryRequest) (protection: BranchProtectionPolicy option) =
        protection
        |> Option.map (fun policy ->
            let checks = String.Join(", ", policy.RequiredChecks)

            { Description = $"Protect {policy.Branch}: pull requests and the checks {checks}"
              Command =
                GitHubWithBody(
                    [ "api"; "--method"; "PUT"; $"repos/{fullName request}/branches/{policy.Branch}/protection" ],
                    BranchProtection.toGitHubRequest policy
                ) })
        |> Option.toList

    let private deploySteps (request: RepositoryRequest) =
        match request.Deploy with
        | NoDeployment -> []
        | GitHubPages ->
            [ { Description = "Enable GitHub Pages, built by GitHub Actions"
                Command = GitHub [ "api"; "--method"; "POST"; $"repos/{fullName request}/pages"; "-f"; "build_type=workflow" ] }
              { Description = "Select the GitHub Pages deployment (DEPLOY_TARGET)"
                Command = GitHub [ "variable"; "set"; "DEPLOY_TARGET"; "--body"; "github-pages"; "--repo"; fullName request ] } ]

    /// The ordered steps, or every reason the target cannot be published.
    let plan
        (request: RepositoryRequest)
        (local: LocalRepositoryState)
        (remote: RemoteRepositoryState)
        (protection: BranchProtectionPolicy option)
        =
        let protectionErrors =
            match protection with
            | Some policy when policy.Branch <> Branch ->
                [ $"The declared branch protection names '{policy.Branch}', but the repository is published from '{Branch}'." ]
            | _ -> []

        match preconditions request local remote @ protectionErrors with
        | [] ->
            Ok(
                commitSteps request local
                @ publishSteps request
                @ settingsSteps request
                @ protectionSteps request protection
                @ deploySteps request
            )
        | errors -> Error errors

    let private quote (argument: string) =
        if argument |> Seq.exists (fun character -> Char.IsWhiteSpace character || character = '"' || character = ',') then
            "\"" + argument.Replace("\"", "\\\"") + "\""
        else
            argument

    /// The command line a step runs, for `--dry-run` and failure reports.
    let commandLine (step: RepositoryStep) =
        let join executable arguments = String.Join(" ", executable :: (arguments |> List.map quote))

        match step.Command with
        | Git arguments -> join "git" arguments
        | GitHub arguments -> join "gh" arguments
        | GitHubWithBody(arguments, _) -> join "gh" (arguments @ [ "--input"; "<body>" ])

    /// Runs the steps in order and stops at the first failure. Nothing is
    /// rolled back: a created GitHub repository is never deleted
    /// automatically, and the outcome names what remains to be done.
    /// `run executable arguments` runs one process; `withBody body action`
    /// writes the body to a file, calls `action` with its path and removes it.
    let execute
        (run: string -> string list -> ProcessResult)
        (withBody: string -> (string -> ProcessResult) -> ProcessResult)
        (steps: RepositoryStep list)
        =
        let runStep step =
            match step.Command with
            | Git arguments -> run "git" arguments
            | GitHub arguments -> run "gh" arguments
            | GitHubWithBody(arguments, body) -> withBody body (fun path -> run "gh" (arguments @ [ "--input"; path ]))

        let rec loop completed remaining =
            match remaining with
            | [] ->
                { Completed = List.rev completed
                  Failed = None
                  Remaining = [] }
            | step :: rest ->
                let result = runStep step

                if result.ExitCode = 0 then
                    loop (step :: completed) rest
                else
                    { Completed = List.rev completed
                      Failed = Some(step, result)
                      Remaining = rest }

        loop [] steps

    /// Reads the local repository state with read-only Git commands.
    let observeLocal (run: string -> string list -> ProcessResult) =
        let succeeded arguments = (run "git" arguments).ExitCode = 0
        let output arguments =
            let result = run "git" arguments
            if result.ExitCode = 0 then Some(result.StandardOutput.Trim()) else None

        if not (succeeded [ "rev-parse"; "--is-inside-work-tree" ]) then
            { IsRepository = false
              HasCommits = false
              Branch = None
              HasChanges = false
              Origin = None }
        else
            { IsRepository = true
              HasCommits = succeeded [ "rev-parse"; "--verify"; "--quiet"; "HEAD" ]
              Branch = output [ "symbolic-ref"; "--short"; "HEAD" ] |> Option.filter (String.IsNullOrWhiteSpace >> not)
              HasChanges = output [ "status"; "--porcelain" ] |> Option.exists (String.IsNullOrWhiteSpace >> not)
              Origin = output [ "remote"; "get-url"; "origin" ] |> Option.filter (String.IsNullOrWhiteSpace >> not) }

    /// Asks GitHub whether the repository exists with a read-only GET.
    let observeRemote (run: string -> string list -> ProcessResult) (request: RepositoryRequest) =
        let result = run "gh" [ "api"; $"repos/{fullName request}"; "--jq"; ".full_name" ]

        if result.ExitCode = 0 then
            RemotePresent
        elif result.StandardError.Contains("HTTP 404", StringComparison.Ordinal)
             || result.StandardError.Contains("Not Found", StringComparison.Ordinal) then
            RemoteAbsent
        else
            let detail =
                [ result.StandardError.Trim(); result.StandardOutput.Trim() ]
                |> List.filter (String.IsNullOrWhiteSpace >> not)
                |> String.concat " "

            RemoteUnknown(if String.IsNullOrWhiteSpace detail then $"gh exited with {result.ExitCode}." else detail)

    /// Reads the scaffold's branch-protection declaration, if it has one.
    /// A declaration that exists but does not parse is an error.
    let readProtection (target: string) =
        let path = Path.Combine(target, BranchProtection.RelativePath)

        if File.Exists path then
            BranchProtection.parse (File.ReadAllText path)
            |> Result.map Some
            |> Result.mapError (List.map (fun error -> $"{BranchProtection.RelativePath}: {error}"))
        else
            Ok None
