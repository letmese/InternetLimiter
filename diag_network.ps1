Write-Output "=== ACTIVE NETWORK ADAPTERS ==="
Get-NetAdapter | Select-Object Name, InterfaceDescription, Status, LinkSpeed

Write-Output "`n=== PROXY SETTINGS ==="
netsh winhttp show proxy
Get-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Internet Settings' | 
    Select-Object ProxyEnable, ProxyServer, AutoDetect

Write-Output "`n=== SPEED TEST / INTERNET PING ==="
Test-NetConnection -ComputerName 8.8.8.8 -InformationLevel Detailed

Write-Output "`n=== NETLIMITER 4 DRIVERS IN KERNEL ==="
Get-SystemDriver | Where-Object { $_.Name -like "*nl*" -or $_.Name -like "*locktime*" -or $_.Name -like "*divert*" }
