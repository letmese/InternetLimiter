# Valve Windows Installer & Deployment Guide

This directory contains the production installer build configuration for **Valve**, matching NetLimiter enterprise deployment specifications (Ledger rows 28.08 - 28.11).

## Components
- `setup.iss`: Official Inno Setup 6 compilation script for Valve.
  - Target architecture: `x64 Native`
  - Required privileges: `admin` (UAC elevation)
  - Packages: `Valve.exe`, `app.manifest`, `WinDivert.dll`, `WinDivert64.sys`, and runtime dependencies.
- `build_installer.ps1`: Automated PowerShell build script that publishes Valve self-contained binaries, bundles driver components, and invokes the Inno Setup compiler (`ISCC.exe`).

## Building the Installer

### Prerequisites
1. .NET 8 SDK
2. Inno Setup 6 (`ISCC.exe` in `C:\Program Files (x86)\Inno Setup 6\`)

### Build Command
```powershell
powershell -ExecutionPolicy Bypass -File build_installer.ps1
```
Output artifact: `out\Valve-setup.exe`

## Enterprise Silent Installation Switches

### 1. Silent Install (Row 28.08)
Installs Valve quietly without displaying GUI wizards:
```cmd
Valve-setup.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /DIR="C:\Program Files\Valve" /LOG="valve-install.log"
```

### 2. Silent Install with Custom Target Directory (Row 28.09)
```cmd
Valve-setup.exe /VERYSILENT /DIR="D:\CustomApps\Valve"
```

### 3. In-Place Update over Existing Install (Row 28.11)
Running the installer over an existing installation automatically overwrites updated binaries while preserving existing rules, statistics databases, and custom settings located in `%PROGRAMDATA%\Valve`:
```cmd
Valve-setup.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART
```

### 4. Silent Uninstall with Settings Purge (Row 28.10)
Standard silent uninstall:
```cmd
"C:\Program Files\Valve\unins000.exe" /VERYSILENT /SUPPRESSMSGBOXES /NORESTART
```
Silent uninstall with full configuration & database purge (`NL_CLEAN_SETTINGS=1`):
```cmd
"C:\Program Files\Valve\unins000.exe" /VERYSILENT /SUPPRESSMSGBOXES /NORESTART NL_CLEAN_SETTINGS=1
```
When `NL_CLEAN_SETTINGS=1` is passed, the uninstaller purges:
- `%PROGRAMDATA%\Valve` (all traffic statistics, rules, and settings)
- `%LOCALAPPDATA%\Valve` (user layouts and telemetry logs)
- Registry keys under `HKLM\Software\Valve` and `HKCU\Software\Valve`
