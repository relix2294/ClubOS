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
| POST | `/api/v1/devices/{deviceId}/sessions` | запрос старта; тело необязательно: `{durationMinutes?: 1..1440}` — лимит времени (без него — открытая сессия, оплата по факту). **202**, `state=Created`; `Active` и `plannedEndAtUtc` приходят по событию от Edge. **409**, если открытая сессия уже есть; **400** — лимит вне диапазона. `packageId` — пакет зоны ПК: лимит и цена из пакета (`durationMinutes` не передаётся); **409** `package_zone_mismatch`, `package_not_available` (вне окна начала), **404** — пакет отключён. Снимок тарифа сессии: цена, периоды, смещение местного времени (`utcOffsetMinutes`), пакет (`packageName`, `packageMinutes`, `packagePriceMinorUnits`) |
| POST | `/api/v1/sessions/{sessionId}/end` | запрос завершения (идемпотентно): **202**, или **200** если уже завершена/запрошена |
| POST | `/api/v1/devices/{deviceId}/revoke` | `enrollment.manage`: удалить (отозвать) устройство. **409**, если идёт сессия. Edge получает `RevokeDevice`, история сохраняется |
| POST | `/api/v1/edges/{edgeId}/revoke` | `enrollment.manage`: отключить Edge. Его запросы получают **401**; для локации нужен новый Edge |
| GET | `/api/v1/locations/{locationId}/diskless-candidates` | `enrollment.manage`: бездисковые ПК, ожидающие подтверждения (D-018): MAC, имя в Windows, IP, когда загружался |
| POST | `/api/v1/diskless-candidates/{id}/approve` | `enrollment.manage`: `{displayName, zoneId}` → устройство с `hardwareId` (MAC); Edge получает `RefreshConfig` и выдаёт ПК сертификат при следующей попытке загрузки (≤ 10 с) |
| POST | `/api/v1/diskless-candidates/{id}/dismiss` | `enrollment.manage`: убрать из списка (ПК появится снова при следующей загрузке) |
| GET | `/api/v1/pki/ca` | `enrollment.manage`: `{fingerprintSha256, expiresAtUtc}` — отпечаток CA организации для `install-agent.ps1 -EdgeCaFingerprint` |
| GET | `/api/v1/live` | `devices.view`: поток Server-Sent Events. События: `ready`; `change` с `{topic, locationId, deviceId, id}`, где topic — `devices`/`commands`/`sessions`/`audit`/`edges`/`staff` (аудит — только с `audit.view`, персонал — только с `staff.manage`); `resync` — клиент отстал, перечитать всё; `ping` раз в 15 с; `reauth` — токен истёк или доступ отозван, поток закрывается. Только tenant сотрудника. Admin Web подключается через BFF `/api/live` |
| POST | `/api/v1/sessions/{sessionId}/extend` | `{minutes: 1..720}` — продление сессии с лимитом (суммарно не больше 24 ч). **202**; новое `plannedEndAtUtc` приходит событием `SessionExtended` от Edge. **409** — сессия не идёт или без лимита |
| GET | `/api/v1/audit?locationId=&target=device:{id}&limit=` | журнал аудита (новые сверху) |
| POST | `/api/v1/enrollment-tokens/device` | `enrollment.manage`. `{locationId, zoneId, displayName, simulated}` → одноразовый токен (24 ч) |
| POST | `/api/v1/enrollment-tokens/edge` | `enrollment.manage`. `{locationId, name}` → одноразовый токен Edge |
| POST | `/api/v1/me/password` | любой сотрудник: `{currentPassword, newPassword}` → новые токены; прочие сессии отзываются |
| POST | `/api/v1/auth/login` | при включённой 2FA вместо токенов: `{mfaRequired: true, mfaToken, expiresAtUtc}` (5 мин) |
| POST | `/api/v1/auth/mfa` | анонимно: `{mfaToken, code}` или `{mfaToken, recoveryCode}` → токены. Не больше 5 попыток на `mfaToken`; код одного 30-секундного шага принимается один раз |
| GET | `/api/v1/me/mfa` | `{enabled, required, recoveryCodesLeft, enabledAtUtc}` |
| POST | `/api/v1/me/mfa/setup` | секрет TOTP (Base32) и `otpauth://` URI для QR; действует 15 минут до подтверждения |
| POST | `/api/v1/me/mfa/enable` | `{code}` → `{session: LoginResponse, recoveryCodes[10]}`; прочие сессии отзываются |
| POST | `/api/v1/me/mfa/disable` | `{password, code \| recoveryCode}`; нельзя, если роль требует MFA (**409**) |
| POST | `/api/v1/me/mfa/recovery-codes` | `{code}` → новые 10 кодов, старые недействительны |
| POST | `/api/v1/staff/{id}/reset-mfa` | `staff.manage`: отключить 2FA сотрудника (потерял телефон), все его сессии отозваны; себе — нельзя |
| GET | `/api/v1/staff` | `staff.manage`: сотрудники организации |
| POST | `/api/v1/staff` | `staff.manage`: `{email, displayName, role, locationIds?}` → `{user, temporaryPassword}` (показывается один раз). Без `locationIds` — доступ ко всем локациям |
| POST | `/api/v1/staff/{id}/locations` | `staff.manage`: `{allLocations: true}` или `{allLocations: false, locationIds: [...]}`. Действует со следующего запроса сотрудника; Owner всегда видит все локации (**409**) |
| POST | `/api/v1/locations` | `locations.manage`: `{name, timezone (IANA), currency (ISO 4217), zones: [{name, pricePerHourMinorUnits}]}` |
| POST | `/api/v1/locations/{id}/zones` | `locations.manage`: `{name, pricePerHourMinorUnits}` — новая зона (до 20 на локацию) |
| POST | `/api/v1/zones/{id}` | `locations.manage`: `{name, pricePerHourMinorUnits}`. Смена цены увеличивает версию правила; идущие сессии досчитываются по снимку тарифа; Edge получает цену при обновлении конфигурации |
| POST | `/api/v1/zones/{id}/periods` | `locations.manage`: `{periods: [{days, startMinute, endMinute, pricePerHourMinorUnits}]}` — заменить цены по времени (до 12). `days` — маска (пн = 1 … вс = 64, 127 — все), минуты от полуночи, `start > end` — через полночь. Минута сессии стоит цену первого подходящего периода по местному времени её начала, вне периодов — цена зоны. Изменение — новая версия правила, аудит `tariff.periods_changed`. **400** `invalid_period`, `too_many_periods` |
| POST | `/api/v1/zones/{id}/packages` | `locations.manage`: `{name, durationMinutes, priceMinorUnits, availableFromMinute?, availableToMinute?, isActive?}` — пакет зоны (до 20). Окно — когда пакет можно начать (через полночь, если начало больше конца). **409** `duplicate_package` |
| POST | `/api/v1/packages/{id}` | `locations.manage`: то же тело — изменить или отключить (`isActive: false`). Начатые сессии считаются по своему снимку |
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
| `locations.manage` — локации, зоны, тарифы | ✓ | — | — |

