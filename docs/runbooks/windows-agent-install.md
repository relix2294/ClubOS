# Runbook: установка и ручная проверка Windows Agent (M0)

Этот тест выполняется **на реальном Windows ПК** (ТЗ §3.3, §25.3 шаг 2). Его нельзя заменить
Device Simulator'ом. CI гарантирует только компиляцию агента на `windows-latest`.

## 0. Что понадобится

| Что | Где взять |
|-----|-----------|
| Windows 10 22H2 / Windows 11, x64, права администратора | ПК клуба или тестовый ПК |
| .NET 10 **Desktop Runtime** x64 (содержит и .NET Runtime для службы, и WinForms для SessionHost) | https://dotnet.microsoft.com/download/dotnet/10.0 |
| Работающий стек ClubOS (Cloud + Edge) | `docker compose up -d` на машине разработчика (см. README) |
| Сетевой доступ ПК → Edge, TCP **7070** | Edge слушает `0.0.0.0:7070`. Проверьте firewall машины с Edge |
| Пакет агента | артефакт CI `clubos-windows-agent` **или** сборка ниже |

### Сборка пакета (на любой машине с .NET 10 SDK)

```powershell
dotnet publish services/windows-agent/ClubOS.Agent.Service/ClubOS.Agent.Service.csproj -c Release -r win-x64 --self-contained false -o out/agent
dotnet publish services/windows-agent/ClubOS.Agent.SessionHost/ClubOS.Agent.SessionHost.csproj -c Release -r win-x64 --self-contained false -o out/agent
Copy-Item services/windows-agent/install/*.ps1 out/agent/
```

Скопируйте каталог `out/agent` на тестовый ПК.

## 1. Получить одноразовый enrollment-токен

Admin Web → **Подключение** → «Токен для Windows Agent». Укажите имя ПК (например `PC-01`)
и зону, затем «Создать одноразовый токен». Токен показывается один раз, действует 24 часа и
используется только один раз.

## 2. Установка

PowerShell **от имени администратора**, в каталоге пакета:

```powershell
Set-ExecutionPolicy -Scope Process Bypass
.\install-agent.ps1 -EdgeUrl http://<IP-машины-с-Edge>:7070 -EnrollmentToken <токен>
```

Скрипт делает следующее:
- копирует файлы в `C:\Program Files\ClubOS\Agent`;
- создаёт `C:\ProgramData\ClubOS\Agent` с доступом только для SYSTEM и Administrators;
- пишет `agent.json`;
- регистрирует службу `ClubOSAgent` (LocalSystem, автозапуск, перезапуск при сбое);
- регистрирует задачу планировщика «ClubOS Agent SessionHost», которая запускает UI-процесс при входе пользователя.

## 3. Чек-лист приёмки (ТЗ §25.3 шаг 2)

Отмечайте результат и время. Всё проверяется в Admin Web (`http://<машина>:3000`).

| # | Проверка | Ожидаемый результат |
|---|----------|---------------------|
| 1 | Служба запущена: `Get-Service ClubOSAgent` | `Running`, StartType `Automatic` |
| 2 | Enrollment | Через 5–10 с ПК появляется на странице «Устройства» **без** метки SIMULATED. В `C:\ProgramData\ClubOS\Agent` есть `identity.json` и `device.key` (зашифрован DPAPI) |
| 3 | Инвентаризация | В карточке устройства видны имя ПК, версия Windows (с build), CPU, ОЗУ, IPv4, версия агента |
| 4 | Heartbeat | «Последний heartbeat» обновляется примерно раз в 10 с, статус «Свободен» |
| 5 | ShowMessage | Карточка → «Показать сообщение» → «Отправить». На экране ПК появляется окно поверх остальных. В Admin Web команда проходит путь «В очереди» → «Доставлена» → «Принята агентом» → «Выполнена» |
| 6 | LockTestMode | «Включить тестовую блокировку»: на всех мониторах полноэкранный overlay «тестовый режим блокировки», Alt+F4 его не закрывает. «Снять тестовую блокировку» убирает overlay |
| 7 | Аварийное снятие | При включённом overlay нажмите **Ctrl+Shift+F12**: overlay снимается локально. Ctrl+Alt+Del работает всегда. Shell/GPO не изменены |
| 8 | Нет интерактивной сессии | Выйдите из пользователя (служба продолжает работать) и отправьте ShowMessage. Команда завершается «Ошибка: AgentSessionHost не подключён», ложного «Выполнена» нет |
| 9 | Перезапуск службы | `Restart-Service ClubOSAgent`: устройство возвращается в «Свободен», повторной регистрации нет (тот же deviceId) |
| 10 | Остановка → offline | `Stop-Service ClubOSAgent`: через ~30 с статус «Не в сети», в аудите «Устройство не в сети» |
| 11 | Перезагрузка ПК | После входа служба стартует автоматически, SessionHost запускается задачей при логоне |
| 12 | Журнал исполненных команд | В `C:\ProgramData\ClubOS\Agent\executed-commands.json` есть ID выполненных команд. По нему агент не исполняет повторно доставленную команду (автотесты `CommandExecutorTests`) |
| 13 | Логи без секретов | Event Viewer → Application, источник ClubOSAgent: нет токенов, ключей и PEM |

## 4. Где смотреть при проблемах

- **Служба:** Event Viewer → Windows Logs → Application.
- **Консольный режим для диагностики:** остановите службу и запустите
  `"C:\Program Files\ClubOS\Agent\ClubOS.Agent.Service.exe"` от администратора. Логи пойдут в консоль.
- **«Enrollment отклонён»:** токен истёк или уже использован. Создайте новый, обновите `agent.json`
  (`Agent:EnrollmentToken`) и перезапустите службу.
- **Нет связи с Edge:** `Test-NetConnection <IP> -Port 7070`.

## 5. Удаление

```powershell
.\uninstall-agent.ps1            # удалить службу, задачу и файлы (identity сохраняется)
.\uninstall-agent.ps1 -RemoveData  # плюс удалить identity и ключ (потребуется новый токен)
```

## 6. Протокол теста

Запишите результат в `docs/STATUS.md` (раздел «Windows Agent на реальном ПК»):
дата, версия Windows (build), версия агента, результаты пунктов 1–13, найденные проблемы.
