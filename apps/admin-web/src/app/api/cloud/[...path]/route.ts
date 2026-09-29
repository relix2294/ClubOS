import { NextRequest, NextResponse } from "next/server";
import {
  ACCESS_COOKIE,
  REFRESH_COOKIE,
  clearAuthCookies,
  cloudUrl,
  isSameOriginMutation,
  refreshTokens,
  setAuthCookies,
} from "@/lib/server/cloud";
import type { LoginResponse } from "@clubos/contracts";

// Прокси Admin Web → Cloud API. Пропускает только staff-эндпоинты (allow-list),
// добавляет Bearer из httpOnly-cookie и прозрачно обновляет access-токен по refresh.
const ALLOWED = [
  /^me$/,
  /^locations\/[\w-]+\/devices$/,
  /^devices\/[\w-]+$/,
  /^devices\/[\w-]+\/(commands|sessions)$/,
  /^sessions\/[\w-]+\/(end|extend)$/,
  /^audit$/,
  /^enrollment-tokens\/(device|edge)$/,
  /^staff$/,
  /^staff\/[\w-]+\/(role|activate|deactivate|reset-password|reset-mfa|locations)$/,
  /^locations$/,
  /^locations\/[\w-]+\/zones$/,
  /^zones\/[\w-]+$/,
  /^me\/mfa(\/(setup|recovery-codes))?$/,
];
// Смена своего пароля идёт через /api/auth/password (BFF обновляет cookie).

type Ctx = { params: Promise<{ path: string[] }> };

async function forward(req: NextRequest, ctx: Ctx): Promise<NextResponse> {
  const { path } = await ctx.params;
  const joined = path.join("/");
  if (!ALLOWED.some((re) => re.test(joined))) {
    return NextResponse.json({ detail: "Not found" }, { status: 404 });
  }

  if (req.method !== "GET" && !isSameOriginMutation(req)) {
    return NextResponse.json({ detail: "CSRF check failed" }, { status: 403 });
  }

  const body = req.method === "GET" ? undefined : await req.text();
  const target = cloudUrl(`api/v1/${joined}${req.nextUrl.search}`);

  const call = (token: string | undefined) =>
    fetch(target, {
      method: req.method,
      headers: {
        ...(body ? { "content-type": "application/json" } : {}),
        ...(token ? { authorization: `Bearer ${token}` } : {}),
      },
      body,
      cache: "no-store",
    });

  let refreshed: LoginResponse | null = null;
  let upstream: Response;
  try {
    upstream = await call(req.cookies.get(ACCESS_COOKIE)?.value);
    if (upstream.status === 401) {
      const refreshToken = req.cookies.get(REFRESH_COOKIE)?.value;
      refreshed = refreshToken ? await refreshTokens(refreshToken) : null;
      if (refreshed) {
        upstream = await call(refreshed.accessToken);
      }
    }
  } catch {
    return NextResponse.json({ detail: "Cloud API недоступен." }, { status: 502 });
  }

  const text = await upstream.text();
  const res = new NextResponse(text || null, {
    status: upstream.status,
    headers: { "content-type": upstream.headers.get("content-type") ?? "application/json" },
  });

  if (refreshed) {
    setAuthCookies(req, res, refreshed);
  } else if (upstream.status === 401) {
    clearAuthCookies(res);
  }

  return res;
}

export const GET = forward;
export const POST = forward;
