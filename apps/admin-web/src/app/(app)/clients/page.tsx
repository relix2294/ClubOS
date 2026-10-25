"use client";

import { useEffect, useState, type FormEvent } from "react";
import type { ClientDetailsView, ClientView } from "@clubos/contracts";
import { useShell } from "@/components/AppShell";
import { Button, Card, EmptyState, ErrorState, Field, Loading, inputClass } from "@/components/ui";
import { apiGet, apiPost } from "@/lib/api";
import { formatDateTime, formatMoney, formatPhone, parseMoney } from "@/lib/format";
import { t } from "@/lib/i18n";
import { usePolling } from "@/lib/usePolling";

function newKey(): string {
  return typeof crypto !== "undefined" && "randomUUID" in crypto
    ? crypto.randomUUID()
    : `${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 12)}`;
}

function useAction() {
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string>();
  const [done, setDone] = useState<string>();
  const run = async (action: () => Promise<void>, doneText?: string) => {
    setBusy(true);
    setError(undefined);
    setDone(undefined);
    try {
      await action();
      setDone(doneText);
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setBusy(false);
    }
  };
  return { busy, error, done, run, setError };
}

function Feedback({ error, done }: { error?: string; done?: string }) {
  if (error) {
    return (
      <p role="alert" className="rounded-lg bg-red-50 px-3 py-2 text-sm text-red-800">
        {error}
      </p>
    );
  }
  return done ? (
    <p role="status" className="text-sm text-emerald-700">
      {done}
    </p>
  ) : null;
}

/** Клиенты организации: поиск, регистрация, баланс (пополнение через кассу, корректировка), блокировка, журнал. */
export default function ClientsPage() {
  const { can } = useShell();
  const [query, setQuery] = useState("");
  const [debounced, setDebounced] = useState("");
  const [selectedId, setSelectedId] = useState<string | null>(null);

  useEffect(() => {
    const timer = setTimeout(() => setDebounced(query.trim()), 250);
    return () => clearTimeout(timer);
  }, [query]);

  const list = usePolling(
    (s) => apiGet<ClientView[]>(`clients${debounced ? `?query=${encodeURIComponent(debounced)}` : ""}`, s),
    30_000,
    [debounced],
    { topics: ["cash"] },
  );

  if (!can("cash.operate")) {
    return <EmptyState>Раздел недоступен для вашей роли.</EmptyState>;
  }

  return (
    <div className="flex flex-col gap-6">
      <h1 className="text-2xl font-bold text-slate-900">{t.clients.title}</h1>
      <div className="grid gap-6 lg:grid-cols-[minmax(0,2fr)_minmax(0,3fr)]">
        <div className="flex flex-col gap-6">
          <Card>
            <Field label={t.clients.search}>
              <input
                className={inputClass}
                value={query}
                onChange={(e) => setQuery(e.target.value)}
                placeholder="+992 90… / Имя"
                aria-label={t.clients.search}
              />
            </Field>
            <div className="mt-4">
              {list.loading && !list.data && <Loading />}
              {list.error && <ErrorState error={list.error} onRetry={list.refresh} />}
              {list.data && list.data.length === 0 && <EmptyState>{debounced ? t.clients.empty : t.clients.startTyping}</EmptyState>}
              {list.data && list.data.length > 0 && (
                <ul className="flex flex-col divide-y divide-slate-100 text-sm" data-testid="clients-list">
                  {list.data.map((c) => (
                    <li key={c.clientId}>
                      <button
                        type="button"
                        onClick={() => setSelectedId(c.clientId)}
                        className={`flex w-full items-center gap-3 rounded-lg px-2 py-2 text-left hover:bg-slate-50 ${selectedId === c.clientId ? "bg-brand-50" : ""}`}
                      >
                        <span className="flex flex-col">
                          <span className="font-medium text-slate-900">{c.displayName}</span>
                          <span className="text-xs text-slate-500">{formatPhone(c.phone)}</span>
                        </span>
                        <span className="ml-auto text-right tabular-nums">
                          {formatMoney(c.balanceMinorUnits, c.currency)}
                          {c.isBlocked && <span className="block text-xs font-semibold text-red-700">{t.clients.blocked}</span>}
                        </span>
                      </button>
                    </li>
                  ))}
                </ul>
              )}
            </div>
          </Card>
          <NewClientCard
            onCreated={(c) => {
              setQuery(c.phone);
              setSelectedId(c.clientId);
              list.refresh();
            }}
          />
        </div>
        <div>{selectedId ? <ClientCard key={selectedId} clientId={selectedId} onChange={list.refresh} /> : null}</div>
      </div>
    </div>
  );
}

