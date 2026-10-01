param(
    [string]$Version = $(if ($env:CONDITOR_VERSION) { $env:CONDITOR_VERSION } else { "latest" }),
    [string]$InstallDir = $(if ($env:CONDITOR_INSTALL_DIR) { $env:CONDITOR_INSTALL_DIR } else { Join-Path $env:LOCALAPPDATA "Conditor\bin" }),
    [switch]$NoPathUpdate = $(if ($env:CONDITOR_NO_PATH_UPDATE -eq "1") { $true } else { $false }),
    [string]$ReleaseBase = $env:CONDITOR_RELEASE_BASE,
    [string[]]$Run = @()
)

$ErrorActionPreference = "Stop"
$repository = $(if ($env:CONDITOR_REPOSITORY) { $env:CONDITOR_REPOSITORY } else { "kemiller2002/conditor" })

$architecture = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()
switch ($architecture) {
    "X64" { $arch = "x64" }
    "Arm64" { $arch = "arm64" }
    default { throw "Unsupported architecture: $architecture" }
}

$rid = "win-$arch"
$asset = "conditor-$rid.exe"

if ($ReleaseBase) {
    $base = $ReleaseBase.TrimEnd("/")
} elseif ($Version -eq "latest") {
    $base = "https://github.com/$repository/releases/latest/download"
} else {
    $tag = if ($Version.StartsWith("v")) { $Version } else { "v$Version" }
    $base = "https://github.com/$repository/releases/download/$tag"
}

$temp = Join-Path ([System.IO.Path]::GetTempPath()) ("conditor-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $temp | Out-Null

try {
    $binary = Join-Path $temp $asset
    $checksums = Join-Path $temp "checksums.txt"

    Invoke-WebRequest -Uri "$base/$asset" -OutFile $binary
    Invoke-WebRequest -Uri "$base/checksums.txt" -OutFile $checksums

    $line = Get-Content $checksums | Where-Object { $_ -match "\s+$([Regex]::Escape($asset))$" } | Select-Object -First 1
    if (-not $line) {
        throw "No checksum published for $asset"
    }

    $expected = ($line -split "\s+")[0].ToLowerInvariant()
    $actual = (Get-FileHash -Algorithm SHA256 -Path $binary).Hash.ToLowerInvariant()

    if ($actual -ne $expected) {
        throw "Checksum verification failed for $asset"
    }

    New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
    $destination = Join-Path $InstallDir "conditor.exe"
    $staged = Join-Path $InstallDir (".conditor.new." + [Guid]::NewGuid().ToString("N"))
    Copy-Item $binary $staged
    Move-Item -Force $staged $destination

    Write-Host "Installed Conditor to $destination"

    $currentUserPath = [Environment]::GetEnvironmentVariable("PATH", "User")
    $pathParts = @()
    if ($currentUserPath) {
        $pathParts = $currentUserPath -split ";" | Where-Object { $_ }
    }

    if (($env:PATH -split ";") -notcontains $InstallDir) {
        $env:PATH = "$InstallDir;$env:PATH"
    }

    if ($pathParts -notcontains $InstallDir) {
        if ($NoPathUpdate) {
            Write-Host "Add $InstallDir to the user PATH to run 'conditor' directly in future shells."
        } else {
            $newUserPath = if ($currentUserPath) { "$InstallDir;$currentUserPath" } else { $InstallDir }
            [Environment]::SetEnvironmentVariable("PATH", $newUserPath, "User")
            Write-Host "Added $InstallDir to the user PATH for future shells."
        }
    }

    if ($Run.Count -gt 0) {
        Write-Host "Running installed Conditor: $($Run -join ' ')"
        & $destination @Run
        if ($LASTEXITCODE -ne 0) {
            exit $LASTEXITCODE
        }
    }
}
finally {
    if (Test-Path $temp) {
        Remove-Item -Recurse -Force $temp
    }
}
