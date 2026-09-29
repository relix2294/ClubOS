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
}

export type SessionEndReason = "staff" | "timeLimit";

export interface StartSessionRequest {
  durationMinutes?: number | null;
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
  | "locations.manage";

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

// ---- Live-обновления Admin Web (SSE /api/v1/live, DEVIATIONS D-008) ----

export type LiveTopic = "devices" | "commands" | "sessions" | "audit" | "edges" | "staff";

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
