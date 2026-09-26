"use client";

// Клиентский доступ к Cloud API через BFF Admin Web (/api/cloud/*).

export class ApiError extends Error {
  constructor(
    public readonly status: number,
    message: string,
    public readonly code?: string,
  ) {
    super(message);
  }
}

export const UNAUTHORIZED_EVENT = "clubos:unauthorized";

async function handle<T>(res: Response): Promise<T> {
  if (res.status === 401) {
    // AppShell слушает это событие и уводит на страницу входа.
    if (typeof window !== "undefined") window.dispatchEvent(new Event(UNAUTHORIZED_EVENT));
    throw new ApiError(401, "Сессия истекла — войдите снова.");
  }

  const text = await res.text();
  const data = text ? JSON.parse(text) : null;
  if (!res.ok) {
    const detail = data?.detail ?? data?.title ?? `Ошибка ${res.status}`;
    throw new ApiError(res.status, detail, data?.code);
  }

  return data as T;
}

export async function apiGet<T>(path: string, signal?: AbortSignal): Promise<T> {
  return handle<T>(await fetch(`/api/cloud/${path}`, { cache: "no-store", signal }));
}

export async function apiPost<T>(path: string, body?: unknown): Promise<T> {
  return handle<T>(
    await fetch(`/api/cloud/${path}`, {
      method: "POST",
      headers: { "content-type": "application/json", "x-clubos-csrf": "1" },
      body: JSON.stringify(body ?? {}),
    }),
  );
}
