# ClubOS

Платформа управления компьютерным клубом:
- облако: Cloud API и Admin Web;
- сервер клуба: Edge Controller, который работает и без интернета;
- агент на игровых ПК: Windows Agent и Player Shell.

```
Admin Web ────BFF────▶ Cloud API ◀──исходящее── Edge Controller ◀──LAN, mTLS── Windows Agent ─pipe─▶ AgentSessionHost (UI)
(Next.js 16)            (.NET 10,                  (.NET 10, SQLite WAL,          (.NET Windows Service)   (WinForms, сессия
                         PostgreSQL 18)             outbox/inbox, offline)                                  пользователя)
```

**Сдача проекта:** что сделано, как развернуть и принять, что осталось — [`docs/HANDOVER.md`](docs/HANDOVER.md).

Возможности:
- **устройства и сессии:**
  - ПК (обычные и бездисковые) и команды;
  - снимок экрана, процессы, питание;
  - тарифы по времени и пакеты;
  - Player Shell;
- **работа клуба:** касса и отчёты, клиенты с балансом, бронирования, бар и склад;
- **без интернета:** сессии и касса продолжают работать на сервере клуба;
- **персонал:** роли и 2FA, а также полный аудит действий.

Безопасность:
- изоляция организаций;
- индивидуальные сертификаты ПК и Edge;
- подпись каждого запроса;
- mTLS в LAN клуба и список отзыва;
- автопродление сертификатов с ротацией ключей.

Статус и тесты — [`docs/STATUS.md`](docs/STATUS.md); отклонения от ТЗ — [`docs/DEVIATIONS.md`](docs/DEVIATIONS.md);
архитектура — [`docs/adr/0001-m0-architecture.md`](docs/adr/0001-m0-architecture.md).

## Структура

| Путь | Что |
|------|-----|
| `packages/contracts-dotnet` | versioned контракты Cloud↔Edge↔Agent (envelope, DTO, state machines, BillingCalculator) |
| `packages/contracts-typescript` | зеркальные типы для Admin Web |
| `packages/security-dotnet` | dev CA, ключи устройств, подписанные токены запросов |
| `services/cloud-api` | Cloud API: модульный монолит ASP.NET Core + EF Core/PostgreSQL |
| `services/edge-controller` | Edge Controller: SQLite WAL, durable outbox, inbox, API агентов, локальный admin API |
| `services/edge-cli` | `edge-cli`: offline start/end сессий без прямого SQL |
| `services/windows-agent` | `ClubOS.Agent.Core` (ядро), `ClubOS.Agent.Service` (служба), `ClubOS.Agent.SessionHost` (UI), `install/*.ps1` |
| `tools/device-simulator` | SIMULATED ПК (обычные и бездисковые) на ядре реального агента |
| `apps/admin-web` | Admin Web (Next.js 16, TypeScript, Tailwind 4, RU) + Playwright e2e |
| `tests/unit`, `tests/integration` | xUnit; интеграционные — PostgreSQL 18 через Testcontainers |
| `infrastructure/docker` | Dockerfile'ы; `docker-compose.yml` в корне |
| `docs` | STATUS, DEVIATIONS, ADR, API, runbooks, security, THIRD_PARTY |

## Быстрый старт (Docker)

Нужны Docker с compose v2 и свободные порты 3000, 5080, 7070, 5432 (5432 — только localhost).

```bash
cp .env.example .env        # задать свои пароли/ключи (подсказки внутри)
docker compose up -d --build
docker compose --profile simulator up -d device-simulator   # 5 SIMULATED ПК
```

- Admin Web: http://localhost:3000. Вход: `CLUBOS_SEED_OWNER_EMAIL` / `CLUBOS_SEED_OWNER_PASSWORD` из `.env`.
- Cloud API и Swagger: http://localhost:5080/swagger.
- Edge (для агентов): `https://<IP машины>:7443` (mTLS) или переходный `http://<IP машины>:7070`.

