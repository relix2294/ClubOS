# M0 — пофайловый план реализации

Порядок — по зависимостям контура (ТЗ §25.1). Легенда: ⬜ не начато · 🟡 в работе · ✅ готово и собрано. Итог и проверки — `docs/STATUS.md`.

## Слой 0 — Контракты (пишутся первыми, от них зависит всё) — ✅
- ✅ `packages/contracts-dotnet/` — `EventEnvelope`, `CommandEnvelope`, DTO enrollment/heartbeat/session, enums состояний команды и сессии, `SchemaVersions`, `Money`, `BillingCalculator`.
- ✅ `packages/contracts-typescript/` — зеркальные типы для Admin Web (device, command lifecycle, session, audit).
- ✅ `tests/unit/` — тесты `BillingCalculator` (эталон 60сек=2,00 TJS и др.) + `ClubOS.slnx` + CI `.github/workflows/ci.yml`.
- ✅ `packages/security-dotnet/` — dev CA, ключи устройств, подписанные токены запросов (добавлено в ходе реализации).

## Слой 1 — Cloud API (`services/cloud-api`)
- ✅ `ClubOS.CloudApi.csproj` (ASP.NET Core, .NET 10), модульный монолит.
- ✅ EF Core модель + PostgreSQL migrations: Organization, Location, Zone, User, Device, DeviceCommand, Session, SessionEvent, AuditEvent, OutboxEvent, InboxReceipt.
- ✅ Seed/dev bootstrap: `Demo Club Group` / `Dushanbe Pilot` / TZ `Asia/Dushanbe` / TJS / зоны `Standard`,`VIP` / dev Owner.
- ✅ Auth (минимальная реальная): `POST /api/v1/auth/login`, `refresh`, `logout`; PBKDF2 (DEVIATIONS D-009).
- ✅ Edge enrollment: регистрация Edge, выдача one-time enrollment token, обмен на индивидуальный cert/credential (dev CA).
- ✅ Sync batch приём с идемпотентностью `UNIQUE(eventId)`.
- ✅ Devices: list/status; `POST /devices/{id}/commands` (ShowMessage); приём результата.
- ✅ Sessions: start/end, расчёт тарифа 120 TJS/час, price snapshot.
- ✅ Audit timeline endpoint.
- ✅ Health endpoints + OpenAPI/Swagger.

## Слой 2 — Edge Controller (`services/edge-controller`)
- ✅ `.NET Worker Service` (console-hostable).
- ✅ SQLite WAL: devices, sessions, event log, outbox, inbox receipts, commands/results, cached config.
- ✅ Исходящее соединение с Cloud: HTTP long-poll + sync (DEVIATIONS D-010).
- ✅ Локальный API/gRPC для Agent + heartbeat processing.
- ✅ Durable outbox с retry/backoff; идемпотентный inbox.
- ✅ Start/end session при недоступном Cloud; автосинк после восстановления; restart не теряет активную сессию.
- ✅ Local health endpoint + `edge-cli` для offline start/end (без прямого SQL).

## Слой 3 — Windows Agent (`services/windows-agent`)
- ✅ `.NET Windows Service` (+ console mode для dev).
- ✅ Enrollment по one-time token → индивидуальный device ID.
- ✅ Инвентаризация: hostname, Windows version, CPU, RAM, IPv4, Agent version.
- ✅ Heartbeat каждые 10 сек.
- ✅ `AgentSessionHost` (интерактивная сессия) + Named Pipes ACL IPC.
- ✅ `ShowMessage` (видимое сообщение), `LockTestMode` (overlay), ack/succeeded/failed.
- ✅ Structured logs без секретов; корректная остановка/перезапуск.
- ✅ `docs/runbooks/windows-agent-install.md` — ручной тест на реальном Windows.

## Слой 3 — ручной тест на реальном Windows ПК — ⏳ по `docs/runbooks/windows-agent-install.md`

## Слой 4 — Device Simulator (`tools/device-simulator`)
- ✅ 5 симулированных ПК: heartbeat, online/offline, результат ShowMessage; явная маркировка «SIMULATED».

## Слой 5 — Admin Web (`apps/admin-web`)
- ✅ Next.js 16 + TS, i18n (ru), Tailwind (без shadcn — DEVIATIONS D-012).
- ✅ Login, Dashboard локации, карта/сетка устройств (зоны Standard/VIP).
- ✅ Статус цвет+текст/иконка (не только цвет); карточка устройства с инвентаризацией и last heartbeat.
- ✅ Отправка ShowMessage; отображение жизненного цикла команды.
- ✅ Start/End test session; текущая длительность и стоимость; audit timeline.
- ✅ Loading/error/empty states; кнопки будущих модулей помечены `Not implemented in M0`.

## Слой 6 — Инфраструктура и тесты
- ✅ `docker-compose.yml` — PostgreSQL 18 (+ dev deps).
- ✅ `.env.example` — только имена и безопасные примеры, без секретов.
- ✅ `tests/unit` — тариф (60 сек=2 TJS), повторный EndSession, повторный sync, expired command.
- ✅ `tests/integration` — PostgreSQL (Testcontainers), tenant isolation, outbox persist/clear, edge restart восстановление.
- ✅ Playwright smoke (`apps/admin-web/e2e`): login → devices → карточка + жизненный цикл ShowMessage.
- ✅ `docs/THIRD_PARTY.md` — лицензии зависимостей.
- ✅ `README.md` — запуск dev + отдельная установка Agent на Windows.
- ✅ GitHub Actions: lint → unit → integration → build (+ Windows runner для Agent).
