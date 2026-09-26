"use client";

import Link from "next/link";
import { useParams } from "next/navigation";
import { useState, type FormEvent } from "react";
import type { AuditEventView, CommandView, DeviceView, SessionView, ShowMessagePayload, LockTestModePayload } from "@clubos/contracts";
import { useShell } from "@/components/AppShell";
import { useSessionClock } from "@/components/SessionTimer";
import { CommandStateBadge, DeviceStatusBadge, SessionStateBadge } from "@/components/StatusBadge";
import { Button, Card, EmptyState, ErrorState, Field, Loading, SimulatedBadge, inputClass } from "@/components/ui";
import { ApiError, apiGet, apiPost } from "@/lib/api";
import { formatDateTime, formatMoney } from "@/lib/format";
import { actionLabel, t } from "@/lib/i18n";
import { usePolling } from "@/lib/usePolling";

export default function DevicePage() {
  const { deviceId } = useParams<{ deviceId: string }>();
  const { location } = useShell();
  const device = usePolling((s) => apiGet<DeviceView>(`devices/${deviceId}`, s), 3000, [deviceId]);

  if (device.error instanceof ApiError && device.error.status === 404) {
    return <EmptyState>{t.device.notFound}</EmptyState>;
  }

  return (
    <div className="flex flex-col gap-6">
      <Link href="/" className="text-sm text-brand-700 hover:underline">
        {t.device.back}
      </Link>
      {device.loading && !device.data && <Loading />}
      {device.error && <ErrorState error={device.error} onRetry={device.refresh} />}
      {device.data && (
        <>
          <div className="flex flex-wrap items-center gap-3">
            <h1 className="text-2xl font-bold text-slate-900" data-testid="device-name">
              {device.data.displayName}
            </h1>
            <DeviceStatusBadge status={device.data.status} />
            {device.data.simulated && <SimulatedBadge />}
            <span className="text-sm text-slate-500">Зона {device.data.zoneName}</span>
          </div>

          <div className="grid grid-cols-1 gap-6 xl:grid-cols-2">
            <InventoryCard device={device.data} timezone={location.timezone} />
            <SessionCard device={device.data} onChange={device.refresh} />
            <CommandsCard deviceId={deviceId} timezone={location.timezone} />
            <DeviceAuditCard deviceId={deviceId} timezone={location.timezone} />
          </div>
        </>
      )}
    </div>
  );
}

function InventoryCard({ device, timezone }: { device: DeviceView; timezone: string }) {
  const inv = device.inventory;
  const rows: [string, string][] = inv
    ? [
        [t.device.hostname, inv.hostname],
        [t.device.windows, inv.windowsVersion],
        [t.device.cpu, inv.cpu],
        [t.device.ram, `${(inv.ramMegabytes / 1024).toFixed(1)} ГБ`],
        [t.device.ip, inv.ipv4],
        [t.device.agent, inv.agentVersion],
      ]
    : [];
  rows.push([t.dashboard.lastHeartbeat, formatDateTime(device.lastHeartbeatUtc, timezone)]);
  rows.push([t.device.enrolled, formatDateTime(device.enrolledAtUtc, timezone)]);

  return (
    <Card title={t.device.inventory}>
      {!inv && <p className="mb-3 text-sm text-slate-500">{t.device.noInventory}</p>}
      <dl className="grid grid-cols-[max-content_1fr] gap-x-6 gap-y-2 text-sm" data-testid="inventory">
        {rows.map(([k, v]) => (
          <div key={k} className="contents">
            <dt className="text-slate-500">{k}</dt>
            <dd className="font-medium break-words text-slate-900">{v}</dd>
          </div>
        ))}
      </dl>
    </Card>
  );
}