function NewClientCard({ onCreated }: { onCreated: (client: ClientView) => void }) {
  const { location } = useShell();
  const [phone, setPhone] = useState("");
  const [name, setName] = useState("");
  const [note, setNote] = useState("");
  const action = useAction();

  const submit = (e: FormEvent) => {
    e.preventDefault();
    void action.run(async () => {
      const created = await apiPost<ClientView>("clients", {
        phone,
        displayName: name,
        note: note || null,
        locationId: location.locationId,
      });
      setPhone("");
      setName("");
      setNote("");
      onCreated(created);
    });
  };

  return (
    <Card title={t.clients.newTitle}>
      <form onSubmit={submit} className="flex flex-col gap-3" aria-label={t.clients.newTitle}>
        <Field label={t.clients.phone}>
          <input
            className={inputClass}
            type="tel"
            required
            placeholder={t.clients.phoneHint}
            value={phone}
            onChange={(e) => setPhone(e.target.value)}
          />
        </Field>
        <Field label={t.clients.name}>
          <input className={inputClass} required maxLength={80} value={name} onChange={(e) => setName(e.target.value)} />
        </Field>
        <Field label={t.clients.note}>
          <input className={inputClass} maxLength={500} value={note} onChange={(e) => setNote(e.target.value)} />
        </Field>
        <div>
          <Button type="submit" disabled={action.busy}>
            {t.clients.create}
          </Button>
        </div>
        <Feedback error={action.error} />
      </form>
    </Card>
  );
}

