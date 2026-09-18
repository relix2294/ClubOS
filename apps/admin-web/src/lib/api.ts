import type {
  AuditDto,
  CommandDto,
  DeviceDto,
  LocationDto,
  SessionSummary,
  TokenResponse,
} from "./types";

const BASE_URL =
  process.env.NEXT_PUBLIC_API_URL?.replace(/\/$/, "") ?? "http://localhost:5000";

export class ApiError extends Error {
  constructor(
    public readonly status: number,
    message: string,
  ) {
    super(message);
    this.name = "ApiError";
  }
}

async function request<T>(
  path: string,
  token: string | null,
  init?: RequestInit,
): Promise<T> {
  let response: Response;
  try {
    response = await fetch(`${BASE_URL}${path}`, {
      ...init,
      headers: {
        "Content-Type": "application/json",
        ...(token ? { Authorization: `Bearer ${token}` } : {}),
        ...(init?.headers ?? {}),
      },
      cache: "no-store",
    });
  } catch {
    throw new ApiError(0, "connection");
  }

  if (!response.ok) {
    const body = await response.text().catch(() => "");
    throw new ApiError(response.status, body);
  }

  const text = await response.text();
  return (text ? JSON.parse(text) : undefined) as T;
}

export function login(email: string, password: string): Promise<TokenResponse> {
  return request<TokenResponse>("/api/v1/auth/login", null, {
    method: "POST",
    body: JSON.stringify({ email, password }),
  });
}

export function getLocations(token: string): Promise<LocationDto[]> {
  return request<LocationDto[]>("/api/v1/locations", token);
}

export function getDevices(token: string, locationId?: string): Promise<DeviceDto[]> {
  const query = locationId ? `?locationId=${encodeURIComponent(locationId)}` : "";
  return request<DeviceDto[]>(`/api/v1/devices${query}`, token);
}

export function getDevice(token: string, id: string): Promise<DeviceDto> {
  return request<DeviceDto>(`/api/v1/devices/${id}`, token);
}

export function getDeviceCommands(token: string, id: string): Promise<CommandDto[]> {
  return request<CommandDto[]>(`/api/v1/devices/${id}/commands`, token);
}

export function issueShowMessage(
  token: string,
  deviceId: string,
  title: string,
  message: string,
): Promise<CommandDto> {
  return request<CommandDto>(`/api/v1/devices/${deviceId}/commands`, token, {
    method: "POST",
    body: JSON.stringify({ title, message }),
  });
}

export function startSession(
  token: string,
  deviceId: string,
  actor: string,
): Promise<SessionSummary> {
  return request<SessionSummary>("/api/v1/sessions/start", token, {
    method: "POST",
    body: JSON.stringify({ deviceId, actor, correlationId: crypto.randomUUID() }),
  });
}

export function endSession(token: string, sessionId: string): Promise<SessionSummary> {
  return request<SessionSummary>(`/api/v1/sessions/${sessionId}/end`, token, {
    method: "POST",
  });
}

export function getAudit(token: string, locationId?: string): Promise<AuditDto[]> {
  const query = locationId ? `?locationId=${encodeURIComponent(locationId)}` : "";
  return request<AuditDto[]>(`/api/v1/audit${query}`, token);
}
