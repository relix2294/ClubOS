"use client";

import { useState, type FormEvent } from "react";
import type { RevenueDayView, RevenueReportView } from "@clubos/contracts";
import { useShell } from "@/components/AppShell";
import { Button, Card, EmptyState, ErrorState, Field, Loading, inputClass } from "@/components/ui";
import { apiGet } from "@/lib/api";
import { formatMoney } from "@/lib/format";
import { t } from "@/lib/i18n";
import { usePolling } from "@/lib/usePolling";

/** Сегодня в часовом поясе локации, ГГГГ-ММ-ДД. */
function localToday(timeZone: string): string {
  return new Intl.DateTimeFormat("en-CA", { timeZone, year: "numeric", month: "2-digit", day: "2-digit" }).format(new Date());
}

function shiftDays(date: string, days: number): string {
  const d = new Date(`${date}T00:00:00Z`);
  d.setUTCDate(d.getUTCDate() + days);
  return d.toISOString().slice(0, 10);
}

function formatDate(date: string): string {
  const [y, m, d] = date.split("-");
  return `${d}.${m}.${y}`;
}

export default function ReportsPage() {
  const { location, can } = useShell();
  const today = localToday(location.timezone);
  const [period, setPeriod] = useState({ from: shiftDays(today, -6), to: today });
  const [draft, setDraft] = useState(period);
  const [error, setError] = useState<string>();

  const report = usePolling(
    (s) =>
      apiGet<RevenueReportView>(
        `reports/revenue?locationId=${encodeURIComponent(location.locationId)}&from=${period.from}&to=${period.to}`,
        s,
      ),
    60_000,
    [location.locationId, period.from, period.to],
    { topics: ["cash", "sessions"], locationId: location.locationId },
  );

  if (!can("reports.view")) {
    return <EmptyState>Раздел доступен администратору и владельцу.</EmptyState>;
  }

  const submit = (e: FormEvent) => {
    e.preventDefault();
    const days = (Date.parse(draft.to) - Date.parse(draft.from)) / 86_400_000;
    if (!draft.from || !draft.to || Number.isNaN(days) || days < 0 || days >= 92) {
      setError(t.reports.badPeriod);
      return;
    }
    setError(undefined);
    setPeriod(draft);
  };

  const data = report.data;
  const m = (v: number) => formatMoney(v, data?.currency ?? location.currency);
  const row = (d: RevenueDayView, total = false) => (
    <tr key={d.date} className={total ? "border-t-2 border-slate-300 font-semibold" : undefined} data-testid={total ? "revenue-total" : "revenue-day"}>
      <td className="py-2 pr-4 whitespace-nowrap">{total ? t.reports.total : formatDate(d.date)}</td>
      <td className="py-2 pr-4 text-right tabular-nums">{d.sessionsEnded}</td>
      <td className="py-2 pr-4 text-right tabular-nums">{m(d.chargedMinorUnits)}</td>
      <td className="py-2 pr-4 text-right tabular-nums">{m(d.cashMinorUnits)}</td>
      <td className="py-2 pr-4 text-right tabular-nums">{m(d.cardMinorUnits)}</td>
      <td className="py-2 pr-4 text-right tabular-nums text-red-700">{d.refundsMinorUnits ? m(-d.refundsMinorUnits) : "—"}</td>
      <td className="py-2 text-right tabular-nums" data-testid={total ? "revenue-net" : undefined}>
        {m(d.netMinorUnits)}
      </td>
    </tr>
  );

  return (
    <div className="flex flex-col gap-6">
      <h1 className="text-2xl font-bold text-slate-900">{t.reports.title}</h1>
      <Card>
        <form onSubmit={submit} className="flex flex-wrap items-end gap-3" aria-label={t.reports.title}>
          <Field label={t.reports.from}>
            <input type="date" className={inputClass} value={draft.from} max={today} onChange={(e) => setDraft({ ...draft, from: e.target.value })} />
          </Field>
          <Field label={t.reports.to}>
            <input type="date" className={inputClass} value={draft.to} max={today} onChange={(e) => setDraft({ ...draft, to: e.target.value })} />
          </Field>
          <Button type="submit">{t.reports.show}</Button>
          {error && (
            <p role="alert" className="text-sm text-red-700">
              {error}
            </p>
          )}
        </form>
        <p className="mt-3 text-xs text-slate-500">{t.reports.hint}</p>
      </Card>
      {report.loading && !data && <Loading />}
      {report.error && <ErrorState error={report.error} onRetry={report.refresh} />}
      {data && (
        <Card>
          <div className="overflow-x-auto">
            <table className="w-full text-left text-sm" data-testid="revenue-table">
              <thead className="text-xs uppercase text-slate-500">
                <tr>
                  <th className="py-2 pr-4">{t.reports.date}</th>
                  <th className="py-2 pr-4 text-right">{t.reports.sessions}</th>
                  <th className="py-2 pr-4 text-right">{t.reports.charged}</th>
                  <th className="py-2 pr-4 text-right">{t.reports.cash}</th>
                  <th className="py-2 pr-4 text-right">{t.reports.card}</th>
                  <th className="py-2 pr-4 text-right">{t.reports.refunds}</th>
                  <th className="py-2 text-right">{t.reports.net}</th>
                </tr>
              </thead>
              <tbody className="divide-y divide-slate-100">
                {[...data.days].reverse().map((d) => row(d))}
                {row(data.totals, true)}
              </tbody>
            </table>
          </div>
          <p className={`mt-4 text-sm ${data.unpaidMinorUnits > 0 ? "font-semibold text-amber-800" : "text-slate-600"}`} data-testid="revenue-unpaid">
            {t.reports.unpaid}: {m(data.unpaidMinorUnits)}
          </p>
        </Card>
      )}
    </div>
  );
}
