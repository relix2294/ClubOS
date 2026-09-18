"use client";

import { useEffect, useState } from "react";
import { ApiError, endSession, startSession } from "@/lib/api";
import { estimateCostMinorUnits, formatDuration, formatMoney } from "@/lib/format";
import { t } from "@/lib/i18n";
import type { SessionSummary } from "@/lib/types";

export function SessionPanel({ token, deviceId }: { token: string; deviceId: string }) {
  const [summary, setSummary] = useState<SessionSummary | null>(null);
  const [nowMs, setNowMs] = useState(() => Date.now());
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    const id = setInterval(() => setNowMs(Date.now()), 1000);
    return () => clearInterval(id);
  }, []);

  const active = summary !== null && summary.state !== "Ended";

  async function onStart() {
    setBusy(true);
    setError(null);
    try {
      setSummary(await startSession(token, deviceId, "admin-web"));
    } catch (err) {
      setError(
        err instanceof ApiError && err.status === 409
          ? "На устройстве уже есть активная сессия"
          : t("connectionError"),
      );
    } finally {
      setBusy(false);
    }
  }

  async function onEnd() {
    if (!summary) {
      return;
    }
    setBusy(true);
    setError(null);
    try {
      setSummary(await endSession(token, summary.sessionId));
    } catch {
      setError(t("connectionError"));
    } finally {
      setBusy(false);
    }
  }

  const elapsedSeconds = computeElapsedSeconds(summary, active, nowMs);
  const costMinor = summary
    ? summary.totalMinorUnits ?? estimateCostMinorUnits(summary.priceSnapshot, elapsedSeconds)
    : 0;

  return (
    <section className="flex flex-col gap-3 rounded-xl border border-[var(--border)] bg-[var(--surface)] p-4">
      <h2 className="font-medium">{t("session")}</h2>

      {summary ? (
        <div className="grid grid-cols-2 gap-3 text-sm">
          <Metric label={t("duration")} value={formatDuration(elapsedSeconds)} />
          <Metric label={t("cost")} value={formatMoney(costMinor, summary.priceSnapshot.currency)} />
        </div>
      ) : (
        <p className="text-sm text-[var(--muted)]">{t("noActiveSession")}</p>
      )}

      {error ? <div className="text-sm text-rose-300">{error}</div> : null}

      <div className="flex gap-2">
        <button
          onClick={onStart}
          disabled={busy || active}
          className="rounded-lg bg-[var(--accent)] px-3 py-1.5 text-sm font-medium text-white disabled:opacity-50"
        >
          {t("startSession")}
        </button>
        <button
          onClick={onEnd}
          disabled={busy || !active}
          className="rounded-lg border border-[var(--border)] px-3 py-1.5 text-sm disabled:opacity-50"
        >
          {t("endSession")}
        </button>
      </div>
    </section>
  );
}

function computeElapsedSeconds(summary: SessionSummary | null, active: boolean, nowMs: number): number {
  if (!summary) {
    return 0;
  }
  const started = Date.parse(summary.startedAtUtc);
  const endMs = active || !summary.endedAtUtc ? nowMs : Date.parse(summary.endedAtUtc);
  return (endMs - started) / 1000;
}

function Metric({ label, value }: { label: string; value: string }) {
  return (
    <div className="rounded-lg bg-[var(--surface-2)] p-3">
      <div className="text-xs text-[var(--muted)]">{label}</div>
      <div className="text-lg font-semibold tabular-nums">{value}</div>
    </div>
  );
}