function ClientCard({ clientId, onChange }: { clientId: string; onChange: () => void }) {
  const { location, can } = useShell();
  const details = usePolling((s) => apiGet<ClientDetailsView>(`clients/${encodeURIComponent(clientId)}`, s), 15_000, [clientId], {
    topics: ["cash"],
  });
  const refresh = () => {
    details.refresh();
    onChange();
  };

  if (details.loading && !details.data) return <Loading />;
  if (details.error && !details.data) return <ErrorState error={details.error} onRetry={details.refresh} />;
  if (!details.data) return null;
  const { client, ledger } = details.data;
  const m = (v: number) => formatMoney(v, client.currency);

  return (
    <div className="flex flex-col gap-6" data-testid="client-card">
      <Card
        title={
          <span className="flex flex-wrap items-center gap-3">
            {client.displayName}
            {client.isBlocked && (
              <span className="rounded bg-red-100 px-2 py-0.5 text-xs font-semibold text-red-800">{t.clients.blocked}</span>
            )}
          </span>
        }
      >
        <div className="grid gap-3 sm:grid-cols-3">
          <div className="rounded-lg bg-slate-50 px-3 py-2">
            <div className="text-xs text-slate-500">{t.clients.balance}</div>
            <div className="text-2xl font-bold tabular-nums text-slate-900" data-testid="client-balance">
              {m(client.balanceMinorUnits)}
            </div>
          </div>
          <div className="rounded-lg bg-slate-50 px-3 py-2">
            <div className="text-xs text-slate-500">{t.clients.phone}</div>
            <div className="font-semibold text-slate-800">{formatPhone(client.phone)}</div>
          </div>
          <div className="rounded-lg bg-slate-50 px-3 py-2">
            <div className="text-xs text-slate-500">{t.clients.since}</div>
            <div className="font-semibold text-slate-800">{formatDateTime(client.createdAtUtc, location.timezone)}</div>
          </div>
        </div>
        <EditClient client={client} onDone={refresh} />
      </Card>
      {!client.isBlocked && <TopUpCard client={client} onDone={refresh} />}
      {can("cash.refund") && <AdjustCard client={client} onDone={refresh} />}
      <Card title={t.clients.ledger}>
        {ledger.length === 0 ? (
          <EmptyState>{t.clients.ledgerEmpty}</EmptyState>
        ) : (
          <div className="overflow-x-auto">
            <table className="w-full text-left text-sm" data-testid="client-ledger">
              <thead className="text-xs uppercase text-slate-500">
                <tr>
                  <th className="py-2 pr-4">{t.clients.when}</th>
                  <th className="py-2 pr-4">{t.clients.kind}</th>
                  <th className="py-2 pr-4 text-right">{t.clients.amount}</th>
                  <th className="py-2 pr-4 text-right">{t.clients.after}</th>
                  <th className="py-2">{t.clients.who}</th>
                </tr>
              </thead>
              <tbody className="divide-y divide-slate-100">
                {ledger.map((e) => (
                  <tr key={e.entryId}>
                    <td className="py-2 pr-4 whitespace-nowrap text-slate-600">{formatDateTime(e.createdAtUtc, location.timezone)}</td>
                    <td className="py-2 pr-4">
                      {t.clients.kinds[e.kind]}
                      {e.reason && <span className="block text-xs text-slate-500">{e.reason}</span>}
                    </td>
                    <td className={`py-2 pr-4 text-right tabular-nums ${e.amountMinorUnits < 0 ? "text-red-700" : "text-emerald-700"}`}>
                      {e.amountMinorUnits > 0 ? "+" : ""}
                      {m(e.amountMinorUnits)}
                    </td>
                    <td className="py-2 pr-4 text-right tabular-nums">{m(e.balanceAfterMinorUnits)}</td>
                    <td className="py-2">{e.createdByName}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </Card>
    </div>
  );
}

function EditClient({ client, onDone }: { client: ClientView; onDone: () => void }) {
  const { can } = useShell();
  const [name, setName] = useState(client.displayName);
  const [note, setNote] = useState(client.note ?? "");
  const action = useAction();
  const dirty = name !== client.displayName || note !== (client.note ?? "");

  const save = (e: FormEvent) => {
    e.preventDefault();
    void action.run(async () => {
      await apiPost(`clients/${encodeURIComponent(client.clientId)}`, { displayName: name, note });
      onDone();
    }, t.clients.saved);
  };

  const toggleBlock = () => {
    if (!client.isBlocked && !window.confirm(t.clients.blockConfirm)) return;
    void action.run(async () => {
      await apiPost(`clients/${encodeURIComponent(client.clientId)}`, { isBlocked: !client.isBlocked });
      onDone();
    });
  };

  return (
    <form onSubmit={save} className="mt-4 flex flex-col gap-3" aria-label={t.clients.save}>
      <div className="grid gap-3 sm:grid-cols-2">
        <Field label={t.clients.name}>
          <input className={inputClass} required maxLength={80} value={name} onChange={(e) => setName(e.target.value)} />
        </Field>
        <Field label={t.clients.note}>
          <input className={inputClass} maxLength={500} value={note} onChange={(e) => setNote(e.target.value)} />
        </Field>
      </div>
      <div className="flex flex-wrap gap-2">
        <Button type="submit" variant="secondary" disabled={action.busy || !dirty}>
          {t.clients.save}
        </Button>
        {can("cash.refund") && (
          <Button variant={client.isBlocked ? "secondary" : "danger"} disabled={action.busy} onClick={toggleBlock}>
            {client.isBlocked ? t.clients.unblock : t.clients.block}
          </Button>
        )}
      </div>
      <Feedback error={action.error} done={action.done} />
    </form>
  );
}

function TopUpCard({ client, onDone }: { client: ClientView; onDone: () => void }) {
  const { location } = useShell();
  const [amount, setAmount] = useState("");
  const [key, setKey] = useState(newKey);
  const action = useAction();

  const submit = (method: "Cash" | "Card") => {
    const minor = parseMoney(amount);
    if (minor === null || minor <= 0) {
      action.setError(t.cash.badAmount);
      return;
    }
    void action.run(async () => {
      await apiPost(`clients/${encodeURIComponent(client.clientId)}/topups`, {
        locationId: location.locationId,
        amountMinorUnits: minor,
        method,
        idempotencyKey: key,
      });
      setAmount("");
      setKey(newKey());
      onDone();
    }, `${t.clients.topUpDone}: +${formatMoney(minor, client.currency)}`);
  };

  return (
    <Card title={t.clients.topUpTitle}>
      <form
        onSubmit={(e) => {
          e.preventDefault();
          submit("Cash");
        }}
        className="flex flex-col gap-3"
        aria-label={t.clients.topUpTitle}
      >
        <p className="text-xs text-slate-500">
          {t.clients.topUpHint} {t.clients.noShift}
        </p>
        <div className="flex flex-wrap items-end gap-2">
          <Field label={`${t.clients.amount}, ${client.currency}`}>
            <input className={`${inputClass} w-32`} inputMode="decimal" value={amount} onChange={(e) => setAmount(e.target.value)} />
          </Field>
          <Button type="submit" disabled={action.busy}>
            {t.clients.topUpCash}
          </Button>
          <Button variant="secondary" disabled={action.busy} onClick={() => submit("Card")}>
            {t.clients.topUpCard}
          </Button>
        </div>
        <Feedback error={action.error} done={action.done} />
      </form>
    </Card>
  );
}

function AdjustCard({ client, onDone }: { client: ClientView; onDone: () => void }) {
  const [amount, setAmount] = useState("");
  const [reason, setReason] = useState("");
  const action = useAction();

  const submit = (e: FormEvent) => {
    e.preventDefault();
    const text = amount.trim();
    const negative = text.startsWith("-") || text.startsWith("−");
    const minor = parseMoney(negative ? text.slice(1) : text);
    if (minor === null || minor === 0) {
      action.setError(t.cash.badAmount);
      return;
    }
    void action.run(async () => {
      await apiPost(`clients/${encodeURIComponent(client.clientId)}/adjustments`, {
        amountMinorUnits: negative ? -minor : minor,
        reason,
      });
      setAmount("");
      setReason("");
      onDone();
    }, t.clients.saved);
  };

  return (
    <Card title={t.clients.adjustTitle}>
      <form onSubmit={submit} className="flex flex-col gap-3" aria-label={t.clients.adjustTitle}>
        <p className="text-xs text-slate-500">{t.clients.adjustHint}</p>
        <div className="grid gap-3 sm:grid-cols-2">
          <Field label={`${t.clients.adjustAmount}, ${client.currency}`}>
            <input className={inputClass} required inputMode="decimal" value={amount} onChange={(e) => setAmount(e.target.value)} />
          </Field>
          <Field label={t.clients.adjustReason}>
            <input className={inputClass} required minLength={3} maxLength={200} value={reason} onChange={(e) => setReason(e.target.value)} />
          </Field>
        </div>
        <div>
          <Button type="submit" variant="secondary" disabled={action.busy}>
            {t.clients.adjust}
          </Button>
        </div>
        <Feedback error={action.error} done={action.done} />
      </form>
    </Card>
  );
}
