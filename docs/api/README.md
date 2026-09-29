# ClubOS Cloud API · Edge API (M0)

Машиночитаемая спецификация: `GET /openapi/v1.json`, Swagger UI: `/swagger` (Cloud API).
Все времена в UTC (ISO-8601), деньги в minor units (`12000` = 120,00 TJS). Ошибки в формате RFC 9457
ProblemDetails с полем `code`.

## Cloud API: сотрудники (JWT Bearer)

Tenant берётся только из JWT. Чужие объекты возвращают **404**.

| Метод | Путь | Назначение |
|-------|------|-----------|
| POST | `/api/v1/auth/login` | `{email,password}` → access (15 мин) + refresh (7 дней, ротация) |
| POST | `/api/v1/auth/refresh` | `{refreshToken}` → новая пара. Повтор старого refresh отзывает всю цепочку |
| POST | `/api/v1/auth/logout` | отзыв refresh |
| GET | `/api/v1/me` | пользователь, организация, локации (зоны, тарифы, статус Edge) |
| GET | `/api/v1/locations/{locationId}/devices` | устройства с эффективным статусом и активной сессией |
| GET | `/api/v1/devices/{deviceId}` | карточка: инвентаризация, heartbeat, статус |
| GET | `/api/v1/devices/{deviceId}/commands` | последние 50 команд |
| POST | `/api/v1/devices/{deviceId}/commands` | `ShowMessage {title≤80, message≤500}` или `LockTestMode {lock, reason?}`; `ttlSeconds` 10–3600 (по умолчанию 120); `commandId` для идемпотентности |
| GET | `/api/v1/devices/{deviceId}/sessions` | последние 20 сессий |
| POST | `/api/v1/devices/{deviceId}/sessions` | запрос старта; тело необязательно: `{durationMinutes?: 1..1440}` — лимит времени (без него — открытая сессия, оплата по факту). **202**, `state=Created`; `Active` и `plannedEndAtUtc` приходят по событию от Edge. **409**, если открытая сессия уже есть; **400** — лимит вне диапазона |
| POST | `/api/v1/sessions/{sessionId}/end` | запрос завершения (идемпотентно): **202**, или **200** если уже завершена/запрошена |
| GET | `/api/v1/live` | `devices.view`: поток Server-Sent Events. События: `ready`; `change` с `{topic, locationId, deviceId, id}`, где topic — `devices`/`commands`/`sessions`/`audit`/`edges`/`staff` (аудит — только с `audit.view`, персонал — только с `staff.manage`); `resync` — клиент отстал, перечитать всё; `ping` раз в 15 с; `reauth` — токен истёк или доступ отозван, поток закрывается. Только tenant сотрудника. Admin Web подключается через BFF `/api/live` |
| POST | `/api/v1/sessions/{sessionId}/extend` | `{minutes: 1..720}` — продление сессии с лимитом (суммарно не больше 24 ч). **202**; новое `plannedEndAtUtc` приходит событием `SessionExtended` от Edge. **409** — сессия не идёт или без лимита |
| GET | `/api/v1/audit?locationId=&target=device:{id}&limit=` | журнал аудита (новые сверху) |
| POST | `/api/v1/enrollment-tokens/device` | `enrollment.manage`. `{locationId, zoneId, displayName, simulated}` → одноразовый токен (24 ч) |
| POST | `/api/v1/enrollment-tokens/edge` | `enrollment.manage`. `{locationId, name}` → одноразовый токен Edge |
| POST | `/api/v1/me/password` | любой сотрудник: `{currentPassword, newPassword}` → новые токены; прочие сессии отзываются |
| GET | `/api/v1/staff` | `staff.manage`: сотрудники организации |
| POST | `/api/v1/staff` | `staff.manage`: `{email, displayName, role}` → `{user, temporaryPassword}` (показывается один раз) |
| POST | `/api/v1/staff/{id}/role` | `staff.manage`: `{role}`; последнего активного Owner понизить нельзя |
| POST | `/api/v1/staff/{id}/deactivate`, `/activate` | `staff.manage`: отключение действует сразу; себя отключить нельзя |
| POST | `/api/v1/staff/{id}/reset-password` | `staff.manage`: временный пароль, все сессии сотрудника отозваны |

### Роли и права (ТЗ §8)

