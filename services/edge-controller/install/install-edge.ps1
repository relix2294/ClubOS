<#
.SYNOPSIS
  Установка ClubOS Edge Controller как Windows-службы на сервере клуба. M0.

.DESCRIPTION
  1. Копирует Edge и edge-cli в Program Files.
  2. Создаёт %ProgramData%\ClubOS\Edge с ACL только для SYSTEM и Administrators
     (SQLite edge.db, identity, ключ Edge под DPAPI, токен локального admin API).
  3. Пишет edge.json: адрес Cloud и одноразовый enrollment-токен Edge.
  4. Регистрирует службу ClubOSEdge (LocalSystem, автозапуск, перезапуск при сбое) и источник Event Log.
  5. Открывает в firewall входящий TCP-порт агентов (по умолчанию 7070), только профили Domain и Private.

.EXAMPLE
  .\install-edge.ps1 -CloudUrl https://clubos.example.tj -EnrollmentToken <токен из Admin Web>
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string] $CloudUrl,
    [string] $EnrollmentToken,
    [int] $AgentPort = 7070,
    [string] $SourceDir = $PSScriptRoot,
    [string] $InstallDir = "$env:ProgramFiles\ClubOS\Edge"
)

$ErrorActionPreference = 'Stop'
$ServiceName = 'ClubOSEdge'
$FirewallRule = 'ClubOS Edge (agents)'
$DataDir = Join-Path $env:ProgramData 'ClubOS\Edge'

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Запустите PowerShell от имени администратора.'
}

foreach ($exe in 'ClubOS.EdgeController.exe', 'edge-cli.exe') {
    if (-not (Test-Path (Join-Path $SourceDir $exe))) { throw "Не найден $exe в $SourceDir (см. runbook: сборка пакета)." }
}

if ($CloudUrl -notmatch '^https://' -and $CloudUrl -notmatch '^http://(localhost|127\.0\.0\.1)') {
    Write-Warning "CloudUrl без HTTPS: трафик Edge↔Cloud пойдёт открытым текстом. Для VPS используйте https://."
}

Write-Host "==> Остановка предыдущей версии (если есть)"
if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
    Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
}

Write-Host "==> Копирование файлов в $InstallDir"
New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
Copy-Item -Path (Join-Path $SourceDir '*') -Destination $InstallDir -Recurse -Force -Exclude '*.ps1'

Write-Host "==> Каталог данных $DataDir (ACL: SYSTEM, Administrators)"
New-Item -ItemType Directory -Force -Path $DataDir | Out-Null
& icacls.exe $DataDir /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' | Out-Null

$identityExists = Test-Path (Join-Path $DataDir 'identity.json')
if (-not $identityExists -and [string]::IsNullOrWhiteSpace($EnrollmentToken)) {
    throw 'Edge ещё не зарегистрирован: укажите -EnrollmentToken (Admin Web → Подключение → Токен для Edge).'
}

$edge = @{ CloudUrl = $CloudUrl.TrimEnd('/'); AgentApiPort = $AgentPort }
if (-not $identityExists) { $edge.EnrollmentToken = $EnrollmentToken }
@{ Edge = $edge } | ConvertTo-Json | Set-Content -Path (Join-Path $DataDir 'edge.json') -Encoding UTF8

Write-Host "==> Служба $ServiceName"
$binPath = '"' + (Join-Path $InstallDir 'ClubOS.EdgeController.exe') + '"'
if (-not (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue)) {
    & sc.exe create $ServiceName binPath= $binPath start= auto obj= LocalSystem DisplayName= 'ClubOS Edge Controller' | Out-Null
}
else {
    & sc.exe config $ServiceName binPath= $binPath start= auto | Out-Null
}
& sc.exe description $ServiceName 'ClubOS Edge Controller (M0): локальный контроллер клуба, offline-сессии, связь агентов с Cloud.' | Out-Null
& sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/5000/restart/30000 | Out-Null
if (-not [System.Diagnostics.EventLog]::SourceExists($ServiceName)) {
    [System.Diagnostics.EventLog]::CreateEventSource($ServiceName, 'Application')
}

Write-Host "==> Firewall: входящий TCP $AgentPort (агенты в LAN клуба)"
Get-NetFirewallRule -DisplayName $FirewallRule -ErrorAction SilentlyContinue | Remove-NetFirewallRule
New-NetFirewallRule -DisplayName $FirewallRule -Direction Inbound -Protocol TCP -LocalPort $AgentPort `
    -Action Allow -Profile Domain, Private -Program (Join-Path $InstallDir 'ClubOS.EdgeController.exe') | Out-Null

Write-Host "==> Запуск"
Start-Service -Name $ServiceName

$health = $null
for ($i = 0; $i -lt 20 -and -not $health; $i++) {
    Start-Sleep -Seconds 2
    try { $health = Invoke-RestMethod -Uri "http://localhost:$AgentPort/health" -TimeoutSec 3 } catch { }
}

Get-Service -Name $ServiceName | Format-Table -AutoSize Name, Status, StartType
if ($health) {
    $health | Format-List
    if (-not $health.enrolled) {
        Write-Warning 'Edge ещё не зарегистрирован в Cloud — смотрите Event Viewer (источник ClubOSEdge) и доступность CloudUrl.'
    }
}
else {
    Write-Warning "Edge не ответил на http://localhost:$AgentPort/health — смотрите Event Viewer (источник ClubOSEdge)."
}

Write-Host "Готово. Агенты подключаются к http://<IP этого сервера>:$AgentPort"
Write-Host "Локальное управление (от администратора): & '$InstallDir\edge-cli.exe' status"
