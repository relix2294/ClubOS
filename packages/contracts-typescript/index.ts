// Versioned контракты Admin Web <-> Cloud API (ClubOS CA, M0).
// Зеркало services/cloud-api/Api/Dtos.cs и ClubOS.Contracts (C#).
// При изменении — синхронно править обе стороны (ТЗ §4, §24).

export const SchemaVersions = {
  event: 1,
  command: 1,
  heartbeat: 1,
  enrollment: 1,
} as const;

// ---- Устройства (ТЗ §9) ----

export type DeviceStatus =
  | "Offline"
  | "Idle"
  | "Locked"
  | "Active"
  | "Reserved"
  | "Maintenance"
  | "Updating"
  | "Error";

export interface DeviceInventory {
  hostname: string;
  windowsVersion: string;
  cpu: string;
  ramMegabytes: number;
  ipv4: string;
  agentVersion: string;
}

export interface DeviceView {
  deviceId: string;
  displayName: string;
  locationId: string;
  zoneId: string;
  zoneName: string;
  status: DeviceStatus;
  /** true — симулированный ПК (ТЗ §3.4, обязательная маркировка). */
  simulated: boolean;
  lastHeartbeatUtc: string | null;
  inventory: DeviceInventory | null;
  enrolledAtUtc: string;
  activeSession: SessionView | null;
  /** Срок сертификата устройства; агент продлевает его сам за 30 дней. */
  certificateExpiresAtUtc: string;
  /** Бездисковый ПК (D-018): MAC загрузочной карты; сертификат выдаёт Edge при каждой загрузке. */
  hardwareId: string | null;
  /** Ближайшая бронь ПК (ждёт гостя), если есть. */
  nextBooking?: BookingBrief | null;
}

/** Бездисковый ПК, который загрузился в клубе и ждёт подтверждения (D-018). */
export interface PendingDisklessView {
  candidateId: string;
  locationId: string;
  hardwareId: string;
  mac: string;
  macAddresses: string[];
  hostname: string;
  ipv4: string | null;
  simulated: boolean;
  firstSeenUtc: string;
  lastSeenUtc: string;
}

// ---- Команды (ТЗ §10.2, §24.3) ----

export type CommandType = "ShowMessage" | "LockTestMode";

export type CommandState =
  | "Queued"
  | "Delivered"
  | "Acknowledged"
  | "Succeeded"
  | "Failed"
  | "Expired"
  | "Cancelled";

export const TerminalCommandStates: readonly CommandState[] = ["Succeeded", "Failed", "Expired", "Cancelled"];

export interface ShowMessagePayload {
  title: string;
  message: string;
}

export interface LockTestModePayload {
  lock: boolean;
  reason?: string | null;
}

export interface IssueCommandRequest {
  commandType: CommandType;
  title?: string;
  message?: string;
  lock?: boolean;
  reason?: string;
  ttlSeconds?: number;
  commandId?: string;
}

export interface CommandView {
  commandId: string;
  commandType: CommandType;
  deviceId: string;
  state: CommandState;
  issuedBy: string;
  issuedAtUtc: string;
  expiresAtUtc: string;
  updatedAtUtc: string;
  error: string | null;
  payload: ShowMessagePayload | LockTestModePayload;
}

// ---- Сессии (ТЗ §12) ----

export type SessionState =
  | "Created"
  | "Reserved"
  | "Ready"
  | "Active"
  | "Paused"
  | "Ending"
  | "Ended"
  | "Reconciled"
  | "Cancelled"
  | "Failed";

export type RoundingRule = "CeilingPerMinute";

export interface SessionView {
  sessionId: string;
  deviceId: string;
  state: SessionState;
  /** "cloud" — из Admin Web; "edge" — начата локально на Edge (offline / edge-cli). */
  origin: string;
  requestedAtUtc: string;
  startedAtUtc: string | null;
  endRequestedAtUtc: string | null;
  endedAtUtc: string | null;
  /** Цена за час в минимальных единицах (12000 = 120,00 TJS/час). */
  pricePerHourMinorUnits: number;
  currency: string;
  rounding: RoundingRule;
  /** Итоговая стоимость в минимальных единицах (после завершения). */
  totalMinorUnits: number | null;
  failureReason: string | null;
  startedBy: string;
  endedBy: string | null;
  /** Лимит времени (минуты); null — открытая сессия (постоплата). */
  durationMinutes: number | null;
  /** Плановое окончание по часам Edge; известно после старта, сдвигается продлениями. */
  plannedEndAtUtc: string | null;
  /** "staff" | "timeLimit" — причина завершения. */
  endReason: SessionEndReason | null;
  /** Клиент клуба, на которого начата сессия (оплата с его баланса). */
  clientId?: string | null;
  /** Периоды тарифа (ночь, выходные) из снимка; пусто — одна цена. */
  periods?: PricePeriod[];
  /** Смещение местного времени от UTC на старте (минуты). */
  utcOffsetMinutes?: number;
  packageName?: string | null;
  packageMinutes?: number | null;
  packagePriceMinorUnits?: number | null;
}

