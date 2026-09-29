"use client";

import Link from "next/link";
import type { DeviceStatus, DeviceView } from "@clubos/contracts";
import { useShell } from "@/components/AppShell";
import { IconServer } from "@/components/icons";
import { SessionTimer } from "@/components/SessionTimer";
import { DeviceStatusBadge } from "@/components/StatusBadge";
import { Card, EmptyState, ErrorState, Loading, SimulatedBadge } from "@/components/ui";
import { apiGet } from "@/lib/api";
import { formatAgo, formatDateTime } from "@/lib/format";
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
                        <span className="font-semibold text-slate-900">{d.displayName}</span>
                        <DeviceStatusBadge status={d.status} />
                      </div>
                      <div className="flex flex-wrap items-center gap-2 text-xs text-slate-500">
                        {d.simulated && <SimulatedBadge />}
                        {d.inventory && (
                          <span>
                            {d.inventory.hostname} · {d.inventory.ipv4}
                          </span>
                        )}
                      </div>
                      <div className="mt-auto text-xs text-slate-500" title={formatDateTime(d.lastHeartbeatUtc, location.timezone)}>
                        {t.dashboard.lastHeartbeat}: {formatAgo(d.lastHeartbeatUtc, now)}
                      </div>
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

function EdgePanel() {
  const { location } = useShell();
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
          </li>
        ))}
      </ul>
    </Card>
  );
}
