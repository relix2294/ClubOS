"use client";

import Link from "next/link";
import { useState } from "react";
import { BookingRules, type BookingBrief, type DeviceStatus, type DeviceView, type PendingDisklessView } from "@clubos/contracts";
import { useShell } from "@/components/AppShell";
import { IconServer } from "@/components/icons";
import { SessionTimer } from "@/components/SessionTimer";
import { DeviceStatusBadge } from "@/components/StatusBadge";
import { Button, Card, EmptyState, ErrorState, Loading, SimulatedBadge } from "@/components/ui";
import { apiGet, apiPost } from "@/lib/api";
import { formatAgo, formatDateTime, formatTime } from "@/lib/format";
import { deviceStatusLabel, t } from "@/lib/i18n";
import { useNow, usePolling } from "@/lib/usePolling";

export default function DashboardPage() {
  const { location } = useShell();
  const now = useNow(5000);
  const { data: devices, error, loading, refresh } = usePolling(
    (signal) => apiGet<DeviceView[]>(`locations/${location.locationId}/devices`, signal),
    3000,
    [location.locationId],
    { topics: ["devices", "sessions"], locationId: location.locationId },
  );

  const counts = (devices ?? []).reduce<Partial<Record<DeviceStatus, number>>>((acc, d) => {
    acc[d.status] = (acc[d.status] ?? 0) + 1;
    return acc;
  }, {});

  return (
    <div className="flex flex-col gap-6">
      <div className="flex flex-wrap items-end justify-between gap-4">
        <h1 className="text-2xl font-bold text-slate-900">{t.dashboard.title}</h1>
        {devices && devices.length > 0 && (
          <ul className="flex flex-wrap gap-2 text-sm" aria-label="Сводка по статусам">
            {(Object.keys(counts) as DeviceStatus[]).map((status) => (
              <li key={status} className="rounded-lg border border-slate-200 bg-white px-3 py-1">
                {deviceStatusLabel[status]}: <span className="font-semibold">{counts[status]}</span>
              </li>
            ))}
          </ul>
        )}
      </div>

      <EdgePanel />
      <DisklessPanel />

      {loading && !devices && <Loading />}
      {error && <ErrorState error={error} onRetry={refresh} />}
      {devices && devices.length === 0 && <EmptyState>{t.dashboard.empty}</EmptyState>}

      {devices &&
        location.zones.map((zone) => {
          const zoneDevices = devices.filter((d) => d.zoneId === zone.zoneId);
          if (zoneDevices.length === 0) return null;
          return (
            <section key={zone.zoneId} aria-labelledby={`zone-${zone.zoneId}`}>
              <h2 id={`zone-${zone.zoneId}`} className="mb-3 text-sm font-semibold uppercase tracking-wide text-slate-500">
                Зона {zone.name} · {zoneDevices.length}
              </h2>
              <ul className="grid grid-cols-1 gap-3 sm:grid-cols-2 lg:grid-cols-3 xl:grid-cols-4">
                {zoneDevices.map((d) => (
                  <li key={d.deviceId}>
                    <Link
                      href={`/devices/${d.deviceId}`}
                      data-testid="device-tile"
                      className="flex h-full flex-col gap-2 rounded-xl border border-slate-200 bg-white p-4 shadow-sm transition hover:border-brand-500 hover:shadow"
                    >
                      <div className="flex items-start justify-between gap-2">
                        <span className="font-semibold text-slate-900" data-testid="device-tile-name">{d.displayName}</span>
                        <DeviceStatusBadge status={d.status} />
                      </div>
                      <div className="flex flex-wrap items-center gap-2 text-xs text-slate-500">
                        {d.simulated && <SimulatedBadge />}
                        {d.hardwareId && (
                          <span title={`MAC ${d.hardwareId}`} className="rounded border border-sky-300 bg-sky-50 px-1.5 py-0.5 text-[10px] font-semibold text-sky-800">
                            {t.diskless.badge}
                          </span>
                        )}
                        {d.inventory && (
                          <span>
                            {d.inventory.hostname} · {d.inventory.ipv4}
                          </span>
                        )}
                      </div>
                      <div className="mt-auto text-xs text-slate-500" title={formatDateTime(d.lastHeartbeatUtc, location.timezone)}>
                        {t.dashboard.lastHeartbeat}: {formatAgo(d.lastHeartbeatUtc, now)}
                      </div>
                      {d.nextBooking && <BookingBadge booking={d.nextBooking} timeZone={location.timezone} now={now} />}
                      {d.activeSession?.state === "Active" && <SessionTimer session={d.activeSession} />}
                      {d.activeSession?.state === "Created" && <span className="text-xs text-amber-700">{t.device.waitingEdge}</span>}
                    </Link>
                  </li>
                ))}
              </ul>
            </section>
          );
        })}
    </div>
  );
}

/** Бездисковые ПК, ждущие подтверждения (D-018). Видит тот, кто может подключать ПК. */
function DisklessPanel() {
  const { location, can } = useShell();
  const allowed = can("enrollment.manage");
  const pending = usePolling(
    (signal) =>
      allowed
        ? apiGet<PendingDisklessView[]>(`locations/${location.locationId}/diskless-candidates`, signal)
        : Promise.resolve([] as PendingDisklessView[]),
    15_000,
    [location.locationId, allowed],
    { topics: ["devices"], locationId: location.locationId },
  );

  if (!allowed || !pending.data || pending.data.length === 0) return null;
  return (
    <Card title={`${t.diskless.title} · ${pending.data.length}`} className="border-sky-300">
      <p className="mb-3 text-sm text-slate-600">{t.diskless.intro}</p>
      <ul className="flex flex-col divide-y divide-slate-100" data-testid="diskless-pending">
        {pending.data.map((c) => (
          <DisklessRow key={c.candidateId} candidate={c} onDone={pending.refresh} />
        ))}
      </ul>
    </Card>
  );
}

