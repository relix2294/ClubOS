<#
.SYNOPSIS
  Установка ClubOS Windows Agent (служба + AgentSessionHost + Player Shell) на ПК клуба.

.DESCRIPTION
  1. Копирует файлы агента в Program Files.
  2. Создаёт %ProgramData%\ClubOS\Agent с ACL только для SYSTEM и Administrators
     (там будут identity, защищённый DPAPI ключ устройства и журнал команд).
  3. Записывает agent.json (URL Edge + одноразовый enrollment-токен).
  4. Регистрирует службу ClubOSAgent (LocalSystem, автозапуск, перезапуск при сбое).
  5. Регистрирует задачу планировщика: AgentSessionHost запускается при входе любого пользователя
     (UI рисуется только в сессии пользователя, не из Session 0).
  6. Player Shell (-ShellMode): Off — как в M0; Hud — индикатор сессии; Enforced — свободный ПК закрыт
     экраном клуба. В режимах Hud/Enforced служба перезапускает закрытый SessionHost сама.
     PIN техника (-TechnicianPin) хранится только хэшем в agent.json (каталог с ACL SYSTEM/Administrators).

  НЕ меняет Winlogon Shell, GPO или политики (ТЗ §3.3, §25.2.5). Ctrl+Alt+Del работает всегда.

.EXAMPLE
  .\install-agent.ps1 -EdgeUrl http://192.168.1.10:7070 -EnrollmentToken <токен из Admin Web>

.EXAMPLE
  # ПК удалили в Admin Web (или переносят в другой клуб): новая регистрация с новым токеном.
  .\install-agent.ps1 -EdgeUrl http://192.168.1.10:7070 -EnrollmentToken <новый токен> -ReEnroll

.EXAMPLE
  .\install-agent.ps1 -EdgeUrl http://192.168.1.10:7070 -ShellMode Enforced -TechnicianPin (Read-Host -AsSecureString 'PIN техника')
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string] $EdgeUrl,
    [string] $EnrollmentToken,
    [ValidateSet('Off', 'Hud', 'Enforced')] [string] $ShellMode,
    [SecureString] $TechnicianPin,
    [switch] $ReEnroll,
    [string] $SourceDir = $PSScriptRoot,
    [string] $InstallDir = "$env:ProgramFiles\ClubOS\Agent"
)

$ErrorActionPreference = 'Stop'
$ServiceName = 'ClubOSAgent'
$TaskName = 'ClubOS Agent SessionHost'
$DataDir = Join-Path $env:ProgramData 'ClubOS\Agent'

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Запустите PowerShell от имени администратора.'
}

foreach ($exe in 'ClubOS.Agent.Service.exe', 'ClubOS.Agent.SessionHost.exe') {
    if (-not (Test-Path (Join-Path $SourceDir $exe))) { throw "Не найден $exe в $SourceDir (сначала dotnet publish, см. runbook)." }
}

Write-Host "==> Остановка предыдущей версии (если есть)"
if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
    Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
}
Get-Process -Name 'ClubOS.Agent.SessionHost' -ErrorAction SilentlyContinue | Stop-Process -Force

Write-Host "==> Копирование файлов в $InstallDir"
New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
Copy-Item -Path (Join-Path $SourceDir '*') -Destination $InstallDir -Recurse -Force -Exclude '*.ps1'

Write-Host "==> Каталог данных $DataDir (ACL: SYSTEM, Administrators)"
New-Item -ItemType Directory -Force -Path $DataDir | Out-Null
& icacls.exe $DataDir /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' | Out-Null

if ($ReEnroll) {
    if ([string]::IsNullOrWhiteSpace($EnrollmentToken)) { throw '-ReEnroll требует -EnrollmentToken (Admin Web → Подключение).' }
    Write-Host "==> Повторная регистрация: удаляю прежнюю identity устройства"
    foreach ($f in 'identity.json', 'device.key', 'executed-commands.json', 'shell-state.json') {
        Remove-Item -Force -ErrorAction SilentlyContinue (Join-Path $DataDir $f)
    }
}

