"use client";

import { calculateMinorUnits, type SessionView } from "@clubos/contracts";
import { formatDuration, formatMoney } from "@/lib/format";
import { useNow } from "@/lib/usePolling";

/** Текущая длительность и предварительная стоимость активной сессии (итог считает Edge). */
export function useSessionClock(session: SessionView | null | undefined) {
  const now = useNow();
  if (!session || !session.startedAtUtc) return null;
  const end = session.endedAtUtc ? Date.parse(session.endedAtUtc) : now;
  const elapsed = end - Date.parse(session.startedAtUtc);
  const cost = session.totalMinorUnits ?? calculateMinorUnits(session.pricePerHourMinorUnits, elapsed);
  return { duration: formatDuration(elapsed), cost: formatMoney(cost, session.currency) };
}

export function SessionTimer({ session }: { session: SessionView }) {
  const clock = useSessionClock(session);
  if (!clock) return null;
  return (
    <span className="font-mono text-xs text-blue-800" data-testid="session-timer">
      {clock.duration} · {clock.cost}
    </span>
  );
}
