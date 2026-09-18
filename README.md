# ClubOS — Milestone 0

Управление игровым клубом: Cloud API, Edge Controller, Windows Agent, Device Simulator
и веб-панель Admin Web. Этот репозиторий — срез **M0** (см. `docs/STATUS.md`,
`docs/M0_FILE_PLAN.md`, `docs/DEVIATIONS.md`).

## Состав

| Компонент | Путь | Стек |
|-----------|------|------|
| Контракты | `packages/contracts-dotnet`, `packages/contracts-typescript` | C# / TS |
| Cloud API | `services/cloud-api` | ASP.NET Core (.NET 10) + PostgreSQL |
| Edge Controller | `services/edge-controller` | ASP.NET Core + SQLite (WAL) |
| Windows Agent | `services/windows-agent` | .NET 10 (Windows), WinForms overlay |
| Device Simulator | `tools/device-simulator` | .NET 10 console |
| edge-cli | `tools/edge-cli` | .NET 10 console |
| Admin Web | `apps/admin-web` | Next.js 16 + TypeScript + Tailwind |

## Предпосылки

- .NET SDK **10**
- Node.js **22**
- Docker (для PostgreSQL и интеграционных тестов)
- Windows-ПК — только для реального теста Agent (`docs/runbooks/windows-agent-install.md`)

## Быстрый старт (dev)

```bash
cp .env.example .env
docker compose up -d                     # PostgreSQL 18

# Cloud API (миграции + seed выполняются на старте)
dotnet run --project services/cloud-api  # http://localhost:5000

# Edge Controller (SQLite создаётся автоматически)
dotnet run --project services/edge-controller   # http://localhost:5080

# Admin Web
cd apps/admin-web && npm install && npm run dev  # http://localhost:3000
```

Dev-владелец после seed: `owner@demo.clubos` / пароль из `CLUBOS_DEV_OWNER_PASSWORD`.

### Симулятор устройств

```bash
SIM_EDGE_URL=http://localhost:5080/ dotnet run --project tools/device-simulator
```

### Windows Agent

Собирается и проверяется **на реальном Windows-ПК** отдельно — см.
`docs/runbooks/windows-agent-install.md`. В общий `ClubOS.slnx` не входит
(таргет `net10.0-windows`); собирается отдельным CI-джобом.

## Проверки

```bash
# Backend
dotnet build ClubOS.slnx -c Release
dotnet test tests/unit/ClubOS.Unit.Tests.csproj
dotnet test tests/integration/ClubOS.Integration.Tests.csproj   # требует Docker

# Frontend
cd apps/admin-web && npm run lint && npm run build

# E2E smoke (нужен поднятый Cloud + Admin Web)
cd tests/e2e && npm install && npx playwright test
```

## Лицензии зависимостей

См. `docs/THIRD_PARTY.md`.
