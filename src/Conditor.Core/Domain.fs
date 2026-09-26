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

/// Whether the first component version that provides a capability has been
/// released (and can therefore be qualified) or is still unreleased upstream.
type CapabilityStatus =
    | CapabilityReleased
    | CapabilityUnreleased

/// A named capability a component gains at a known version, declared in the
/// component descriptor's `capabilities` object (CON-068).
type ComponentCapability =
    { Name: string
      Since: string
      Status: CapabilityStatus }

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
      ContractPath: string option }

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
    | ProvenanceVerify
    | ManifestFile

type ActionExecution =
    | ExternalProcess of executable: string * arguments: string list
    | GitHubSourceProcess of source: GitHubSource * arguments: string list
    | EnsureFile of relativePath: string * content: string
    | EnsureManagedRegion of relativePath: string * regionId: string * content: string
    | MaterializeSourceFile of source: GitHubSource * relativePath: string
    | EnsurePraxisMission of mission: PraxisMission
    | VerifyPraxisProvenance

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
