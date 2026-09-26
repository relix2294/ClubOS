import { NextRequest, NextResponse } from "next/server";
import type { LoginResponse } from "@clubos/contracts";
import { cloudUrl, isSameOriginMutation, setAuthCookies } from "@/lib/server/cloud";

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

  const tokens = (await upstream.json()) as LoginResponse;
  const res = NextResponse.json({ user: tokens.user });
  setAuthCookies(req, res, tokens);
  return res;
}
