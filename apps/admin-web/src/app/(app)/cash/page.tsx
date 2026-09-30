"use client";

import { useState, type FormEvent } from "react";
import type {
  CashDeskView,
  CashOperationView,
  CashShiftView,
  PayableSessionView,
  PaymentMethod,
} from "@clubos/contracts";
import { useShell } from "@/components/AppShell";
import { Button, Card, EmptyState, ErrorState, Field, Loading, inputClass } from "@/components/ui";
import { apiGet, apiPost } from "@/lib/api";
import { formatDateTime, formatMoney, formatTime, parseMoney } from "@/lib/format";
import { sessionStateLabel, t } from "@/lib/i18n";
import { usePolling } from "@/lib/usePolling";

/** Новый ключ идемпотентности на каждую попытку: двойной клик или повтор после обрыва не спишет деньги дважды. */
function newKey(): string {
  return typeof crypto !== "undefined" && "randomUUID" in crypto
    ? crypto.randomUUID()
    : `${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 12)}`;
}

function useAction() {
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string>();
  const run = async (action: () => Promise<void>) => {
    setBusy(true);
    setError(undefined);
    try {
      await action();
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setBusy(false);
    }
  };
  return { busy, error, run, setError };
}

function InlineError({ message }: { message?: string }) {
  if (!message) return null;
  return (
    <p role="alert" className="rounded-lg bg-red-50 px-3 py-2 text-sm text-red-800">
      {message}
    </p>
  );
}

export default function CashPage() {
  const { location, can } = useShell();
  const desk = usePolling(
    (s) => apiGet<CashDeskView>(`locations/${encodeURIComponent(location.locationId)}/cash`, s),
    5000,
    [location.locationId],
    { topics: ["cash", "sessions"], locationId: location.locationId },
  );
  const [closed, setClosed] = useState<CashShiftView>();

  if (!can("cash.operate")) {
    return <EmptyState>Раздел недоступен для вашей роли.</EmptyState>;
  }

  const data = desk.data;
  return (
    <div className="flex flex-col gap-6">
      <h1 className="text-2xl font-bold text-slate-900">{t.cash.title}</h1>
      {desk.loading && !data && <Loading />}
      {desk.error && <ErrorState error={desk.error} onRetry={desk.refresh} />}
      {closed && <ClosedShiftNotice shift={closed} onDismiss={() => setClosed(undefined)} />}
      {data && !data.shift && (
        <OpenShiftCard locationId={location.locationId} currency={data.currency} onDone={desk.refresh} />
      )}
      {data?.shift && (
        <>
          <ShiftCard shift={data.shift} timeZone={location.timezone} />
          <PayableCard rows={data.payable} timeZone={location.timezone} onDone={desk.refresh} />
          <div className="grid gap-6 lg:grid-cols-2">
            <MovementCard locationId={location.locationId} currency={data.currency} onDone={desk.refresh} />
            <CloseShiftCard
              shift={data.shift}
              onClosed={(s) => {
                setClosed(s);
                desk.refresh();
              }}
            />
          </div>
          <OperationsCard operations={data.operations} timeZone={location.timezone} onDone={desk.refresh} />
        </>
      )}
      {data && !data.shift && data.payable.length > 0 && (
        <p className="text-sm text-amber-800">
          {t.cash.payable}: {data.payable.length}. {t.cash.noShift}
        </p>
      )}
      {can("reports.view") && <ShiftHistory locationId={location.locationId} timeZone={location.timezone} />}
    </div>
  );
}

function OpenShiftCard({ locationId, currency, onDone }: { locationId: string; currency: string; onDone: () => void }) {
  const [amount, setAmount] = useState("0");
  const action = useAction();
  const submit = (e: FormEvent) => {
    e.preventDefault();
    const minor = parseMoney(amount);
    if (minor === null) {
      action.setError(t.cash.badAmount);
      return;
    }
    void action.run(async () => {
      await apiPost(`locations/${encodeURIComponent(locationId)}/cash/shifts`, { openingCashMinorUnits: minor });
      onDone();
    });
  };

  return (
    <Card title={t.cash.openTitle}>
      <form onSubmit={submit} className="flex flex-col gap-3" aria-label={t.cash.openTitle}>
        <p className="text-sm text-slate-600">{t.cash.noShift}</p>
        <div className="flex flex-wrap items-end gap-3">
          <Field label={`${t.cash.openingCash}, ${currency}`}>
            <input className={inputClass} inputMode="decimal" value={amount} onChange={(e) => setAmount(e.target.value)} />
          </Field>
          <Button type="submit" disabled={action.busy}>
            {t.cash.open}
          </Button>
        </div>
        <InlineError message={action.error} />
      </form>
    </Card>
  );
}

