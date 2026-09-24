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

## Development

Source development still uses the .NET 10 SDK:

```bash
dotnet build Conditor.slnx
dotnet run --project tests/Conditor.Tests
```

The native-binary CI smoke test publishes a self-contained Linux x64 CLI and runs the committed Indy Init plan through that produced executable.
