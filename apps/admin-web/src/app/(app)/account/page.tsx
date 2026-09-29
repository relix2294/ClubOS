"use client";

import { useState, type FormEvent } from "react";
import { useShell } from "@/components/AppShell";
import { MfaCard } from "@/components/MfaCard";
import { Button, Card, Field, inputClass } from "@/components/ui";
import { roleLabel, t } from "@/lib/i18n";

export default function AccountPage() {
  const { me, refreshMe } = useShell();
  const [current, setCurrent] = useState("");
  const [next, setNext] = useState("");
  const [repeat, setRepeat] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string>();
  const [done, setDone] = useState(false);

  const submit = async (e: FormEvent) => {
    e.preventDefault();
    setError(undefined);
    setDone(false);
    if (next !== repeat) {
      setError(t.account.mismatch);
      return;
    }

    setBusy(true);
    try {
      const res = await fetch("/api/auth/password", {
        method: "POST",
        headers: { "content-type": "application/json", "x-clubos-csrf": "1" },
        body: JSON.stringify({ currentPassword: current, newPassword: next }),
      });
      if (!res.ok) {
        const data = await res.json().catch(() => ({}));
        setError(data.detail ?? `Ошибка ${res.status}`);
        return;
      }

      setCurrent("");
      setNext("");
      setRepeat("");
      setDone(true);
      refreshMe();
    } catch {
      setError("Сервер недоступен.");
    } finally {
      setBusy(false);
    }
  };

  return (
    <div className="flex max-w-xl flex-col gap-6">
      <h1 className="text-2xl font-bold text-slate-900">{t.account.title}</h1>
      <p className="text-sm text-slate-600">
        {me.user.displayName} · {me.user.email} · {roleLabel(me.user.role)}
      </p>
      <Card>
        <form onSubmit={submit} className="flex flex-col gap-4" aria-label={t.account.title}>
          <Field label={t.account.current}>
            <input className={inputClass} type="password" autoComplete="current-password" required value={current} onChange={(e) => setCurrent(e.target.value)} />
          </Field>
          <Field label={t.account.next}>
            <input className={inputClass} type="password" autoComplete="new-password" required minLength={10} maxLength={128} value={next} onChange={(e) => setNext(e.target.value)} />
          </Field>
          <Field label={t.account.repeat}>
            <input className={inputClass} type="password" autoComplete="new-password" required value={repeat} onChange={(e) => setRepeat(e.target.value)} />
          </Field>
          <p className="text-xs text-slate-500">{t.account.hint}</p>
          {error && (
            <p role="alert" className="rounded-lg bg-red-50 px-3 py-2 text-sm text-red-800">
              {error}
            </p>
          )}
          {done && (
            <p role="status" className="rounded-lg bg-emerald-50 px-3 py-2 text-sm text-emerald-800">
              {t.account.done}
            </p>
          )}
          <Button type="submit" disabled={busy}>
            {t.account.submit}
          </Button>
        </form>
      </Card>
      {/* 2FA — после смены временного пароля (Cloud не даёт настраивать её с временным паролем). */}
      {!me.user.mustChangePassword && <MfaCard />}
    </div>
  );
}
