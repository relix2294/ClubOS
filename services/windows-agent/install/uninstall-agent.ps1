<#
.SYNOPSIS
  Удаление ClubOS Windows Agent. С -RemoveData удаляет также identity и ключ устройства
  (после этого для повторной установки нужен новый enrollment-токен).
#>
[CmdletBinding()]
param(
    [string] $InstallDir = "$env:ProgramFiles\ClubOS\Agent",
    [switch] $RemoveData
)

$ErrorActionPreference = 'Stop'
$ServiceName = 'ClubOSAgent'
$TaskName = 'ClubOS Agent SessionHost'

if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
    Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
    & sc.exe delete $ServiceName | Out-Null
}

Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false -ErrorAction SilentlyContinue
Get-Process -Name 'ClubOS.Agent.SessionHost' -ErrorAction SilentlyContinue | Stop-Process -Force

if (Test-Path $InstallDir) { Remove-Item -Recurse -Force $InstallDir }
if ($RemoveData) {
    $data = Join-Path $env:ProgramData 'ClubOS\Agent'
    if (Test-Path $data) { Remove-Item -Recurse -Force $data }
}

Write-Host 'ClubOS Agent удалён.'