function Stat({ label, value, strong = false, testId }: { label: string; value: string; strong?: boolean; testId?: string }) {
  return (
    <div className="rounded-lg bg-slate-50 px-3 py-2">
      <div className="text-xs text-slate-500">{label}</div>
      <div data-testid={testId} className={`tabular-nums ${strong ? "text-lg font-bold text-slate-900" : "font-semibold text-slate-800"}`}>
        {value}
      </div>
    </div>
  );
}

function ShiftCard({ shift, timeZone }: { shift: CashShiftView; timeZone: string }) {
  const m = (v: number) => formatMoney(v, shift.currency);
  const s = shift.totals;
  return (
    <Card
      title={
        <span data-testid="shift-open">
          {t.cash.shift} {t.cash.since} {formatDateTime(shift.openedAtUtc, timeZone)} · {t.cash.openedBy} {shift.openedByName}
        </span>
      }
    >
      <div className="grid grid-cols-2 gap-3 sm:grid-cols-4">
        <Stat label={t.cash.opening} value={m(shift.openingCashMinorUnits)} />
        <Stat label={t.cash.cashPayments} value={m(s.cashPaymentsMinorUnits)} />
        <Stat label={t.cash.cardPayments} value={m(s.cardPaymentsMinorUnits)} />
        <Stat label={t.cash.refunds} value={m(s.cashRefundsMinorUnits + s.cardRefundsMinorUnits)} />
        <Stat label={t.cash.cashIn} value={m(s.cashInMinorUnits)} />
        <Stat label={t.cash.cashOut} value={m(s.cashOutMinorUnits)} />
        <Stat label={t.cash.expected} value={m(s.expectedCashMinorUnits)} strong testId="expected-cash" />
        <Stat label={t.cash.revenue} value={m(s.revenueMinorUnits)} strong testId="shift-revenue" />
      </div>
    </Card>
  );
}

function PayableCard({ rows, timeZone, onDone }: { rows: PayableSessionView[]; timeZone: string; onDone: () => void }) {
  return (
    <Card title={t.cash.payable}>
      {rows.length === 0 ? (
        <EmptyState>{t.cash.payableEmpty}</EmptyState>
      ) : (
        <div className="overflow-x-auto">
          <table className="w-full text-left text-sm" data-testid="payable-table">
            <thead className="text-xs uppercase text-slate-500">
              <tr>
                <th className="py-2 pr-4">{t.cash.device}</th>
                <th className="py-2 pr-4">{t.cash.ended}</th>
                <th className="py-2 pr-4 text-right">{t.cash.charge}</th>
                <th className="py-2 pr-4 text-right">{t.cash.paid}</th>
                <th className="py-2 pr-4 text-right">{t.cash.due}</th>
                <th className="py-2" />
              </tr>
            </thead>
            <tbody className="divide-y divide-slate-100">
              {rows.map((row) => (
                // Ключ с суммой: после частичной оплаты или продления поле суммы заново берёт остаток.
                <PayableRow key={`${row.sessionId}:${row.dueMinorUnits}`} row={row} timeZone={timeZone} onDone={onDone} />
              ))}
            </tbody>
          </table>
        </div>
      )}
    </Card>
  );
}

