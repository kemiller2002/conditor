namespace Conditor.Core

open System
open System.IO
open System.Text.Json
open System.Text.RegularExpressions

module Scaffolding =
    let private normalize (value: string) =
        value.Replace("\r\n", "\n")

    let private jsonString value =
        JsonSerializer.Serialize(value)

    let private identifier (value: string) =
        let words =
            Regex.Split(value, "[^A-Za-z0-9]+")
            |> Array.filter (String.IsNullOrWhiteSpace >> not)

        let candidate =
            words
            |> Array.map (fun word ->
                if word.Length = 1 then
                    word.ToUpperInvariant()
                else
                    word.Substring(0, 1).ToUpperInvariant() + word.Substring(1))
            |> String.concat String.Empty

        let candidate =
            if String.IsNullOrWhiteSpace candidate then "Application" else candidate

        if Char.IsDigit candidate[0] then "Application" + candidate else candidate

    let private packageSlug (value: string) =
        let slug =
            Regex.Replace(value.ToLowerInvariant(), "[^a-z0-9-]+", "-").Trim('-')

        if String.IsNullOrWhiteSpace slug then "application" else slug

    let private resolvedBindings (manifest: ProjectManifest) =
        manifest.Components
        |> List.choose (fun request ->
            Registry.tryFind request.Id
            |> Option.bind (fun definition ->
                definition.ApplicationBinding
                |> Option.map (fun binding ->
                    let version =
                        request.Version |> Option.defaultValue definition.DefaultVersion

                    binding, definition.Package, version)))

    let private renderDependencies dependencies =
        match dependencies with
        | [] -> "  }"
        | values ->
            values
            |> List.mapi (fun index (package, version) ->
                let comma = if index = values.Length - 1 then String.Empty else ","
                $"    {jsonString package}: {jsonString version}{comma}")
            |> fun lines -> String.concat "\n" lines + "\n  }"

    let private packageJson projectName manifest =
        let dependencies =
            resolvedBindings manifest
            |> List.choose (fun (binding, package, version) ->
                match binding with
                | NpmDependency -> Some(package, version)
                | NugetReference -> None)
            |> List.sortBy fst

        let dependencyBody = renderDependencies dependencies

        $"{{\n  \"name\": {jsonString (packageSlug projectName + "-kernel")},\n  \"private\": true,\n  \"type\": \"module\",\n  \"scripts\": {{\n    \"check\": \"tsc --noEmit\"\n  }},\n  \"dependencies\": {{\n{dependencyBody},\n  \"devDependencies\": {{\n    \"typescript\": \"5.9.3\"\n  }}\n}}\n"

    let private projectFile projectName manifest =
        let packageReferences =
            resolvedBindings manifest
            |> List.choose (fun (binding, package, version) ->
                match binding with
                | NugetReference -> Some(package, version)
                | NpmDependency -> None)
            |> List.sortBy fst

        let itemGroup =
            match packageReferences with
            | [] -> String.Empty
            | references ->
                let lines =
                    references
                    |> List.map (fun (package, version) ->
                        $"    <PackageReference Include=\"{package}\" Version=\"{version}\" />")
                    |> String.concat "\n"

                $"\n  <ItemGroup>\n{lines}\n  </ItemGroup>"

        let assembly = identifier projectName + ".Engine"

        $"<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>net10.0</TargetFramework>\n    <Nullable>enable</Nullable>\n    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>\n    <AssemblyName>{assembly}</AssemblyName>\n  </PropertyGroup>{itemGroup}\n  <ItemGroup>\n    <Compile Include=\"Domain.fs\" />\n  </ItemGroup>\n</Project>\n"

    let private desiredFiles (manifest: ProjectManifest) (scaffold: ScaffoldRequest) =
        let projectName =
            scaffold.Name
            |> Option.filter (String.IsNullOrWhiteSpace >> not)
            |> Option.defaultValue manifest.Name

        let ns = identifier projectName

        match scaffold.Kind with
        | "fsharp-limen-web" ->
            Ok
                [ "Directory.Build.props",
                  "<Project>\n  <PropertyGroup>\n    <TargetFramework>net10.0</TargetFramework>\n    <LangVersion>latest</LangVersion>\n    <Nullable>enable</Nullable>\n    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>\n    <Deterministic>true</Deterministic>\n  </PropertyGroup>\n</Project>\n"
                  "App.slnx",
                  "<Solution>\n  <Folder Name=\"/src/\">\n    <Project Path=\"src/engine/App.Engine.fsproj\" />\n  </Folder>\n</Solution>\n"
                  "src/engine/App.Engine.fsproj", projectFile projectName manifest
                  "src/engine/Domain.fs",
                  $"namespace {ns}.Engine\n\ntype State =\n    | Uninitialized\n\nmodule State =\n    let initial = Uninitialized\n"
                  "src/kernel/package.json", packageJson projectName manifest
                  "src/kernel/tsconfig.json",
                  "{\n  \"compilerOptions\": {\n    \"target\": \"ES2022\",\n    \"module\": \"ES2022\",\n    \"moduleResolution\": \"Bundler\",\n    \"strict\": true,\n    \"noEmit\": true,\n    \"lib\": [\"ES2022\", \"DOM\"]\n  },\n  \"include\": [\"**/*.ts\"]\n}\n"
                  "src/kernel/bootstrap.ts",
                  "export const scaffoldReady = true as const;\n" ]
        | unknown ->
            Error [ $"Unsupported scaffold kind '{unknown}'." ]

    let private safeFullPath target relativePath =
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

    let plan target (manifest: ProjectManifest) =
        match manifest.Scaffold with
        | None -> Ok []
        | Some scaffold ->
            match desiredFiles manifest scaffold with
            | Error errors -> Error errors
            | Ok desired ->
                let errors = ResizeArray<string>()
                let changes = ResizeArray<string * string>()

                for relativePath, content in desired do
                    match safeFullPath target relativePath with
                    | None ->
                        errors.Add $"Scaffold path escapes the target repository: {relativePath}"
                    | Some fullPath ->
                        if File.Exists fullPath then
                            let existing = File.ReadAllText fullPath

                            if normalize existing <> normalize content then
                                errors.Add
                                    $"Scaffold file '{relativePath}' already exists with different content; Conditor will not overwrite it."
                        else
                            changes.Add(relativePath, content)

                if errors.Count > 0 then
                    Error(List.ofSeq errors)
                else
                    Ok(List.ofSeq changes)
