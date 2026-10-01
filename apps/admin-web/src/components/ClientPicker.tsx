"use client";

import { useEffect, useState } from "react";
import type { ClientView } from "@clubos/contracts";
import { apiGet } from "@/lib/api";
import { formatMoney, formatPhone } from "@/lib/format";
import { t } from "@/lib/i18n";
import { Button, Field, inputClass } from "./ui";

/** Поиск клиента по телефону или имени с задержкой ввода; выбор показывает имя и баланс. */
export function ClientPicker({ value, onChange }: { value: ClientView | null; onChange: (client: ClientView | null) => void }) {
  const [query, setQuery] = useState("");
  const [results, setResults] = useState<ClientView[]>([]);
  const [error, setError] = useState<string>();

  const text = query.trim();
  const tooShort = text.length < 2 && text.replace(/\D/g, "").length < 3;

  useEffect(() => {
    if (tooShort) return;
    const controller = new AbortController();
    const timer = setTimeout(() => {
      apiGet<ClientView[]>(`clients?query=${encodeURIComponent(text)}`, controller.signal)
        .then((list) => {
          setResults(list.slice(0, 8));
          setError(undefined);
        })
        .catch((e: Error) => {
          if (e.name !== "AbortError") setError(e.message);
        });
    }, 250);
    return () => {
      clearTimeout(timer);
      controller.abort();
    };
  }, [text, tooShort]);

  if (value) {
    return (
      <div className="flex flex-wrap items-center gap-3 rounded-lg bg-slate-50 px-3 py-2 text-sm" data-testid="picked-client">
        <span className="font-medium text-slate-900">{value.displayName}</span>
        <span className="text-slate-500">{formatPhone(value.phone)}</span>
        <span className="tabular-nums text-slate-700">
          {t.clients.balance}: {formatMoney(value.balanceMinorUnits, value.currency)}
        </span>
        <Button variant="secondary" className="ml-auto py-1" onClick={() => onChange(null)} aria-label={t.clients.pickNone}>
          ✕
        </Button>
      </div>
    );
  }

  return (
    <div className="flex flex-col gap-2">
      <Field label={t.clients.pick}>
        <input
          className={inputClass}
          placeholder={t.clients.search}
          value={query}
          onChange={(e) => setQuery(e.target.value)}
          aria-label={t.clients.search}
        />
      </Field>
      {error && <p className="text-xs text-red-700">{error}</p>}
      {!tooShort && results.length > 0 && (
        <ul className="flex flex-col divide-y divide-slate-100 rounded-lg border border-slate-200 text-sm" data-testid="client-results">
          {results.map((c) => (
            <li key={c.clientId}>
              <button
                type="button"
                disabled={c.isBlocked}
                onClick={() => {
                  onChange(c);
                  setQuery("");
                }}
                className="flex w-full items-center gap-3 px-3 py-2 text-left hover:bg-slate-50 disabled:cursor-not-allowed disabled:opacity-50"
              >
                <span className="font-medium">{c.displayName}</span>
                <span className="text-slate-500">{formatPhone(c.phone)}</span>
                <span className="ml-auto tabular-nums">{c.isBlocked ? t.clients.blocked : formatMoney(c.balanceMinorUnits, c.currency)}</span>
              </button>
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}
