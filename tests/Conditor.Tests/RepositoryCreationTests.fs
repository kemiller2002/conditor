module RepositoryCreationTests

open System
open System.IO
open System.Text.Json
open Conditor.Core

let private request =
    { Owner = "kemiller2002"
      Name = "indy-app"
      Visibility = PrivateRepository
      Deploy = NoDeployment }

let private freshInit =
    { IsRepository = true
      HasCommits = false
      Branch = Some "main"
      HasChanges = true
      Origin = None }

let private protection = Some Scaffolding.webBranchProtection

let private ok exitCode =
    { ExitCode = exitCode
      StandardOutput = String.Empty
      StandardError = String.Empty }

let private failed (error: string) =
    { ExitCode = 1
      StandardOutput = String.Empty
      StandardError = error }

let private joined (errors: string list) = String.concat "; " errors

let private commands (steps: RepositoryStep list) = steps |> List.map _.Command

let private refusedWith (needle: string) result =
    match result with
    | Error errors -> errors |> List.exists (fun (error: string) -> error.Contains(needle, StringComparison.Ordinal))
    | Ok _ -> false

/// Records every call; `respond` decides each result.
let private recordingRunner (respond: string -> string list -> ProcessResult) =
    let calls = ResizeArray<string * string list>()

    let run executable arguments =
        calls.Add((executable, arguments))
        respond executable arguments

    calls, run

