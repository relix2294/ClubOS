"use client";

import { useCallback, useEffect, useState } from "react";
import { ApiError, getAudit } from "@/lib/api";
import { useRequireAuth } from "@/components/Guard";
import { EmptyView, ErrorView, LoadingView } from "@/components/States";
import { formatTime } from "@/lib/format";
import { t } from "@/lib/i18n";
import type { AuditDto } from "@/lib/types";

export default function AuditPage() {
  const { session, ready } = useRequireAuth();
  const [events, setEvents] = useState<AuditDto[]>([]);
  const [status, setStatus] = useState<"loading" | "ready" | "error">("loading");
  const [errorMessage, setErrorMessage] = useState("");

  const load = useCallback(async () => {
    if (!session) {
      return;
    }
    setStatus("loading");
    try {
      setEvents(await getAudit(session.accessToken));
      setStatus("ready");
    } catch (err) {
      setErrorMessage(err instanceof ApiError && err.status === 0 ? t("connectionError") : String(err));
      setStatus("error");
    }
  }, [session]);

  useEffect(() => {
    if (ready && session) {
      void load();
    }
  }, [ready, session, load]);

  if (!ready || !session) {
    return <LoadingView />;
  }

  return (
    <div className="flex flex-col gap-4">
      <h1 className="text-lg font-semibold">{t("audit")}</h1>

      {status === "loading" ? <LoadingView /> : null}
      {status === "error" ? <ErrorView message={errorMessage} onRetry={() => void load()} /> : null}
      {status === "ready" && events.length === 0 ? <EmptyView /> : null}

      {status === "ready" && events.length > 0 ? (
        <div className="overflow-x-auto rounded-xl border border-[var(--border)] bg-[var(--surface)]">
          <table className="w-full text-left text-sm">
            <thead className="text-[var(--muted)]">
              <tr className="border-b border-[var(--border)]">
                <th className="px-4 py-2 font-medium">{t("time")}</th>
                <th className="px-4 py-2 font-medium">{t("actor")}</th>
                <th className="px-4 py-2 font-medium">{t("action")}</th>
                <th className="px-4 py-2 font-medium">{t("target")}</th>
                <th className="px-4 py-2 font-medium">{t("result")}</th>
              </tr>
            </thead>
            <tbody>
              {events.map((event) => (
                <tr key={event.id} className="border-b border-[var(--border)] last:border-0">
                  <td className="px-4 py-2 tabular-nums">{formatTime(event.occurredAtUtc)}</td>
                  <td className="px-4 py-2">{event.actor}</td>
                  <td className="px-4 py-2">{event.action}</td>
                  <td className="px-4 py-2">{event.target}</td>
                  <td className="px-4 py-2">{event.result}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      ) : null}
    </div>
  );
}
