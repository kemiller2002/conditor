namespace Conditor.Core

type Distribution =
    | HostTool
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

type RegistryAuthority =
    { Kind: string
      Path: string
      Sha256: string }

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
      RegistryAuthority: RegistryAuthority option
      Scaffold: ScaffoldRequest option
      Requirements: RequirementSource list
      Execution: ExecutionRequest option }

/// A package identity a component was distributed under before its current
/// `Package` name. Each listed version is published only under this name.
type HistoricalPackage =
    { Package: string
      Versions: Set<string> }

/// A verification that fails closed on installation integrity and reports
/// structural review signals instead of failing on them. Only the exact
/// qualified versions listed carry it; every other version keeps
/// `VerifyArguments`.
type IntegrityGate =
    { Versions: Set<string>
      Arguments: string list }

/// A structural review finding a component reported without failing its
/// integrity gate (for Ordo, SDE-STRUCT-001). Recorded, never a refusal.
type ReviewSignal =
    { ComponentId: string
      Code: string
      Band: string
      Path: string
      LineCount: int }

type ComponentDefinition =
    { Id: string
      DisplayName: string
      Distribution: Distribution
      /// The component's current package identity.
      Package: string
      /// Earlier package identities, each owning an explicit set of qualified
      /// versions. Versions not listed here use `Package`.
      HistoricalPackages: HistoricalPackage list
      LifecycleSource: LifecycleSource option
      ApplicationBinding: ApplicationBinding option
      Command: string option
      DefaultVersion: string
      VersionArguments: string list
      InitArguments: string list
      VerifyArguments: string list
      DoctorArguments: string list
      UpgradeArguments: string list
      IntegrityGate: IntegrityGate option }

module ComponentDefinition =
    /// The package identity under which `version` of the component is
    /// distributed: the historical identity that lists the version, otherwise
    /// the current `Package`.
    let packageFor (version: string) (definition: ComponentDefinition) =
        definition.HistoricalPackages
        |> List.tryFind (fun historical -> historical.Versions.Contains version)
        |> Option.map _.Package
        |> Option.defaultValue definition.Package

type Operation =
    | Init
    | Verify
    | Doctor
    | Upgrade

type PlanActionKind =
    | InstallLifecycle
    | VerifyLifecycle
    /// A verify that gates the lock through the component's integrity gate.
    | IntegrityVerifyLifecycle
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
