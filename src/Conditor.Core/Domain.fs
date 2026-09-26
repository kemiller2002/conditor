namespace Conditor.Core

type Distribution =
    | LifecycleNpm
    | NpmPackage
    | NugetPackage

type SourceEntrypoint =
    | NodeScript of string
    | FileArtifact of string

type GitHubSource =
    { Repository: string
      Commit: string
      Entrypoint: SourceEntrypoint }

type LifecycleSource =
    | RegistryPackage
    | GitHubSource of GitHubSource

type ApplicationBinding =
    | NpmDependency
    | NugetReference

type ComponentRequest =
    { Id: string
      Version: string option
      Required: bool }

type ScaffoldRequest =
    { Kind: string
      Name: string option }

type RequirementSource =
    { Id: string
      Source: GitHubSource
      TargetPath: string }

type ExecutionRequest =
    { Enabled: bool
      Launcher: string option
      Mission: string option
      ContractPath: string option
      /// Operator-configured model for the launched agent CLI. When set,
      /// Conditor passes it to the CLI and declares it to Praxis; when absent the
      /// model is unknown and never guessed (CON-131).
      Model: string option }

type PraxisMission =
    { Id: string
      Title: string
      Description: string
      ContractPath: string }

type ProjectManifest =
    { SchemaVersion: int
      Name: string
      Components: ComponentRequest list
      Scaffold: ScaffoldRequest option
      Requirements: RequirementSource list
      Execution: ExecutionRequest option }

type ComponentDefinition =
    { Id: string
      DisplayName: string
      Distribution: Distribution
      Package: string
      LifecycleSource: LifecycleSource option
      ApplicationBinding: ApplicationBinding option
      Command: string option
      DefaultVersion: string
      InitArguments: string list
      VerifyArguments: string list
      DoctorArguments: string list
      UpgradeArguments: string list }

type Operation =
    | Init
    | Verify
    | Doctor
    | Upgrade

type PlanActionKind =
    | InstallLifecycle
    | VerifyLifecycle
    | DiagnoseLifecycle
    | UpgradeLifecycle
    | ScaffoldFile
    | ReadinessVerify
    | RequirementFile
    | MissionWorkItem
    | ManifestFile

type ActionExecution =
    | ExternalProcess of executable: string * arguments: string list
    | GitHubSourceProcess of source: GitHubSource * arguments: string list
    | EnsureFile of relativePath: string * content: string
    | EnsureManagedRegion of relativePath: string * regionId: string * content: string
    | MaterializeSourceFile of source: GitHubSource * relativePath: string
    | EnsurePraxisMission of mission: PraxisMission

type PlanAction =
    { Sequence: int
      ComponentId: string
      ComponentVersion: string
      Kind: PlanActionKind
      Execution: ActionExecution }

type ResolvedComponent =
    { Id: string
      Version: string
      Distribution: Distribution
      Package: string
      SourceReference: string option }

type InstallationPlan =
    { ProjectName: string
      Operation: Operation
      Components: ResolvedComponent list
      Actions: PlanAction list }

type ProcessResult =
    { ExitCode: int
      StandardOutput: string
      StandardError: string }

/// Praxis provenance contract 1.2 rule 2: "blank" is judged over ASCII
/// whitespace only (tab, LF, VT, FF, CR, space). .NET `Trim`/`IsNullOrWhiteSpace`,
/// JavaScript `trim`, and Python `strip` disagree about Unicode whitespace
/// (U+0085, U+FEFF, U+001C, U+00A0), so every other character is content and
/// every reader of an identity or provenance value reaches the same verdict.
[<RequireQualifiedAccess>]
module AsciiText =
    let private whitespace = [| '\t'; '\n'; '\011'; '\012'; '\r'; ' ' |]

    let trim (value: string) = value.Trim whitespace

    let isBlank (value: string | null) =
        match value with
        | null -> true
        | text -> (trim text).Length = 0
