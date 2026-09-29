import { NextResponse, type NextRequest } from "next/server";
import type { LoginResponse } from "@clubos/contracts";
import { ACCESS_COOKIE, REFRESH_COOKIE, cloudUrl, refreshTokens, setAuthCookies } from "@/lib/server/cloud";

// Live-поток для браузера (DEVIATIONS D-008): BFF открывает SSE к Cloud с токеном из httpOnly-cookie
// и пробрасывает поток как есть. Браузер не видит JWT; EventSource сам отправляет cookie.
// Когда Cloud закрывает поток по истечении токена, браузер переподключается, а BFF обновляет токен.

export const dynamic = "force-dynamic";
export const runtime = "nodejs";

export async function GET(req: NextRequest): Promise<Response> {
  const call = (token: string | undefined) =>
    fetch(cloudUrl("api/v1/live"), {
      headers: { accept: "text/event-stream", ...(token ? { authorization: `Bearer ${token}` } : {}) },
      cache: "no-store",
      signal: req.signal, // браузер закрыл EventSource — закрываем поток к Cloud
    });

  let refreshed: LoginResponse | null = null;
  let upstream: Response;
  try {
    upstream = await call(req.cookies.get(ACCESS_COOKIE)?.value);
    if (upstream.status === 401) {
      await upstream.body?.cancel();
      const refreshToken = req.cookies.get(REFRESH_COOKIE)?.value;
      refreshed = refreshToken ? await refreshTokens(refreshToken) : null;
      if (refreshed) {
        upstream = await call(refreshed.accessToken);
      }
    }
  } catch {
    return NextResponse.json({ detail: "Cloud API недоступен." }, { status: 502 });
  }

  if (!upstream.ok || !upstream.body) {
    const res = NextResponse.json({ detail: "Live-поток недоступен." }, { status: upstream.status === 200 ? 502 : upstream.status });
    return res;
  }

  const res = new NextResponse(upstream.body, {
    status: 200,
    headers: {
      "content-type": "text/event-stream; charset=utf-8",
      "cache-control": "no-cache, no-transform",
      "x-accel-buffering": "no",
    },
  });
  if (refreshed) setAuthCookies(req, res, refreshed);
  return res;
}
