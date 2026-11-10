#Requires -RunAsAdministrator
<#  Installs/updates the LedBarCam Windows service on this PC.
    - Binaries:  C:\Program Files\LedBarCam\  (no secrets)
    - Secret:    C:\ProgramData\LedBarCam\mqtt.pwd  (DPAPI-encrypted; folder ACL: SYSTEM, Administrators,
                 and this service's own SID NT SERVICE\LedBarCam (read) — not LocalService in general)
    - Runs as:   NT AUTHORITY\LocalService (no admin rights) with service SID type 'unrestricted', so the
                 process token carries the per-service SID used in the ACL.
                 (A NT SERVICE\LedBarCam virtual account was rejected with error 1057 on an Intune-managed test PC.)
    Re-running updates the binaries and keeps the stored password and an existing appsettings.json.                              #>
$ErrorActionPreference = 'Stop'
$Name = 'LedBarCam'
$Src  = Join-Path $PSScriptRoot 'bin'
$Dir  = Join-Path $env:ProgramFiles $Name
$Data = Join-Path $env:ProgramData $Name
$Exe  = Join-Path $Dir 'LedBarCam.exe'

function Invoke-Sc { & sc.exe @args | Out-Host; if ($LASTEXITCODE -ne 0) { throw "sc.exe $($args -join ' ') failed ($LASTEXITCODE)" } }

$svc = Get-Service $Name -ErrorAction SilentlyContinue
if ($svc -and $svc.Status -ne 'Stopped') { Write-Host "Stopping $Name"; Stop-Service $Name -Force; Start-Sleep 2 }

Write-Host "Copying binaries to $Dir"
New-Item -ItemType Directory -Force $Dir | Out-Null
Copy-Item (Join-Path $Src 'LedBarCam.exe') $Dir -Force
if (-not (Test-Path (Join-Path $Dir 'appsettings.json'))) { Copy-Item (Join-Path $Src 'appsettings.json') $Dir }
else { Write-Host "Keeping existing $Dir\appsettings.json" }

if (-not [Diagnostics.EventLog]::SourceExists($Name)) { New-EventLog -LogName Application -Source $Name }

if (-not $svc) {
    Write-Host "Creating service $Name"
    Invoke-Sc create $Name binPath= "`"$Exe`"" start= delayed-auto DisplayName= "LED Bar Webcam Monitor"
}
# Least-privilege built-in account. Windows PowerShell 5.1 drops an empty "" argument to native commands
# (sc.exe then prints usage, error 1639), so hand the exact command line to cmd.exe.
Invoke-Sc sidtype $Name unrestricted
& cmd.exe /c "sc.exe config $Name obj= `"NT AUTHORITY\LocalService`" password= `"`"" | Out-Host
$acct = (Get-CimInstance -ClassName Win32_Service -Filter "Name='$Name'").StartName
if ($acct -notmatch '^NT AUTHORITY\\LocalService$') { throw "Service account is '$acct', expected 'NT AUTHORITY\LocalService'" }
Write-Host "Service account: $acct"
Invoke-Sc description $Name "Publishes webcam in-use state (device power state) to MQTT for Home Assistant."
Invoke-Sc failure $Name reset= 86400 actions= restart/5000/restart/10000/restart/60000

Write-Host "Securing $Data"
New-Item -ItemType Directory -Force $Data | Out-Null
$svcSid = ((& sc.exe showsid $Name) -match 'SERVICE SID:' -replace '.*SERVICE SID:\s*', '').Trim()
if ($svcSid -notmatch '^S-1-5-80-') { throw "Could not determine service SID ($svcSid)" }
& icacls.exe $Data /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' "*${svcSid}:(OI)(CI)RX" | Out-Host
if ($LASTEXITCODE -ne 0) { throw "icacls failed ($LASTEXITCODE)" }

if (-not (Test-Path (Join-Path $Data 'mqtt.pwd'))) {
    Write-Host "`nEnter the MQTT password for the login set in appsettings.json:"
    & $Exe --set-password
    if ($LASTEXITCODE -ne 0) { throw "Password not saved" }
} else { Write-Host "Keeping existing stored password ($Data\mqtt.pwd)" }

Start-Service $Name
Start-Sleep 6
Get-Service $Name | Format-Table Name, Status, StartType -AutoSize
Get-WinEvent -FilterHashtable @{ LogName = 'Application'; ProviderName = $Name; StartTime = (Get-Date).AddMinutes(-2) } -ErrorAction SilentlyContinue |
    Sort-Object TimeCreated | Format-Table TimeCreated, LevelDisplayName, Message -AutoSize -Wrap