let run (check: string -> bool -> unit) =
    check "OWNER/NAME parses"
        (RepositoryCreation.parseRepository "kemiller2002/indy-app" = Ok("kemiller2002", "indy-app"))

    for invalid in [ "indy-app"; "a/b/c"; "/indy"; "kemiller2002/"; "kemiller2002/indy app"; "kemiller2002/x.git"; "-x/indy" ] do
        check $"'{invalid}' is not a repository name"
            (match RepositoryCreation.parseRepository invalid with
             | Error _ -> true
             | Ok _ -> false)

    // The full plan for a freshly initialized target.
    match RepositoryCreation.plan request freshInit RemoteAbsent protection with
    | Error errors -> check $"a fresh target plans: {joined errors}" false
    | Ok steps ->
        let expected =
                [ Git [ "add"; "--all" ]
                  Git [ "commit"; "--quiet"; "-m"; "Bootstrap indy-app with Conditor" ]
                  GitHub [ "repo"; "create"; "kemiller2002/indy-app"; "--private"; "--disable-wiki" ]
                  Git [ "remote"; "add"; "origin"; "https://github.com/kemiller2002/indy-app.git" ]
                  Git [ "push"; "--set-upstream"; "origin"; "main" ]
                  GitHub
                      [ "api"; "--method"; "PATCH"; "repos/kemiller2002/indy-app"
                        "-F"; "allow_auto_merge=true"
                        "-F"; "allow_merge_commit=true"
                        "-F"; "allow_squash_merge=false"
                        "-F"; "allow_rebase_merge=false"
                        "-F"; "delete_branch_on_merge=true" ]
                  GitHub
                      [ "api"; "--method"; "PUT"; "repos/kemiller2002/indy-app/actions/permissions/workflow"
                        "-f"; "default_workflow_permissions=read"
                        "-F"; "can_approve_pull_request_reviews=false" ]
                  GitHubWithBody(
                      [ "api"; "--method"; "PUT"; "repos/kemiller2002/indy-app/branches/main/protection" ],
                      BranchProtection.toGitHubRequest Scaffolding.webBranchProtection
                  ) ]

        check "a fresh target is committed, created, pushed, configured and protected, in that order"
            (commands steps = expected)

        check "the plan is deterministic"
            (RepositoryCreation.plan request freshInit RemoteAbsent protection = Ok steps)

        check "the dry-run command line shows the protection call with its body placeholder"
            (RepositoryCreation.commandLine (List.last steps) = "gh api --method PUT repos/kemiller2002/indy-app/branches/main/protection --input <body>")

    match RepositoryCreation.plan request { freshInit with HasCommits = true; HasChanges = false } RemoteAbsent None with
    | Error errors -> check $"a committed target plans: {joined errors}" false
    | Ok steps ->
        check "a committed target is not committed again" (steps |> List.forall (fun step -> step.Command <> Git [ "add"; "--all" ]))
        check "without a declaration no branch protection is applied"
            (steps |> List.forall (fun step -> match step.Command with GitHubWithBody _ -> false | _ -> true))

    match RepositoryCreation.plan { request with Visibility = PublicRepository; Deploy = GitHubPages } freshInit RemoteAbsent protection with
    | Error errors -> check $"a public Pages target plans: {joined errors}" false
    | Ok steps ->
        let all = commands steps
        check "a public repository is requested as public"
            (all |> List.contains (GitHub [ "repo"; "create"; "kemiller2002/indy-app"; "--public"; "--disable-wiki" ]))
        let pagesSteps =
                [ GitHub [ "variable"; "set"; "DEPLOY_TARGET"; "--body"; "github-pages"; "--repo"; "kemiller2002/indy-app" ]
                  GitHub [ "api"; "--method"; "POST"; "repos/kemiller2002/indy-app/pages"; "-f"; "build_type=workflow" ] ]

        check "GitHub Pages is enabled for Actions and selected, after protection"
            (List.rev all |> List.take 2 = pagesSteps)

    // Fail closed.
    check "a directory that is not a Git repository is refused"
        (RepositoryCreation.plan request { freshInit with IsRepository = false } RemoteAbsent protection |> refusedWith "not a Git repository")
    check "a branch other than main is refused"
        (RepositoryCreation.plan request { freshInit with Branch = Some "master" } RemoteAbsent protection |> refusedWith "git init -b main")
    check "uncommitted changes on top of commits are refused"
        (RepositoryCreation.plan request { freshInit with HasCommits = true } RemoteAbsent protection |> refusedWith "uncommitted changes")
    check "an empty target is refused"
        (RepositoryCreation.plan request { freshInit with HasChanges = false } RemoteAbsent protection |> refusedWith "nothing to publish")
    check "a target that already has origin is refused"
        (RepositoryCreation.plan request { freshInit with Origin = Some "https://github.com/x/y.git" } RemoteAbsent protection |> refusedWith "already has an 'origin'")
    check "an existing GitHub repository is never adopted or overwritten"
        (RepositoryCreation.plan request freshInit RemotePresent protection |> refusedWith "already exists")
    check "an unanswerable existence check creates nothing"
        (RepositoryCreation.plan request freshInit (RemoteUnknown "gh: not logged in") protection |> refusedWith "not logged in")
    check "protection declared for another branch is refused"
        (RepositoryCreation.plan request freshInit RemoteAbsent (Some { Scaffolding.webBranchProtection with Branch = "trunk" }) |> refusedWith "names 'trunk'")
    check "every refusal reason is reported at once"
        (match RepositoryCreation.plan request { freshInit with Origin = Some "o" } RemotePresent protection with
         | Error errors -> errors.Length = 2
         | Ok _ -> false)

    // Execution.
    match RepositoryCreation.plan request freshInit RemoteAbsent protection with
    | Error _ -> check "execution fixture plans" false
    | Ok steps ->
        let bodies = ResizeArray<string>()
        let calls, run = recordingRunner (fun _ _ -> ok 0)

        let withBody (body: string) (action: string -> ProcessResult) =
            bodies.Add body
            action "/tmp/body.json"

        let outcome = RepositoryCreation.execute run withBody steps

        check "every step runs once, in order, when each succeeds"
            (outcome.Completed = steps && outcome.Failed.IsNone && outcome.Remaining.IsEmpty && calls.Count = steps.Length)

        check "the protection body is sent through --input"
            (bodies |> List.ofSeq = [ BranchProtection.toGitHubRequest Scaffolding.webBranchProtection ]
             && snd calls[calls.Count - 1] |> List.rev |> List.take 2 = [ "/tmp/body.json"; "--input" ])

        let calls, run =
            recordingRunner (fun executable arguments ->
                if executable = "git" && arguments |> List.contains "push" then failed "rejected" else ok 0)

        let outcome = RepositoryCreation.execute run (fun _ action -> action "/tmp/body.json") steps

        check "execution stops at the first failure and runs nothing after it"
            (outcome.Completed.Length = 4
             && (outcome.Failed |> Option.exists (fun (step, result) -> step = steps[4] && result.StandardError = "rejected"))
             && outcome.Remaining = List.skip 5 steps
             && calls.Count = 5)

    // Observation.
    let localRunner (responses: Map<string list, ProcessResult>) _ arguments =
        responses |> Map.tryFind arguments |> Option.defaultValue (failed "unexpected")

    let observed =
        RepositoryCreation.observeLocal (
            localRunner (
                Map.ofList
                    [ [ "rev-parse"; "--is-inside-work-tree" ], ok 0
                      [ "rev-parse"; "--verify"; "--quiet"; "HEAD" ], ok 1
                      [ "symbolic-ref"; "--short"; "HEAD" ], { ok 0 with StandardOutput = "main\n" }
                      [ "status"; "--porcelain" ], { ok 0 with StandardOutput = "?? conditor.json\n" }
                      [ "remote"; "get-url"; "origin" ], failed "error: No such remote 'origin'" ]
            )
        )

    check "a freshly initialized target is observed as uncommitted main without origin" (observed = freshInit)

    check "outside a work tree nothing else is asked"
        (not (RepositoryCreation.observeLocal (fun _ _ -> failed "fatal: not a git repository")).IsRepository)

    let remoteFor result = RepositoryCreation.observeRemote (fun _ _ -> result) request
    check "a found repository is present" (remoteFor (ok 0) = RemotePresent)
    check "a 404 means absent" (remoteFor (failed "gh: Not Found (HTTP 404)") = RemoteAbsent)
    check "any other answer is unknown"
        (match remoteFor (failed "gh: HTTP 401: Bad credentials") with
         | RemoteUnknown reason -> reason.Contains "401"
         | _ -> false)

    // The declaration on disk.
    let target = Path.Combine(Path.GetTempPath(), $"conditor-repo-{Guid.NewGuid():N}")
    Directory.CreateDirectory(Path.Combine(target, ".github")) |> ignore

    try
        check "a target without a declaration is published without protection" (RepositoryCreation.readProtection target = Ok None)

        File.WriteAllText(Path.Combine(target, BranchProtection.RelativePath), BranchProtection.render Scaffolding.webBranchProtection)
        check "the scaffold's declaration is read" (RepositoryCreation.readProtection target = Ok protection)

        File.WriteAllText(Path.Combine(target, BranchProtection.RelativePath), """{"schema":"conditor.branch-protection/v1"}""")
        check "an invalid declaration stops publication"
            (match RepositoryCreation.readProtection target with
             | Error errors -> errors |> List.forall (fun error -> error.StartsWith(BranchProtection.RelativePath, StringComparison.Ordinal))
             | Ok _ -> false)
    finally
        Directory.Delete(target, true)

    // Integration: real Git for the local steps, GitHub and the network faked.
    let target = Path.Combine(Path.GetTempPath(), $"conditor-repo-{Guid.NewGuid():N}")
    Directory.CreateDirectory target |> ignore

    try
        let git arguments = ProcessRunner.runProcess target "git" arguments
        git [ "init"; "--quiet"; "-b"; "main" ] |> ignore
        git [ "config"; "user.email"; "conditor-test@example.invalid" ] |> ignore
        git [ "config"; "user.name"; "Conditor Test" ] |> ignore
        File.WriteAllText(Path.Combine(target, "conditor.json"), "{}\n")

        let ghCalls = ResizeArray<string list>()

        let run executable (arguments: string list) =
            match executable, arguments with
            | "gh", [ "api"; "repos/kemiller2002/indy-app"; "--jq"; ".full_name" ] -> failed "gh: Not Found (HTTP 404)"
            | "gh", _ ->
                ghCalls.Add arguments
                ok 0
            | "git", "push" :: _ -> ok 0
            | _ -> ProcessRunner.runProcess target executable arguments

        let local = RepositoryCreation.observeLocal run
        let remote = RepositoryCreation.observeRemote run request

        match RepositoryCreation.plan request local remote protection with
        | Error errors -> check $"a real fresh repository plans: {joined errors}" false
        | Ok steps ->
            let outcome = RepositoryCreation.execute run (fun _ action -> action "/tmp/body.json") steps
            let log = git [ "log"; "--format=%s" ]
            let origin = git [ "remote"; "get-url"; "origin" ]

            check "a real fresh repository is committed with the bootstrap message and given origin"
                (outcome.Failed.IsNone
                 && log.StandardOutput.Trim() = "Bootstrap indy-app with Conditor"
                 && origin.StandardOutput.Trim() = "https://github.com/kemiller2002/indy-app.git")

            check "after publishing, the same target is refused (origin exists)"
                (RepositoryCreation.plan request (RepositoryCreation.observeLocal run) RemoteAbsent protection
                 |> refusedWith "already has an 'origin'")

            check "GitHub received the create, two settings and the protection calls" (ghCalls.Count = 4)
    finally
        Directory.Delete(target, true)
