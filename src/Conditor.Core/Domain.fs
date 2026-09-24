namespace Conditor.Core

type Distribution =
    | LifecycleNpm
    | NpmPackage
    | NugetPackage

type SourceEntrypoint =
    | NodeScript of string

type GitHubSource =
    { Repository: string
      Commit: string
      Entrypoint: SourceEntrypoint }

type LifecycleSource =
    | RegistryPackage
    | GitHubSource of GitHubSource

type ComponentRequest =
    { Id: string
      Version: string option
      Required: bool }

type ScaffoldRequest =
    { Kind: string
      Name: string option }

type ExecutionRequest =
    { Enabled: bool
      Launcher: string option
      Mission: string option }

type ProjectManifest =
    { SchemaVersion: int
      Name: string
      Components: ComponentRequest list
      Scaffold: ScaffoldRequest option
      Execution: ExecutionRequest option }

type ComponentDefinition =
    { Id: string
      DisplayName: string
      Distribution: Distribution
      Package: string
      LifecycleSource: LifecycleSource option
      Command: string option
      DefaultVersion: string
      InitArguments: string list
      VerifyArguments: string list
      DoctorArguments: string list }

type Operation =
    | Init
    | Verify
    | Doctor

type PlanActionKind =
    | InstallLifecycle
    | VerifyLifecycle
    | DiagnoseLifecycle
    | ScaffoldFile
    | ReadinessVerify

type ActionExecution =
    | ExternalProcess of executable: string * arguments: string list
    | GitHubSourceProcess of source: GitHubSource * arguments: string list
    | EnsureFile of relativePath: string * content: string

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
