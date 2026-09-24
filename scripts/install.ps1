param(
    [string]$Version = $(if ($env:CONDITOR_VERSION) { $env:CONDITOR_VERSION } else { "latest" }),
    [string]$InstallDir = $(if ($env:CONDITOR_INSTALL_DIR) { $env:CONDITOR_INSTALL_DIR } else { Join-Path $env:LOCALAPPDATA "Conditor\bin" })
)

$ErrorActionPreference = "Stop"
$repository = "kemiller2002/conditor"

switch ($env:PROCESSOR_ARCHITECTURE) {
    "AMD64" { $arch = "x64" }
    "ARM64" { $arch = "arm64" }
    default { throw "Unsupported architecture: $env:PROCESSOR_ARCHITECTURE" }
}

$rid = "win-$arch"
$asset = "conditor-$rid.exe"

if ($Version -eq "latest") {
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
    Move-Item -Force $binary $destination

    Write-Host "Installed Conditor to $destination"

    if (($env:PATH -split ";") -notcontains $InstallDir) {
        Write-Host "Add $InstallDir to PATH to run 'conditor' directly."
    }
}
finally {
    if (Test-Path $temp) {
        Remove-Item -Recurse -Force $temp
    }
}