function PayableRow({ row, timeZone, onDone }: { row: PayableSessionView; timeZone: string; onDone: () => void }) {
  const overpaid = row.dueMinorUnits < 0;
  const [amount, setAmount] = useState(() => (Math.abs(row.dueMinorUnits) / 100).toFixed(2).replace(".", ","));
  const [key, setKey] = useState(newKey);
  const action = useAction();

  const submit = (method: PaymentMethod) => {
    const minor = parseMoney(amount);
    if (minor === null || minor <= 0) {
      action.setError(t.cash.badAmount);
      return;
    }
    void action.run(async () => {
      if (overpaid) {
        await apiPost(`sessions/${encodeURIComponent(row.sessionId)}/refunds`, {
          amountMinorUnits: minor,
          method,
          reason: t.cash.refundDefaultReason,
          idempotencyKey: key,
        });
      } else {
        await apiPost(`sessions/${encodeURIComponent(row.sessionId)}/payments`, { amountMinorUnits: minor, method, idempotencyKey: key });
      }
      setKey(newKey());
      onDone();
    });
  };

  return (
    <tr data-testid="payable-row" data-session={row.sessionId}>
      <td className="py-2 pr-4 font-medium">{row.deviceName}</td>
      <td className="py-2 pr-4 whitespace-nowrap text-slate-600">
        {row.state === "Ended" ? formatDateTime(row.endedAtUtc, timeZone) : `${sessionStateLabel[row.state]}, ${t.cash.running} ${formatTime(row.plannedEndAtUtc, timeZone)}`}
      </td>
      <td className="py-2 pr-4 text-right tabular-nums">{formatMoney(row.chargeMinorUnits, row.currency)}</td>
      <td className="py-2 pr-4 text-right tabular-nums">{formatMoney(row.paidMinorUnits, row.currency)}</td>
      <td className={`py-2 pr-4 text-right font-semibold tabular-nums ${overpaid ? "text-amber-700" : "text-red-700"}`} data-testid="due">
        {overpaid ? `${t.cash.overpaid} ${formatMoney(-row.dueMinorUnits, row.currency)}` : formatMoney(row.dueMinorUnits, row.currency)}
      </td>
      <td className="py-2">
        <div className="flex flex-wrap items-center justify-end gap-2">
          <input
            aria-label={t.cash.amount}
            className={`${inputClass} w-24 py-1 text-right`}
            inputMode="decimal"
            value={amount}
            onChange={(e) => setAmount(e.target.value)}
          />
          {overpaid ? (
            <Button variant="secondary" className="py-1" disabled={action.busy} onClick={() => submit("Cash")}>
              {t.cash.refundOverpaid}
            </Button>
          ) : (
            <>
              <Button className="py-1" disabled={action.busy} onClick={() => submit("Cash")}>
                {t.cash.payCash}
              </Button>
              <Button variant="secondary" className="py-1" disabled={action.busy} onClick={() => submit("Card")}>
                {t.cash.payCard}
              </Button>
            </>
          )}
        </div>
        <InlineError message={action.error} />
      </td>
    </tr>
  );
}

function MovementCard({ locationId, currency, onDone }: { locationId: string; currency: string; onDone: () => void }) {
  const [kind, setKind] = useState<"CashIn" | "CashOut">("CashOut");
  const [amount, setAmount] = useState("");
  const [reason, setReason] = useState("");
  const [key, setKey] = useState(newKey);
  const action = useAction();

  const submit = (e: FormEvent) => {
    e.preventDefault();
    const minor = parseMoney(amount);
    if (minor === null || minor <= 0) {
      action.setError(t.cash.badAmount);
      return;
    }
    void action.run(async () => {
      await apiPost(`locations/${encodeURIComponent(locationId)}/cash/movements`, {
        kind,
        amountMinorUnits: minor,
        reason,
        idempotencyKey: key,
      });
      setAmount("");
      setReason("");
      setKey(newKey());
      onDone();
    });
  };

  return (
    <Card title={t.cash.movementTitle}>
      <form onSubmit={submit} className="flex flex-col gap-3" aria-label={t.cash.movementTitle}>
        <Field label={t.cash.movementKind}>
          <select className={inputClass} value={kind} onChange={(e) => setKind(e.target.value as "CashIn" | "CashOut")}>
            <option value="CashOut">{t.cash.kindCashOut}</option>
            <option value="CashIn">{t.cash.kindCashIn}</option>
          </select>
        </Field>
        <Field label={`${t.cash.amount}, ${currency}`}>
          <input className={inputClass} inputMode="decimal" required value={amount} onChange={(e) => setAmount(e.target.value)} />
        </Field>
        <Field label={t.cash.reason}>
          <input className={inputClass} required minLength={3} maxLength={200} value={reason} onChange={(e) => setReason(e.target.value)} />
        </Field>
        <div>
          <Button type="submit" variant="secondary" disabled={action.busy}>
            {t.cash.add}
          </Button>
        </div>
        <InlineError message={action.error} />
      </form>
    </Card>
  );
}

