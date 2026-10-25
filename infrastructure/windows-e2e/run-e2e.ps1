<#
.SYNOPSIS
  Сквозная проверка ClubOS на настоящем Windows: Cloud API + Edge (служба) + бездисковый агент (служба).

.DESCRIPTION
  Без симуляторов и моков: Edge и агент ставятся теми же install-edge.ps1 / install-agent.ps1, что в клубе,
  работают как службы Windows (LocalSystem, DPAPI, SChannel), агент — в бездисковом режиме по MAC реальной
  сетевой карты. Cloud API — на локальном PostgreSQL. Проверяется:
    1. регистрация Edge по токену, TLS-сертификат API агентов;
    2. бездисковый ПК: появляется в «Ожидают подтверждения» с MAC этой машины, подтверждение, ПК «Свободен»;
    3. сессия с лимитом 1 минута: Edge завершает её сам, итог 2,00 TJS (120 TJS/час);
    4. команды: список процессов (WindowsRemoteActions) и сообщение;
    5. перезагрузка бездискового ПК (перезапуск службы): та же запись устройства, снова «Свободен»;
    6. mTLS «только с сертификатом» (-RequireClientCertificate -DisableAgentHttp): ПК остаётся на связи;
    7. нет интернета: Cloud остановлен, сессия через edge-cli, итог доходит в Cloud после восстановления;
    8. удаление ПК: после перезагрузки снова ждёт подтверждения.
  Нужны права администратора (GitHub windows-latest: да). Логи — в -WorkDir.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string] $CloudDir,
    [Parameter(Mandatory = $true)] [string] $EdgeDir,
    [Parameter(Mandatory = $true)] [string] $AgentDir,
    [string] $WorkDir = (Join-Path $env:RUNNER_TEMP 'clubos-e2e')
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
New-Item -ItemType Directory -Force -Path $WorkDir | Out-Null

$Cloud = 'http://localhost:5080'
$OwnerEmail = 'owner@demo.clubos.local'
$OwnerPassword = 'e2e-owner-password-1'
$EdgeToken = 'e2e0edge0enrollment0token00000000000000'
$LocationId = 'loc_dushanbe_pilot'
$Zone = 'zone_standard'
$PcName = 'PC-E2E'
$EdgeCli = Join-Path $env:ProgramFiles 'ClubOS\Edge\edge-cli.exe'
$script:CloudProcess = $null

function Step([string] $text) { Write-Host "`n=== $text" -ForegroundColor Cyan }

function Wait-Until([scriptblock] $probe, [string] $what, [int] $seconds = 90) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) {
        try { $value = & $probe } catch { $value = $null }
        if ($value) { return $value }
        Start-Sleep -Seconds 2
    }
    throw "Не дождались за $seconds с: $what"
}

function Api([string] $method, [string] $path, $body = $null) {
    $req = @{ Method = $method; Uri = "$Cloud$path"; Headers = @{ Authorization = "Bearer $script:Token" }; TimeoutSec = 30 }
    if ($null -ne $body) { $req.Body = [Text.Encoding]::UTF8.GetBytes(($body | ConvertTo-Json -Depth 6)); $req.ContentType = 'application/json; charset=utf-8' }
    Invoke-RestMethod @req
}

# Время из JSON (ConvertFrom-Json отдаёт DateTime) → UTC для сравнений.
function Utc($value) { if ($null -eq $value) { [DateTimeOffset]::MinValue } else { [DateTimeOffset]$value } }

# Установщики запускаются в Windows PowerShell 5.1 — как INSTALL.cmd в клубе.
function Invoke-Installer([string] $command) {
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "`$ErrorActionPreference='Stop'; $command"
    if ($LASTEXITCODE -ne 0) { throw "Установщик завершился с кодом $LASTEXITCODE`: $command" }
}