**Права по локациям.** Owner и сотрудники с «все локации» видят все локации организации. Остальные видят
только назначенные: чужая локация, её устройства, сессии и события отвечают **404**, как объекты другой
организации. Аудит без локации (персонал, входы) виден только при доступе ко всей организации. Live-поток
фильтруется так же.

Токен проверяется по БД на каждом запросе (активность, роль, версия токенов): отключение,
смена роли или пароля действуют сразу. С временным паролем доступны только `/me` и `/me/password`.

Восстановление доступа (на сервере): `docker compose exec cloud-api dotnet ClubOS.CloudApi.dll admin reset-password <email>`,
при потере телефона с 2FA — `admin reset-mfa <email>`.

**2FA (TOTP, RFC 6238).** Обязательна для ролей из `Auth:MfaRequiredRoles` (на VPS по умолчанию Owner, Admin;
в dev-стеке добровольная). Пока сотрудник такой роли не настроил 2FA, его токен несёт claim `mfs=1`: доступны
только `/me`, `/me/password` и `/me/mfa/*`, как при временном пароле. Секрет TOTP хранится зашифрованным
(AES-256-GCM, ключ из `Auth:MfaEncryptionKey` или выведен из ключа подписи). Коды восстановления хранятся
хэшами и работают один раз.
| GET | `/health/live`, `/health/ready` | liveness / readiness (PostgreSQL) |

