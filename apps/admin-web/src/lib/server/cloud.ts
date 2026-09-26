import "server-only";
import type { NextRequest, NextResponse } from "next/server";
import type { LoginResponse } from "@clubos/contracts";

// BFF: браузер никогда не видит JWT. Токены лежат в httpOnly-cookie Admin Web,
// а запросы к Cloud API идут с сервера Next.js.

export const ACCESS_COOKIE = "clubos_at";
export const REFRESH_COOKIE = "clubos_rt";

export function cloudUrl(path: string): string {
  const base = (process.env.CLUBOS_CLOUD_API_URL ?? "http://localhost:5080").replace(/\/+$/, "");
  return `${base}/${path.replace(/^\/+/, "")}`;
}

function isSecure(req: NextRequest): boolean {
  if (process.env.CLUBOS_COOKIE_SECURE === "true") return true;
  if (process.env.CLUBOS_COOKIE_SECURE === "false") return false;
  return (req.headers.get("x-forwarded-proto") ?? req.nextUrl.protocol.replace(":", "")) === "https";
}

export function setAuthCookies(req: NextRequest, res: NextResponse, tokens: LoginResponse): void {
  const secure = isSecure(req);
  const now = Date.now();
  res.cookies.set(ACCESS_COOKIE, tokens.accessToken, {
    httpOnly: true,
    sameSite: "lax",
    secure,
    path: "/",
    maxAge: Math.max(1, Math.floor((Date.parse(tokens.expiresAtUtc) - now) / 1000)),
  });
  res.cookies.set(REFRESH_COOKIE, tokens.refreshToken, {
    httpOnly: true,
    sameSite: "lax",
    secure,
    path: "/",
    maxAge: Math.max(1, Math.floor((Date.parse(tokens.refreshExpiresAtUtc) - now) / 1000)),
  });
}

export function clearAuthCookies(res: NextResponse): void {
  res.cookies.set(ACCESS_COOKIE, "", { path: "/", maxAge: 0 });
  res.cookies.set(REFRESH_COOKIE, "", { path: "/", maxAge: 0 });
}

/** Защита от CSRF для изменяющих запросов: только same-origin и с кастомным заголовком. */
export function isSameOriginMutation(req: NextRequest): boolean {
  if (req.headers.get("x-clubos-csrf") !== "1") return false;
  const origin = req.headers.get("origin");
  if (!origin) return false;
  const host = req.headers.get("x-forwarded-host") ?? req.headers.get("host");
  try {
    return new URL(origin).host === host;
  } catch {
    return false;
  }
}

export async function refreshTokens(refreshToken: string): Promise<LoginResponse | null> {
  const res = await fetch(cloudUrl("api/v1/auth/refresh"), {
    method: "POST",
    headers: { "content-type": "application/json" },
    body: JSON.stringify({ refreshToken }),
    cache: "no-store",
  });
  return res.ok ? ((await res.json()) as LoginResponse) : null;
}