| Право | Owner | Admin | Operator |
|-------|:-----:|:-----:|:--------:|
| `devices.view` — устройства, команды, история сессий | ✓ | ✓ | ✓ |
| `devices.command` — ShowMessage, LockTestMode | ✓ | ✓ | ✓ |
| `sessions.manage` — старт/стоп сессий | ✓ | ✓ | ✓ |
| `audit.view` — журнал аудита | ✓ | ✓ | ✓ |
| `enrollment.manage` — токены Edge и ПК | ✓ | ✓ | — |
| `staff.manage` — персонал | ✓ | — | — |

Токен проверяется по БД на каждом запросе (активность, роль, версия токенов): отключение,
смена роли или пароля действуют сразу. С временным паролем доступны только `/me` и `/me/password`.

Восстановление доступа (на сервере): `docker compose exec cloud-api dotnet ClubOS.CloudApi.dll admin reset-password <email>`.
| GET | `/health/live`, `/health/ready` | liveness / readiness (PostgreSQL) |

Rate limit на `/auth/*` и enrollment: `RateLimits:AuthPerMinute` (по умолчанию 20 запросов в минуту с одного IP).

## Cloud API: Edge (`Authorization: ClubOS-Sig <JWS>`)

| Метод | Путь | Назначение |
|-------|------|-----------|
| POST | `/api/v1/edge/enroll` | анонимно: `{enrollmentToken, certificateSigningRequestPem}` → edgeId, сертификат, CA |
| GET | `/api/v1/edge/config` | локация, зоны и тарифы, устройства (кэш для offline) |
| POST | `/api/v1/edge/sync` | `{events: EventEnvelope[]}` (≤500) → `{accepted, duplicates, rejected}`. Идемпотентно по `eventId` |
| GET | `/api/v1/edge/commands?waitSeconds=0..25` | long-poll очереди Cloud→Edge (неподтверждённые, непросроченные) |
| POST | `/api/v1/edge/commands/ack` | `{ids}` → подтверждение получения |
| POST | `/api/v1/edge/status` | снимок статусов устройств и размер outbox (не durable) |
| POST | `/api/v1/edge/devices/enroll` | пересылка enrollment агента (токен привязан к локации Edge) |

Типы событий sync: `SessionStarted` (с `plannedEndAtUtc` для сессии с лимитом), `SessionStartRejected`,
`SessionExtended`, `SessionEnded` (с `reason`: `staff` / `timeLimit`), `CommandStateChanged`,
`DeviceConnectivityChanged` (payload — см. `packages/contracts-dotnet/Events.cs`).

Сессия с лимитом времени. Отсчёт идёт от фактического старта на Edge. По плановому окончанию таймер Edge
завершает сессию сам, в том числе без связи с Cloud: актор `system:edge-timer`, причина `timeLimit`.
Время окончания всегда равно плановому, даже если Edge был выключен, поэтому клиент не платит за лишнее время.
Если сотрудник завершает сессию позже планового окончания, время окончания тоже ограничивается плановым.

## Edge: API агентов (порт 7070, `Authorization: ClubOS-Sig <JWS>` ключом устройства)

| Метод | Путь | Назначение |
|-------|------|-----------|
| POST | `/agent/v1/enroll` | анонимно: `DeviceEnrollRequest` → `DeviceEnrollResponse` (через Cloud) |
| POST | `/agent/v1/heartbeat` | `HeartbeatMessage` каждые 10 с (статус агента `Idle` / `Locked` / `Maintenance`); инвентаризация примерно раз в 5 мин. В ответе тот же `state` |
| GET | `/agent/v1/commands?waitSeconds=0..25&sessionStamp=` | long-poll команд устройства (повторная выдача Delivered возможна, агент дедуплицирует). В ответе `state` (`AgentDeviceState`): имя ПК и клуба, часы Edge, активная сессия, итог последней. Если `sessionStamp` агента устарел (старт, продление, завершение), ответ приходит сразу |
| POST | `/agent/v1/commands/{commandId}/result` | `{state: Acknowledged/Succeeded/Failed, error?}`, переходы только вперёд |
| GET | `/health` | состояние Edge, связь с Cloud, размер outbox |

## Edge: локальный admin API (127.0.0.1:7071, Bearer из `edge-data/local-admin.token`)

Используется `edge-cli`: `GET /local/v1/status`, `GET /local/v1/devices`, `GET /local/v1/sessions[?active=true]`,
`POST /local/v1/sessions {deviceId, actor, durationMinutes?}`, `POST /local/v1/sessions/{id}/end {actor}`,
`POST /local/v1/sessions/{id}/extend {minutes, actor}`.
