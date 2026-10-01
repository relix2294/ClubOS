"use client";

import { useState, type FormEvent } from "react";
import { apiPost } from "@/lib/api";
import { t } from "@/lib/i18n";
import { useShell } from "./AppShell";
import { Button, Card, Field, inputClass } from "./ui";

/** PIN кассы Edge без интернета (D-023): вход в https://<сервер клуба>:7443/cash, когда облако недоступно. */
export function OfflinePinCard() {
  const { me, refreshMe } = useShell();
  const [password, setPassword] = useState("");
  const [pin, setPin] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string>();
  const [done, setDone] = useState<string>();
  const isSet = me.user.offlinePinSet === true;

  const send = async (value: string | null) => {
    setBusy(true);
    setError(undefined);
    setDone(undefined);
    try {
      await apiPost("me/offline-pin", { currentPassword: password, pin: value });
      setPassword("");
      setPin("");
      setDone(value ? t.offlinePin.saved : t.offlinePin.removed);
      refreshMe();
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setBusy(false);
    }
  };

  const submit = (e: FormEvent) => {
    e.preventDefault();
    if (!/^\d{6,8}$/.test(pin)) {
      setError(t.offlinePin.badPin);
      return;
    }
    void send(pin);
  };

  return (
    <Card title={t.offlinePin.title}>
      <form onSubmit={submit} className="flex flex-col gap-3" aria-label={t.offlinePin.title}>
        <p className="text-sm text-slate-600">{t.offlinePin.hint}</p>
        <p className="text-sm font-medium" data-testid="offline-pin-state">
          {isSet ? t.offlinePin.isSet : t.offlinePin.notSet}
        </p>
        <div className="grid gap-3 sm:grid-cols-2">
          <Field label={t.offlinePin.pin}>
            <input className={inputClass} type="password" inputMode="numeric" autoComplete="off" maxLength={8} value={pin} onChange={(e) => setPin(e.target.value)} />
          </Field>
          <Field label={t.offlinePin.password}>
            <input className={inputClass} type="password" autoComplete="current-password" required value={password} onChange={(e) => setPassword(e.target.value)} />
          </Field>
        </div>
        <div className="flex flex-wrap gap-2">
          <Button type="submit" disabled={busy}>
            {isSet ? t.offlinePin.change : t.offlinePin.set}
          </Button>
          {isSet && (
            <Button variant="secondary" disabled={busy || !password} onClick={() => send(null)}>
              {t.offlinePin.remove}
            </Button>
          )}
        </div>
        {done && (
          <p role="status" className="text-sm text-emerald-700">
            {done}
          </p>
        )}
        {error && (
          <p role="alert" className="text-sm text-red-700">
            {error}
          </p>
        )}
      </form>
    </Card>
  );
}