function CloseShiftCard({ shift, onClosed }: { shift: CashShiftView; onClosed: (shift: CashShiftView) => void }) {
  const [counted, setCounted] = useState("");
  const [note, setNote] = useState("");
  const action = useAction();

  const submit = (e: FormEvent) => {
    e.preventDefault();
    const minor = parseMoney(counted);
    if (minor === null) {
      action.setError(t.cash.badAmount);
      return;
    }
    if (!window.confirm(t.cash.closeConfirm)) return;
    void action.run(async () => {
      const result = await apiPost<CashShiftView>(`cash/shifts/${encodeURIComponent(shift.shiftId)}/close`, {
        countedCashMinorUnits: minor,
        note: note || null,
      });
      setCounted("");
      setNote("");
      onClosed(result);
    });
  };

  return (
    <Card title={t.cash.closeTitle}>
      <form onSubmit={submit} className="flex flex-col gap-3" aria-label={t.cash.closeTitle}>
        <p className="text-sm text-slate-600">
          {t.cash.expected}: <strong className="tabular-nums">{formatMoney(shift.totals.expectedCashMinorUnits, shift.currency)}</strong>
        </p>
        <Field label={`${t.cash.counted}, ${shift.currency}`}>
          <input className={inputClass} inputMode="decimal" required value={counted} onChange={(e) => setCounted(e.target.value)} />
        </Field>
        <Field label={t.cash.note}>
          <input className={inputClass} maxLength={500} value={note} onChange={(e) => setNote(e.target.value)} />
        </Field>
        <div>
          <Button type="submit" variant="danger" disabled={action.busy}>
            {t.cash.close}
          </Button>
        </div>
        <InlineError message={action.error} />
      </form>
    </Card>
  );
}

function discrepancyText(value: number | null, currency: string): string {
  if (value === null || value === 0) return t.cash.exact;
  return `${formatMoney(Math.abs(value), currency)} — ${value < 0 ? t.cash.shortage : t.cash.surplus}`;
}

function ClosedShiftNotice({ shift, onDismiss }: { shift: CashShiftView; onDismiss: () => void }) {
  const bad = (shift.discrepancyMinorUnits ?? 0) !== 0;
  return (
    <div
      role="status"
      data-testid="shift-closed"
      className={`flex flex-wrap items-center gap-3 rounded-lg border px-4 py-3 text-sm ${bad ? "border-amber-300 bg-amber-50 text-amber-900" : "border-emerald-300 bg-emerald-50 text-emerald-900"}`}
    >
      <strong>{t.cash.closedTitle}.</strong>
      <span>
        {t.cash.revenue}: {formatMoney(shift.totals.revenueMinorUnits, shift.currency)} · {t.cash.expected}:{" "}
        {formatMoney(shift.totals.expectedCashMinorUnits, shift.currency)} · {t.cash.counted}:{" "}
        {formatMoney(shift.countedCashMinorUnits ?? 0, shift.currency)}
      </span>
      <span data-testid="discrepancy">
        {t.cash.discrepancy}: {discrepancyText(shift.discrepancyMinorUnits, shift.currency)}
      </span>
      <Button variant="secondary" className="ml-auto py-1" onClick={onDismiss}>
        OK
      </Button>
    </div>
  );
}

