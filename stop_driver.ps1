Write-Output "=== STOPPING WINDIVERT DRIVER ==="
sc.exe stop WinDivert
sc.exe query WinDivert

Write-Output "`n=== CHECKING NLSVC ==="
Get-WmiObject Win32_Service | Where-Object { $_.Name -match "nlsvc|netlimiter" } | Select-Object Name, DisplayName, State, PathName

Write-Output "`n=== CHECKING WFP FILTERS (NETSH WFP) ==="
netsh wfp show state file="C:\Users\xbxga\WorkBuddy AI\2026-10-27-19-20-32\Valve\wfpstate.xml"
if (Test-Path "C:\Users\xbxga\WorkBuddy AI\2026-10-27-19-20-32\Valve\wfpstate.xml") {
    $xmlSize = (Get-Item "C:\Users\xbxga\WorkBuddy AI\2026-10-27-19-20-32\Valve\wfpstate.xml").Length
    Write-Output "WFP State XML created, size: $xmlSize bytes"
}
