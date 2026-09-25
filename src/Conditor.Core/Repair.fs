namespace Conditor.Core

module Repair =
    let reconcile target manifestPath (manifest: ProjectManifest) =
        match LockFile.verifyManifest target manifestPath with
        | Error errors -> Error errors
        | Ok() ->
            match Planner.create target Init manifest with
            | Error errors -> Error errors
            | Ok plan ->
                Installer.execute target manifestPath plan
                |> Result.map (fun _ -> ())
