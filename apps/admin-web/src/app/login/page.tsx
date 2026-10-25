"use client";

import { Suspense, useState, type FormEvent } from "react";
import { useRouter, useSearchParams } from "next/navigation";
import { Button, Field, inputClass } from "@/components/ui";
import { t } from "@/lib/i18n";

function LoginForm() {
  const router = useRouter();
  const params = useSearchParams();
  const [email, setEmail] = useState("");
  const [password, setPassword] = useState("");
  const [step, setStep] = useState<"password" | "mfa">("password");
  const [useRecovery, setUseRecovery] = useState(false);
  const [code, setCode] = useState("");
  const [error, setError] = useState<string>();
  const [busy, setBusy] = useState(false);

  const done = () => {
    const next = params.get("next");
    router.replace(next && next.startsWith("/") && !next.startsWith("//") ? next : "/");
  };

  const post = async (url: string, body: unknown) => {
    const res = await fetch(url, {
      method: "POST",
      headers: { "content-type": "application/json", "x-clubos-csrf": "1" },
      body: JSON.stringify(body),
    });
    return { res, data: await res.json().catch(() => ({})) };
  };

  const submitPassword = async (e: FormEvent) => {
    e.preventDefault();
    setBusy(true);
    setError(undefined);
    try {
      const { res, data } = await post("/api/auth/login", { email, password });
      if (!res.ok) {
        setError(res.status === 401 ? t.login.failed : (data.detail ?? `Ошибка ${res.status}`));
        return;
      }

      if (data.mfaRequired) {
        setPassword("");
        setStep("mfa");
        return;
      }

      done();
    } catch {
      setError("Сервер недоступен.");
    } finally {
      setBusy(false);
    }
  };

  const submitCode = async (e: FormEvent) => {
    e.preventDefault();
    setBusy(true);
    setError(undefined);
    try {
      const { res, data } = await post("/api/auth/mfa", useRecovery ? { recoveryCode: code } : { code });
      if (!res.ok) {
        setError(data.detail ?? `Ошибка ${res.status}`);
        setCode("");
        return;
      }

      done();
    } catch {
      setError("Сервер недоступен.");
    } finally {
      setBusy(false);
    }
  };

  const errorBox = error && (
    <p role="alert" className="rounded-lg bg-red-50 px-3 py-2 text-sm text-red-800">
      {error}
    </p>
  );

  if (step === "mfa") {
    return (
      <form onSubmit={submitCode} className="flex flex-col gap-4" aria-label={t.login.mfaTitle}>
        <h2 className="text-base font-semibold text-slate-900">{t.login.mfaTitle}</h2>
        <p className="text-sm text-slate-600">{useRecovery ? t.login.recoveryHint : t.login.mfaHint}</p>
        <Field label={useRecovery ? t.login.recoveryCode : t.login.mfaCode}>
          <input
            className={`${inputClass} font-mono tracking-widest`}
            inputMode={useRecovery ? "text" : "numeric"}
            autoComplete="one-time-code"
            autoFocus
            required
            maxLength={useRecovery ? 11 : 7}
            value={code}
            onChange={(e) => setCode(e.target.value)}
          />
        </Field>
        {errorBox}
        <Button type="submit" disabled={busy}>
          {busy ? t.login.submitting : t.login.confirm}
        </Button>
        <div className="flex flex-col gap-1 text-sm">
          <button
            type="button"
            className="text-left text-brand-700 underline"
            onClick={() => {
              setUseRecovery((x) => !x);
              setCode("");
              setError(undefined);
            }}
          >
            {useRecovery ? t.login.useApp : t.login.useRecovery}
          </button>
          <button
            type="button"
            className="text-left text-slate-500 underline"
            onClick={() => {
              setStep("password");
              setCode("");
              setError(undefined);
            }}
          >
            {t.login.backToPassword}
          </button>
        </div>
      </form>
    );
  }

  return (
    <form onSubmit={submitPassword} className="flex flex-col gap-4" aria-label={t.login.title}>
      <Field label={t.login.email}>
        <input className={inputClass} type="email" autoComplete="username" required value={email} onChange={(e) => setEmail(e.target.value)} />
      </Field>
      <Field label={t.login.password}>
        <input className={inputClass} type="password" autoComplete="current-password" required value={password} onChange={(e) => setPassword(e.target.value)} />
      </Field>
      {errorBox}
      <Button type="submit" disabled={busy}>
        {busy ? t.login.submitting : t.login.submit}
      </Button>
    </form>
  );
}

export default function LoginPage() {
  return (
    <main className="flex min-h-screen items-center justify-center p-6">
      <div className="w-full max-w-sm rounded-2xl border border-slate-200 bg-white p-8 shadow-sm">
        <div className="mb-6">
          <div className="text-2xl font-bold text-slate-900">
            {t.appName} <span className="rounded bg-brand-50 px-1.5 py-0.5 text-sm text-brand-700">{t.milestone}</span>
          </div>
          <h1 className="mt-1 text-sm text-slate-500">{t.login.title}</h1>
        </div>
        <Suspense>
          <LoginForm />
        </Suspense>
      </div>
    </main>
  );
}
