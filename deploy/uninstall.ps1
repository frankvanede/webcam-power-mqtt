#Requires -RunAsAdministrator
# Removes the LedBarCam service, binaries, stored password and event-log source.
$ErrorActionPreference = 'Continue'
$Name = 'LedBarCam'
if (Get-Service $Name -ErrorAction SilentlyContinue) { Stop-Service $Name -Force; & sc.exe delete $Name | Out-Host }
Remove-Item (Join-Path $env:ProgramFiles $Name) -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item (Join-Path $env:ProgramData  $Name) -Recurse -Force -ErrorAction SilentlyContinue
if ([Diagnostics.EventLog]::SourceExists($Name)) { Remove-EventLog -Source $Name }
Write-Host "LedBarCam removed. In Home Assistant the entity becomes unavailable; delete the service's MQTT device to remove it."