function SessionCard({ device, onChange }: { device: DeviceView; onChange: () => void }) {
  const { location } = useShell();
  const history = usePolling((s) => apiGet<SessionView[]>(`devices/${device.deviceId}/sessions`, s), 5000, [device.deviceId]);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string>();
  const session = device.activeSession;
  const clock = useSessionClock(session);
  const zone = location.zones.find((z) => z.zoneId === device.zoneId);

  const act = async (call: () => Promise<unknown>) => {
    setBusy(true);
    setError(undefined);
    try {
      await call();
      onChange();
      history.refresh();
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setBusy(false);
    }
  };

  return (
    <Card title={t.device.session}>
      <div className="flex flex-col gap-4">
        {zone && (
          <p className="text-sm text-slate-600">
            {t.device.tariff}: <span className="font-semibold">{formatMoney(zone.pricePerHourMinorUnits, location.currency)}</span>
            {t.device.perHour}
          </p>
        )}

        {!session && <p className="text-sm text-slate-500">{t.device.noSession}</p>}
        {session?.state === "Created" && <p className="text-sm text-amber-700">{t.device.waitingEdge}</p>}
        {session?.state === "Active" && clock && (
          <div className="grid grid-cols-2 gap-4 rounded-lg bg-blue-50 p-4" data-testid="active-session">
            <div>
              <div className="text-xs text-blue-700">{t.device.duration}</div>
              <div className="font-mono text-2xl font-semibold text-blue-900">{clock.duration}</div>
            </div>
            <div>
              <div className="text-xs text-blue-700">
                {t.device.cost} <span className="text-[10px]">({t.device.costPreview})</span>
              </div>
              <div className="font-mono text-2xl font-semibold text-blue-900">{clock.cost}</div>
            </div>
          </div>
        )}
        {session?.endRequestedAtUtc && <p className="text-sm text-amber-700">{t.device.endRequested}</p>}

        {error && (
          <p role="alert" className="text-sm text-red-700">
            {error}
          </p>
        )}

        <div className="flex gap-3">
          {!session && (
            <Button disabled={busy || device.status === "Offline"} onClick={() => act(() => apiPost(`devices/${device.deviceId}/sessions`))}>
              {t.device.startSession}
            </Button>
          )}
          {session && (
            <Button
              variant="danger"
              disabled={busy || !!session.endRequestedAtUtc}
              onClick={() => act(() => apiPost(`sessions/${session.sessionId}/end`))}
            >
              {t.device.endSession}
            </Button>
          )}
        </div>

        <div>
          <h3 className="mb-2 text-sm font-semibold text-slate-700">{t.device.history}</h3>
          {history.data && history.data.length === 0 && <p className="text-sm text-slate-500">—</p>}
          <ul className="flex flex-col divide-y divide-slate-100 text-sm" data-testid="session-history">
            {history.data?.map((s) => (
              <li key={s.sessionId} className="flex flex-wrap items-center gap-3 py-2">
                <SessionStateBadge state={s.state} />
                <span className="text-slate-600">{formatDateTime(s.startedAtUtc ?? s.requestedAtUtc, location.timezone)}</span>
                {s.totalMinorUnits !== null && <span className="font-semibold">{formatMoney(s.totalMinorUnits, s.currency)}</span>}
                {s.origin === "edge" && <span className="rounded bg-slate-100 px-1.5 text-xs text-slate-600">Edge offline</span>}
                {s.failureReason && <span className="text-xs text-red-700">{s.failureReason}</span>}
              </li>
            ))}
          </ul>
        </div>
      </div>
    </Card>
  );
}

