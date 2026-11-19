# BtAudioGuard diagnostic. Normal PowerShell, no admin needed.
# Run once when audio WORKS and once when it is BROKEN. Paste both outputs back.

$ErrorActionPreference = 'SilentlyContinue'
Write-Host "===== $(Get-Date -Format o) =====" -ForegroundColor Cyan

$all = Get-PnpDevice   # includes non-present devices (Status = Unknown)

Write-Host "`n-- Bose audio endpoints (render + capture, all states) --" -ForegroundColor Yellow
$all | Where-Object { $_.Class -eq 'AudioEndpoint' -and $_.FriendlyName -like '*Bose*' } |
    Sort-Object FriendlyName |
    Format-Table Status, FriendlyName, InstanceId -AutoSize -Wrap

# Bluetooth instance IDs carry the device address (Classic and LE may differ).
# Collect every address from nodes named Bose, then list every node with that address:
# this catches the LE Audio service nodes (generic names like "Bluetooth LE Generic Attribute Service").
$macs = $all | Where-Object { $_.FriendlyName -like '*Bose*' } |
    ForEach-Object { [regex]::Matches($_.InstanceId, '(?i)DEV_([0-9A-F]{12})') } |
    ForEach-Object { $_.Groups[1].Value.ToUpper() } | Sort-Object -Unique
Write-Host "`n-- Bose addresses: $($macs -join ', ') --" -ForegroundColor Yellow

$all | Where-Object { $id = $_.InstanceId.ToUpper(); $macs | Where-Object { $id.Contains($_) } } |
    Sort-Object InstanceId |
    Format-Table Status, Class, FriendlyName, InstanceId -AutoSize -Wrap

Write-Host "`n-- Is the USB cable audio device present? --" -ForegroundColor Yellow
$all | Where-Object { $_.FriendlyName -like '*Bose*' -and $_.InstanceId -like 'USB*' } |
    Format-Table Status, Class, FriendlyName -AutoSize
