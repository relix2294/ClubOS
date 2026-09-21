# Runbook — установка и ручная проверка Windows Agent (M0)

Этот сценарий выполняется на **реальном Windows-ПК** (ТЗ §3.3, §25.3 шаг 2 — блокер B3).
Здесь Agent собирается, запускается, проходит enrollment, шлёт heartbeat и исполняет
команды `ShowMessage` / `LockTestMode`.

> Overlay/сообщения показываются процессом **session-host** в интерактивной сессии.
> Служба Windows работает в session 0 и напрямую UI показать не может (изоляция сессий).
> Поэтому session-host запускается отдельно в сессии пользователя (D-012).

## 0. Предпосылки

- Windows 10/11 x64, права администратора.
- .NET SDK **10.0** (`dotnet --version` → `10.x`).
- Запущены (локально или по сети) **Cloud API** (порт 5000) и **Edge Controller** (порт 5080).
  - Cloud: `dotnet run --project services/cloud-api` (нужен PostgreSQL — см. docker-compose).
  - Edge: `dotnet run --project services/edge-controller` (SQLite создаётся сам).

## 1. Сборка

```powershell
dotnet build services\windows-agent -c Release
```

## 2. Получить enrollment-токен (в Cloud)

Авторизуйтесь как dev Owner и выпустите одноразовый токен для устройства:

```powershell
# 1) login → accessToken
$login = curl.exe -s -X POST http://localhost:5000/api/v1/auth/login `
  -H "Content-Type: application/json" `
  -d '{"email":"owner@demo.clubos","password":"ChangeMe123!"}' | ConvertFrom-Json

# 2) взять LocationId и ZoneId из БД seed (Dushanbe Pilot / зона Standard)
#    (значения можно посмотреть в таблицах locations/zones или через будущий Admin Web)

# 3) выпустить токен
$token = curl.exe -s -X POST http://localhost:5000/api/v1/enrollment/tokens `
  -H "Authorization: Bearer $($login.accessToken)" `
  -H "Content-Type: application/json" `
  -d '{"locationId":"<LOCATION_ID>","zoneId":"<ZONE_ID>","displayName":"Test PC 1"}' | ConvertFrom-Json

$env:CLUBOS_ENROLLMENT_TOKEN = $token.enrollmentToken
```

## 3. Запуск session-host (интерактивная сессия)

В отдельном окне под тем же пользователем:

```powershell
dotnet run --project services\windows-agent -c Release -- --session-host
```

Окно останется «пустым» — это нормально: оно ждёт команды по named pipe и показывает overlay/сообщения.

## 4. Запуск агента в консольном режиме (dev)

В другом окне:

```powershell
$env:CLUBOS_ENROLLMENT_TOKEN = "<как в шаге 2>"
dotnet run --project services\windows-agent -c Release
```

Ожидаемо в логах: `Enrollment успешен, deviceId=...`, затем heartbeat каждые 10 сек.
Идентичность сохраняется в `%ProgramData%\ClubOS\agent\` (identity.json + device-key.pem).

## 5. Проверка heartbeat

- Edge получает heartbeat: устройство появляется в его БД (таблица `devices`), статус `Idle`.
- После следующего sync-цикла Edge → Cloud устройство видно в Cloud (`GET /api/v1/devices`).

## 6. Проверка команд

Отправьте команду устройству через Cloud (она дойдёт до Edge, затем Agent её заберёт):

```powershell
# ShowMessage
curl.exe -s -X POST "http://localhost:5000/api/v1/devices/<DEVICE_ID>/commands" `
  -H "Authorization: Bearer $($login.accessToken)" `
  -H "Content-Type: application/json" `
  -d '{"title":"Внимание","message":"Тестовое сообщение ClubOS"}'
```

> В M0 доставка команды Cloud → Edge выполняется через `POST /api/edge/commands`
> (см. `services/edge-controller`). Убедитесь, что команда попала в Edge, затем Agent
> покажет сообщение в окне session-host. Для `LockTestMode` payload: `{"lock":true}` /
> `{"lock":false}` (снятие overlay).

Ожидаемо:
- `ShowMessage` → всплывает окно с сообщением, авто-закрытие через 10 сек.
- `LockTestMode {lock:true}` → полноэкранный overlay «ТЕСТОВЫЙ РЕЖИМ»; `{lock:false}` — снимается.
- В Cloud/Edge статус команды проходит `Acknowledged → Succeeded`.

## 7. Установка как Windows-служба (боевой режим)

```powershell
# путь к собранному exe
$exe = (Resolve-Path services\windows-agent\bin\Release\net10.0-windows\ClubOS.WindowsAgent.exe).Path
sc.exe create "ClubOSAgent" binPath= "`"$exe`"" start= auto
sc.exe description "ClubOSAgent" "ClubOS Windows Agent (M0)"
sc.exe start "ClubOSAgent"
```

Секреты службе передаются через машинные переменные окружения
(`CLUBOS_ENROLLMENT_TOKEN`, при необходимости `CLUBOS_CLOUD_URL`, `CLUBOS_EDGE_URL`).

> Для показа UI из службы session-host должен работать в сессии пользователя
> (например, автозапуск при входе через «Планировщик заданий» с аргументом `--session-host`).

Проверка статуса и логов:

```powershell
sc.exe query "ClubOSAgent"
Get-EventLog -LogName Application -Source "ClubOS Agent" -Newest 20   # если логирование в EventLog настроено
```

## 8. Остановка и удаление

```powershell
sc.exe stop "ClubOSAgent"
sc.exe delete "ClubOSAgent"
```

## Чек-лист приёмки Agent (M0)

- [ ] Собирается на Windows (.NET 10).
- [ ] Enrollment по one-time токену → получен deviceId, приватный ключ не покидает ПК.
- [ ] Инвентаризация (hostname, Windows version, CPU, RAM, IPv4, версия) видна в карточке.
- [ ] Heartbeat каждые 10 сек.
- [ ] `ShowMessage` показывает видимое сообщение; статус доходит до `Succeeded`.
- [ ] `LockTestMode` включает/снимает overlay (без подмены Shell — D-003).
- [ ] Служба корректно стартует/останавливается; повторный старт не теряет идентичность.
