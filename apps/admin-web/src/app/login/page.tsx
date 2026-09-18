"use client";

import { type FormEvent, useState } from "react";
import { useRouter } from "next/navigation";
import { ApiError, login as apiLogin } from "@/lib/api";
import { useAuth } from "@/lib/auth";
import { t } from "@/lib/i18n";

export default function LoginPage() {
  const { signIn } = useAuth();
  const router = useRouter();
  const [email, setEmail] = useState("owner@demo.clubos");
  const [password, setPassword] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  async function onSubmit(event: FormEvent) {
    event.preventDefault();
    setBusy(true);
    setError(null);
    try {
      const tokens = await apiLogin(email, password);
      signIn(tokens);
      router.replace("/dashboard");
    } catch (err) {
      setError(err instanceof ApiError && err.status === 0 ? t("connectionError") : t("loginFailed"));
    } finally {
      setBusy(false);
    }
  }

  return (
    <div className="mx-auto mt-16 max-w-sm">
      <h1 className="mb-6 text-center text-xl font-semibold">{t("appName")}</h1>
      <form
        onSubmit={onSubmit}
        className="flex flex-col gap-4 rounded-2xl border border-[var(--border)] bg-[var(--surface)] p-6"
      >
        <label className="flex flex-col gap-1 text-sm">
          {t("email")}
          <input
            type="email"
            value={email}
            onChange={(e) => setEmail(e.target.value)}
            required
            className="rounded-lg border border-[var(--border)] bg-[var(--surface-2)] px-3 py-2 outline-none focus:border-[var(--accent)]"
          />
        </label>
        <label className="flex flex-col gap-1 text-sm">
          {t("password")}
          <input
            type="password"
            value={password}
            onChange={(e) => setPassword(e.target.value)}
            required
            className="rounded-lg border border-[var(--border)] bg-[var(--surface-2)] px-3 py-2 outline-none focus:border-[var(--accent)]"
          />
        </label>
        {error ? <div className="text-sm text-rose-300">{error}</div> : null}
        <button
          type="submit"
          disabled={busy}
          className="rounded-lg bg-[var(--accent)] px-3 py-2 font-medium text-white disabled:opacity-50"
        >
          {busy ? t("loading") : t("signIn")}
        </button>
      </form>
    </div>
  );
}
