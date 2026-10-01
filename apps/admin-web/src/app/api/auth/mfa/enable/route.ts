import type { NextRequest } from "next/server";
import type { MfaEnableResponse } from "@clubos/contracts";
import { forwardSessionChange } from "@/lib/server/cloud";

// Включение MFA: новые токены (прочие сессии отозваны) и коды восстановления — их браузер покажет один раз.
export function POST(req: NextRequest) {
  return forwardSessionChange(req, "api/v1/me/mfa/enable", (body) => {
    const data = body as MfaEnableResponse;
    return { tokens: data.session, extra: { recoveryCodes: data.recoveryCodes } };
  });
}
