import type { NextRequest } from "next/server";
import type { LoginResponse } from "@clubos/contracts";
import { forwardSessionChange } from "@/lib/server/cloud";

export function POST(req: NextRequest) {
  return forwardSessionChange(req, "api/v1/me/mfa/disable", (body) => ({ tokens: body as LoginResponse }));
}
