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

    let tryFind id =
        all |> List.tryFind (fun definition -> definition.Id = id)
