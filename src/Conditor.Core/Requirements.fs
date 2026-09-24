namespace Conditor.Core

open System
open System.IO

module Requirements =
    let private safePath target relativePath =
        let root = Path.GetFullPath target
        let full = Path.GetFullPath(Path.Combine(root, relativePath))
        let rootPrefix =
            root.TrimEnd(Path.DirectorySeparatorChar) + string Path.DirectorySeparatorChar

        let comparison =
            if OperatingSystem.IsWindows() then
                StringComparison.OrdinalIgnoreCase
            else
                StringComparison.Ordinal

        if full.StartsWith(rootPrefix, comparison) then Some full else None

    let private filesEqual left right =
        let leftInfo = FileInfo left
        let rightInfo = FileInfo right

        if leftInfo.Length <> rightInfo.Length then
            false
        else
            use leftStream = File.OpenRead left
            use rightStream = File.OpenRead right
            let leftBuffer = Array.zeroCreate<byte> 81920
            let rightBuffer = Array.zeroCreate<byte> 81920

            let rec compare () =
                let leftCount = leftStream.Read(leftBuffer, 0, leftBuffer.Length)
                let rightCount = rightStream.Read(rightBuffer, 0, rightBuffer.Length)

                if leftCount <> rightCount then
                    false
                elif leftCount = 0 then
                    true
                else
                    let mutable equal = true
                    let mutable index = 0

                    while equal && index < leftCount do
                        if leftBuffer[index] <> rightBuffer[index] then
                            equal <- false

                        index <- index + 1

                    equal && compare ()

            compare ()

    let private verifyOne target (requirement: RequirementSource) =
        match safePath target requirement.TargetPath with
        | None ->
            Error [ $"Requirement target path escapes the repository: {requirement.TargetPath}" ]
        | Some targetPath when not (File.Exists targetPath) ->
            Error [ $"Required governing artifact is missing: {requirement.TargetPath}" ]
        | Some targetPath ->
            match SourceCache.ensure $"requirements:{requirement.Id}" requirement.Source with
            | Error errors ->
                Error(errors |> List.map (fun error -> $"Requirement '{requirement.Id}': {error}"))
            | Ok checkout ->
                match SourceCache.resolveEntrypoint checkout requirement.Source with
                | Error errors ->
                    Error(errors |> List.map (fun error -> $"Requirement '{requirement.Id}': {error}"))
                | Ok sourcePath ->
                    if filesEqual sourcePath targetPath then
                        Ok()
                    else
                        Error
                            [ $"Governing requirement '{requirement.Id}' has drifted from its pinned source: {requirement.TargetPath}"
                              "Re-run Conditor initialization only after reviewing the changed governing input." ]

    let verify target (manifest: ProjectManifest) =
        let errors = ResizeArray<string>()

        for requirement in manifest.Requirements do
            match verifyOne target requirement with
            | Ok() -> ()
            | Error requirementErrors -> requirementErrors |> List.iter errors.Add

        if errors.Count = 0 then Ok() else Error(List.ofSeq errors)
