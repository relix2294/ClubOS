<#
.SYNOPSIS
  Мастер установки ClubOS Edge на сервер клуба (запускается из INSTALL.cmd).
  Задаёт вопросы и вызывает install-edge.ps1. Токен — Admin Web → «Подключение» → «Токен для Edge Controller».
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
    Write-Host '=== ClubOS: установка Edge на сервер клуба ===' -ForegroundColor Cyan
    Write-Host 'Edge — посредник между ПК клуба и облаком; работает и без интернета. Enter — значение в [скобках].'
    Write-Host ''

    $cloudUrl = ''
    while ($cloudUrl -notmatch '^https?://') {
        $cloudUrl = Ask 'Адрес облака ClubOS (как в браузере, например https://clubos.example.tj)'
    }

    $identity = Join-Path $env:ProgramData 'ClubOS\Edge\identity.json'
    $token = ''
    if (Test-Path $identity) {
        Write-Host 'Edge уже зарегистрирован — токен не нужен (обновление).'
    }
    else {
        while ($token.Length -lt 20) { $token = Ask 'Одноразовый токен Edge' }
    }

    $names = Ask 'Доп. имена сервера для HTTPS через запятую (DNS-имя; IP добавятся сами)' ''

    $install = @{ CloudUrl = $cloudUrl.TrimEnd('/') }
    if ($token) { $install.EnrollmentToken = $token }
    if ($names) { $install.TlsHostNames = $names }

    & (Join-Path $here 'install-edge.ps1') @install

    $ips = Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue |
        Where-Object { $_.IPAddress -notlike '127.*' -and $_.IPAddress -notlike '169.254.*' } |
        Select-Object -ExpandProperty IPAddress
    Write-Host ''
    Write-Host 'Готово. В Admin Web на дашборде появится «Edge на связи».' -ForegroundColor Green
    Write-Host "Адрес для игровых ПК: $(( $ips | ForEach-Object { "https://${_}:7443" } ) -join '  или  ')"
    Write-Host 'Отпечаток CA для агентов — Admin Web → «Подключение».'
}
catch {
    Write-Host ''
    Write-Host "Ошибка: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host 'Исправьте и запустите INSTALL.cmd снова. Подробности: docs/runbooks/edge-windows-install.md'
    exit 1
}
