$ErrorActionPreference = "SilentlyContinue"
Get-Process AShareRadar.Desktop | Stop-Process -Force
Get-Process AShareRadar.ServiceHost | Stop-Process -Force
Write-Host "AShareRadar stopped."