Rate limit на `/auth/*` и enrollment: `RateLimits:AuthPerMinute` (по умолчанию 20 запросов в минуту с одного IP).

## Cloud API: касса и отчёты (JWT Bearer)

Деньги — целые minor units (1 TJS = 100). Операции иммутабельны: ошибка исправляется новой операцией (ТЗ §12.2.1),
UPDATE/DELETE в `cash_operations` запрещены триггером БД. Изменения идут в транзакции с блокировкой строк
открытой смены и сессии: параллельные оплаты не превышают долг. `idempotencyKey` (8–64 символа, необязателен)
защищает от двойного списания: повтор с тем же ключом возвращает уже созданную операцию (**200**).

| Метод | Путь | Назначение |
|-------|------|-----------|
| GET | `/api/v1/locations/{locationId}/cash` | `cash.operate`: открытая смена с итогами, сессии к расчёту (`dueMinorUnits` > 0 — долг, < 0 — переплата), операции смены |
| POST | `/api/v1/locations/{locationId}/cash/shifts` | `cash.operate`: открыть смену `{openingCashMinorUnits}`. **409** `shift_already_open` — одна открытая смена на локацию (уникальный индекс) |
| POST | `/api/v1/cash/shifts/{shiftId}/close` | `cash.operate`: закрыть `{countedCashMinorUnits, note?}` → фиксируются ожидаемые наличные и расхождение (`discrepancyMinorUnits`: минус — недостача) |
| POST | `/api/v1/sessions/{sessionId}/payments` | `cash.operate`: `{amountMinorUnits, method: Cash/Card/Balance, idempotencyKey?, clientId?}`, частичная оплата допустима. Начисление: завершённая — итог Edge; идущая с лимитом — до планового окончания (предоплата, продление добавляет долг); без лимита — только после завершения (**409** `session_not_payable`). **409** `shift_not_open`, `session_paid`; **400** `amount_exceeds_due`. `Balance` — списание с баланса клиента (`clientId` или клиент сессии; **400** `client_required`, **409** `insufficient_balance`, `client_blocked`, `currency_mismatch`); в кассу наличных не попадает |
| POST | `/api/v1/sessions/{sessionId}/refunds` | `cash.operate`: `{amountMinorUnits, method, reason, idempotencyKey?}`. Переплату возвращает любой кассир; больше переплаты — только с `cash.refund` (иначе **403**). Не больше оплаченного; наличными — не больше, чем в кассе. `Balance` — на баланс клиента (`clientId`, иначе клиент сессии или того, кто платил с баланса) |
| POST | `/api/v1/locations/{locationId}/cash/movements` | `cash.operate`: `{kind: CashIn/CashOut, amountMinorUnits, reason}` — внесение (размен) или изъятие (инкассация); изъятие не больше наличных в кассе |
| GET | `/api/v1/locations/{locationId}/cash/shifts?limit=` | `reports.view`: история смен с итогами и расхождениями |
| GET | `/api/v1/cash/shifts/{shiftId}` | `reports.view`: смена и все её операции |
| GET | `/api/v1/reports/revenue?locationId=&from=ГГГГ-ММ-ДД&to=` | `reports.view`: по дням в часовом поясе локации (≤ 92 дней): сессий завершено, начислено, наличные, карта, возвраты, итог; `unpaidMinorUnits` — долг по сессиям периода |

Права: `cash.operate` — Owner, Admin, Operator; `cash.refund` и `reports.view` — Owner, Admin. Аудит: `cash.shift_opened`,
`cash.shift_closed`, `cash.payment` и `cash.refund` (target `device:<id>`), `cash.cash_in`, `cash.cash_out`.
Live-поток: тема `cash` (только с `cash.operate`).

