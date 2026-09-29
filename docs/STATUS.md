# STATUS — ClubOS CA, Milestone 0 → M1

**Обновлено:** 2026-09-29 (M1: персонал и доступ, Player Shell)
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

## 3. Развёртывание пилота (проверено)

- **VPS-стек** (`infrastructure/vps`): PostgreSQL, Cloud API, Admin Web и Caddy. Поднят из репозитория
  в среде разработки, домен `localhost`, сертификат от внутреннего CA Caddy. Проверено:
  - вход через BFF по HTTPS (secure httpOnly-cookie);
  - наружу закрыты Swagger, OpenAPI и health, HTTP перенаправляется на HTTPS;
  - Edge зарегистрировался и работает через HTTPS, 2 симулированных ПК подключились;
  - Playwright e2e 3/3 через Caddy;
  - `backup.sh` снял дамп базы и dev CA.
- **Edge на Windows:** режим службы `ClubOSEdge`, данные в `%ProgramData%\ClubOS\Edge`, ключ под DPAPI,
  `install-edge.ps1` / `uninstall-edge.ps1`. CI (`windows-latest`): unit-тесты на Windows,
  самодостаточный пакет `clubos-edge-windows`, пробный запуск с проверкой `/health` и `edge-cli status`.
- Ограничения этой среды: исходящий HTTPS идёт через прокси с собственным CA, поэтому образы здесь собирались
  с временно подложенным CA. Dockerfile'ы в репозитории стандартные. CI на GitHub собирает их без обходов.

## 4. Открыто: Windows Agent на реальном Windows ПК (ТЗ §3.3, §25.3 шаг 2)

Ожидаемый блокер (B3, см. DEVIATIONS ENV-3): в облачной среде нет Windows. Нужно выполнить
[`docs/runbooks/windows-agent-install.md`](runbooks/windows-agent-install.md) на реальном ПК (13 пунктов)
и записать протокол сюда. Схема теста: Cloud на VPS ([vps-deploy.md](runbooks/vps-deploy.md)), Edge на сервере
клуба ([edge-windows-install.md](runbooks/edge-windows-install.md)), агент на игровом ПК. Пакеты: артефакты CI
`clubos-edge-windows` и `clubos-windows-agent-selfcontained`.

| Дата | Windows (build) | Версия агента | Пункты 1–13 | Проблемы |
|------|-----------------|---------------|-------------|----------|
| — | — | — | не выполнено | — |

## 5. Компоненты M0

| Компонент | Статус | Примечание |
|-----------|--------|-----------|
| Контракты C# + TS | ✅ | envelope, DTO, события, state machines, BillingCalculator (C# и TS) |
| Security (dev CA, ключи, подписанные токены) | ✅ | `docs/security/dev-ca.md` |
| Cloud API | ✅ | auth, tenant scope, devices, commands, sessions, audit, enrollment, edge sync/queue, health, OpenAPI/Swagger, rate limit |
| Edge Controller + edge-cli | ✅ код / ⏳ ручной тест на Windows | SQLite WAL, outbox/inbox, long-poll к Cloud, API агентов, loopback admin API; Windows-служба + install-edge.ps1 |
| Windows Agent (Core/Service/SessionHost + install.ps1) | ✅ код / ⏳ ручной тест | DPAPI, Named Pipe с ACL и проверкой клиента, LockTestMode overlay |
| Device Simulator | ✅ | 5 SIMULATED ПК, команды `offline N` / `online N` |
| Admin Web | ✅ | Next.js 16 BFF (httpOnly-cookie, CSRF-проверка), RU, Playwright |
| docker-compose, `.env.example` | ✅ | dev-bootstrap Edge по seed-токену |
| VPS (`infrastructure/vps`) | ✅ | Caddy + Let's Encrypt, backup.sh, runbook `vps-deploy.md` |
| CI | ✅ зелёный | .NET + integration, web, Windows (Agent + Edge, unit-тесты, пробный запуск), e2e compose, валидация VPS-конфига |
| Документация | ✅ | README, API, runbook, security, THIRD_PARTY, DEVIATIONS D-001…D-015 |

## 5a. M1 — в работе

| Блок | Статус | Проверка |
|------|--------|----------|
| Персонал и доступ (ТЗ §8): роли Owner/Admin/Operator, 6 прав на backend, управление сотрудниками, временные пароли, смена пароля, мгновенный отзыв доступа, Argon2id с переходом с PBKDF2, `admin reset-password` на сервере | ✅ | unit 75/75, integration 21/21 (7 новых), Playwright «владелец → оператор → смена пароля → отключение» |
| Player Shell (ТЗ §26, D-003/D-016): режимы Off / Hud / Enforced. Экран клуба на свободном ПК, индикатор сессии (остаток, стоимость по часам Edge), итог по окончании, вход техника по PIN (режим обслуживания), автоперезапуск SessionHost службой. Сессии с лимитом времени и продлением: таймер на Edge работает без интернета, окончание = плановое (без переплаты). Admin Web: выбор лимита, обратный отсчёт, «Продлить» | ✅ код / ⏳ ручной тест на Windows (runbook п. 14–25) | unit 149/149 (+74: таймер Edge, продление, состояние агента, контроллер Shell, PIN, клавиши, совместимость очереди), integration 24/24 (+3: лимит → продление → итог на экране; таймер при отключённом WAN; валидация), Playwright 5/5 (+1: лимит → остаток → продление → итог) |
| Live-обновления Admin Web (D-008): SSE `/api/v1/live` через BFF, подсказки об изменениях из перехватчика EF (после commit) и монитора присутствия, изоляция tenant, фильтр по правам, перепроверка доступа, индикатор «Онлайн» в шапке; опрос — страховка раз в 30 с | ✅ | unit 162/162 (+13: брокер, переполнение, правила публикации, права), integration 28/28 (+4: push своему tenant, чужой не получает; Edge offline; права оператора и закрытие потока при отключении; 401), Playwright 6/6 (+1: изменение из второй вкладки видно без опроса) |
| MFA (TOTP) для Owner/Admin | ⬜ | — |
| Выбор локации в UI, права по локациям | ⬜ | — |

## 6. Следующие шаги

1. Развернуть Cloud на VPS, Edge на сервере клуба, агент на игровом ПК; выполнить чек-листы runbook'ов и заполнить п. 4.
2. Настроить ежедневный `backup.sh` и копирование бэкапов за пределы VPS.
3. Кандидаты на M1 (из DEVIATIONS): TLS Edge↔Agent и подпись тела (D-007), SignalR push (D-008), Argon2id (D-009),
   отзыв и перевыпуск сертификатов (D-011), выбор локации и полный RBAC (D-013).