export type SessionEndReason = "staff" | "timeLimit";

export interface StartSessionRequest {
  durationMinutes?: number | null;
  clientId?: string | null;
  /** Пакет зоны: лимит и цена берутся из пакета, durationMinutes не передаётся. */
  packageId?: string | null;
}

export interface ExtendSessionRequest {
  minutes: number;
}

/** Ограничения лимита (как SessionLimits в C#). */
export const SessionLimits = {
  minDurationMinutes: 1,
  maxDurationMinutes: 24 * 60,
  minExtendMinutes: 1,
  maxExtendMinutes: 12 * 60,
} as const;

/** Остаток лимита в мс (не меньше 0) или null для открытой сессии. */
export function remainingMs(session: Pick<SessionView, "plannedEndAtUtc">, nowMs: number): number | null {
  if (!session.plannedEndAtUtc) return null;
  return Math.max(0, Date.parse(session.plannedEndAtUtc) - nowMs);
}

// ---- Audit (ТЗ §27.4) ----

export interface AuditEventView {
  auditId: string;
  occurredAtUtc: string;
  actor: string;
  actorDisplay: string;
  action: string;
  target: string;
  result: string;
  correlationId: string | null;
  details: Record<string, unknown> | null;
}

// ---- Auth / организация ----

export interface LoginRequest {
  email: string;
  password: string;
}

export type StaffRole = "Owner" | "Admin" | "Operator";

export type Permission =
  | "devices.view"
  | "devices.command"
  | "sessions.manage"
  | "audit.view"
  | "enrollment.manage"
  | "staff.manage"
  | "locations.manage"
  | "cash.operate"
  | "cash.refund"
  | "reports.view";

export interface UserView {
  userId: string;
  email: string;
  displayName: string;
  role: StaffRole;
  organizationId: string;
  organizationName: string;
  /** Права роли (пусто, пока не сменён временный пароль или не настроена обязательная MFA). Проверяются на backend. */
  permissions: Permission[];
  mustChangePassword: boolean;
  mfaEnabled: boolean;
  /** Роль требует MFA, а она не настроена: доступна только её настройка. */
  mfaSetupRequired: boolean;
}

// ---- Персонал (ТЗ §8) ----

export interface StaffMemberView {
  userId: string;
  email: string;
  displayName: string;
  role: StaffRole;
  isActive: boolean;
  mustChangePassword: boolean;
  createdAtUtc: string;
  lastLoginAtUtc: string | null;
  mfaEnabled: boolean;
  /** Доступ ко всем локациям (Owner — всегда). */
  allLocations: boolean;
  /** Назначенные локации при ограниченном доступе. */
  locationIds: string[];
}

export interface CreateStaffRequest {
  email: string;
  displayName: string;
  role: StaffRole;
  /** null/нет — все локации. */
  locationIds?: string[] | null;
}

export interface StaffLocationsRequest {
  allLocations: boolean;
  locationIds?: string[];
}

// ---- Локации и тарифы (locations.manage) ----

export interface ZoneInput {
  name: string;
  pricePerHourMinorUnits: number;
}

export interface CreateLocationRequest {
  name: string;
  timezone: string;
  currency: string;
  zones: ZoneInput[];
}

export interface TemporaryPasswordResponse {
  user: StaffMemberView;
  /** Показывается один раз. */
  temporaryPassword: string;
}

export interface ChangePasswordRequest {
  currentPassword: string;
  newPassword: string;
}

export interface LoginResponse {
  accessToken: string;
  expiresAtUtc: string;
  refreshToken: string;
  refreshExpiresAtUtc: string;
  user: UserView;
}

export interface ZoneView {
  zoneId: string;
  name: string;
  pricePerHourMinorUnits: number;
  /** Цена по времени суток и дням недели (первый подходящий период). */
  periods: PricePeriod[];
  /** Активные пакеты зоны (и неактивные — для locations.manage). */
  packages: TariffPackageView[];
}

