namespace Conditor.Core

open System

module Registry =
    let descriptors =
        match ComponentDescriptors.loadAll () with
        | Ok values -> values
        | Error errors ->
            invalidOp $"Invalid embedded Conditor component registry:{Environment.NewLine}{String.Join(Environment.NewLine, errors)}"

    let all =
        descriptors |> List.map _.Definition

    let qualifiedVersions id =
        descriptors
        |> List.tryFind (fun descriptor -> descriptor.Definition.Id = id)
        |> Option.map _.QualifiedVersions

    /// The qualified range and denied versions a project binding may be
    /// qualified by under a verified Registry release attestation.
    let attestedQualification id =
        descriptors
        |> List.tryFind (fun descriptor -> descriptor.Definition.Id = id)
        |> Option.bind (fun descriptor ->
            descriptor.QualifiedRange |> Option.map (fun range -> range, descriptor.DeniedVersions))

    let descriptorSha256 id =
        descriptors
        |> List.tryFind (fun descriptor -> descriptor.Definition.Id = id)
        |> Option.map _.Sha256

    let tryFind id =
        all |> List.tryFind (fun definition -> definition.Id = id)