$identityExists = Test-Path (Join-Path $DataDir 'identity.json')
if (-not $identityExists -and [string]::IsNullOrWhiteSpace($EnrollmentToken)) {
    throw 'Устройство ещё не зарегистрировано: укажите -EnrollmentToken (Admin Web → Подключение).'
}

$configPath = Join-Path $DataDir 'agent.json'
$previousShell = $null
if (Test-Path $configPath) {
    try { $previousShell = (Get-Content $configPath -Raw | ConvertFrom-Json).Agent.Shell } catch { $previousShell = $null }
}

# Player Shell: при переустановке без параметров сохраняются прежние режим и хэш PIN.
$shell = @{ Mode = 'Off' }
if ($previousShell) {
    if ($previousShell.Mode) { $shell.Mode = [string]$previousShell.Mode }
    if ($previousShell.TechnicianPinHash) { $shell.TechnicianPinHash = [string]$previousShell.TechnicianPinHash }
}
if ($ShellMode) { $shell.Mode = $ShellMode }
if ($TechnicianPin) {
    $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($TechnicianPin)
    try { $plainPin = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr) }
    finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr) }
    $hash = $plainPin | & (Join-Path $InstallDir 'ClubOS.Agent.Service.exe') hash-pin
    $plainPin = $null
    if ($LASTEXITCODE -ne 0 -or -not $hash) { throw 'PIN техника не принят: нужно 6–12 цифр.' }
    $shell.TechnicianPinHash = ([string]$hash).Trim()
}
if ($shell.Mode -eq 'Enforced' -and -not $shell.TechnicianPinHash) {
    Write-Warning 'Режим Enforced без PIN техника: без сессии экран клуба снимается только остановкой службы ClubOSAgent (права администратора) или стартом сессии из Admin Web.'
}

$agent = @{ EdgeUrl = $EdgeUrl; Shell = $shell }
if (-not $identityExists) { $agent.EnrollmentToken = $EnrollmentToken }
@{ Agent = $agent } | ConvertTo-Json -Depth 5 | Set-Content -Path $configPath -Encoding UTF8

Write-Host "==> Служба $ServiceName"
$binPath = '"' + (Join-Path $InstallDir 'ClubOS.Agent.Service.exe') + '"'
if (-not (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue)) {
    & sc.exe create $ServiceName binPath= $binPath start= auto obj= LocalSystem DisplayName= 'ClubOS Agent' | Out-Null
}
else {
    & sc.exe config $ServiceName binPath= $binPath start= auto | Out-Null
}
& sc.exe description $ServiceName 'ClubOS Windows Agent: heartbeat, inventory, commands, Player Shell.' | Out-Null
& sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/5000/restart/30000 | Out-Null

if (-not [System.Diagnostics.EventLog]::SourceExists($ServiceName)) {
    [System.Diagnostics.EventLog]::CreateEventSource($ServiceName, 'Application')
}

Write-Host "==> Задача планировщика «$TaskName» (запуск SessionHost при входе пользователя)"
$action = New-ScheduledTaskAction -Execute (Join-Path $InstallDir 'ClubOS.Agent.SessionHost.exe')
$trigger = New-ScheduledTaskTrigger -AtLogOn
$taskPrincipal = New-ScheduledTaskPrincipal -GroupId 'S-1-5-32-545' -RunLevel Limited   # BUILTIN\Users
$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([TimeSpan]::Zero) -MultipleInstances IgnoreNew
Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger -Principal $taskPrincipal -Settings $settings -Force | Out-Null

Write-Host "==> Запуск"
Start-Service -Name $ServiceName
Start-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue

Get-Service -Name $ServiceName | Format-Table -AutoSize Name, Status, StartType
Write-Host "Player Shell: $($shell.Mode)$(if ($shell.TechnicianPinHash) { ', PIN техника задан' } else { '' })"
Write-Host "Готово. Проверка: Admin Web → Устройства; журнал: Event Viewer → Windows Logs → Application (источник ClubOSAgent)."
