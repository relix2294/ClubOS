import { NextRequest, NextResponse } from "next/server";
import type { LoginResponse } from "@clubos/contracts";
import { ACCESS_COOKIE, cloudUrl, isSameOriginMutation, setAuthCookies } from "@/lib/server/cloud";

// Смена своего пароля. Cloud отзывает все прежние сессии и выдаёт новые токены —
// BFF сразу обновляет cookie, чтобы пользователь не вылетал из панели.
export async function POST(req: NextRequest) {
  if (!isSameOriginMutation(req)) {
    return NextResponse.json({ detail: "CSRF check failed" }, { status: 403 });
  }

  const token = req.cookies.get(ACCESS_COOKIE)?.value;
  if (!token) {
    return NextResponse.json({ detail: "Сессия истекла — войдите снова." }, { status: 401 });
  }

  let upstream: Response;
  try {
    upstream = await fetch(cloudUrl("api/v1/me/password"), {
      method: "POST",
      headers: { "content-type": "application/json", authorization: `Bearer ${token}` },
      body: await req.text(),
      cache: "no-store",
    });
  } catch {
    return NextResponse.json({ detail: "Cloud API недоступен." }, { status: 502 });
  }

  if (!upstream.ok) {
    return NextResponse.json(await upstream.json().catch(() => ({})), { status: upstream.status });
  }

  const tokens = (await upstream.json()) as LoginResponse;
  const res = NextResponse.json({ user: tokens.user });
  setAuthCookies(req, res, tokens);
  return res;
}