Итоги смены (`totals`): `revenueMinorUnits` = оплаты сессий наличными, картой и с баланса минус возвраты;
`expectedCashMinorUnits` включает пополнения балансов наличными; `topUpCashMinorUnits`/`topUpCardMinorUnits` —
пополнения (аванс, не выручка), `balancePaymentsMinorUnits`/`balanceRefundsMinorUnits` — оплаты и возвраты балансом.
В отчёте по дням: `balanceMinorUnits` (оплаты с баланса, входят в `netMinorUnits`) и `topUpsMinorUnits` (не входят).

## Cloud API: клиенты и балансы (JWT Bearer, `cash.operate`)

Клиенты общие для организации (сеть клубов), телефон уникален в организации и хранится цифрами (7–15).
Баланс — аванс клиента в валюте локации, где он зарегистрирован; не уходит в минус и не больше 1 000 000.
Журнал баланса `client_ledger` иммутабелен (триггер БД), каждая запись хранит остаток после неё.

| Метод | Путь | Назначение |
|-------|------|-----------|
| GET | `/api/v1/clients?query=` | Поиск по цифрам телефона (от 3 цифр) или по имени (без учёта регистра), до 50 клиентов |
| POST | `/api/v1/clients` | `{phone, displayName, note?, locationId}` → **201**. **409** `client_exists`, **400** `invalid_phone` |
| GET | `/api/v1/clients/{clientId}` | `{client, ledger}` — последние 200 записей журнала |
| POST | `/api/v1/clients/{clientId}` | `{displayName?, note?, isBlocked?}`; блокировка — `cash.refund`. Заблокированному нельзя начать сессию и тратить баланс |
| POST | `/api/v1/clients/{clientId}/topups` | `{locationId, amountMinorUnits, method: Cash/Card, idempotencyKey?}` — пополнение в открытой смене локации (операция `BalanceTopUp`); **409** `shift_not_open` |
| POST | `/api/v1/clients/{clientId}/adjustments` | `cash.refund`: `{amountMinorUnits ≠ 0, reason}` — бонус или исправление, в кассу не попадает; **409** `balance_out_of_range` |

Сессия на клиента: `POST /api/v1/devices/{id}/sessions {durationMinutes?, clientId?, packageId?}` (**409** `client_blocked`);
`SessionView.clientId`, в кассе у строки к расчёту — `clientName` и `clientBalanceMinorUnits`.
Аудит: `client.created` (телефон маскирован), `client.updated`, `client.blocked`, `client.unblocked`, `client.topup`,
`client.adjustment`. Live: тема `cash`.

## Cloud API: бар / POS (JWT Bearer)

Товары — по локации (D-021). Продажа — `cash.operate` в открытой смене; каталог, склад и возврат чека —
`cash.refund`. Чек — одна кассовая операция `ProductSale` (возврат — `ProductRefund`) с позициями по цене на
момент продажи. Блокировки: смена → товары (по Id) → клиент; остаток не уходит в минус. Журнал склада и
позиции чеков иммутабельны (триггеры БД).

| Метод | Путь | Назначение |
|-------|------|-----------|
| GET | `/api/v1/locations/{id}/products?all=` | Витрина (активные); `all=true` — с отключёнными (только `cash.refund`) |
| POST | `/api/v1/locations/{id}/products` | `cash.refund`: `{name, category?, priceMinorUnits, trackStock?, isActive?}`. **409** `duplicate_product` |
| POST | `/api/v1/products/{id}` | `cash.refund`: изменить / снять с продажи (`isActive: false`) |
| POST | `/api/v1/products/{id}/stock` | `cash.refund`: `{kind: Receipt/WriteOff/Count, quantity, reason}` — приход, списание, инвентаризация (quantity — пересчитанный остаток). Основание обязательно, кроме прихода. **409** `insufficient_stock`, `stock_not_tracked` |
| GET | `/api/v1/products/{id}/movements` | `cash.refund`: последние 200 движений склада |
| POST | `/api/v1/locations/{id}/sales` | `{items: [{productId, quantity}], method: Cash/Card/Balance, clientId?, idempotencyKey?}` → **201** чек. **409** `shift_not_open`, `insufficient_stock`, `product_unavailable`, `insufficient_balance`; **400** `client_required` |
| GET | `/api/v1/locations/{id}/sales?shiftId=` | Чеки смены (по умолчанию — открытой) |
| POST | `/api/v1/sales/{id}/refund` | `cash.refund`: `{reason, idempotencyKey?}` — возврат целиком в открытой смене тем же способом, товары на склад, баланс клиенту. **409** `sale_refunded`, `insufficient_cash` |

