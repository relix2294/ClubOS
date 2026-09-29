"use client";

import { useEffect, useState, type FormEvent } from "react";
import type { EnrollmentTokenResponse } from "@clubos/contracts";
import { useShell } from "@/components/AppShell";
import { Button, Card, Field, inputClass } from "@/components/ui";
import { apiGet, apiPost } from "@/lib/api";
import { formatDateTime } from "@/lib/format";
import { t } from "@/lib/i18n";

export default function EnrollmentPage() {
  const { location, can } = useShell();
  const isOwner = can("enrollment.manage");

  return (
    <div className="flex flex-col gap-6">
      <h1 className="text-2xl font-bold text-slate-900">{t.enrollment.title}</h1>
      {!isOwner && <p className="text-sm text-slate-600">Создавать токены может владелец или администратор.</p>}
      <div className="grid grid-cols-1 gap-6 xl:grid-cols-2">
        <EdgeTokenForm locationId={location.locationId} timezone={location.timezone} disabled={!isOwner} />
        <DeviceTokenForm disabled={!isOwner} />
      </div>
      {isOwner && <CaFingerprint />}
    </div>
  );
}

/** Отпечаток dev CA для установки агента по HTTPS (D-007): агент доверяет Edge только от этого CA. */
function CaFingerprint() {
  const [fingerprint, setFingerprint] = useState<string>();
  const [copied, setCopied] = useState(false);

  useEffect(() => {
    const controller = new AbortController();
    apiGet<{ fingerprintSha256: string }>("pki/ca", controller.signal)
      .then((x) => setFingerprint(x.fingerprintSha256))
      .catch(() => undefined);
    return () => controller.abort();
  }, []);

  if (!fingerprint) return null;
  const command = `.\\install-agent.ps1 -EdgeUrl https://<IP сервера Edge>:7443 -EdgeCaFingerprint ${fingerprint} -EnrollmentToken <токен>`;

  return (
    <Card title={t.enrollment.caTitle}>
      <div className="flex flex-col gap-3 text-sm" data-testid="ca-fingerprint">
        <p className="text-slate-600">{t.enrollment.caHint}</p>
        <code className="block rounded bg-slate-50 p-2 font-mono text-xs break-all">{fingerprint}</code>
        <p className="text-slate-600">{t.enrollment.caCommand}</p>
        <code className="block rounded bg-slate-50 p-2 font-mono text-xs break-all">{command}</code>
        <div>
          <Button
            variant="secondary"
            className="py-1"
            onClick={async () => {
              await navigator.clipboard.writeText(command);
              setCopied(true);
            }}
          >
            {copied ? t.mfa.copied : t.enrollment.copy}
          </Button>
        </div>
      </div>
    </Card>
  );
}

function TokenResult({ token, timezone }: { token: EnrollmentTokenResponse; timezone: string }) {
  const [copied, setCopied] = useState(false);
  return (
    <div className="mt-4 rounded-lg border border-emerald-300 bg-emerald-50 p-4 text-sm" data-testid="enrollment-token">
      <p className="mb-2 text-emerald-900">{t.enrollment.tokenOnce}</p>
      <code className="block rounded bg-white p-2 font-mono text-xs break-all">{token.enrollmentToken}</code>
      <div className="mt-2 flex items-center gap-3">
        <Button
          variant="secondary"
          className="py-1"
          onClick={async () => {
            await navigator.clipboard.writeText(token.enrollmentToken);
            setCopied(true);
          }}
        >
          {copied ? t.common.copied : t.enrollment.copy}
        </Button>
        <span className="text-xs text-slate-600">
          {t.enrollment.expires}: {formatDateTime(token.expiresAtUtc, timezone)}
        </span>
      </div>
    </div>
  );
}

function EdgeTokenForm({ locationId, timezone, disabled }: { locationId: string; timezone: string; disabled: boolean }) {
  const [name, setName] = useState("Edge-1");
  const [token, setToken] = useState<EnrollmentTokenResponse>();
  const [error, setError] = useState<string>();

  const submit = async (e: FormEvent) => {
    e.preventDefault();
    setError(undefined);
    try {
      setToken(await apiPost<EnrollmentTokenResponse>("enrollment-tokens/edge", { locationId, name }));
    } catch (err) {
      setError((err as Error).message);
    }
  };

  return (
    <Card title={t.enrollment.edgeTitle}>
      <form onSubmit={submit} className="flex flex-col gap-3">
        <Field label={t.enrollment.edgeName}>
          <input className={inputClass} required maxLength={64} value={name} onChange={(e) => setName(e.target.value)} />
        </Field>
        <Button type="submit" disabled={disabled}>
          {t.enrollment.create}
        </Button>
        <p className="text-xs text-slate-500">
          Передайте токен Edge через переменную окружения <code>CLUBOS_Edge__EnrollmentToken</code> при первом запуске.
        </p>
      </form>
      {error && <p role="alert" className="mt-3 text-sm text-red-700">{error}</p>}
      {token && <TokenResult token={token} timezone={timezone} />}
    </Card>
  );
}

function DeviceTokenForm({ disabled }: { disabled: boolean }) {
  const { location } = useShell();
  const [name, setName] = useState("PC-01");
  const [zoneId, setZoneId] = useState(location.zones[0]?.zoneId ?? "");
  const [token, setToken] = useState<EnrollmentTokenResponse>();
  const [error, setError] = useState<string>();

  const submit = async (e: FormEvent) => {
    e.preventDefault();
    setError(undefined);
    try {
      setToken(
        await apiPost<EnrollmentTokenResponse>("enrollment-tokens/device", {
          locationId: location.locationId,
          zoneId,
          displayName: name,
          simulated: false,
        }),
      );
    } catch (err) {
      setError((err as Error).message);
    }
  };

  return (
    <Card title={t.enrollment.deviceTitle}>
      <form onSubmit={submit} className="flex flex-col gap-3">
        <Field label={t.enrollment.deviceName}>
          <input className={inputClass} required maxLength={64} value={name} onChange={(e) => setName(e.target.value)} />
        </Field>
        <Field label={t.enrollment.zone}>
          <select className={inputClass} value={zoneId} onChange={(e) => setZoneId(e.target.value)}>
            {location.zones.map((z) => (
              <option key={z.zoneId} value={z.zoneId}>
                {z.name}
              </option>
            ))}
          </select>
        </Field>
        <Button type="submit" disabled={disabled}>
          {t.enrollment.create}
        </Button>
        <p className="text-xs text-slate-500">
          Укажите токен в <code>agent.json</code> (<code>Agent:EnrollmentToken</code>) — см. runbook установки агента.
        </p>
      </form>
      {error && <p role="alert" className="mt-3 text-sm text-red-700">{error}</p>}
      {token && <TokenResult token={token} timezone={location.timezone} />}
    </Card>
  );
}
