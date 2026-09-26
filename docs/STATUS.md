# STATUS — ClubOS CA, Milestone 0

**Обновлено:** 2026-09-26
**Фаза:** M0 реализован целиком. Автоматические проверки и сквозной сценарий пройдены в облачной
среде разработки (Linux, .NET 10.0.112, Docker, PostgreSQL 18.6).
Открыт один пункт — ручной тест Windows Agent на реальном Windows ПК (ТЗ §3.3), см. раздел 4.

---

## 1. Что работает (проверено)

| Контур | Как проверено | Результат |
|--------|---------------|-----------|
| Cloud API: login/refresh/logout, `/me`, seed Demo Club Group / Dushanbe Pilot / Asia/Dushanbe / TJS / Standard+VIP / Owner | интеграционные тесты + ручной прогон | ✅ |
| Tenant isolation: чужая организация получает 404 на устройства, команды, сессии, enrollment и аудит | `AuthAndTenancyTests.Other_tenant_cannot_see_or_command_devices` | ✅ |
| Edge enrollment по одноразовому токену → сертификат dev CA; повторное использование токена отклоняется | интеграционные тесты, compose-bootstrap | ✅ |
| Подписанные запросы Edge/Agent: подделка ключа, чужой CA, replay, чужая роль/аудитория отклоняются | `SecurityTests` (9), `EdgeProtocolTests` | ✅ |
| Agent enrollment через Edge → индивидуальный deviceId; инвентаризация; heartbeat 10 с; offline через 30 с | интеграционные тесты, симулятор, ручной прогон | ✅ |
| ShowMessage: Admin → Cloud → Edge → Agent → `Queued→Delivered→Acknowledged→Succeeded` → audit | `EndToEndTests`, Playwright e2e | ✅ |
| Повторная доставка команды не исполняет её дважды (Edge inbox + журнал агента, в т.ч. после рестарта агента) | `EdgeStoreTests`, `CommandExecutorTests`, `EndToEndTests` | ✅ |
| Просроченная команда → Expired и не исполняется | `EdgeStoreTests`, `CommandExecutorTests`, `EndToEndTests` | ✅ |
| Сессия из Admin Web: Created → Active (Edge) → Ended; 409 на вторую открытую сессию; повторный End идемпотентен | `EndToEndTests`, ручной прогон (65 с → 4,00 TJS) | ✅ |
| Тариф 120 TJS/час: 60 с = 2,00; 61 с = 4,00; 30 мин = 60,00; расчёт детерминирован | `BillingCalculatorTests`, `EdgeStoreTests` | ✅ |
| WAN offline: `edge-cli start/end` без Cloud, события в durable outbox, автосинк после восстановления | `EndToEndTests.Edge_keeps_working_offline…`, ручной прогон в `docker compose` | ✅ |
| Рестарт Edge не теряет активную сессию | `EdgeStoreTests`, `EndToEndTests.Edge_restart…`, `docker compose restart edge-controller` | ✅ |
| Повторный sync-batch не создаёт дублей (UNIQUE eventId в БД) | `EdgeProtocolTests.Repeated_sync_batch…` | ✅ |
| Admin Web (RU): вход, дашборд с сеткой по зонам, статус иконкой+текстом+цветом, метка SIMULATED, карточка устройства, команды с жизненным циклом, сессия с таймером и стоимостью, аудит, enrollment-токены, кнопки «Not implemented in M0» | Playwright (3 теста) против compose-стека, скриншоты | ✅ |
| `docker compose`: postgres + cloud-api + edge-controller + device-simulator поднимаются и связываются без ручных шагов | запуск в среде разработки | ✅ (см. п. 3 про образ admin-web) |
| Windows Agent Service + SessionHost компилируются (`net10.0-windows`, EnableWindowsTargeting) | `dotnet build ClubOS.slnx -c Release` на Linux | ✅ компиляция; ручной тест на Windows — п. 4 |