/** Дни недели маской: пн = 1, вт = 2, ср = 4, чт = 8, пт = 16, сб = 32, вс = 64; 127 — все. */
export interface PricePeriod {
  days: number;
  /** Минуты от полуночи, [start, end); start > end — через полночь (22:00–08:00). */
  startMinute: number;
  endMinute: number;
  pricePerHourMinorUnits: number;
}

export interface TariffPackageView {
  packageId: string;
  zoneId: string;
  name: string;
  durationMinutes: number;
  priceMinorUnits: number;
  /** Окно начала по местному времени (минуты от полуночи); null — в любое время. */
  availableFromMinute: number | null;
  availableToMinute: number | null;
  isActive: boolean;
}

export interface TariffPackageInput {
  name: string;
  durationMinutes: number;
  priceMinorUnits: number;
  availableFromMinute?: number | null;
  availableToMinute?: number | null;
  isActive?: boolean;
}

export interface EdgeView {
  edgeId: string;
  name: string;
  online: boolean;
  lastSeenAtUtc: string | null;
  pendingOutboxEvents: number;
  enrolledAtUtc: string;
  certificateExpiresAtUtc: string;
}

export interface LocationView {
  locationId: string;
  name: string;
  timezone: string;
  currency: string;
  zones: ZoneView[];
  edges: EdgeView[];
}

export interface MeResponse {
  user: UserView;
  locations: LocationView[];
}

// ---- Enrollment ----

export interface DeviceEnrollmentTokenRequest {
  locationId: string;
  zoneId: string;
  displayName: string;
  simulated?: boolean;
}

export interface EdgeEnrollmentTokenRequest {
  locationId: string;
  name: string;
}

export interface EnrollmentTokenResponse {
  enrollmentToken: string;
  expiresAtUtc: string;
}

// ---- Деньги и тариф ----

/** Форматирует минимальные единицы в строку, напр. 200 -> "2,00". */
export function formatMinorUnits(minorUnits: number): string {
  const sign = minorUnits < 0 ? "-" : "";
  const abs = Math.abs(minorUnits);
  const whole = Math.trunc(abs / 100);
  const frac = abs % 100;
  return `${sign}${whole},${String(frac).padStart(2, "0")}`;
}

/**
 * Зеркало BillingCalculator (C#) для отображения текущей стоимости активной сессии.
 * Итог сессии всегда считает Edge; это значение — только предварительное отображение.
 */
export function calculateMinorUnits(pricePerHourMinorUnits: number, elapsedMs: number): number {
  if (elapsedMs <= 0) return 0;
  const totalSeconds = Math.ceil(elapsedMs / 1000);
  const minutes = Math.ceil(totalSeconds / 60);
  return Math.floor((minutes * pricePerHourMinorUnits) / 60);
}

export interface SessionPricing {
  pricePerHourMinorUnits: number;
  periods?: PricePeriod[];
  utcOffsetMinutes?: number;
  packageMinutes?: number | null;
  packagePriceMinorUnits?: number | null;
}

/** День недели в маске периода: пн = бит 0 … вс = бит 6 (JS getUTCDay: вс = 0). */
export function dayBit(jsDay: number): number {
  return 1 << ((jsDay + 6) % 7);
}

export function periodContains(p: PricePeriod, jsDay: number, minuteOfDay: number): boolean {
  if (p.startMinute < p.endMinute) {
    return (p.days & dayBit(jsDay)) !== 0 && minuteOfDay >= p.startMinute && minuteOfDay < p.endMinute;
  }
  const previous = (jsDay + 6) % 7;
  return ((p.days & dayBit(jsDay)) !== 0 && minuteOfDay >= p.startMinute) || ((p.days & dayBit(previous)) !== 0 && minuteOfDay < p.endMinute);
}

/** Цена часа в момент atMs по периодам снимка (как BillingCalculator.PricePerHourAt). */
export function pricePerHourAt(pricing: SessionPricing, atMs: number): number {
  const periods = pricing.periods ?? [];
  if (periods.length === 0) return pricing.pricePerHourMinorUnits;
  const local = new Date(atMs + (pricing.utcOffsetMinutes ?? 0) * 60_000);
  const minute = local.getUTCHours() * 60 + local.getUTCMinutes();
  return periods.find((p) => periodContains(p, local.getUTCDay(), minute))?.pricePerHourMinorUnits ?? pricing.pricePerHourMinorUnits;
}

