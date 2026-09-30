<#
.SYNOPSIS
  Мастер установки ClubOS Agent на игровой ПК (запускается из INSTALL.cmd).
  Задаёт вопросы и вызывает install-agent.ps1. Все ответы можно найти в Admin Web → «Подключение».
#>
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot

function Ask([string] $prompt, [string] $default = '') {
    $suffix = if ($default) { " [$default]" } else { '' }
    $value = Read-Host "$prompt$suffix"
    if ([string]::IsNullOrWhiteSpace($value)) { return $default }
    return $value.Trim()
}

try {
    # Файлы из скачанного zip помечены «из интернета» — снимаем пометку, иначе Windows блокирует скрипты.
    Get-ChildItem -Path $here -Recurse -File | Unblock-File

    Write-Host ''
    Write-Host '=== ClubOS: установка агента на игровой ПК ===' -ForegroundColor Cyan
    Write-Host 'Ответы — в Admin Web → «Подключение». Enter — значение в [скобках].'
    Write-Host ''

    $previous = Join-Path $env:ProgramData 'ClubOS\Agent\agent.json'
    $reinstall = Test-Path $previous

    $edgeUrl = ''
    while ($edgeUrl -notmatch '^https?://') {
        $edgeUrl = Ask 'Адрес Edge (например https://192.168.1.10:7443)'
    }

    $fingerprint = ''
    if ($edgeUrl.StartsWith('https://') -and -not $reinstall) {
        while ($fingerprint.Length -lt 64) {
            $fingerprint = (Ask 'Отпечаток CA (64 символа)') -replace '[^0-9A-Fa-f]', ''
        }
    }

    $token = Ask 'Одноразовый токен ПК (пусто — если ПК уже зарегистрирован)'

    Write-Host ''
    Write-Host 'Экран клуба (Player Shell):'
    Write-Host '  1 — выключен (только сообщения и тестовая блокировка)'
    Write-Host '  2 — индикатор сессии в углу (свободный ПК не закрыт)'
    Write-Host '  3 — полный: свободный ПК закрыт экраном клуба (рекомендуется)'
    $mode = @{ '1' = 'Off'; '2' = 'Hud'; '3' = 'Enforced' }[(Ask 'Выберите' '3')]
    if (-not $mode) { $mode = 'Enforced' }

    $install = @{ EdgeUrl = $edgeUrl; ShellMode = $mode }
    if ($fingerprint) { $install.EdgeCaFingerprint = $fingerprint }
    if ($token) { $install.EnrollmentToken = $token }
    if ($mode -ne 'Off') {
        Write-Host 'PIN техника: Ctrl+Shift+F12 на экране клуба → режим обслуживания на 15 минут.'
        $pin = Read-Host -AsSecureString 'PIN техника, 6–12 цифр (пусто — оставить прежний)'
        if ($pin.Length -gt 0) { $install.TechnicianPin = $pin }
    }

    & (Join-Path $here 'install-agent.ps1') @install
    Write-Host ''
    Write-Host 'Готово. Через 10–20 секунд ПК появится в Admin Web → «Устройства».' -ForegroundColor Green
}
catch {
    Write-Host ''
    Write-Host "Ошибка: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host 'Исправьте и запустите INSTALL.cmd снова. Подробности: docs/runbooks/windows-agent-install.md'
    exit 1
}