Итоги смены: `productSalesMinorUnits`, `productRefundsMinorUnits` (входят в выручку; наличные — в «наличных по
учёту»); отчёт по дням: `productsMinorUnits` (входит в `netMinorUnits`). Аудит: `product.created`, `product.updated`,
`stock.receipt`, `stock.writeoff`, `stock.count`, `pos.sale`, `pos.refund`.

## Cloud API: бронирования (JWT Bearer, `sessions.manage`)

Брони ПК (D-020). Пока бронь ждёт гостя (`Booked`), интервалы одного ПК не пересекаются — ограничение БД
(`EXCLUDE USING gist`, расширение `btree_gist`). За 15 минут до начала ПК держится: обычный старт сессии —
**409** `device_booked`, сессия с лимитом, заходящая на будущую бронь, — **409** `booking_conflict`.
Гость, опоздавший больше чем на 15 минут, — `NoShow` (снимается при чтении и проверках).

| Метод | Путь | Назначение |
|-------|------|-----------|
| GET | `/api/v1/locations/{locationId}/bookings?date=ГГГГ-ММ-ДД` | Брони местного дня (по умолчанию сегодня), все статусы |
| POST | `/api/v1/locations/{locationId}/bookings` | `{deviceId, startsAt: "ГГГГ-ММ-ДДTчч:мм" (местное время), durationMinutes 15–1440, clientId?, guestName?, guestPhone?, note?}` → **201**. Имя по умолчанию — клиента. **409** `booking_overlap`, `session_overlap`, `client_blocked`; **400** `start_in_past`, `too_far` (> 30 дней), `invalid_start` |
| POST | `/api/v1/bookings/{id}/start` | Гость пришёл: сессия на ПК брони (клиент брони, лимит до конца брони; раньше начала — полная длительность), бронь `Started` в той же транзакции. **409** `booking_not_due` (раньше чем за 15 мин), `booking_expired`, `booking_closed` |
| POST | `/api/v1/bookings/{id}/cancel` | `{reason?}` → `Cancelled` |

`DeviceView.nextBooking` — ближайшая ждущая бронь ПК (`{bookingId, guestName, startsAtUtc, endsAtUtc}`).
Аудит: `booking.created`, `booking.cancelled`; старт по брони — `session.start` с `bookingId`. Live: темы `sessions`, `devices`.

## Cloud API: Edge (`Authorization: ClubOS-Sig <JWS>`)

Токен `ClubOS-Sig` (ES256, 60 с, одноразовый `jti`) подписан ключом Edge и привязан к запросу:
`htm` — метод, `htu` — путь с query, `bh` — base64url(SHA-256 тела), тело до 1 МБ. Несовпадение → 401.
Cloud принимает только привязанные токены (`Pki:RequireEdgeRequestBinding=true`). Клиенты: `SignedRequest.Create`.

| Метод | Путь | Назначение |
|-------|------|-----------|
| POST | `/api/v1/edge/enroll` | анонимно: `{enrollmentToken, certificateSigningRequestPem}` → edgeId, сертификат, CA |
| GET | `/api/v1/edge/config` | локация, зоны и тарифы, устройства (кэш для offline) |
| POST | `/api/v1/edge/sync` | `{events: EventEnvelope[]}` (≤500) → `{accepted, duplicates, rejected}`. Идемпотентно по `eventId` |
| GET | `/api/v1/edge/commands?waitSeconds=0..25` | long-poll очереди Cloud→Edge (неподтверждённые, непросроченные) |
| POST | `/api/v1/edge/commands/ack` | `{ids}` → подтверждение получения |
| POST | `/api/v1/edge/status` | снимок статусов устройств и размер outbox (не durable) |
| POST | `/api/v1/edge/devices/enroll` | пересылка enrollment агента (токен привязан к локации Edge) |
| POST | `/api/v1/edge/renew` | `{certificateSigningRequestPem}` → новый сертификат Edge (тот же ключ) |
| POST | `/api/v1/edge/devices/{deviceId}/renew` | продление сертификата устройства своей локации (агент → Edge → Cloud) |
| POST | `/api/v1/edge/server-certificate` | `{certificateSigningRequestPem, dnsNames, ipAddresses}` → TLS-сертификат Edge (serverAuth, SAN ≤ 16 имён и 16 IP), аудит `edge.tls_certificate_issued` |

