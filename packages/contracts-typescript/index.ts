// Versioned контракты Admin Web <-> Cloud API (ClubOS CA, M0).
// Зеркало ClubOS.Contracts (C#). При изменении — синхронно править обе стороны (ТЗ §4, §24).

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
  zoneId: string;
  zoneName: string;
  status: DeviceStatus;
  /** true — симулированный ПК (ТЗ §3.4, обязательная маркировка). */
  simulated: boolean;
  lastHeartbeatUtc: string | null;
  inventory: DeviceInventory | null;
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

export interface ShowMessagePayload {
  title: string;
  message: string;
}

export interface CommandView {
  commandId: string;
  commandType: CommandType;
  deviceId: string;
  state: CommandState;
  issuedBy: string;
  issuedAtUtc: string;
  expiresAtUtc: string;
  error: string | null;
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

export interface SessionSummary {
  sessionId: string;
  deviceId: string;
  state: SessionState;
  startedAtUtc: string;
  endedAtUtc: string | null;
  /** Цена за час в минимальных единицах (12000 = 120,00 TJS/час). */
  pricePerHourMinorUnits: number;
  currency: string;
  /** Итоговая стоимость в минимальных единицах (после завершения). */
  totalMinorUnits: number | null;
}

// ---- Audit (ТЗ §27.4) ----

export interface AuditEventView {
  auditId: string;
  occurredAtUtc: string;
  actor: string;
  action: string;
  target: string;
  result: string;
  correlationId: string | null;
}

// ---- Auth ----

export interface LoginRequest {
  email: string;
  password: string;
}

export interface LoginResponse {
  accessToken: string;
  expiresAtUtc: string;
}

/** Форматирует минимальные единицы в строку, напр. 200 -> "2,00". */
export function formatMinorUnits(minorUnits: number): string {
  const whole = Math.trunc(minorUnits / 100);
  const frac = Math.abs(minorUnits % 100);
  return `${whole},${String(frac).padStart(2, "0")}`;
}
