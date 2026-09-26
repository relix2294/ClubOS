<#
.SYNOPSIS
  Удаление ClubOS Edge Controller. С -RemoveData удаляет также edge.db, identity и ключ Edge:
  несинхронизированные события будут потеряны, для повторной установки нужен новый enrollment-токен.
#>
[CmdletBinding()]
param(
    [string] $InstallDir = "$env:ProgramFiles\ClubOS\Edge",
    [switch] $RemoveData
)

$ErrorActionPreference = 'Stop'
$ServiceName = 'ClubOSEdge'

if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
    Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
    & sc.exe delete $ServiceName | Out-Null
}

Get-NetFirewallRule -DisplayName 'ClubOS Edge (agents)' -ErrorAction SilentlyContinue | Remove-NetFirewallRule
if (Test-Path $InstallDir) { Remove-Item -Recurse -Force $InstallDir }

if ($RemoveData) {
    $data = Join-Path $env:ProgramData 'ClubOS\Edge'
    if (Test-Path $data) { Remove-Item -Recurse -Force $data }
}

Write-Host 'ClubOS Edge удалён.'
