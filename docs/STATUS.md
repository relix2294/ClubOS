# STATUS — ClubOS CA, Milestone 0

**Обновлено:** 2026-09-17
**Фаза:** Реализация M0 начата. Решение по среде: код пишется как готовый к сборке срез, проверка на CI/другой машине (.NET 10 + Docker + Windows). Локально (macOS, .NET 2.1) сборка не выполняется — помечается «не собрано здесь».

---

## 1. Что реально работает сейчас

**Admin Web** реально собирается в этой среде: `npm run lint` и `npm run build`
проходят (Next.js 16, все маршруты компилируются, TypeScript strict — 0 ошибок).

**Backend (Cloud API, Edge Controller, Windows Agent, Device Simulator)** написан как
готовый к сборке срез, но локально не собран — нет .NET 10 SDK (B1). Проверка — на CI
(джоб `dotnet` для solution; `windows-agent` — на Windows-раннере). Ни один backend-слой
не выдаётся за «проверенный на исполнение» до зелёного CI. Это соответствует ТЗ §0:
«Не выдавать mock/заглушку за завершённую функцию».

## 2. Блокеры среды (реальные, требуют решения)

Проверка окружения на машине разработки (macOS, arm64) выявила блокеры сборки и
приёмки. По правилам стартового промпта (§2.6) о них сообщается явно.

| # | Зависимость | Требуется (ТЗ §7.1, §25.2) | Фактически | Влияние |
|---|-------------|----------------------------|-----------|---------|
| B1 | .NET SDK | **10 LTS** | только **2.1.818** (EOL) | Cloud API, Edge Controller, Windows Agent, Device Simulator не собираются |
| B2 | Docker + compose | нужен для PostgreSQL и `docker compose up` (§25.2, §25.3) | **не установлен** | Приёмочный сценарий шага 1 невыполним локально |
| B3 | ОС Windows | реальный тест Windows Service (§25.3 шаг 2) | macOS arm64 | Agent-служба не может быть проверена здесь — по ТЗ §3.3 это отдельный тест на реальном Windows ПК; код обязан компилироваться на Windows CI runner |

B3 — ожидаемый и предусмотренный ТЗ блокер (не маскируется, инструкция для Windows-теста
будет в `docs/runbooks/`). B1 и B2 — блокеры «обязательной зависимости», требующие решения
до заявления любого шага приёмки выполненным.

## 3. Прогресс по компонентам M0

| Компонент | Статус | Примечание |
|-----------|--------|-----------|
| Monorepo структура | ✅ создана | §7.3 |
| docs (STATUS/DEVIATIONS/ADR/план) | ✅ готово | подготовительный проход |
| Контракты C# + TS | ✅ написано | envelope/DTO/enums, Money, BillingCalculator; сборка на CI |
| Unit-тесты тарифа | ✅ написано | эталон §12.4; прогон на CI |
| CI (GitHub Actions) | 🟡 расширен | джобы: admin-web (lint+build), dotnet (build+unit), windows-agent |
| Cloud API | ✅ написано | auth/enrollment/sync/devices/sessions/audit/health + seed; сборка на CI (B1) |
| Edge Controller | ✅ написано | SQLite WAL, durable outbox+backoff, offline start/end, local API, edge-cli; сборка на CI (B1) |
| Windows Agent | ✅ написано | служба+console, enrollment/CSR, WMI-инвентаризация, heartbeat, ShowMessage/LockTestMode overlay, Named Pipes ACL; ручной тест завтра (B3) |
| Device Simulator | ✅ написано | 5 SIMULATED ПК против Edge: heartbeat, online/offline, ответы на команды; сборка на CI (B1) |
| Admin Web | ✅ написано+собрано | Next.js 16/TS/Tailwind, login, дашборд, карточка, ShowMessage, сессии, аудит; **lint+build проходят здесь** и в CI |
| docker-compose (PostgreSQL) | ⬜ следующий | нужен Docker (B2) |
| Автотесты M0 (§5) | ⬜ не начато | зависят от B1/B2 |

## 4. Команды проверки (зафиксированы, будут работать после снятия B1/B2)

Backend (после установки .NET 10):
```bash
dotnet restore ClubOS.sln
dotnet build ClubOS.sln -c Release
dotnet test tests/unit
dotnet test tests/integration   # требует PostgreSQL (Testcontainers/Docker, B2)
```

Frontend:
```bash
cd apps/admin-web && npm install && npm run lint && npm run build
npx playwright test                # smoke: login → devices → карточка
```

Инфраструктура:
```bash
docker compose up -d               # PostgreSQL + dev cloud deps (B2)
```

Windows Agent (только на реальном Windows ПК, см. runbook):
```powershell
dotnet build services\windows-agent -c Release
# установка службы и ручная проверка — по docs/runbooks/windows-agent-install.md
```

## 5. Следующий шаг

Слои 1 (Cloud API) и 2 (Edge Controller) написаны как готовые к сборке срезы и
добавлены в `ClubOS.slnx`. Локально не собрано (B1: нет .NET 10 SDK) — проверка на CI.

Edge Controller: SQLite WAL (EnsureCreated), durable outbox с exponential backoff,
идемпотентный inbox, offline start/end сессий (persist через перезапуск), локальный
API для Agent (heartbeat/команды/результат), health-эндпоинты и `tools/edge-cli`.

Слой 3 (Windows Agent) написан: служба + console-режим, enrollment по one-time токену
(CSR, приватный ключ только локально), WMI-инвентаризация, heartbeat, исполнение
ShowMessage/LockTestMode через session-host (WinForms overlay) по named pipe с ACL.
Проект таргетит `net10.0-windows` и собирается отдельным Windows-джобом CI (не в `ClubOS.slnx`).
Ручная проверка на реальном Windows-ПК — завтра по `docs/runbooks/windows-agent-install.md` (B3).

Слой 5 (Admin Web) написан и **реально собран здесь** (lint + build зелёные): Next.js 16,
TypeScript strict, Tailwind v4, i18n (ru). Экраны: login, дашборд локации с сеткой
устройств по зонам, карточка устройства (инвентаризация, last heartbeat, ShowMessage,
жизненный цикл команды, start/end сессии с live длительностью и стоимостью), audit timeline.
Добавлен CI-джоб `admin-web` (npm ci → lint → build). Для дашборда в Cloud добавлен
эндпоинт `GET /api/v1/locations` (локации + зоны).

Дальше по плану — **Слой 6, инфраструктура и тесты**: `docker-compose.yml` (PostgreSQL 18),
`.env.example`, unit-тесты Cloud (повторный EndSession, повторный sync, expired command),
интеграционные тесты (Testcontainers, tenant isolation, outbox), Playwright smoke, README,
THIRD_PARTY. Часть требует Docker/PostgreSQL (B2).
