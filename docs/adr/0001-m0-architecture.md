# ADR-0001 — Архитектура Milestone 0

- **Статус:** Принято
- **Дата:** 2026-09-17
- **Контекст:** первый сквозной вертикальный срез ClubOS CA (ТЗ §25).

## Контекст

Нужно доказать один сквозной контур, а не воспроизвести SENET:

```
Admin Web → Cloud API → Edge Controller → реальный Windows Agent → результат → audit
```

плюс базовая сессия (тариф 120 TJS/час) и сохранность/синхронизация сессии при
отключении WAN между Edge и Cloud.

## Неподлежащие изменению решения (из стартового промпта §1)

1. Backend: ASP.NET Core, C#, **.NET 10 LTS**, модульный монолит.
2. Admin Web: **Next.js 16**, TypeScript.
3. Cloud DB: **PostgreSQL 18**.
4. Edge local DB на M0: **SQLite WAL**.
5. Realtime: SignalR/WebSocket (или обоснованный эквивалент внутри стека).
6. Windows Agent: **.NET Windows Service** (+ отдельный `AgentSessionHost` в интерактивной сессии; IPC через Named Pipes с ACL — UI не рисуется из Session 0).
7. Cloud↔Edge и Edge↔Agent разделены **versioned контрактами**; UI не общается с Agent напрямую.
8. Local-first: сессия продолжается и сохраняется на Edge без WAN.
9. Деньги — **integer minor units**; базовая валюта seed-локации — **TJS**.
10. Время хранится в **UTC**; локация — **Asia/Dushanbe**.
11. Без Kubernetes/Kafka/микросервисов на M0. Без секретов в git. Без опасных произвольных команд.

## Решения архитектуры M0

### Направление соединений
Edge **инициирует исходящее** соединение к Cloud (ТЗ §7.2, §6.2). Cloud никогда не
подключается во входящий порт клуба. Admin Web общается только с Cloud API (не с Edge/Agent).

### Границы контрактов
- `packages/contracts-dotnet` — DTO/envelope для Cloud↔Edge и Edge↔Agent (C#).
- `packages/contracts-typescript` — типы для Admin Web ↔ Cloud API.
- Envelope событий и команд — по ТЗ §24.2/§24.3 (`eventId`, `sequence`, `correlationId`; `commandId`, `issuedAtUtc`, `expiresAtUtc`, `actor`).

### Идемпотентность (на уровне БД, не только в памяти — §4, §7)
- Cloud: `UNIQUE(eventId)` на приёме sync-batch → повторный event не создаёт дубль.
- Command: состояния `queued→delivered→acknowledged→succeeded/failed/expired`; повтор доставки одной команды не выполняет её дважды (`UNIQUE(commandId)` + проверка состояния).
- Edge outbox: at-least-once доставка; inbox receipts дедуплицируют по ключу.

### Сессии и деньги
- Активная сессия хранит **price snapshot** в момент старта (§12.2).
- Edge — источник истины для активной сессии при offline (§23.3).
- Завершённая сессия не редактируется; коррекция — отдельная связанная операция (§12.2.1).
- Расчёт детерминирован; тестовый эталон 120 TJS/час: 60 сек = 2,00 TJS (§12.4).

### Windows Agent UI
Служба (Session 0) **не рисует UI**. Отдельный `AgentSessionHost` в интерактивной
сессии показывает `ShowMessage`/`LockTestMode` overlay. Service↔SessionHost — Named
Pipes с ACL (§11.1). `LockTestMode` на M0 = overlay, без подмены Shell/GPO (§25.2.5).

### Multitenancy
Tenant/organization scope проверяется backend, а не доверяется ID из UI (§4, §8 AUTH-001).

## Реализация (2026-09-26)

- Cloud→Edge — HTTP long-poll с ack и inbox на Edge (D-010); Edge→Cloud — sync-batch из durable outbox.
- Аутентификация Edge и Agent — подписанные ES256-токены по сертификатам dev CA (`docs/security/dev-ca.md`, D-007).
- Сессия из Admin Web — запрос в Cloud (`Created`), исполнение и расчёт на Edge, `Active/Ended` по событиям.
- Admin Web — BFF (route handlers Next.js) с httpOnly-cookie; realtime через polling (D-008).
- Ядро агента (`ClubOS.Agent.Core`) кроссплатформенное и переиспользуется Device Simulator'ом.

## Последствия

- Локальный запуск требует .NET 10 SDK и Docker (PostgreSQL); см. README.
- Windows Agent проверяется отдельно на реальном Windows ПК; здесь гарантируется только компиляция на Windows CI runner.

## Отложенные решения (требуют отдельного ADR — §36)

SQLite vs local PostgreSQL для production Edge; промышленный PKI/отзыв; финальный бренд; тарифы ClubOS.
