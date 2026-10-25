"use client";

import { useEffect, useState, type FormEvent, type ReactNode } from "react";
import type { EnrollmentTokenResponse } from "@clubos/contracts";
import { useShell } from "@/components/AppShell";
import { Button, Card, Field, inputClass } from "@/components/ui";
import { apiGet, apiPost } from "@/lib/api";
import { formatDateTime } from "@/lib/format";
import { t } from "@/lib/i18n";

export default function EnrollmentPage() {
  const { location, can } = useShell();
  const isOwner = can("enrollment.manage");
  const fingerprint = useCaFingerprint(isOwner);

  return (
    <div className="flex flex-col gap-6">
      <h1 className="text-2xl font-bold text-slate-900">{t.enrollment.title}</h1>
      {!isOwner && <p className="text-sm text-slate-600">Создавать токены может владелец или администратор.</p>}
      <DownloadsCard />
      <div className="grid grid-cols-1 gap-6 xl:grid-cols-2">
        <EdgeTokenForm locationId={location.locationId} timezone={location.timezone} disabled={!isOwner} />
        <DeviceTokenForm disabled={!isOwner} fingerprint={fingerprint} />
      </div>
      {isOwner && fingerprint && <CaFingerprint fingerprint={fingerprint} />}
    </div>
  );
}

/** Отпечаток dev CA для установки агента по HTTPS (D-007): агент доверяет Edge только от этого CA. */
function useCaFingerprint(enabled: boolean): string | undefined {
  const [fingerprint, setFingerprint] = useState<string>();
  useEffect(() => {
    if (!enabled) return;
    const controller = new AbortController();
    apiGet<{ fingerprintSha256: string }>("pki/ca", controller.signal)
      .then((x) => setFingerprint(x.fingerprintSha256))
      .catch(() => undefined);
    return () => controller.abort();
  }, [enabled]);
  return fingerprint;
}

function CopyButton({ text }: { text: string }) {
  const [copied, setCopied] = useState(false);
  return (
    <Button
      variant="secondary"
      className="shrink-0 px-2 py-0.5 text-xs"
      onClick={async () => {
        await navigator.clipboard.writeText(text);
        setCopied(true);
      }}
    >
      {copied ? t.common.copied : t.enrollment.copy}
    </Button>
  );
}

/** Строка «подпись: значение [Скопировать]» — то, что мастер установки спросит у техника. */
function Answer({ label, value, hint, testId }: { label: string; value?: string; hint?: ReactNode; testId?: string }) {
  return (
    <div className="flex flex-col gap-1">
      <span className="text-xs font-semibold text-slate-600">{label}</span>
      {value ? (
        <div className="flex items-start gap-2">
          <code data-testid={testId} className="block min-w-0 flex-1 rounded bg-white p-2 font-mono text-xs break-all">
            {value}
          </code>
          <CopyButton text={value} />
        </div>
      ) : (
        <span className="text-xs text-slate-600">{hint}</span>
      )}
    </div>
  );
}

function DownloadsCard() {
  const [version, setVersion] = useState<string>();
  useEffect(() => {
    fetch("/downloads/VERSION.txt", { cache: "no-store" })
      .then((r) => (r.ok ? r.text() : undefined))
      .then((v) => setVersion(v?.trim() || undefined))
      .catch(() => undefined);
  }, []);

  return (
    <Card title={t.enrollment.downloadsTitle}>
      <div className="flex flex-col gap-4 text-sm" data-testid="downloads">
        <p className="text-slate-600">{t.enrollment.downloadsIntro}</p>
        <div className="flex flex-wrap gap-3">
          <a
            href="/downloads/ClubOS-Edge-win-x64.zip"
            download
            className="inline-flex items-center rounded-lg bg-brand-600 px-4 py-2 font-medium text-white hover:bg-brand-700"
          >
            {t.enrollment.downloadEdge}
          </a>
          <a
            href="/downloads/ClubOS-Agent-win-x64.zip"
            download
            className="inline-flex items-center rounded-lg bg-brand-600 px-4 py-2 font-medium text-white hover:bg-brand-700"
          >
            {t.enrollment.downloadAgent}
          </a>
        </div>
        <div>
          <div className="font-semibold text-slate-800">{t.enrollment.stepsTitle}</div>
          <ol className="mt-1 list-decimal space-y-1 pl-5 text-slate-700">
            <li>{t.enrollment.step1}</li>
            <li>{t.enrollment.step2}</li>
            <li>{t.enrollment.step3}</li>
          </ol>
        </div>
        {version && (
          <p className="text-xs text-slate-500">
            {t.enrollment.downloadVersion}: <span className="font-mono">{version}</span> ·{" "}
            <a className="underline" href="/downloads/SHA256SUMS.txt">
              SHA-256
            </a>
          </p>
        )}
      </div>
    </Card>
  );
}

