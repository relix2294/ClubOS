"use client";

import { calculateMinorUnits, remainingMs, type SessionView } from "@clubos/contracts";
import { formatDuration, formatMoney } from "@/lib/format";
import { t } from "@/lib/i18n";
import { useNow } from "@/lib/usePolling";

/** Порог предупреждения: как у индикатора на ПК (Player Shell). */
const SOON_MS = 5 * 60_000;

/**
 * Текущая длительность, остаток лимита и предварительная стоимость активной сессии.
 * Итог считает Edge; для сессии с лимитом стоимость не растёт после планового окончания.
 */
export function useSessionClock(session: SessionView | null | undefined) {
  const now = useNow();
  if (!session || !session.startedAtUtc) return null;
  const planned = session.plannedEndAtUtc ? Date.parse(session.plannedEndAtUtc) : null;
  const end = session.endedAtUtc ? Date.parse(session.endedAtUtc) : planned !== null ? Math.min(now, planned) : now;
  const elapsed = Math.max(0, end - Date.parse(session.startedAtUtc));
  const cost = session.totalMinorUnits ?? calculateMinorUnits(session.pricePerHourMinorUnits, elapsed);
  const remaining = session.endedAtUtc ? null : remainingMs(session, now);
  return {
    duration: formatDuration(elapsed),
    cost: formatMoney(cost, session.currency),
    remaining: remaining === null ? null : formatDuration(remaining),
    soon: remaining !== null && remaining <= SOON_MS,
  };
}

export function SessionTimer({ session }: { session: SessionView }) {
  const clock = useSessionClock(session);
  if (!clock) return null;
  return (
    <span className={`font-mono text-xs ${clock.soon ? "font-semibold text-amber-700" : "text-blue-800"}`} data-testid="session-timer">
      {clock.remaining !== null ? `${t.device.remainingShort} ${clock.remaining}` : clock.duration} · {clock.cost}
    </span>
  );
}
