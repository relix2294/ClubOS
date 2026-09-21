// Типы Admin Web, зеркалят DTO Cloud API (camelCase JSON).

export type DeviceStatus =
  | "Offline"
  | "Idle"
  | "Locked"
  | "Active"
  | "Reserved"
  | "Maintenance"
  | "Updating"
  | "Error";

export type CommandState =
  | "Queued"
  | "Delivered"
  | "Acknowledged"
  | "Succeeded"
  | "Failed"
  | "Expired"
  | "Cancelled";

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

export interface TokenResponse {
  accessToken: string;
  refreshToken: string;
  accessTokenExpiresAtUtc: string;
  role: string;
}

export interface Zone {
  id: string;
  name: string;
}

export interface LocationDto {
  id: string;
  name: string;
  timezone: string;
  currency: string;
  zones: Zone[];
}

export interface DeviceDto {
  id: string;
  locationId: string;
  zoneId: string;
  displayName: string;
  simulated: boolean;
  status: DeviceStatus;
  lastHeartbeatUtc: string | null;
  hostname: string | null;
  windowsVersion: string | null;
  cpu: string | null;
  ramMegabytes: number | null;
  ipv4: string | null;
  agentVersion: string | null;
}

export interface CommandDto {
  id: string;
  deviceId: string;
  commandType: string;
  state: CommandState;
  issuedBy: string;
  issuedAtUtc: string;
  expiresAtUtc: string;
  correlationId: string;
  error: string | null;
  updatedAtUtc: string;
}

export interface PriceSnapshot {
  pricePerHourMinorUnits: number;
  currency: string;
  rounding: string;
  ruleVersion: number;
}

export interface SessionSummary {
  sessionId: string;
  deviceId: string;
  state: SessionState;
  startedAtUtc: string;
  endedAtUtc: string | null;
  priceSnapshot: PriceSnapshot;
  totalMinorUnits: number | null;
}

export interface AuditDto {
  id: string;
  occurredAtUtc: string;
  actor: string;
  action: string;
  target: string;
  result: string;
  correlationId: string | null;
}