/**
 * Зеркало BillingCalculator.CalculateMinorUnits(snapshot, start, elapsed): минуты вверх, каждая — по цене
 * периода её начала, пакет покрывает первые N минут своей ценой. Только для предварительного отображения.
 */
export function calculateSessionMinorUnits(pricing: SessionPricing, startedAtMs: number, elapsedMs: number): number {
  if (elapsedMs <= 0 && !(pricing.packageMinutes && pricing.packagePriceMinorUnits != null)) return 0;
  const minutes = elapsedMs <= 0 ? 0 : Math.ceil(Math.ceil(elapsedMs / 1000) / 60);
  let total = 0;
  let from = 0;
  if (pricing.packageMinutes && pricing.packageMinutes > 0 && pricing.packagePriceMinorUnits != null) {
    total = pricing.packagePriceMinorUnits;
    from = Math.min(minutes, pricing.packageMinutes);
  }
  let sum = 0;
  if ((pricing.periods ?? []).length === 0) {
    sum = (minutes - from) * pricing.pricePerHourMinorUnits;
  } else {
    for (let i = from; i < minutes; i++) sum += pricePerHourAt(pricing, startedAtMs + i * 60_000);
  }
  return total + Math.floor(sum / 60);
}

/** «22:00» из минут от полуночи; 1440 → «24:00». */
export function formatMinuteOfDay(minute: number): string {
  return `${String(Math.floor(minute / 60)).padStart(2, "0")}:${String(minute % 60).padStart(2, "0")}`;
}

// ---- Live-обновления Admin Web (SSE /api/v1/live, DEVIATIONS D-008) ----

export type LiveTopic = "devices" | "commands" | "sessions" | "audit" | "edges" | "staff" | "cash";

/** Подсказка «изменилось»: данные клиент перечитывает через REST. */
export interface LiveEvent {
  topic: LiveTopic;
  locationId: string | null;
  deviceId: string | null;
  id: string | null;
}

// ---- MFA (TOTP, ТЗ §8) ----

/** Ответ /auth/login при включённой MFA: нужен второй шаг /auth/mfa. */
export interface MfaChallengeResponse {
  mfaRequired: true;
  mfaToken: string;
  expiresAtUtc: string;
}

export interface MfaStatusView {
  enabled: boolean;
  required: boolean;
  recoveryCodesLeft: number;
  enabledAtUtc: string | null;
}

export interface MfaSetupResponse {
  /** Секрет в Base32 — для ручного ввода, если QR не сканируется. */
  secret: string;
  otpAuthUri: string;
}

export interface MfaEnableResponse {
  session: LoginResponse;
  recoveryCodes: string[];
}

export interface RecoveryCodesResponse {
  recoveryCodes: string[];
}

// ---- Касса и отчёты (ТЗ §12) ----

/** Balance — списание с баланса клиента (аванс); в кассу наличных не попадает. */
export type PaymentMethod = "Cash" | "Card" | "Balance";

export type CashOperationKind = "SessionPayment" | "Refund" | "CashIn" | "CashOut" | "BalanceTopUp";

export interface ShiftTotals {
  cashPaymentsMinorUnits: number;
  cardPaymentsMinorUnits: number;
  cashRefundsMinorUnits: number;
  cardRefundsMinorUnits: number;
  cashInMinorUnits: number;
  cashOutMinorUnits: number;
  /** Наличные по учёту: остаток на начало + все движения наличных. */
  expectedCashMinorUnits: number;
  /** Оплаты сессий минус возвраты (наличные, карта, баланс). Пополнения балансов — не выручка. */
  revenueMinorUnits: number;
  paymentCount: number;
  balancePaymentsMinorUnits: number;
  balanceRefundsMinorUnits: number;
  /** Пополнения балансов клиентов наличными / картой (в кассе, но аванс). */
  topUpCashMinorUnits: number;
  topUpCardMinorUnits: number;
}

export interface CashShiftView {
  shiftId: string;
  locationId: string;
  currency: string;
  openedBy: string;
  openedByName: string;
  openedAtUtc: string;
  openingCashMinorUnits: number;
  closedBy: string | null;
  closedByName: string | null;
  closedAtUtc: string | null;
  countedCashMinorUnits: number | null;
  /** Пересчитано минус по учёту: минус — недостача, плюс — излишек. */
  discrepancyMinorUnits: number | null;
  closeNote: string | null;
  totals: ShiftTotals;
}

