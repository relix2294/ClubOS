import { NextRequest, NextResponse } from "next/server";
import type { LoginResponse, MfaChallengeResponse } from "@clubos/contracts";
import { MFA_COOKIE, cloudUrl, isSameOriginMutation, isSecure, setAuthCookies } from "@/lib/server/cloud";

export async function POST(req: NextRequest) {
  if (!isSameOriginMutation(req)) {
    return NextResponse.json({ detail: "CSRF check failed" }, { status: 403 });
  }

  let upstream: Response;
  try {
    upstream = await fetch(cloudUrl("api/v1/auth/login"), {
      method: "POST",
      headers: { "content-type": "application/json", "x-forwarded-for": req.headers.get("x-forwarded-for") ?? "" },
      body: await req.text(),
      cache: "no-store",
    });
  } catch {
    return NextResponse.json({ detail: "Cloud API недоступен." }, { status: 502 });
  }

  if (!upstream.ok) {
    return NextResponse.json(await upstream.json().catch(() => ({})), { status: upstream.status });
  }

  const body = (await upstream.json()) as LoginResponse | MfaChallengeResponse;
  if ("mfaRequired" in body && body.mfaRequired) {
    // Пароль верный, нужен код. Токен challenge не отдаём в JS — только httpOnly-cookie для /api/auth/mfa.
    const res = NextResponse.json({ mfaRequired: true });
    res.cookies.set(MFA_COOKIE, body.mfaToken, {
      httpOnly: true,
      sameSite: "strict",
      secure: isSecure(req),
      path: "/api/auth/mfa",
      maxAge: Math.max(1, Math.floor((Date.parse(body.expiresAtUtc) - Date.now()) / 1000)),
    });
    return res;
  }

  const tokens = body as LoginResponse;
  const res = NextResponse.json({ user: tokens.user });
  setAuthCookies(req, res, tokens);
  return res;
}