Типы событий sync: `SessionStarted` (с `plannedEndAtUtc` для сессии с лимитом), `SessionStartRejected`,
`SessionExtended`, `SessionEnded` (с `reason`: `staff` / `timeLimit`), `CommandStateChanged`,
`DeviceConnectivityChanged` (payload — см. `packages/contracts-dotnet/Events.cs`).

Сессия с лимитом времени. Отсчёт идёт от фактического старта на Edge. По плановому окончанию таймер Edge
завершает сессию сам, в том числе без связи с Cloud: актор `system:edge-timer`, причина `timeLimit`.
Время окончания всегда равно плановому, даже если Edge был выключен, поэтому клиент не платит за лишнее время.
Если сотрудник завершает сессию позже планового окончания, время окончания тоже ограничивается плановым.

## Edge: API агентов (HTTPS 7443 и HTTP 7070, `Authorization: ClubOS-Sig <JWS>` ключом устройства)

HTTPS 7443: сертификат Edge выпускает dev CA организации (Edge запрашивает его после регистрации и продлевает
заранее). Агент доверяет только CA с отпечатком SHA-256 из Admin Web (`GET /api/v1/pki/ca`). HTTP 7070 — переходный,
закрывается `Edge:AgentHttpEnabled=false`. Токен привязан к методу, пути и телу, как у Edge→Cloud;
обязательность привязки на Edge — `Edge:RequireAgentRequestBinding`.

| Метод | Путь | Назначение |
|-------|------|-----------|
| POST | `/agent/v1/diskless/boot` | анонимно: `DisklessBootRequest` `{hardwareId (MAC), macAddresses, inventory, certificateSigningRequestPem}` → `{status: Approved/Pending/Conflict, enrollment?, retryAfterSeconds, message}`. Approved — сертификат локального CA Edge на deviceId подтверждённого ПК (D-018) |
| GET | `/agent/v1/ca` | анонимно: `{caCertificatePem}` — CA организации для первичной проверки по отпечатку (503 до регистрации Edge) |
| POST | `/agent/v1/enroll` | анонимно: `DeviceEnrollRequest` → `DeviceEnrollResponse` (через Cloud; в ответе и `caCertificatePem`) |
| POST | `/agent/v1/heartbeat` | `HeartbeatMessage` каждые 10 с (статус агента `Idle` / `Locked` / `Maintenance`); инвентаризация примерно раз в 5 мин. В ответе тот же `state` |
| GET | `/agent/v1/commands?waitSeconds=0..25&sessionStamp=` | long-poll команд устройства (повторная выдача Delivered возможна, агент дедуплицирует). В ответе `state` (`AgentDeviceState`): имя ПК и клуба, часы Edge, активная сессия, итог последней. Если `sessionStamp` агента устарел (старт, продление, завершение), ответ приходит сразу |
| POST | `/agent/v1/renew` | `{certificateSigningRequestPem}` → новый сертификат устройства; нужна связь Edge с Cloud (иначе **503**, агент повторит) |
| POST | `/agent/v1/commands/{commandId}/result` | `{state: Acknowledged/Succeeded/Failed, error?}`, переходы только вперёд |
| GET | `/health` | состояние Edge, связь с Cloud, размер outbox |

## Edge: локальный admin API (127.0.0.1:7071, Bearer из `edge-data/local-admin.token`)

Используется `edge-cli`: `GET /local/v1/status`, `GET /local/v1/devices`, `GET /local/v1/sessions[?active=true]`,
`POST /local/v1/sessions {deviceId, actor, durationMinutes?}`, `POST /local/v1/sessions/{id}/end {actor}`,
`POST /local/v1/sessions/{id}/extend {minutes, actor}`.
