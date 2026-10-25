import type { NextRequest } from "next/server";
import type { LoginResponse } from "@clubos/contracts";
import { forwardSessionChange } from "@/lib/server/cloud";

// Смена своего пароля: Cloud отзывает прежние сессии и выдаёт новые токены, BFF обновляет cookie.
export function POST(req: NextRequest) {
  return forwardSessionChange(req, "api/v1/me/password", (body) => ({ tokens: body as LoginResponse }));
}
