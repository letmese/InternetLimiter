$publishDir = "C:\Users\xbxga\WorkBuddy AI\2026-10-27-19-20-32\Valve\publish"
$destApp = "C:\Users\xbxga\Downloads\Valve_App"
$destDownloads = "C:\Users\xbxga\Downloads"

# Stop running processes if any
Get-Process -Name "Valve", "InternetLimiter" -ErrorAction SilentlyContinue | ForEach-Object {
    Write-Output "Stopping running process $($_.ProcessName) (PID $($_.Id))..."
    Stop-Process -Id $_.Id -Force
}

Start-Sleep -Milliseconds 500

if (-not (Test-Path $destApp)) {
    New-Item -ItemType Directory -Path $destApp -Force | Out-Null
}

Copy-Item -Path "$publishDir\*" -Destination $destApp -Recurse -Force
if (Test-Path "$publishDir\InternetLimiter.exe") {
    Copy-Item -Path "$publishDir\InternetLimiter.exe" -Destination "$destDownloads\InternetLimiter.exe" -Force
    Copy-Item -Path "$publishDir\InternetLimiter.exe" -Destination "$destDownloads\Valve.exe" -Force
    Copy-Item -Path "$publishDir\InternetLimiter.exe" -Destination "$destApp\Valve.exe" -Force
}

Write-Output "Copied binaries to Downloads and Valve_App"

& "C:\Users\xbxga\WorkBuddy AI\2026-10-27-19-20-32\Valve\scratch\bundle_source.ps1"

Write-Output "Deployment verification:"
Get-Item "$publishDir\InternetLimiter.exe", "$destApp\InternetLimiter.exe", "$destDownloads\InternetLimiter.exe", "$destDownloads\Valve_Complete_Source_Code.txt" | 
    ForEach-Object { Write-Output "$($_.FullName) | Size: $($_.Length) bytes | LastWriteTime: $($_.LastWriteTime)" }
