"use client";

import Link from "next/link";
import { useParams, useRouter } from "next/navigation";
import { useState, type FormEvent } from "react";
import {
  SessionLimits,
  periodContains,
  type AuditEventView,
  type ClientView,
  type CommandView,
  type DeviceView,
  type ExtendSessionRequest,
  type KillProcessPayload,
  type LockTestModePayload,
  type SessionView,
  type ShowMessagePayload,
  type StartSessionRequest,
} from "@clubos/contracts";
import { useShell } from "@/components/AppShell";
import { RemoteCard } from "@/components/RemoteCard";
import { ClientPicker } from "@/components/ClientPicker";
import { useSessionClock } from "@/components/SessionTimer";
import { CommandStateBadge, DeviceStatusBadge, SessionStateBadge } from "@/components/StatusBadge";
import { Button, Card, EmptyState, ErrorState, Field, Loading, SimulatedBadge, inputClass } from "@/components/ui";
import { ApiError, apiGet, apiPost } from "@/lib/api";
import { formatDateTime, formatLimit, formatMoney, formatTime, inStartWindow, localDayMinute } from "@/lib/format";
import { windowLabel } from "@/components/ZoneTariffEditor";
import { actionLabel, t } from "@/lib/i18n";
import { usePolling } from "@/lib/usePolling";