function OperationsCard({ operations, timeZone, onDone }: { operations: CashOperationView[]; timeZone: string; onDone: () => void }) {
  const { can } = useShell();
  return (
    <Card title={t.cash.operations}>
      {operations.length === 0 ? (
        <EmptyState>{t.cash.noOperations}</EmptyState>
      ) : (
        <div className="overflow-x-auto">
          <table className="w-full text-left text-sm" data-testid="operations-table">
            <thead className="text-xs uppercase text-slate-500">
              <tr>
                <th className="py-2 pr-4">{t.cash.when}</th>
                <th className="py-2 pr-4">{t.cash.kind}</th>
                <th className="py-2 pr-4">{t.cash.method}</th>
                <th className="py-2 pr-4 text-right">{t.cash.amount}</th>
                <th className="py-2 pr-4">{t.cash.device}</th>
                <th className="py-2 pr-4">{t.cash.who}</th>
                <th className="py-2" />
              </tr>
            </thead>
            <tbody className="divide-y divide-slate-100">
              {operations.map((op) => (
                <tr key={op.operationId}>
                  <td className="py-2 pr-4 whitespace-nowrap text-slate-600">{formatTime(op.createdAtUtc, timeZone)}</td>
                  <td className="py-2 pr-4">
                    {t.cash.kinds[op.kind]}
                    {op.reason && <span className="block text-xs text-slate-500">{op.reason}</span>}
                  </td>
                  <td className="py-2 pr-4">{t.cash.methods[op.method]}</td>
                  <td className={`py-2 pr-4 text-right tabular-nums ${op.amountMinorUnits < 0 ? "text-red-700" : "text-slate-900"}`}>
                    {formatMoney(op.amountMinorUnits, op.currency)}
                  </td>
                  <td className="py-2 pr-4">{op.deviceName ?? "—"}</td>
                  <td className="py-2 pr-4">{op.createdByName}</td>
                  <td className="py-2">
                    {op.kind === "SessionPayment" && op.sessionId && can("cash.refund") && <RefundButton op={op} onDone={onDone} />}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </Card>
  );
}

/** Возврат оплаченной сессии (право cash.refund): сумма и причина обязательны, операция — отдельная. */
function RefundButton({ op, onDone }: { op: CashOperationView; onDone: () => void }) {
  const [open, setOpen] = useState(false);
  const [amount, setAmount] = useState(() => (op.amountMinorUnits / 100).toFixed(2).replace(".", ","));
  const [reason, setReason] = useState("");
  const [key, setKey] = useState(newKey);
  const action = useAction();

  if (!open) {
    return (
      <Button variant="secondary" className="py-1" onClick={() => setOpen(true)}>
        {t.cash.refund}
      </Button>
    );
  }

  const submit = (e: FormEvent) => {
    e.preventDefault();
    const minor = parseMoney(amount);
    if (minor === null || minor <= 0) {
      action.setError(t.cash.badAmount);
      return;
    }
    void action.run(async () => {
      await apiPost(`sessions/${encodeURIComponent(op.sessionId!)}/refunds`, {
        amountMinorUnits: minor,
        method: op.method,
        reason,
        idempotencyKey: key,
      });
      setKey(newKey());
      setOpen(false);
      onDone();
    });
  };

  return (
    <form onSubmit={submit} className="flex flex-col gap-2" aria-label={t.cash.refundTitle}>
      <input aria-label={t.cash.amount} className={`${inputClass} w-28 py-1`} inputMode="decimal" value={amount} onChange={(e) => setAmount(e.target.value)} />
      <input
        aria-label={t.cash.refundReason}
        placeholder={t.cash.refundReason}
        className={`${inputClass} py-1`}
        required
        minLength={3}
        maxLength={200}
        value={reason}
        onChange={(e) => setReason(e.target.value)}
      />
      <div className="flex gap-2">
        <Button type="submit" variant="danger" className="py-1" disabled={action.busy}>
          {t.cash.refund}
        </Button>
        <Button variant="secondary" className="py-1" onClick={() => setOpen(false)}>
          ✕
        </Button>
      </div>
      <InlineError message={action.error} />
    </form>
  );
}

function ShiftHistory({ locationId, timeZone }: { locationId: string; timeZone: string }) {
  const history = usePolling(
    (s) => apiGet<CashShiftView[]>(`locations/${encodeURIComponent(locationId)}/cash/shifts?limit=20`, s),
    30_000,
    [locationId],
    { topics: ["cash"], locationId },
  );
  const closed = history.data?.filter((s) => s.closedAtUtc) ?? [];
  return (
    <Card title={t.cash.history}>
      {history.error && <ErrorState error={history.error} onRetry={history.refresh} />}
      {history.data && closed.length === 0 && <EmptyState>{t.cash.historyEmpty}</EmptyState>}
      {closed.length > 0 && (
        <div className="overflow-x-auto">
          <table className="w-full text-left text-sm" data-testid="shift-history">
            <thead className="text-xs uppercase text-slate-500">
              <tr>
                <th className="py-2 pr-4">{t.cash.shift}</th>
                <th className="py-2 pr-4">{t.cash.closedBy}</th>
                <th className="py-2 pr-4 text-right">{t.cash.revenue}</th>
                <th className="py-2 pr-4 text-right">{t.cash.expected}</th>
                <th className="py-2 pr-4 text-right">{t.cash.counted}</th>
                <th className="py-2">{t.cash.discrepancy}</th>
              </tr>
            </thead>
            <tbody className="divide-y divide-slate-100">
              {closed.map((s) => (
                <tr key={s.shiftId}>
                  <td className="py-2 pr-4 whitespace-nowrap text-slate-600">
                    {formatDateTime(s.openedAtUtc, timeZone)} — {formatTime(s.closedAtUtc, timeZone)}
                  </td>
                  <td className="py-2 pr-4">{s.closedByName}</td>
                  <td className="py-2 pr-4 text-right tabular-nums">{formatMoney(s.totals.revenueMinorUnits, s.currency)}</td>
                  <td className="py-2 pr-4 text-right tabular-nums">{formatMoney(s.totals.expectedCashMinorUnits, s.currency)}</td>
                  <td className="py-2 pr-4 text-right tabular-nums">{formatMoney(s.countedCashMinorUnits ?? 0, s.currency)}</td>
                  <td className={`py-2 ${(s.discrepancyMinorUnits ?? 0) !== 0 ? "font-semibold text-amber-800" : "text-slate-600"}`}>
                    {discrepancyText(s.discrepancyMinorUnits, s.currency)}
                    {s.closeNote && <span className="block text-xs font-normal text-slate-500">{s.closeNote}</span>}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </Card>
  );
}