function DisklessRow({ candidate, onDone }: { candidate: PendingDisklessView; onDone: () => void }) {
  const { location } = useShell();
  const now = useNow(15_000);
  const [name, setName] = useState(candidate.hostname && candidate.hostname !== "-" ? candidate.hostname : "PC-");
  const [zoneId, setZoneId] = useState(location.zones[0]?.zoneId ?? "");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string>();

  const act = async (action: () => Promise<unknown>) => {
    setBusy(true);
    setError(undefined);
    try {
      await action();
      onDone();
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setBusy(false);
    }
  };

  return (
    <li className="flex flex-wrap items-end gap-3 py-3" data-testid="diskless-row" data-mac={candidate.mac}>
      <div className="min-w-48 text-sm">
        <div className="font-mono font-semibold text-slate-900">{candidate.mac}</div>
        <div className="text-xs text-slate-500">
          {candidate.hostname}
          {candidate.ipv4 ? ` · ${candidate.ipv4}` : ""} · {t.diskless.seen} {formatAgo(candidate.lastSeenUtc, now)}
          {candidate.simulated && " · SIMULATED"}
        </div>
      </div>
      <label className="flex flex-col gap-1 text-xs text-slate-600">
        {t.diskless.name}
        <input aria-label={t.diskless.name} className="rounded-lg border border-slate-300 px-2 py-1 text-sm" maxLength={64} value={name} onChange={(e) => setName(e.target.value)} />
      </label>
      <label className="flex flex-col gap-1 text-xs text-slate-600">
        {t.diskless.zone}
        <select aria-label={t.diskless.zone} className="rounded-lg border border-slate-300 px-2 py-1 text-sm" value={zoneId} onChange={(e) => setZoneId(e.target.value)}>
          {location.zones.map((z) => (
            <option key={z.zoneId} value={z.zoneId}>
              {z.name}
            </option>
          ))}
        </select>
      </label>
      <Button className="py-1" disabled={busy || !name.trim()} onClick={() => act(() => apiPost(`diskless-candidates/${candidate.candidateId}/approve`, { displayName: name.trim(), zoneId }))}>
        {t.diskless.approve}
      </Button>
      <Button
        variant="secondary"
        className="py-1"
        disabled={busy}
        onClick={() => window.confirm(t.diskless.dismissConfirm) && act(() => apiPost(`diskless-candidates/${candidate.candidateId}/dismiss`))}
      >
        {t.diskless.dismiss}
      </Button>
      {error && (
        <p role="alert" className="w-full text-sm text-red-700">
          {error}
        </p>
      )}
    </li>
  );
}

function EdgePanel() {
  const { location, can, refreshMe } = useShell();
  if (location.edges.length === 0) {
    return <EmptyState>{t.dashboard.noEdge}</EmptyState>;
  }

  return (
    <Card>
      <ul className="flex flex-col gap-2">
        {location.edges.map((edge) => (
          <li key={edge.edgeId} className="flex flex-wrap items-center gap-3 text-sm" data-testid="edge-status">
            <IconServer className="h-5 w-5 text-slate-500" />
            <span className="font-medium">
              {t.dashboard.edge} «{edge.name}»
            </span>
            <span
              className={`rounded-full border px-2.5 py-0.5 text-xs font-semibold ${edge.online ? "border-emerald-300 bg-emerald-50 text-emerald-800" : "border-red-300 bg-red-50 text-red-800"}`}
            >
              {edge.online ? `● ${t.dashboard.edgeOnline}` : `○ ${t.dashboard.edgeOffline}`}
            </span>
            {edge.pendingOutboxEvents > 0 && (
              <span className="text-xs text-amber-700">
                {edge.pendingOutboxEvents} {t.dashboard.pendingSync}
              </span>
            )}
            <span className="text-xs text-slate-500">последняя связь: {formatDateTime(edge.lastSeenAtUtc, location.timezone)}</span>
            <span className="text-xs text-slate-500">
              {t.dashboard.edgeCertificate}: {formatDateTime(edge.certificateExpiresAtUtc, location.timezone)}
            </span>
            {can("enrollment.manage") && (
              <button
                type="button"
                className="ml-auto text-xs text-red-700 underline"
                onClick={async () => {
                  if (!window.confirm(t.dashboard.revokeEdgeConfirm(edge.name))) return;
                  try {
                    await apiPost(`edges/${edge.edgeId}/revoke`);
                    refreshMe();
                  } catch (e) {
                    window.alert((e as Error).message);
                  }
                }}
              >
                {t.dashboard.revokeEdge}
              </button>
            )}
          </li>
        ))}
      </ul>
    </Card>
  );
}

/** Бронь на плитке: янтарная, когда ПК уже держится для гостя (за 15 минут до начала). */
function BookingBadge({ booking, timeZone, now }: { booking: BookingBrief; timeZone: string; now: number }) {
  const holding = Date.parse(booking.startsAtUtc) - now <= BookingRules.holdMinutes * 60_000;
  return (
    <span
      data-testid="tile-booking"
      className={`rounded px-1.5 py-0.5 text-xs ${holding ? "bg-amber-100 font-semibold text-amber-900" : "bg-violet-50 text-violet-800"}`}
    >
      {t.bookings.badge} {formatTime(booking.startsAtUtc, timeZone)}–{formatTime(booking.endsAtUtc, timeZone)} · {booking.guestName}
    </span>
  );
}