export default function DevicePage() {
  const { deviceId } = useParams<{ deviceId: string }>();
  const { location, can } = useShell();
  const device = usePolling((s) => apiGet<DeviceView>(`devices/${deviceId}`, s), 3000, [deviceId], {
    topics: ["devices", "sessions"],
    deviceId,
  });

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
            {can("devices.remote") && <RemoteCard device={device.data} />}
            {can("audit.view") && <DeviceAuditCard deviceId={deviceId} timezone={location.timezone} />}
            {can("enrollment.manage") && <RevokeCard device={device.data} />}
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
  if (device.hardwareId) {
    rows.push([t.diskless.mode, `MAC ${device.hardwareId}`]);
  } else {
    rows.push([t.device.certificate, formatDateTime(device.certificateExpiresAtUtc, timezone)]);
  }

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

const LIMIT_PRESETS = [30, 60, 120, 180];

/** Итоговый лимит с продлениями: от старта до планового окончания; до старта — запрошенный. */
function limitMinutes(s: SessionView): number | null {
  if (s.plannedEndAtUtc && s.startedAtUtc) return Math.round((Date.parse(s.plannedEndAtUtc) - Date.parse(s.startedAtUtc)) / 60_000);
  return s.durationMinutes;
}
const EXTEND_PRESETS = [15, 30, 60];

function SessionCard({ device, onChange }: { device: DeviceView; onChange: () => void }) {
  const { location, can } = useShell();
  const history = usePolling((s) => apiGet<SessionView[]>(`devices/${device.deviceId}/sessions`, s), 5000, [device.deviceId], {
    topics: ["sessions"],
    deviceId: device.deviceId,
  });
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string>();
  const [limit, setLimit] = useState<string>("60");
  const [custom, setCustom] = useState("45");
  const [client, setClient] = useState<ClientView | null>(null);
  const [extendedFrom, setExtendedFrom] = useState<string | null>(null);
  const session = device.activeSession;
  const clock = useSessionClock(session);
  const zone = location.zones.find((z) => z.zoneId === device.zoneId);
  const limited = session?.durationMinutes != null;
  // Продление подтверждено, когда Edge сдвинул плановое окончание.
  const extendPending = extendedFrom !== null && session?.plannedEndAtUtc === extendedFrom;

  const act = async (call: () => Promise<unknown>) => {
    setBusy(true);
    setError(undefined);
    try {
      await call();
      onChange();
      history.refresh();
      return true;
    } catch (e) {
      setError((e as Error).message);
      return false;
    } finally {
      setBusy(false);
    }
  };

  // Цена «сейчас» и доступность пакетов — по местному времени локации.
  const local = localDayMinute(location.timezone);
  const nowPeriod = zone?.periods.find((p) => periodContains(p, local.day, local.minute));
  const packages = zone?.packages.filter((p) => p.isActive) ?? [];

  const start = () => {
    if (limit.startsWith("pkg:")) {
      const body: StartSessionRequest = { packageId: limit.slice(4), clientId: client?.clientId ?? null };
      void act(async () => {
        await apiPost(`devices/${device.deviceId}/sessions`, body);
        setClient(null);
      });
      return;
    }
    const minutes = limit === "open" ? null : limit === "custom" ? Number(custom) : Number(limit);
    if (minutes !== null && (!Number.isInteger(minutes) || minutes < SessionLimits.minDurationMinutes || minutes > SessionLimits.maxDurationMinutes)) {
      setError(`Лимит — от ${SessionLimits.minDurationMinutes} до ${SessionLimits.maxDurationMinutes} минут.`);
      return;
    }
    const body: StartSessionRequest = { durationMinutes: minutes, clientId: client?.clientId ?? null };
    void act(async () => {
      await apiPost(`devices/${device.deviceId}/sessions`, body);
      setClient(null);
    });
  };

  const extend = async (minutes: number) => {
    if (!session) return;
    const body: ExtendSessionRequest = { minutes };
    const before = session.plannedEndAtUtc;
    if (await act(() => apiPost(`sessions/${session.sessionId}/extend`, body))) setExtendedFrom(before);
  };

  return (
    <Card title={t.device.session}>
      <div className="flex flex-col gap-4">
        {zone && (
          <p className="text-sm text-slate-600">
            {t.device.tariff}: <span className="font-semibold">{formatMoney(zone.pricePerHourMinorUnits, location.currency)}</span>
            {t.device.perHour}
            {nowPeriod && (
              <span className="ml-2 rounded bg-emerald-50 px-1.5 py-0.5 text-xs font-semibold text-emerald-800" data-testid="price-now">
                {t.tariffs.nowPrice}: {formatMoney(nowPeriod.pricePerHourMinorUnits, location.currency)}
                {t.device.perHour}
              </span>
            )}
          </p>
        )}

        {!session && <p className="text-sm text-slate-500">{t.device.noSession}</p>}
        {session?.state === "Created" && <p className="text-sm text-amber-700">{t.device.waitingEdge}</p>}
        {device.nextBooking && (
          <p className="rounded-lg bg-violet-50 px-3 py-2 text-sm text-violet-900" data-testid="device-booking">
            {t.bookings.badge}: {formatDateTime(device.nextBooking.startsAtUtc, location.timezone)} –{" "}
            {formatTime(device.nextBooking.endsAtUtc, location.timezone)} · {device.nextBooking.guestName}.{" "}
            <Link href="/bookings" className="underline">
              {t.nav.bookings}
            </Link>
          </p>
        )}
        {session?.packageName && (
          <p className="text-sm text-slate-700" data-testid="session-package">
            {t.tariffs.package}: <span className="font-semibold">{session.packageName}</span>
            {session.packagePriceMinorUnits != null && ` · ${formatMoney(session.packagePriceMinorUnits, session.currency)}`}
          </p>
        )}
        {session?.state === "Active" && clock && (
          <div className={`grid gap-4 rounded-lg p-4 ${clock.soon ? "bg-amber-50" : "bg-blue-50"} ${limited ? "grid-cols-3" : "grid-cols-2"}`} data-testid="active-session">
            <div>
              <div className="text-xs text-blue-700">{t.device.duration}</div>
              <div className="font-mono text-2xl font-semibold text-blue-900">{clock.duration}</div>
            </div>
            {limited && (
              <div data-testid="session-remaining">
                <div className="text-xs text-blue-700">
                  {t.device.remaining} · {t.device.plannedEnd} {formatTime(session.plannedEndAtUtc, location.timezone)}
                </div>
                <div className={`font-mono text-2xl font-semibold ${clock.soon ? "text-amber-700" : "text-blue-900"}`}>{clock.remaining ?? "—"}</div>
              </div>
            )}
            <div>
              <div className="text-xs text-blue-700">
                {t.device.cost} <span className="text-[10px]">({t.device.costPreview})</span>
              </div>
              <div className="font-mono text-2xl font-semibold text-blue-900">{clock.cost}</div>
            </div>
          </div>
        )}
        {session?.endRequestedAtUtc && <p className="text-sm text-amber-700">{t.device.endRequested}</p>}
        {extendPending && <p className="text-sm text-amber-700">{t.device.extendPending}</p>}

        {error && (
          <p role="alert" className="text-sm text-red-700">
            {error}
          </p>
        )}

        {can("sessions.manage") && !session && (
          <div className="flex flex-col gap-3" aria-label={t.device.startSession} role="group">
            <Field label={t.device.limit}>
              <select className={inputClass} value={limit} onChange={(e) => setLimit(e.target.value)}>
                {LIMIT_PRESETS.map((m) => (
                  <option key={m} value={String(m)}>
                    {formatLimit(m)}
                  </option>
                ))}
                <option value="custom">{t.device.limitCustom}</option>
                <option value="open">{t.device.limitOpen}</option>
                {packages.length > 0 && (
                  <optgroup label={t.tariffs.packagesTitle}>
                    {packages.map((p) => {
                      const available = inStartWindow(p.availableFromMinute, p.availableToMinute, local.minute);
                      const window = windowLabel(p);
                      return (
                        <option key={p.packageId} value={`pkg:${p.packageId}`} disabled={!available}>
                          {p.name} — {formatMoney(p.priceMinorUnits, location.currency)} ({formatLimit(p.durationMinutes)}
                          {window ? `, ${window}` : ""}){available ? "" : ` · ${t.tariffs.unavailableNow}`}
                        </option>
                      );
                    })}
                  </optgroup>
                )}
              </select>
            </Field>
            {limit === "custom" && (
              <Field label={t.device.limitMinutes}>
                <input
                  className={inputClass}
                  type="number"
                  min={SessionLimits.minDurationMinutes}
                  max={SessionLimits.maxDurationMinutes}
                  value={custom}
                  onChange={(e) => setCustom(e.target.value)}
                />
              </Field>
            )}
            <p className="text-xs text-slate-500">{t.device.limitHint}</p>
            {can("cash.operate") && (
              <>
                <ClientPicker value={client} onChange={setClient} />
                <p className="text-xs text-slate-500">{t.clients.pickHint}</p>
              </>
            )}
            <div>
              <Button disabled={busy || device.status === "Offline"} onClick={start}>
                {t.device.startSession}
              </Button>
            </div>
          </div>
        )}
        {can("sessions.manage") && session && (
          <div className="flex flex-wrap items-center gap-3">
            {limited && session.state === "Active" && !session.endRequestedAtUtc && (
              <div className="flex flex-wrap items-center gap-2" role="group" aria-label={t.device.extend}>
                <span className="text-sm text-slate-600">{t.device.extend}:</span>
                {EXTEND_PRESETS.map((m) => (
                  <Button key={m} variant="secondary" disabled={busy || extendPending} onClick={() => extend(m)}>
                    +{formatLimit(m)}
                  </Button>
                ))}
              </div>
            )}
            <Button
              variant="danger"
              disabled={busy || !!session.endRequestedAtUtc}
              onClick={() => act(() => apiPost(`sessions/${session.sessionId}/end`))}
            >
              {t.device.endSession}
            </Button>
          </div>
        )}

        <div>
          <h3 className="mb-2 text-sm font-semibold text-slate-700">{t.device.history}</h3>
          {history.data && history.data.length === 0 && <p className="text-sm text-slate-500">—</p>}
          <ul className="flex flex-col divide-y divide-slate-100 text-sm" data-testid="session-history">
            {history.data?.map((s) => (
              <li key={s.sessionId} className="flex flex-wrap items-center gap-3 py-2">
                <SessionStateBadge state={s.state} />
                <span className="text-slate-600">{formatDateTime(s.startedAtUtc ?? s.requestedAtUtc, location.timezone)}</span>
                {limitMinutes(s) !== null && (
                  <span className="text-xs text-slate-500">
                    {t.device.limitLabel} {formatLimit(limitMinutes(s)!)}
                  </span>
                )}
                {s.packageName && <span className="rounded bg-brand-50 px-1.5 text-xs text-brand-700">{s.packageName}</span>}
                {s.totalMinorUnits !== null && <span className="font-semibold">{formatMoney(s.totalMinorUnits, s.currency)}</span>}
                {s.endReason && (
                  <span className="text-xs text-slate-500">{s.endReason === "timeLimit" ? t.device.endReasonTimeLimit : t.device.endReasonStaff}</span>
                )}
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
  const { can } = useShell();
  const commands = usePolling((s) => apiGet<CommandView[]>(`devices/${deviceId}/commands`, s), 2000, [deviceId], {
    topics: ["commands"],
    deviceId,
  });
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
      <form onSubmit={submit} className={can("devices.command") ? "flex flex-col gap-3" : "hidden"} aria-label={t.device.showMessage}>
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
                    : c.commandType === "LockTestMode"
                      ? (c.payload as LockTestModePayload).lock
                        ? "Блокировка: вкл"
                        : "Блокировка: выкл"
                      : c.commandType === "KillProcess"
                        ? `${t.remote.commandTypes.KillProcess}: ${(c.payload as KillProcessPayload).name}`
                        : t.remote.commandTypes[c.commandType]}
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
  const audit = usePolling(
    (s) => apiGet<AuditEventView[]>(`audit?target=${encodeURIComponent(`device:${deviceId}`)}&limit=30`, s),
    4000,
    [deviceId],
    { topics: ["audit"], deviceId },
  );
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

/** Удаление ПК (D-011): сертификат отзывается, Edge забывает устройство, история остаётся. */
function RevokeCard({ device }: { device: DeviceView }) {
  const router = useRouter();
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string>();

  const revoke = async () => {
    if (!window.confirm(t.device.revokeConfirm(device.displayName))) return;
    setBusy(true);
    setError(undefined);
    try {
      await apiPost(`devices/${device.deviceId}/revoke`);
      router.replace("/");
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setBusy(false);
    }
  };

  return (
    <Card title={t.device.revokeTitle} className="border-red-200 xl:col-span-2">
      <div className="flex flex-col gap-3">
        <p className="text-sm text-slate-600">{t.device.revokeHint}</p>
        {error && (
          <p role="alert" className="text-sm text-red-700">
            {error}
          </p>
        )}
        <div>
          <Button variant="danger" disabled={busy || !!device.activeSession} onClick={revoke}>
            {t.device.revoke}
          </Button>
          {device.activeSession && <span className="ml-3 text-xs text-slate-500">{t.device.revokeSessionOpen}</span>}
        </div>
      </div>
    </Card>
  );
}
