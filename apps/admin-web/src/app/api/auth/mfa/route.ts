import { NextRequest, NextResponse } from "next/server";
import type { LoginResponse } from "@clubos/contracts";
import { MFA_COOKIE, cloudUrl, isSameOriginMutation, setAuthCookies } from "@/lib/server/cloud";

// Второй шаг входа: код из приложения или код восстановления. Токен challenge лежит в httpOnly-cookie,
// его выдал /api/auth/login после верного пароля.
export async function POST(req: NextRequest) {
  if (!isSameOriginMutation(req)) {
    return NextResponse.json({ detail: "CSRF check failed" }, { status: 403 });
  }

  const mfaToken = req.cookies.get(MFA_COOKIE)?.value;
  if (!mfaToken) {
    return NextResponse.json({ detail: "Срок подтверждения истёк. Войдите заново." }, { status: 401 });
  }

  const { code, recoveryCode } = (await req.json().catch(() => ({}))) as { code?: string; recoveryCode?: string };
  let upstream: Response;
  try {
    upstream = await fetch(cloudUrl("api/v1/auth/mfa"), {
      method: "POST",
      headers: { "content-type": "application/json", "x-forwarded-for": req.headers.get("x-forwarded-for") ?? "" },
      body: JSON.stringify({ mfaToken, code, recoveryCode }),
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
  res.cookies.set(MFA_COOKIE, "", { path: "/api/auth/mfa", maxAge: 0 });
  return res;
}