Итог прогона автотестов: **unit 53/53, integration 14/14, e2e 3/3**. Release-сборка всего solution:
0 предупреждений (warnings as errors). `dotnet format`: чисто. EF: `has-pending-model-changes` чисто.
Admin Web: `eslint --max-warnings=0`, `tsc --noEmit` и `next build` без ошибок.

## 2. Команды проверки

```bash
dotnet build ClubOS.slnx -c Release
dotnet test tests/unit
dotnet test tests/integration                # Docker (Testcontainers, postgres:18)
cd apps/admin-web && npm ci && npm run lint && npm run typecheck && npm run build
docker compose up -d --build && docker compose --profile simulator up -d device-simulator
E2E_PASSWORD=<пароль-owner> npm run test:e2e # в apps/admin-web, против поднятого стека
```

## 3. Ограничения проверки в этой среде (честно)

- **Образ `admin-web` не собран локально:** Docker Hub отвечал `429 Too Many Requests` на `node:22-alpine`,
  а зеркала закрыты сетевой политикой среды. Вместо образа проверен тот же standalone-сервер
  (`node apps/admin-web/server.js` из `.next/standalone`), который запускает контейнер. Против него прошёл e2e.
  Сборка образа выполняется в CI (job `e2e`).
- **Сборка .NET-образов здесь** шла с временно подложенным CA прокси песочницы (исходящий HTTPS идёт через
  MITM-прокси). Сами Dockerfile'ы в репозитории стандартные, без этого CA.
- **CI на GitHub** ещё не запускался: workflow обновлён, первый прогон будет на PR.

## 4. Открыто: Windows Agent на реальном Windows ПК (ТЗ §3.3, §25.3 шаг 2)

Ожидаемый блокер (B3, см. DEVIATIONS ENV-3): в облачной среде нет Windows. Нужно выполнить
[`docs/runbooks/windows-agent-install.md`](runbooks/windows-agent-install.md) на реальном ПК (13 пунктов)
и записать протокол сюда. Пакет агента: артефакт CI `clubos-windows-agent` или `dotnet publish` по runbook.

| Дата | Windows (build) | Версия агента | Пункты 1–13 | Проблемы |
|------|-----------------|---------------|-------------|----------|
| — | — | — | не выполнено | — |

## 5. Компоненты M0

| Компонент | Статус | Примечание |
|-----------|--------|-----------|
| Контракты C# + TS | ✅ | envelope, DTO, события, state machines, BillingCalculator (C# и TS) |
| Security (dev CA, ключи, подписанные токены) | ✅ | `docs/security/dev-ca.md` |
| Cloud API | ✅ | auth, tenant scope, devices, commands, sessions, audit, enrollment, edge sync/queue, health, OpenAPI/Swagger, rate limit |
| Edge Controller + edge-cli | ✅ | SQLite WAL, outbox/inbox, long-poll к Cloud, API агентов, loopback admin API |
| Windows Agent (Core/Service/SessionHost + install.ps1) | ✅ код / ⏳ ручной тест | DPAPI, Named Pipe с ACL и проверкой клиента, LockTestMode overlay |
| Device Simulator | ✅ | 5 SIMULATED ПК, команды `offline N` / `online N` |
| Admin Web | ✅ | Next.js 16 BFF (httpOnly-cookie, CSRF-проверка), RU, Playwright |
| docker-compose, `.env.example` | ✅ | dev-bootstrap Edge по seed-токену |
| CI | ✅ написан | .NET + integration, web, windows-latest, e2e compose |
| Документация | ✅ | README, API, runbook, security, THIRD_PARTY, DEVIATIONS D-001…D-015 |

## 6. Следующие шаги

1. Выполнить ручной тест Windows Agent по runbook и заполнить п. 4.
2. Открыть PR и довести CI до зелёного на GitHub (первый прогон workflow).
3. Кандидаты на M1 (из DEVIATIONS): TLS Edge↔Agent и подпись тела (D-007), SignalR push (D-008), Argon2id (D-009),
   отзыв и перевыпуск сертификатов (D-011), выбор локации и полный RBAC (D-013).
