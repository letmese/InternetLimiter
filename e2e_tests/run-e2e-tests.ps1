<#
.SYNOPSIS
    Valve E2E Test Suite Runner Script
.DESCRIPTION
    Builds and executes the 4-tier E2E test harness for Valve NetLimiter-class network control system.
.PARAMETER Tier
    Filter tests by tier: 1, 2, 3, 4, or all (default: all)
.PARAMETER Filter
    Filter test names or suites by substring
.PARAMETER JsonOutput
    Optional path to export structured test results as JSON
.PARAMETER Verbose
    Enable verbose assertion logging
.EXAMPLE
    .\run-e2e-tests.ps1
    .\run-e2e-tests.ps1 -Tier 1
    .\run-e2e-tests.ps1 -Filter "Storage" -JsonOutput "results.json"
#>

[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [string]$Tier = "all",

    [Parameter(Position = 1)]
    [string]$Filter = "",

    [Parameter(Position = 2)]
    [string]$JsonOutput = "",

    [switch]$VerboseOutput
)

$ErrorActionPreference = "Stop"
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$ProjectFile = Join-Path $ScriptDir "Valve.E2ETests.csproj"

Write-Host "================================================================================" -ForegroundColor Cyan
Write-Host "                BUILDING VALVE E2E TEST HARNESS                                 " -ForegroundColor Cyan
Write-Host "================================================================================" -ForegroundColor Cyan

$buildOutput = dotnet build "$ProjectFile" -c Debug
if ($LASTEXITCODE -ne 0) {
    Write-Host "E2E Test harness build FAILED!" -ForegroundColor Red
    exit 1
}

Write-Host "Build succeeded. Executing E2E test runner..." -ForegroundColor Green
Write-Host ""

$runnerArgs = @("--tier", $Tier)

if (-not [string]::IsNullOrWhiteSpace($Filter)) {
    $runnerArgs += @("--filter", $Filter)
}

if (-not [string]::IsNullOrWhiteSpace($JsonOutput)) {
    $runnerArgs += @("--json", $JsonOutput)
}

if ($VerboseOutput) {
    $runnerArgs += @("--verbose")
}

dotnet run --project "$ProjectFile" --no-build -c Debug -- @runnerArgs
$testExitCode = $LASTEXITCODE

exit $testExitCode