function CaFingerprint({ fingerprint }: { fingerprint: string }) {
  const command = `.\\install-agent.ps1 -EdgeUrl https://<IP сервера Edge>:7443 -EdgeCaFingerprint ${fingerprint} -EnrollmentToken <токен>`;
  return (
    <Card title={t.enrollment.caTitle}>
      <div className="flex flex-col gap-3 text-sm" data-testid="ca-fingerprint">
        <p className="text-slate-600">{t.enrollment.caHint}</p>
        <Answer label={t.enrollment.fingerprint} value={fingerprint} />
        <Answer label={t.enrollment.caCommand} value={command} />
      </div>
    </Card>
  );
}

function TokenResult({ token, timezone, children }: { token: EnrollmentTokenResponse; timezone: string; children?: ReactNode }) {
  return (
    <div className="mt-4 flex flex-col gap-3 rounded-lg border border-emerald-300 bg-emerald-50 p-4 text-sm" data-testid="enrollment-token">
      <p className="text-emerald-900">
        {t.enrollment.tokenOnce} {t.enrollment.expires}: {formatDateTime(token.expiresAtUtc, timezone)}
      </p>
      <p className="font-semibold text-slate-800">{t.enrollment.wizardAnswers}</p>
      {children}
      <Answer label={t.enrollment.token} value={token.enrollmentToken} testId="token-value" />
    </div>
  );
}

function EdgeTokenForm({ locationId, timezone, disabled }: { locationId: string; timezone: string; disabled: boolean }) {
  const [name, setName] = useState("Edge-1");
  const [token, setToken] = useState<EnrollmentTokenResponse>();
  const [error, setError] = useState<string>();
  const cloudUrl = typeof window === "undefined" ? "" : window.location.origin;

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
      </form>
      {error && <p role="alert" className="mt-3 text-sm text-red-700">{error}</p>}
      {token && (
        <TokenResult token={token} timezone={timezone}>
          <Answer label={t.enrollment.cloudAddress} value={cloudUrl} />
          <Answer
            label={t.enrollment.orCommand}
            value={`.\\install-edge.ps1 -CloudUrl ${cloudUrl} -EnrollmentToken ${token.enrollmentToken}`}
          />
        </TokenResult>
      )}
    </Card>
  );
}

function DeviceTokenForm({ disabled, fingerprint }: { disabled: boolean; fingerprint?: string }) {
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
      </form>
      {error && <p role="alert" className="mt-3 text-sm text-red-700">{error}</p>}
      {token && (
        <TokenResult token={token} timezone={location.timezone}>
          <Answer label={t.enrollment.edgeAddress} hint={t.enrollment.edgeAddressHint} />
          <Answer label={t.enrollment.fingerprint} value={fingerprint} />
          {fingerprint && (
            <Answer
              label={t.enrollment.orCommand}
              value={`.\\install-agent.ps1 -EdgeUrl https://<IP сервера клуба>:7443 -EdgeCaFingerprint ${fingerprint} -EnrollmentToken ${token.enrollmentToken} -ShellMode Enforced`}
            />
          )}
        </TokenResult>
      )}
    </Card>
  );
}
