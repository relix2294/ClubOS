"use client";

import type { AuditEventView } from "@clubos/contracts";
import { useShell } from "@/components/AppShell";
import { Card, EmptyState, ErrorState, Loading } from "@/components/ui";
import { apiGet } from "@/lib/api";
import { formatDateTime } from "@/lib/format";
import { actionLabel, t } from "@/lib/i18n";
import { usePolling } from "@/lib/usePolling";

export default function AuditPage() {
  const { location } = useShell();
  const audit = usePolling(
    (s) => apiGet<AuditEventView[]>(`audit?locationId=${encodeURIComponent(location.locationId)}&limit=200`, s),
    5000,
    [location.locationId],
  );

  return (
    <div className="flex flex-col gap-6">
      <h1 className="text-2xl font-bold text-slate-900">{t.audit.title}</h1>
      {audit.loading && !audit.data && <Loading />}
      {audit.error && <ErrorState error={audit.error} onRetry={audit.refresh} />}
      {audit.data && audit.data.length === 0 && <EmptyState>{t.audit.empty}</EmptyState>}
      {audit.data && audit.data.length > 0 && (
        <Card>
          <div className="overflow-x-auto">
            <table className="w-full text-left text-sm" data-testid="audit-table">
              <thead className="text-xs uppercase text-slate-500">
                <tr>
                  <th className="py-2 pr-4">{t.audit.when}</th>
                  <th className="py-2 pr-4">{t.audit.actor}</th>
                  <th className="py-2 pr-4">{t.audit.action}</th>
                  <th className="py-2 pr-4">{t.audit.target}</th>
                  <th className="py-2">{t.audit.result}</th>
                </tr>
              </thead>
              <tbody className="divide-y divide-slate-100">
                {audit.data.map((a) => (
                  <tr key={a.auditId}>
                    <td className="py-2 pr-4 whitespace-nowrap text-slate-600">{formatDateTime(a.occurredAtUtc, location.timezone)}</td>
                    <td className="py-2 pr-4">{a.actorDisplay}</td>
                    <td className="py-2 pr-4 font-medium">{actionLabel(a.action)}</td>
                    <td className="py-2 pr-4 font-mono text-xs text-slate-500">{a.target}</td>
                    <td className={`py-2 ${a.result === "failed" || a.result === "denied" ? "text-red-700" : "text-slate-700"}`}>{a.result}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </Card>
      )}
    </div>
  );
}
