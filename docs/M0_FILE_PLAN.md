# M0 — пофайловый план реализации

Порядок — по зависимостям контура (ТЗ §25.1). Легенда: ⬜ не начато · 🟡 в работе · ✅ готово и собрано.

## Слой 0 — Контракты (пишутся первыми, от них зависит всё) — ✅ написано (сборка на CI)
- ✅ `packages/contracts-dotnet/` — `EventEnvelope`, `CommandEnvelope`, DTO enrollment/heartbeat/session, enums состояний команды и сессии, `SchemaVersions`, `Money`, `BillingCalculator`.
- ✅ `packages/contracts-typescript/` — зеркальные типы для Admin Web (device, command lifecycle, session, audit).
- ✅ `tests/unit/` — тесты `BillingCalculator` (эталон 60сек=2,00 TJS и др.) + `ClubOS.slnx` + CI `.github/workflows/ci.yml`.

## Слой 1 — Cloud API (`services/cloud-api`) — ✅ написано (сборка на CI)
- ✅ `ClubOS.CloudApi.csproj` (ASP.NET Core, .NET 10), модульный монолит; добавлен в `ClubOS.slnx`.
- ✅ EF Core модель + PostgreSQL migration (InitialCreate): Organization, Location, Zone, User, Edge, EnrollmentToken, Device, DeviceCommand, Session, InboxReceipt, AuditEvent. (Снапшот — D-009.)
- ✅ Seed/dev bootstrap: `Demo Club Group` / `Dushanbe Pilot` / TZ `Asia/Dushanbe` / TJS / зоны `Standard`,`VIP` / dev Owner (`DatabaseBootstrapper`, идемпотентно).
- ✅ Auth (минимальная реальная): `POST /api/v1/auth/login`, `refresh`; хэш пароля PBKDF2 (D-007), JWT (D-008).
- ✅ Enrollment: выдача one-time токена (`/enrollment/tokens`), обмен на индивидуальный cert через dev-CA (`/enrollment/devices`).
- ✅ Sync batch приём с идемпотентностью `UNIQUE(eventId)` (`/sync/events`); heartbeat применяется к устройству.
- ✅ Devices: list/status; `POST /devices/{id}/commands` (ShowMessage, allow-list); приём результата (forward-only, идемпотентно).
- ✅ Sessions: start/end, расчёт тарифа 120 TJS/час через `BillingCalculator`, price snapshot; end идемпотентен.
- ✅ Audit timeline endpoint (`/audit`), изоляция по организации.
- ✅ Health endpoints (`/health/live`, `/health/ready`) + OpenAPI (`/openapi/v1.json`).

## Слой 2 — Edge Controller (`services/edge-controller`) — ✅ написано (сборка на CI)
- ✅ ASP.NET-хост (console-hostable) + фоновый `OutboxSyncService`; добавлен в `ClubOS.slnx`.
- ✅ SQLite WAL (`EnsureCreated`, D-011): outbox, inbox, sessions, commands, devices, config.
- ✅ Исходящее соединение с Cloud — HTTP POST `/api/v1/sync/events` (D-010 вместо gRPC/WebSocket).
- ✅ Локальный API для Agent: heartbeat, приём команд (идемпотентно), выдача команд, приём результата.
- ✅ Durable outbox с retry/exponential backoff; идемпотентный inbox по `commandId`.
- ✅ Start/end session при недоступном Cloud (пишется в SQLite + outbox); автосинк после восстановления; активная сессия переживает перезапуск (persist).
- ✅ Local health (`/health/live`, `/health/ready`) + `tools/edge-cli` (offline start/end через HTTP, без прямого SQL).

## Слой 3 — Windows Agent (`services/windows-agent`) — ✅ написано (сборка на Windows CI; ручной тест — B3, завтра)
- ✅ `.NET Windows Service` (`AddWindowsService`) + console mode для dev; net10.0-windows (вне `ClubOS.slnx`, собирает отдельный CI-джоб).
- ✅ Enrollment по one-time token → CSR → индивидуальный deviceId + сертификат; приватный ключ хранится только локально.
- ✅ Инвентаризация (WMI): hostname, Windows version, CPU, RAM, IPv4, версия Agent.
- ✅ Heartbeat каждые 10 сек (в Edge), инвентаризация — при первом успешном heartbeat.
- ✅ `AgentSessionHost` (WinForms overlay в интерактивной сессии) + Named Pipes с ACL (D-012).
- ✅ `ShowMessage` (видимое сообщение) и `LockTestMode` (overlay, без подмены Shell — D-003); ack → succeeded/failed.
- ✅ Логи без секретов; корректная остановка (штатная отмена цикла), идентичность переживает перезапуск.
- ✅ `docs/runbooks/windows-agent-install.md` — сценарий ручного теста на реальном Windows.

## Слой 4 — Device Simulator (`tools/device-simulator`)
- ⬜ 5 симулированных ПК: heartbeat, online/offline, результат ShowMessage; явная маркировка «SIMULATED».

## Слой 5 — Admin Web (`apps/admin-web`)
- ⬜ Next.js 16 + TS, i18n (ru), Tailwind/shadcn.
- ⬜ Login, Dashboard локации, карта/сетка устройств (зоны Standard/VIP).
- ⬜ Статус цвет+текст/иконка (не только цвет); карточка устройства с инвентаризацией и last heartbeat.
- ⬜ Отправка ShowMessage; отображение жизненного цикла команды.
- ⬜ Start/End test session; текущая длительность и стоимость; audit timeline.
- ⬜ Loading/error/empty states; кнопки будущих модулей помечены `Not implemented in M0`.

## Слой 6 — Инфраструктура и тесты
- ⬜ `docker-compose.yml` — PostgreSQL 18 (+ dev deps).
- ⬜ `.env.example` — только имена и безопасные примеры, без секретов.
- ⬜ `tests/unit` — тариф (60 сек=2 TJS), повторный EndSession, повторный sync, expired command.
- ⬜ `tests/integration` — PostgreSQL (Testcontainers), tenant isolation, outbox persist/clear, edge restart восстановление.
- ⬜ `tests/e2e` — Playwright smoke: login → devices → карточка.
- ⬜ `docs/THIRD_PARTY.md` — лицензии зависимостей.
- ⬜ `README.md` — запуск dev + отдельная установка Agent на Windows.
- ⬜ GitHub Actions: lint → unit → integration → build (+ Windows runner для Agent).