function CommandsCard({ deviceId, timezone }: { deviceId: string; timezone: string }) {
  const commands = usePolling((s) => apiGet<CommandView[]>(`devices/${deviceId}/commands`, s), 2000, [deviceId]);
  const [title, setTitle] = useState("Сообщение от администратора");
  const [message, setMessage] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string>();

  const lastLock = commands.data?.find((c) => c.commandType === "LockTestMode" && c.state === "Succeeded");
  const locked = (lastLock?.payload as LockTestModePayload | undefined)?.lock === true;

  const send = async (body: object) => {
    setBusy(true);
    setError(undefined);
    try {
      await apiPost(`devices/${deviceId}/commands`, body);
      commands.refresh();
      return true;
    } catch (e) {
      setError((e as Error).message);
      return false;
    } finally {
      setBusy(false);
    }
  };

  const submit = async (e: FormEvent) => {
    e.preventDefault();
    if (await send({ commandType: "ShowMessage", title, message })) setMessage("");
  };

  return (
    <Card title={t.device.commands}>
      <form onSubmit={submit} className="flex flex-col gap-3" aria-label={t.device.showMessage}>
        <h3 className="text-sm font-semibold text-slate-700">{t.device.showMessage}</h3>
        <Field label={t.device.messageTitle}>
          <input className={inputClass} maxLength={80} required value={title} onChange={(e) => setTitle(e.target.value)} />
        </Field>
        <Field label={t.device.messageText}>
          <textarea className={inputClass} maxLength={500} required rows={3} value={message} onChange={(e) => setMessage(e.target.value)} />
        </Field>
        <div className="flex flex-wrap gap-3">
          <Button type="submit" disabled={busy}>
            {busy ? t.device.sending : t.device.send}
          </Button>
          <Button variant="secondary" disabled={busy} onClick={() => send({ commandType: "LockTestMode", lock: !locked, reason: "Тестовая блокировка из Admin Web" })}>
            {locked ? t.device.lockOff : t.device.lockOn}
          </Button>
        </div>
        <p className="text-xs text-slate-500">{t.device.lockHint}</p>
        {error && (
          <p role="alert" className="text-sm text-red-700">
            {error}
          </p>
        )}
      </form>

      <div className="mt-5">
        {commands.data && commands.data.length === 0 && <p className="text-sm text-slate-500">{t.device.noCommands}</p>}
        <ul className="flex flex-col divide-y divide-slate-100 text-sm" data-testid="command-list">
          {commands.data?.map((c) => (
            <li key={c.commandId} className="flex flex-col gap-1 py-2">
              <div className="flex flex-wrap items-center gap-2">
                <CommandStateBadge state={c.state} />
                <span className="font-medium">
                  {c.commandType === "ShowMessage"
                    ? `«${(c.payload as ShowMessagePayload).title}»`
                    : (c.payload as LockTestModePayload).lock
                      ? "Блокировка: вкл"
                      : "Блокировка: выкл"}
                </span>
              </div>
              <div className="text-xs text-slate-500">
                {formatDateTime(c.issuedAtUtc, timezone)} → {formatDateTime(c.updatedAtUtc, timezone)}
                {c.error && <span className="ml-2 text-red-700">{c.error}</span>}
              </div>
            </li>
          ))}
        </ul>
      </div>
    </Card>
  );
}

function DeviceAuditCard({ deviceId, timezone }: { deviceId: string; timezone: string }) {
  const audit = usePolling((s) => apiGet<AuditEventView[]>(`audit?target=${encodeURIComponent(`device:${deviceId}`)}&limit=30`, s), 4000, [deviceId]);
  return (
    <Card title={t.device.audit} className="xl:col-span-2">
      {audit.error && <ErrorState error={audit.error} onRetry={audit.refresh} />}
      {audit.data && audit.data.length === 0 && <p className="text-sm text-slate-500">{t.audit.empty}</p>}
      <ol className="flex flex-col gap-2 text-sm" data-testid="device-audit">
        {audit.data?.map((a) => (
          <li key={a.auditId} className="flex flex-wrap gap-x-3 border-l-2 border-slate-200 pl-3">
            <span className="text-slate-500">{formatDateTime(a.occurredAtUtc, timezone)}</span>
            <span className="font-medium">{actionLabel(a.action)}</span>
            <span className={a.result === "failed" || a.result === "denied" ? "text-red-700" : "text-slate-600"}>{a.result}</span>
            <span className="text-slate-500">— {a.actorDisplay}</span>
          </li>
        ))}
      </ol>
    </Card>
  );
}
