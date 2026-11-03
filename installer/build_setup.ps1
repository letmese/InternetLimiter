param(
    [string]$PublishDir = "$PSScriptRoot\..\publish",
    [string]$InstallerDir = "$PSScriptRoot\InternetLimiter.Installer",
    [string]$OutputDir = "$PSScriptRoot\out",
    [string]$DownloadsDir = "C:\Users\xbxga\Downloads"
)
$ErrorActionPreference = "Stop"

Write-Host "=========================================================="
Write-Host "   INTERNET LIMITER - STANDALONE INSTALLER BUILDER        "
Write-Host "=========================================================="

# 1. Ensure Directories exist
New-Item -ItemType Directory -Force -Path $PublishDir | Out-Null
New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null

# 2. Publish Internet Limiter Core Engine
$valveCsproj = Resolve-Path "$PSScriptRoot\..\Valve.csproj"
Write-Host "[1/5] Publishing Internet Limiter Core (win-x64)..."
dotnet publish $valveCsproj -c Release -r win-x64 --self-contained false -o $PublishDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish Valve.csproj failed!" }

# 3. Ensure WinDivert Drivers & Assets are present in publish
Write-Host "[2/5] Staging WinDivert drivers & Assets..."
$divertLib = Resolve-Path "$PSScriptRoot\..\lib\WinDivert"
Copy-Item "$divertLib\*.*" -Destination $PublishDir -Force
$assetsSource = Resolve-Path "$PSScriptRoot\..\Assets"
$assetsDest = Join-Path $PublishDir "Assets"
New-Item -ItemType Directory -Force -Path $assetsDest | Out-Null
Copy-Item "$assetsSource\*.*" -Destination $assetsDest -Force

$manifestSource = Resolve-Path "$PSScriptRoot\..\app.manifest"
Copy-Item $manifestSource -Destination $PublishDir -Force

# Provide Valve.exe copy alongside InternetLimiter.exe for complete compatibility
if (Test-Path "$PublishDir\InternetLimiter.exe") {
    Copy-Item "$PublishDir\InternetLimiter.exe" "$PublishDir\Valve.exe" -Force
}

# 4. Pack Payload.zip for embedding into installer
Write-Host "[3/5] Compressing application payload into Payload.zip..."
Add-Type -AssemblyName System.IO.Compression.FileSystem
$payloadZip = Join-Path $InstallerDir "Payload.zip"
if (Test-Path $payloadZip) { Remove-Item $payloadZip -Force }
[System.IO.Compression.ZipFile]::CreateFromDirectory($PublishDir, $payloadZip, [System.IO.Compression.CompressionLevel]::Optimal, $false)

# 5. Build and Publish Self-Contained Single-File Installer
Write-Host "[4/5] Compiling single-file native installer (InternetLimiter_Setup.exe)..."
$installerCsproj = Join-Path $InstallerDir "InternetLimiter.Installer.csproj"
dotnet publish $installerCsproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $OutputDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish InternetLimiter.Installer.csproj failed!" }

# 6. Deploy to Downloads
Write-Host "[5/5] Deploying InternetLimiter_Setup.exe to Downloads..."
$installerExe = Join-Path $OutputDir "InternetLimiter_Setup.exe"
if (-not (Test-Path $installerExe)) {
    throw "Expected installer binary not found at $installerExe"
}

Copy-Item $installerExe -Destination (Join-Path $DownloadsDir "InternetLimiter_Setup.exe") -Force

Write-Host "----------------------------------------------------------"
Write-Host " Installer Built & Deployed Successfully:"
Write-Host "   -> $installerExe"
Write-Host "   -> $DownloadsDir\InternetLimiter_Setup.exe"
Write-Host "----------------------------------------------------------"
