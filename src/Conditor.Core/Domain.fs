namespace Conditor.Core

type Distribution =
    | LifecycleNpm
    | NpmPackage
    | NugetPackage

type ComponentRequest =
    { Id: string
      Version: string option
      Required: bool }

type ExecutionRequest =
    { Enabled: bool
      Launcher: string option
      Mission: string option }

type ProjectManifest =
    { SchemaVersion: int
      Name: string
      Components: ComponentRequest list
      Execution: ExecutionRequest option }

type ComponentDefinition =
    { Id: string
      DisplayName: string
      Distribution: Distribution
      Package: string
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

type PlanAction =
    { Sequence: int
      ComponentId: string
      ComponentVersion: string
      Kind: PlanActionKind
      Executable: string
      Arguments: string list }

type ResolvedComponent =
    { Id: string
      Version: string
      Distribution: Distribution
      Package: string }

type InstallationPlan =
    { ProjectName: string
      Operation: Operation
      Components: ResolvedComponent list
      Actions: PlanAction list }

type ProcessResult =
    { ExitCode: int
      StandardOutput: string
      StandardError: string }