Seed при первом запуске создаёт Demo Club Group / Dushanbe Pilot (Asia/Dushanbe, TJS), зоны Standard и VIP
по 120 TJS/час, dev Owner и одноразовый dev-токен Edge (`CLUBOS_DEV_EDGE_ENROLLMENT_TOKEN`).
По нему Edge-контейнер регистрируется сам.

Сценарий «нет WAN»:

```bash
docker compose stop cloud-api
docker compose exec edge-controller edge-cli start SIM-PC-01 --actor operator
docker compose exec edge-controller edge-cli status        # cloudReachable=false, outbox растёт
docker compose restart edge-controller                     # активная сессия не теряется
docker compose exec edge-controller edge-cli sessions --active
docker compose exec edge-controller edge-cli end <sessionId>
docker compose start cloud-api                             # outbox синхронизируется сам
```

Полный сброс: `docker compose --profile simulator down -v`.

## Боевое развёртывание (пилот)

| Где | Что | Runbook |
|-----|-----|---------|
| VPS (Linux) | Cloud API + PostgreSQL + Admin Web за Caddy (HTTPS, Let's Encrypt), один `docker compose` | [`vps-deploy.md`](docs/runbooks/vps-deploy.md) |
| Сервер клуба (Windows) | Edge Controller как служба `ClubOSEdge` + `edge-cli` (пакет CI `clubos-edge-windows`) | [`edge-windows-install.md`](docs/runbooks/edge-windows-install.md) |
| Игровые ПК (Windows) | Windows Agent: служба `ClubOSAgent` + AgentSessionHost (пакет CI `clubos-windows-agent-selfcontained`) | [`windows-agent-install.md`](docs/runbooks/windows-agent-install.md) |
| Админские ПК | браузер → `https://clubos.<домен>` | — |

Пакеты для Windows самодостаточные: .NET на серверах и ПК клуба ставить не нужно.

## Разработка без Docker-образов

Нужны .NET 10 SDK, Node.js 22 и PostgreSQL 18 (например `docker compose up -d postgres`).

```bash
# Cloud API (http://localhost:5080)
cd services/cloud-api
CLUBOS_ConnectionStrings__ClubOs="Host=localhost;Port=5432;Database=clubos;Username=clubos;Password=<пароль>" \
CLUBOS_Seed__OwnerPassword=<пароль-owner> ASPNETCORE_ENVIRONMENT=Development ASPNETCORE_URLS=http://localhost:5080 \
dotnet run --no-launch-profile

# Edge (API агентов :7070, локальный :7071); токен — Admin Web → «Подключение»
cd services/edge-controller
CLUBOS_Edge__EnrollmentToken=<токен> CLUBOS_Edge__CloudUrl=http://localhost:5080 dotnet run --no-launch-profile

# Симулятор
cd tools/device-simulator && CLUBOS_SIM_PASSWORD=<пароль-owner> dotnet run

# edge-cli
dotnet run --project services/edge-cli -- status --data services/edge-controller/edge-data

# Admin Web (http://localhost:3000)
cd apps/admin-web && npm ci && CLUBOS_CLOUD_API_URL=http://localhost:5080 npm run dev
```

## Проверки

```bash
dotnet build ClubOS.slnx -c Release                 # warnings as errors, включая Windows-проекты
dotnet test tests/unit                              # тариф, state machines, крипто, Edge store, исполнение команд
dotnet test tests/integration                       # нужен Docker: PostgreSQL 18 (Testcontainers)
dotnet format whitespace ClubOS.slnx --verify-no-changes
dotnet tool restore && dotnet ef migrations has-pending-model-changes --project services/cloud-api

cd apps/admin-web && npm run lint && npm run typecheck && npm run build
E2E_PASSWORD=<пароль-owner> npm run test:e2e        # против запущенного стека с симулятором
```

CI (`.github/workflows/ci.yml`): .NET build + unit + integration, Admin Web lint/typecheck/build,
сборка агента на `windows-latest` (артефакт), e2e smoke на `docker compose` с симулятором.