export interface CashOperationView {
  operationId: string;
  shiftId: string;
  kind: CashOperationKind;
  method: PaymentMethod;
  /** Со знаком: плюс — деньги поступили, минус — ушли. */
  amountMinorUnits: number;
  currency: string;
  sessionId: string | null;
  deviceId: string | null;
  deviceName: string | null;
  reason: string | null;
  createdBy: string;
  createdByName: string;
  createdAtUtc: string;
  clientId: string | null;
  clientName: string | null;
}

/** Сессия с незакрытым расчётом: due > 0 — долг клиента, due < 0 — переплата к возврату. */
export interface PayableSessionView {
  sessionId: string;
  deviceId: string;
  deviceName: string;
  state: SessionState;
  startedAtUtc: string | null;
  endedAtUtc: string | null;
  plannedEndAtUtc: string | null;
  currency: string;
  chargeMinorUnits: number;
  paidMinorUnits: number;
  dueMinorUnits: number;
  clientId: string | null;
  clientName: string | null;
  clientBalanceMinorUnits: number | null;
}

export interface CashDeskView {
  locationId: string;
  currency: string;
  shift: CashShiftView | null;
  payable: PayableSessionView[];
  operations: CashOperationView[];
}

export interface RevenueDayView {
  /** ГГГГ-ММ-ДД в часовом поясе локации; у итоговой строки — "total". */
  date: string;
  sessionsEnded: number;
  chargedMinorUnits: number;
  cashMinorUnits: number;
  cardMinorUnits: number;
  refundsMinorUnits: number;
  netMinorUnits: number;
  /** Оплаты сессий с балансов клиентов. */
  balanceMinorUnits: number;
  /** Пополнения балансов (аванс, в итог не входит). */
  topUpsMinorUnits: number;
}

export interface RevenueReportView {
  locationId: string;
  currency: string;
  timezone: string;
  from: string;
  to: string;
  days: RevenueDayView[];
  totals: RevenueDayView;
  unpaidMinorUnits: number;
}

// ---- Клиенты и балансы ----

export interface ClientView {
  clientId: string;
  /** Только цифры с кодом страны: 992901234567. */
  phone: string;
  displayName: string;
  currency: string;
  balanceMinorUnits: number;
  isBlocked: boolean;
  note: string | null;
  createdAtUtc: string;
}

export type ClientLedgerKind = "TopUp" | "SessionPayment" | "SessionRefund" | "Adjustment";

export interface ClientLedgerView {
  entryId: string;
  kind: ClientLedgerKind;
  /** Со знаком: плюс — на баланс, минус — с баланса. */
  amountMinorUnits: number;
  balanceAfterMinorUnits: number;
  locationId: string | null;
  sessionId: string | null;
  reason: string | null;
  createdBy: string;
  createdByName: string;
  createdAtUtc: string;
}

export interface ClientDetailsView {
  client: ClientView;
  ledger: ClientLedgerView[];
}

export interface CreateClientRequest {
  phone: string;
  displayName: string;
  note?: string | null;
  locationId: string;
}

export interface UpdateClientRequest {
  displayName?: string | null;
  note?: string | null;
  isBlocked?: boolean | null;
}

export interface ClientTopUpRequest {
  locationId: string;
  amountMinorUnits: number;
  method: "Cash" | "Card";
  idempotencyKey?: string | null;
}

export interface ClientAdjustmentRequest {
  amountMinorUnits: number;
  reason: string;
}

// ---- Бронирования (D-020) ----

export type BookingStatus = "Booked" | "Started" | "Cancelled" | "NoShow";

export interface BookingBrief {
  bookingId: string;
  guestName: string;
  startsAtUtc: string;
  endsAtUtc: string;
}

export interface BookingView {
  bookingId: string;
  locationId: string;
  deviceId: string;
  deviceName: string;
  clientId: string | null;
  guestName: string;
  guestPhone: string | null;
  startsAtUtc: string;
  endsAtUtc: string;
  status: BookingStatus;
  note: string | null;
  sessionId: string | null;
  createdByName: string;
  createdAtUtc: string;
  cancelReason: string | null;
}

export interface CreateBookingRequest {
  deviceId: string;
  /** Местное время локации «ГГГГ-ММ-ДДTчч:мм». */
  startsAt: string;
  durationMinutes: number;
  clientId?: string | null;
  guestName?: string | null;
  guestPhone?: string | null;
  note?: string | null;
}

/** За сколько минут до начала бронь держит ПК и сколько ждём опоздавшего (как в Cloud). */
export const BookingRules = { holdMinutes: 15, graceMinutes: 15 } as const;