function Start-Cloud {
    $env:ASPNETCORE_ENVIRONMENT = 'Development'
    $env:ASPNETCORE_URLS = $Cloud
    $env:CLUBOS_ConnectionStrings__ClubOs = 'Host=localhost;Port=5432;Database=clubos;Username=clubos;Password=e2e-db-password'
    $env:CLUBOS_Auth__SigningKey = 'e2e-signing-key-0123456789abcdefghijklmnopqrstuvwxyz'
    $env:CLUBOS_Seed__Enabled = 'true'
    $env:CLUBOS_Seed__OwnerEmail = $OwnerEmail
    $env:CLUBOS_Seed__OwnerPassword = $OwnerPassword
    $env:CLUBOS_Seed__EdgeEnrollmentToken = $EdgeToken
    $env:CLUBOS_DevCa__Path = Join-Path $WorkDir 'dev-ca'
    # Пустое значение в env Windows = переменной нет, поэтому 2FA отключается аргументом командной строки.
    $script:CloudProcess = Start-Process -FilePath (Join-Path $CloudDir 'ClubOS.CloudApi.exe') -WorkingDirectory $CloudDir `
        -ArgumentList '--Auth:MfaRequiredRoles=' -PassThru -NoNewWindow `
        -RedirectStandardOutput (Join-Path $WorkDir "cloud-$(Get-Date -Format HHmmss).log") `
        -RedirectStandardError (Join-Path $WorkDir "cloud-err-$(Get-Date -Format HHmmss).log")
    Wait-Until { (Invoke-RestMethod "$Cloud/health/ready" -TimeoutSec 3) -ne $null } 'Cloud API /health/ready' 120 | Out-Null
}

function Device { (Api GET "/api/v1/locations/$LocationId/devices") | Where-Object displayName -eq $PcName | Select-Object -First 1 }

function Wait-Command([string] $commandId) {
    Wait-Until {
        $c = Api GET "/api/v1/commands/$commandId"
        if ($c.state -in 'Succeeded', 'Failed', 'Expired', 'Rejected') { $c }
    } "команда $commandId" 90
}

try {
    Step '1. PostgreSQL'
    $pg = Get-Service -Name 'postgresql*' | Select-Object -First 1
    if (-not $pg) { throw 'На машине нет службы PostgreSQL' }
    Set-Service -Name $pg.Name -StartupType Manual
    Start-Service -Name $pg.Name
    $env:PGPASSWORD = 'root' # пароль postgres на образах GitHub windows-latest
    $psql = Join-Path $env:PGBIN 'psql.exe'
    & $psql -U postgres -h localhost -v ON_ERROR_STOP=1 -c "CREATE USER clubos WITH PASSWORD 'e2e-db-password';" -c 'CREATE DATABASE clubos OWNER clubos;'
    if ($LASTEXITCODE -ne 0) { throw "psql: код $LASTEXITCODE" }
    & $psql -U postgres -h localhost -d clubos -v ON_ERROR_STOP=1 -c 'CREATE EXTENSION IF NOT EXISTS btree_gist;'
    if ($LASTEXITCODE -ne 0) { throw "psql btree_gist: код $LASTEXITCODE" }

    Step '2. Cloud API'
    Start-Cloud
    $login = Invoke-RestMethod -Method Post -Uri "$Cloud/api/v1/auth/login" -ContentType 'application/json' `
        -Body (@{ email = $OwnerEmail; password = $OwnerPassword } | ConvertTo-Json)
    $script:Token = $login.accessToken
    if (-not $script:Token) { throw 'Вход владельца не выдал токен' }

    Step '3. Edge: install-edge.ps1 (служба ClubOSEdge)'
    Invoke-Installer "& '$EdgeDir\install-edge.ps1' -CloudUrl '$Cloud' -EnrollmentToken '$EdgeToken' -SourceDir '$EdgeDir'"
    Wait-Until { (Invoke-RestMethod 'http://localhost:7071/health' -TimeoutSec 3).enrolled } 'Edge зарегистрирован' 90 | Out-Null
    Wait-Until { $r = Invoke-WebRequest 'https://localhost:7443/cash' -SkipCertificateCheck -TimeoutSec 5; $r.StatusCode -eq 200 } `
        'TLS-сертификат Edge (HTTPS 7443)' 90 | Out-Null
    & $EdgeCli status
    if ($LASTEXITCODE -ne 0) { throw "edge-cli status: код $LASTEXITCODE" }

    Step '4. Бездисковый агент: install-agent.ps1 -Diskless (служба ClubOSAgent)'
    $fingerprint = (Api GET '/api/v1/pki/ca').fingerprintSha256
    Invoke-Installer ("& '$AgentDir\install-agent.ps1' -Diskless -EdgeUrl 'https://localhost:7443' -EdgeCaFingerprint '$fingerprint' " +
        "-ShellMode Enforced -TechnicianPin (ConvertTo-SecureString '246810' -AsPlainText -Force) -SourceDir '$AgentDir'")
    $candidate = Wait-Until {
        Api GET "/api/v1/locations/$LocationId/diskless-candidates" | Where-Object { -not $_.simulated } | Select-Object -First 1
    } 'бездисковый ПК в «Ожидают подтверждения»' 120
    $macs = Get-NetAdapter | Where-Object Status -eq 'Up' | ForEach-Object { ($_.MacAddress -replace '-', ':').ToUpperInvariant() }
    Write-Host "Кандидат: MAC $($candidate.mac), hostname $($candidate.hostname); MAC этой машины: $($macs -join ', ')"
    if ($candidate.mac.ToUpperInvariant() -notin $macs) { throw "MAC кандидата $($candidate.mac) не принадлежит сетевым картам этой машины" }

    Step '5. Подтверждение ПК и выход на связь'
    Api POST "/api/v1/diskless-candidates/$($candidate.candidateId)/approve" @{ displayName = $PcName; zoneId = $Zone } | Out-Null
    $device = Wait-Until { $d = Device; if ($d.status -eq 'Idle') { $d } } "$PcName «Свободен»" 120
    $deviceId = $device.deviceId
    Write-Host "Устройство $deviceId, Windows: $($device.inventory.windowsVersion), CPU: $($device.inventory.cpu)"
    if (-not $device.inventory.windowsVersion) { throw 'Нет инвентаризации Windows' }

    Step '6. Сессия с лимитом 1 минута: Edge завершает сам, итог 2,00 TJS'
    $session = Api POST "/api/v1/devices/$deviceId/sessions" @{ durationMinutes = 1 }
    Wait-Until { (Device).status -eq 'Active' } 'ПК в сессии' 60 | Out-Null
    $ended = Wait-Until {
        Api GET "/api/v1/devices/$deviceId/sessions" | Where-Object { $_.sessionId -eq $session.sessionId -and $_.state -eq 'Ended' }
    } 'сессия завершена по лимиту' 150
    Write-Host "Итог: $($ended.totalMinorUnits), причина: $($ended.endReason)"
    if ($ended.totalMinorUnits -ne 200) { throw "Ожидался итог 200 (2,00 TJS), получено $($ended.totalMinorUnits)" }
    if ($ended.endReason -ne 'timeLimit') { throw "Ожидалась причина timeLimit, получено $($ended.endReason)" }

    Step '7. Команды: список процессов (WindowsRemoteActions) и сообщение'
    $ps = Api POST "/api/v1/devices/$deviceId/commands" @{ commandType = 'ListProcesses'; ttlSeconds = 60 }
    $psDone = Wait-Command $ps.commandId
    Write-Host "ListProcesses: $($psDone.state) $($psDone.error); процессов: $(@($psDone.result.processes).Count)"
    if ($psDone.state -ne 'Succeeded') { throw "ListProcesses: $($psDone.state) $($psDone.error)" }
    $msg = Api POST "/api/v1/devices/$deviceId/commands" @{ commandType = 'ShowMessage'; title = 'E2E'; message = 'Проверка'; ttlSeconds = 60 }
    $msgDone = Wait-Command $msg.commandId
    # На сервере CI нет вошедшего пользователя: честный отказ «SessionHost не подключён» тоже результат агента.
    Write-Host "ShowMessage: $($msgDone.state) $($msgDone.error)"
    if ($msgDone.state -notin 'Succeeded', 'Failed') { throw "ShowMessage: $($msgDone.state)" }

    Step '8. Перезагрузка бездискового ПК (новый ключ, та же запись устройства)'
    $since = [DateTimeOffset]::UtcNow
    Restart-Service -Name ClubOSAgent
    Wait-Until { $d = Device; $d.deviceId -eq $deviceId -and $d.status -eq 'Idle' -and (Utc $d.lastHeartbeatUtc) -gt $since.AddSeconds(3) } `
        'ПК снова на связи после перезагрузки' 120 | Out-Null
    $pending = @(Api GET "/api/v1/locations/$LocationId/diskless-candidates" | Where-Object { -not $_.simulated })
    if ($pending.Count -ne 0) { throw 'Подтверждённый ПК после перезагрузки снова ждёт подтверждения' }

    Step '9. mTLS «только с сертификатом»: -DisableAgentHttp -RequireClientCertificate'
    Invoke-Installer "& '$EdgeDir\install-edge.ps1' -CloudUrl '$Cloud' -SourceDir '$EdgeDir' -DisableAgentHttp -RequireClientCertificate"
    $since = [DateTimeOffset]::UtcNow
    Wait-Until { $d = Device; $d.status -eq 'Idle' -and (Utc $d.lastHeartbeatUtc) -gt $since.AddSeconds(5) } `
        'heartbeat по mTLS после перезапуска Edge' 120 | Out-Null
    $httpClosed = $false
    try { Invoke-WebRequest 'http://localhost:7070/health' -TimeoutSec 3 | Out-Null } catch { $httpClosed = $true }
    if (-not $httpClosed) { throw 'HTTP 7070 должен быть закрыт' }
    $status = & $EdgeCli status | Out-String
    Write-Host $status
    if ($status -notmatch '"requireAgentClientCertificate":\s*true') { throw 'edge-cli status: режим mTLS не включён' }

    Step '10. Нет интернета: Cloud остановлен, сессия через edge-cli'
    Stop-Process -Id $script:CloudProcess.Id -Force
    Start-Sleep -Seconds 3
    $local = & $EdgeCli start $PcName --minutes 1 --actor e2e | Out-String
    Write-Host $local
    if ($LASTEXITCODE -ne 0) { throw "edge-cli start: код $LASTEXITCODE" }
    $offlineSessionId = ([regex]'"sessionId":\s*"([^"]+)"').Match($local).Groups[1].Value
    if (-not $offlineSessionId) { throw 'edge-cli start не вернул sessionId' }
    Wait-Until { (& $EdgeCli sessions --active | Out-String) -notmatch $offlineSessionId } 'Edge завершил сессию без Cloud' 150 | Out-Null
    Start-Cloud
    $synced = Wait-Until {
        Api GET "/api/v1/devices/$deviceId/sessions" | Where-Object { $_.sessionId -eq $offlineSessionId -and $_.state -eq 'Ended' }
    } 'офлайн-сессия дошла до Cloud' 120
    if ($synced.totalMinorUnits -ne 200) { throw "Офлайн-итог $($synced.totalMinorUnits), ожидалось 200" }

    Step '11. Удаление ПК: после перезагрузки снова ждёт подтверждения'
    Api POST "/api/v1/devices/$deviceId/revoke" | Out-Null
    Restart-Service -Name ClubOSAgent
    Wait-Until {
        Api GET "/api/v1/locations/$LocationId/diskless-candidates" | Where-Object { -not $_.simulated -and $_.mac -eq $candidate.mac }
    } 'удалённый ПК снова в «Ожидают подтверждения»' 150 | Out-Null

    Write-Host "`nВСЕ ПРОВЕРКИ НА WINDOWS ПРОЙДЕНЫ" -ForegroundColor Green
}
catch {
    Write-Host "`nОШИБКА: $_" -ForegroundColor Red
    Write-Host '--- Event Log ClubOSEdge / ClubOSAgent (последние 60) ---'
    Get-WinEvent -FilterHashtable @{ LogName = 'Application'; ProviderName = 'ClubOSEdge', 'ClubOSAgent' } -MaxEvents 60 -ErrorAction SilentlyContinue |
        Sort-Object TimeCreated | ForEach-Object { "$($_.TimeCreated.ToString('HH:mm:ss')) $($_.ProviderName) $($_.LevelDisplayName): $($_.Message)" }
    throw
}
finally {
    if ($script:CloudProcess -and -not $script:CloudProcess.HasExited) { Stop-Process -Id $script:CloudProcess.Id -Force }
}
