# Build bootstrapper + silent-capable installer for Valve (ledger 28.08-28.11).
# Compiles Valve via dotnet publish, copies WinDivert signed binaries, and invokes Inno Setup ISCC.exe.
param(
    [string]$PublishDir = "$PSScriptRoot\..\publish",
    [string]$OutputDir = "$PSScriptRoot\out",
    [string]$IsccPath = ""
)
$ErrorActionPreference = "Stop"

# 1. Locate ISCC.exe compiler
if ([string]::IsNullOrWhiteSpace($IsccPath)) {
    $searchPaths = @(
        "C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
        "C:\Program Files\Inno Setup 6\ISCC.exe",
        "C:\Program Files (x86)\Inno Setup 5\ISCC.exe",
        "C:\Program Files\Inno Setup 5\ISCC.exe"
    )
    foreach ($p in $searchPaths) {
        if (Test-Path $p) {
            $IsccPath = $p
            break
        }
    }
    if ([string]::IsNullOrWhiteSpace($IsccPath)) {
        $cmd = Get-Command "ISCC.exe" -ErrorAction SilentlyContinue
        if ($cmd) {
            $IsccPath = $cmd.Source
        }
    }
}

Write-Host "Creating output directories..."
New-Item -ItemType Directory -Force -Path $PublishDir | Out-Null
New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null

# Verify WinDivert signed drivers in-repo before building/publishing (R1)
$divertLib = Resolve-Path "$PSScriptRoot\..\lib\WinDivert" -ErrorAction SilentlyContinue
if (-not $divertLib -or -not (Test-Path (Join-Path $divertLib "WinDivert64.sys"))) {
    throw "WinDivert drivers missing at Valve\lib\WinDivert\ - installer would ship a dead engine."
}

# 2. Publish Valve x64 release
$csproj = Resolve-Path "$PSScriptRoot\..\Valve.csproj"
Write-Host "Publishing Valve from $csproj..."
dotnet publish $csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o $PublishDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }

# 3. Ensure WinDivert signed drivers are alongside the application binary
Write-Host "Copying WinDivert drivers from $divertLib..."
Copy-Item "$divertLib\*.*" -Destination $PublishDir -Force

# 4. Copy manifest
$manifest = Resolve-Path "$PSScriptRoot\..\app.manifest" -ErrorAction SilentlyContinue
if ($manifest -and (Test-Path $manifest)) {
    Copy-Item $manifest -Destination $PublishDir -Force
}

# 5. Compile with Inno Setup if available
if (-not [string]::IsNullOrWhiteSpace($IsccPath) -and (Test-Path $IsccPath)) {
    Write-Host "Compiling setup package using Inno Setup at $IsccPath..."
    & $IsccPath "$PSScriptRoot\setup.iss" "/DAppDir=$PublishDir" "/O$OutputDir"
    if ($LASTEXITCODE -ne 0) { throw "Inno Setup compilation failed with exit code $LASTEXITCODE" }
    Write-Host "Installer compilation completed: $OutputDir\Valve-setup.exe"
} else {
    Write-Warning "Inno Setup compiler (ISCC.exe) was not found on this system. Published files prepared in $PublishDir."
    Write-Warning "To compile installer, install Inno Setup 6 and run: & 'C:\Program Files (x86)\Inno Setup 6\ISCC.exe' '$PSScriptRoot\setup.iss' /DAppDir='$PublishDir' /O='$OutputDir'"
}

Write-Host "Build script complete."
