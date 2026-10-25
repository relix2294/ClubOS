"use client";

import { useEffect, useState, type FormEvent } from "react";
import QRCode from "qrcode";
import type { MfaSetupResponse, MfaStatusView, RecoveryCodesResponse } from "@clubos/contracts";
import { useShell } from "@/components/AppShell";
import { Button, Card, Field, inputClass } from "@/components/ui";
import { apiGet, apiPost } from "@/lib/api";
import { t } from "@/lib/i18n";

type Mode = "idle" | "setup" | "codes" | "disable";

async function postSession<T>(path: string, body: unknown): Promise<T> {
  const res = await fetch(path, {
    method: "POST",
    headers: { "content-type": "application/json", "x-clubos-csrf": "1" },
    body: JSON.stringify(body),
  });
  const data = await res.json().catch(() => ({}));
  if (!res.ok) throw new Error(data.detail ?? `Ошибка ${res.status}`);
  return data as T;
}

/** Двухфакторная аутентификация (TOTP): настройка с QR, коды восстановления, отключение. */
export function MfaCard() {
  const { refreshMe } = useShell();
  const [status, setStatus] = useState<MfaStatusView>();
  const [mode, setMode] = useState<Mode>("idle");
  const [setup, setSetup] = useState<MfaSetupResponse>();
  const [qr, setQr] = useState<string>();
  const [code, setCode] = useState("");
  const [password, setPassword] = useState("");
  const [codes, setCodes] = useState<string[]>([]);
  const [copied, setCopied] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string>();
  const [notice, setNotice] = useState<string>();
  const [reload, setReload] = useState(0);

  useEffect(() => {
    const controller = new AbortController();
    apiGet<MfaStatusView>("me/mfa", controller.signal)
      .then(setStatus)
      .catch((e: Error) => {
        if (e.name !== "AbortError") setError(e.message);
      });
    return () => controller.abort();
  }, [reload]);

  useEffect(() => {
    if (!setup) return;
    let cancelled = false;
    QRCode.toDataURL(setup.otpAuthUri, { margin: 1, width: 200, errorCorrectionLevel: "M" })
      .then((url) => {
        if (!cancelled) setQr(url);
      })
      .catch(() => setQr(undefined));
    return () => {
      cancelled = true;
    };
  }, [setup]);

  const run = async (action: () => Promise<void>) => {
    setBusy(true);
    setError(undefined);
    setNotice(undefined);
    try {
      await action();
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setBusy(false);
    }
  };

  const begin = () =>
    run(async () => {
      setSetup(await apiPost<MfaSetupResponse>("me/mfa/setup"));
      setCode("");
      setMode("setup");
    });

  const enable = (e: FormEvent) => {
    e.preventDefault();
    void run(async () => {
      const data = await postSession<{ recoveryCodes: string[] }>("/api/auth/mfa/enable", { code });
      setCodes(data.recoveryCodes);
      setSetup(undefined);
      setQr(undefined);
      setMode("codes");
      setNotice(t.mfa.enabledDone);
      refreshMe();
    });
  };

  const disable = (e: FormEvent) => {
    e.preventDefault();
    void run(async () => {
      const recovery = code.includes("-") || /[a-z]/i.test(code);
      await postSession("/api/auth/mfa/disable", recovery ? { password, recoveryCode: code } : { password, code });
      setMode("idle");
      setPassword("");
      setCode("");
      setNotice(t.mfa.disabledDone);
      setReload((x) => x + 1);
      refreshMe();
    });
  };

  const regenerate = (e: FormEvent) => {
    e.preventDefault();
    void run(async () => {
      const data = await apiPost<RecoveryCodesResponse>("me/mfa/recovery-codes", { code });
      setCodes(data.recoveryCodes);
      setCode("");
      setMode("codes");
    });
  };

  const copyCodes = async () => {
    try {
      await navigator.clipboard.writeText(codes.join("\n"));
      setCopied(true);
    } catch {
      setCopied(false);
    }
  };

  const codeInput = (label: string) => (
    <Field label={label}>
      <input
        className={`${inputClass} font-mono tracking-widest`}
        inputMode="numeric"
        autoComplete="one-time-code"
        required
        maxLength={11}
        value={code}
        onChange={(e) => setCode(e.target.value)}
      />
    </Field>
  );

  return (
    <Card title={t.mfa.title}>
      <div className="flex flex-col gap-4" data-testid="mfa-card">
        {status && (
          <p className="text-sm">
            <span
              data-testid="mfa-status"
              className={`rounded-full border px-2.5 py-0.5 text-xs font-semibold ${status.enabled ? "border-emerald-300 bg-emerald-50 text-emerald-800" : "border-slate-300 bg-slate-50 text-slate-700"}`}
            >
              {status.enabled ? t.mfa.on : t.mfa.off}
            </span>
            {status.required && <span className="ml-2 text-slate-600">{t.mfa.requiredNote}</span>}
            {status.enabled && (
              <span className="ml-2 text-slate-600">
                · {t.mfa.codesLeft}: {status.recoveryCodesLeft}
              </span>
            )}
          </p>
        )}

        {notice && (
          <p role="status" className="rounded-lg bg-emerald-50 px-3 py-2 text-sm text-emerald-800">
            {notice}
          </p>
        )}
        {error && (
          <p role="alert" className="rounded-lg bg-red-50 px-3 py-2 text-sm text-red-800">
            {error}
          </p>
        )}

        {status && !status.enabled && mode === "idle" && (
          <>
            <p className="text-sm text-slate-600">{t.mfa.intro}</p>
            <div>
              <Button disabled={busy} onClick={begin}>
                {t.mfa.start}
              </Button>
            </div>
          </>
        )}

        {mode === "setup" && setup && (
          <form onSubmit={enable} className="flex flex-col gap-3" aria-label={t.mfa.start}>
            <p className="text-sm text-slate-700">{t.mfa.step1}</p>
            {/* eslint-disable-next-line @next/next/no-img-element -- data URL, оптимизация next/image не нужна */}
            {qr && <img src={qr} alt="QR-код для приложения-аутентификатора" width={200} height={200} className="rounded border border-slate-200" />}
            <p className="text-xs text-slate-500">
              {t.mfa.manual} <code data-testid="mfa-secret" className="rounded bg-slate-100 px-1.5 py-0.5 font-mono text-slate-800">{setup.secret}</code>
            </p>
            <p className="text-sm text-slate-700">{t.mfa.step2}</p>
            {codeInput(t.mfa.code)}
            <div className="flex gap-3">
              <Button type="submit" disabled={busy}>
                {t.mfa.enable}
              </Button>
              <Button variant="secondary" onClick={() => setMode("idle")}>
                {t.mfa.cancel}
              </Button>
            </div>
          </form>
        )}

        {mode === "codes" && codes.length > 0 && (
          <div className="flex flex-col gap-3" data-testid="recovery-codes">
            <h3 className="text-sm font-semibold text-slate-800">{t.mfa.codesTitle}</h3>
            <p className="text-sm text-amber-800">{t.mfa.codesHint}</p>
            <ul className="grid grid-cols-2 gap-2 rounded-lg bg-slate-50 p-3 font-mono text-sm">
              {codes.map((c) => (
                <li key={c}>{c}</li>
              ))}
            </ul>
            <div className="flex gap-3">
              <Button variant="secondary" onClick={copyCodes}>
                {copied ? t.mfa.copied : t.mfa.copy}
              </Button>
              <Button
                onClick={() => {
                  setCodes([]);
                  setCopied(false);
                  setMode("idle");
                  setReload((x) => x + 1);
                }}
              >
                {t.mfa.saved}
              </Button>
            </div>
          </div>
        )}

        {status?.enabled && mode === "idle" && (
          <div className="flex flex-col gap-4">
            <form onSubmit={regenerate} className="flex flex-col gap-2" aria-label={t.mfa.regenerate}>
              {codeInput(t.mfa.code)}
              <p className="text-xs text-slate-500">{t.mfa.regenerateHint}</p>
              <div className="flex flex-wrap gap-3">
                <Button type="submit" variant="secondary" disabled={busy}>
                  {t.mfa.regenerate}
                </Button>
                {!status.required && (
                  <Button variant="danger" disabled={busy} onClick={() => setMode("disable")}>
                    {t.mfa.disable}
                  </Button>
                )}
              </div>
            </form>
          </div>
        )}

        {mode === "disable" && (
          <form onSubmit={disable} className="flex flex-col gap-3" aria-label={t.mfa.disable}>
            <p className="text-sm text-slate-600">{t.mfa.disableHint}</p>
            <Field label={t.mfa.password}>
              <input className={inputClass} type="password" autoComplete="current-password" required value={password} onChange={(e) => setPassword(e.target.value)} />
            </Field>
            {codeInput(t.mfa.code)}
            <div className="flex gap-3">
              <Button type="submit" variant="danger" disabled={busy}>
                {t.mfa.disable}
              </Button>
              <Button variant="secondary" onClick={() => setMode("idle")}>
                {t.mfa.cancel}
              </Button>
            </div>
          </form>
        )}
      </div>
    </Card>
  );
}
