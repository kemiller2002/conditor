# Installing Conditor

Conditor is distributed as a self-contained native executable. A target workstation does not need a machine-wide .NET runtime or SDK.

## Unix

The release installer detects Linux or macOS and x64 or ARM64, downloads the matching binary plus `checksums.txt`, verifies SHA-256, and installs the executable under `~/.local/bin` by default.

```bash
curl -fsSL https://raw.githubusercontent.com/kemiller2002/conditor/main/scripts/install.sh | sh
```

Override the release or install location with `CONDITOR_VERSION` and `CONDITOR_INSTALL_DIR`.

## Windows PowerShell

```powershell
iwr https://raw.githubusercontent.com/kemiller2002/conditor/main/scripts/install.ps1 -OutFile $env:TEMP\conditor-install.ps1
& $env:TEMP\conditor-install.ps1
```

The default Windows location is `%LOCALAPPDATA%\Conditor\bin\conditor.exe`.

## Release integrity

A `vX.Y.Z` tag must match the version in `Directory.Build.props`. The release workflow validates the full solution and tests before producing:

- `conditor-linux-x64`
- `conditor-linux-arm64`
- `conditor-osx-x64`
- `conditor-osx-arm64`
- `conditor-win-x64.exe`
- `conditor-win-arm64.exe`
- `checksums.txt`

Installers refuse a binary whose SHA-256 does not match the release checksum.


## Built-in presets

Release binaries embed the supported Conditor presets. List them with:

```bash
conditor presets
```

A preset can establish an empty target without a Conditor source checkout:

```bash
mkdir project
cd project
git init
conditor init --preset clean-room
```

For an execution-enabled preset, `start` can perform the missing initialization and then launch:

```bash
conditor start --preset indy-init
```

The selected preset is written into the target as `conditor.json`, after which normal `verify`, `doctor`, and `start` commands operate from repository state. Conditor refuses to replace an existing different `conditor.json` during preset start.

## Authentication for private pinned GitHub sources

Conditor source declarations contain repository names, exact 40-character commit SHAs, and entrypoints. They do not contain credentials.

For private GitHub sources, Conditor first allows normal Git credential configuration to work. For non-interactive use it recognizes, in priority order:

1. `CONDITOR_GITHUB_TOKEN`
2. `GH_TOKEN`
3. `GITHUB_TOKEN`

The selected token is supplied only to the child Git fetch process through an in-memory HTTP authorization header. It is not placed in Git arguments, Conditor manifests, lock files, generated repository files, or Conditor diagnostic command strings.

Set the token only in the process environment and give it the minimum repository-read permission required by the preset.

Example:

```bash
export CONDITOR_GITHUB_TOKEN="<token with access to the private governing repo>"
conditor start --preset indy-init
```

Existing Git credential helpers remain valid when no Conditor token environment variable is set.

## Development

Source development still uses the .NET 10 SDK:

```bash
dotnet build Conditor.slnx
dotnet run --project tests/Conditor.Tests
```

The native-binary CI smoke test publishes a self-contained Linux x64 CLI, lists its embedded presets, and plans from the embedded clean-room preset without reading example files from the source checkout.
