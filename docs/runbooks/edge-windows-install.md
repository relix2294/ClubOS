# Runbook: Edge Controller на Windows-сервере клуба

Edge — локальный контроллер клуба. Он хранит сессии в SQLite и продолжает работать без интернета,
принимает подключения агентов игровых ПК по LAN и сам соединяется с Cloud (исходящее HTTPS).
Ставится как служба Windows `ClubOSEdge`.

## 1. Что нужно

| Что | Требование |
|-----|-----------|
| Сервер клуба | Windows 10/11 или Windows Server 2019+, x64, всегда включён, права администратора |
| Сеть | исходящий HTTPS к VPS; игровые ПК в той же LAN; постоянный IP сервера в LAN (DHCP-резерв) |
| .NET | не нужен: пакет самодостаточный |
| Пакет | артефакт CI `clubos-edge-windows` (Edge + `edge-cli` + скрипты) или сборка ниже |
| Токен | Admin Web → **Подключение** → «Токен для Edge Controller» (одноразовый, 24 часа) |

### Сборка пакета (на любой машине с .NET 10 SDK)

```powershell
dotnet publish services/edge-controller/ClubOS.EdgeController.csproj -c Release -r win-x64 --self-contained true -o out/edge
dotnet publish services/edge-cli/ClubOS.EdgeCli.csproj -c Release -r win-x64 --self-contained true -o out/edge
Copy-Item services/edge-controller/install/*.ps1 out/edge/
```

## 2. Установка

PowerShell **от имени администратора**, в каталоге пакета:

```powershell
Set-ExecutionPolicy -Scope Process Bypass
.\install-edge.ps1 -CloudUrl https://clubos.<ваш-домен> -EnrollmentToken <токен>
```

Что делает скрипт:
- копирует файлы в `C:\Program Files\ClubOS\Edge`;
- создаёт `C:\ProgramData\ClubOS\Edge` с доступом только для SYSTEM и Administrators.
  Там лежат `edge.db` (SQLite WAL), identity, ключ Edge под DPAPI, токен локального API и `edge.json`;
- регистрирует службу `ClubOSEdge` (LocalSystem, автозапуск, перезапуск при сбое) и источник Event Log;
- открывает входящие TCP 7443 (HTTPS) и 7070 (переходный HTTP) для программы Edge, только в профилях Domain и Private;
- запускает службу и показывает `/health`. Ожидается `enrolled: True`, `cloudReachable: True`.

После регистрации Edge получает от Cloud TLS-сертификат для API агентов (DEVIATIONS D-007). В сертификат попадают
`localhost`, имя компьютера и все активные IPv4-адреса; продлевается он автоматически и при смене IP.
Дополнительные имена (например DNS-имя сервера): `-TlsHostNames edge.club.local,10.0.0.5`.
Порты: `-AgentTlsPort 7443`, `-AgentPort 7070`. Когда все ПК переведены на HTTPS, закройте HTTP: `-DisableAgentHttp`.

## 3. Проверка

| # | Проверка | Ожидаемо |
|---|----------|----------|
| 1 | `Get-Service ClubOSEdge` | `Running`, `Automatic` |
| 2 | Admin Web → Устройства | «Edge Controller … Edge на связи» |
| 3 | С игрового ПК: `Test-NetConnection <IP сервера> -Port 7443` | `TcpTestSucceeded : True` |
| 3a | Admin Web → Подключение → «Отпечаток CA»; на ПК `install-agent.ps1 -EdgeUrl https://<IP>:7443 -EdgeCaFingerprint …` | ПК «В сети»; с неверным отпечатком агент не подключается |
| 4 | `& 'C:\Program Files\ClubOS\Edge\edge-cli.exe' status` (PowerShell от администратора) | `enrolled: true`, `cloudReachable: true`, `pendingOutboxEvents: 0` |
| 5 | Выдернуть интернет (LAN оставить): `edge-cli start <ПК> --actor admin` | сессия началась, `cloudReachable: false`, outbox растёт |
| 6 | `Restart-Service ClubOSEdge`, затем `edge-cli sessions --active` | активная сессия на месте |
| 7 | `edge-cli end <sessionId>`, вернуть интернет | через несколько секунд outbox = 0, сессия видна в Admin Web |
| 8 | Перезагрузка сервера | служба стартует сама, Edge снова «на связи» |

## 4. `edge-cli` — работа без Cloud

Запускать из PowerShell **от имени администратора**: токен локального API доступен только администраторам.

```powershell
$cli = 'C:\Program Files\ClubOS\Edge\edge-cli.exe'
& $cli status                        # связь с Cloud, очередь outbox, активные сессии
& $cli devices                       # ПК и их статус
& $cli sessions --active
& $cli start PC-01 --actor Иван      # начать открытую сессию (тариф из кэша)
& $cli start PC-01 --minutes 60      # сессия с лимитом: через 60 мин Edge завершит её сам и закроет ПК
& $cli extend <sessionId> 30         # продлить сессию с лимитом на 30 мин
& $cli end <sessionId> --actor Иван  # завершить (идемпотентно)
```

## 5. Где смотреть при проблемах

- **Логи:** Event Viewer → Windows Logs → Application, источник `ClubOSEdge`.
- **Консольный режим:** остановите службу и запустите `"C:\Program Files\ClubOS\Edge\ClubOS.EdgeController.exe"`
  от администратора. Логи пойдут в консоль, данные те же (`C:\ProgramData\ClubOS\Edge`).
- **`enrolled: False`:** токен истёк или уже использован, либо CloudUrl недоступен. Создайте новый токен,
  впишите его в `C:\ProgramData\ClubOS\Edge\edge.json` (`Edge.EnrollmentToken`) и перезапустите службу.
- **Агенты не подключаются:** firewall (п. 3), профиль сети сервера должен быть Private, а не Public.

## 6. Обновление и удаление

Обновление: остановить службу не нужно, повторно запустите `install-edge.ps1 -CloudUrl …` из нового пакета без токена.
Данные и регистрация сохраняются.

```powershell
.\uninstall-edge.ps1              # служба, firewall, файлы; данные сохраняются
.\uninstall-edge.ps1 -RemoveData  # плюс edge.db и identity: несинхронизированные события будут потеряны
```
