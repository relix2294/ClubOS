"use client";

import { useCallback, useEffect, useState } from "react";
import { ApiError, getDevices, getLocations } from "@/lib/api";
import { useRequireAuth } from "@/components/Guard";
import { DeviceCard } from "@/components/DeviceCard";
import { EmptyView, ErrorView, LoadingView } from "@/components/States";
import { t } from "@/lib/i18n";
import type { DeviceDto, LocationDto } from "@/lib/types";

const futureModules = [t("reports"), t("billing"), t("users")];

export default function DashboardPage() {
  const { session, ready } = useRequireAuth();
  const [location, setLocation] = useState<LocationDto | null>(null);
  const [zones, setZones] = useState<{ id: string; name: string }[]>([]);
  const [devices, setDevices] = useState<DeviceDto[]>([]);
  const [status, setStatus] = useState<"loading" | "ready" | "error">("loading");
  const [errorMessage, setErrorMessage] = useState("");

  const load = useCallback(async () => {
    if (!session) {
      return;
    }
    setStatus("loading");
    try {
      const locations = await getLocations(session.accessToken);
      const current = locations[0] ?? null;
      setLocation(current);
      setZones(current?.zones ?? []);
      setDevices(await getDevices(session.accessToken, current?.id));
      setStatus("ready");
    } catch (err) {
      setErrorMessage(err instanceof ApiError && err.status === 0 ? t("connectionError") : String(err));
      setStatus("error");
    }
  }, [session]);

  useEffect(() => {
    if (ready && session) {
      void load();
    }
  }, [ready, session, load]);

  if (!ready || !session) {
    return <LoadingView />;
  }

  const grouped = groupByZone(devices, zones);

  return (
    <div className="flex flex-col gap-6">
      <section className="flex flex-wrap items-center justify-between gap-3">
        <div>
          <h1 className="text-lg font-semibold">{location?.name ?? t("dashboard")}</h1>
          {location ? (
            <p className="text-sm text-[var(--muted)]">
              {location.timezone} · {location.currency}
            </p>
          ) : null}
        </div>
        <div className="flex flex-wrap gap-2">
          {futureModules.map((label) => (
            <button
              key={label}
              disabled
              title={t("notImplemented")}
              className="cursor-not-allowed rounded-lg border border-dashed border-[var(--border)] px-3 py-1.5 text-sm text-[var(--muted)] opacity-70"
            >
              {label} · {t("notImplemented")}
            </button>
          ))}
        </div>
      </section>

      {status === "loading" ? <LoadingView /> : null}
      {status === "error" ? <ErrorView message={errorMessage} onRetry={() => void load()} /> : null}
      {status === "ready" && devices.length === 0 ? <EmptyView /> : null}

      {status === "ready" && devices.length > 0
        ? grouped.map((group) => (
            <section key={group.zoneId} className="flex flex-col gap-3">
              <h2 className="text-sm font-medium text-[var(--muted)]">
                {t("zone")}: {group.zoneName}
              </h2>
              <div className="grid grid-cols-1 gap-3 sm:grid-cols-2 lg:grid-cols-3">
                {group.devices.map((device) => (
                  <DeviceCard key={device.id} device={device} />
                ))}
              </div>
            </section>
          ))
        : null}
    </div>
  );
}

function groupByZone(
  devices: DeviceDto[],
  zones: { id: string; name: string }[],
): { zoneId: string; zoneName: string; devices: DeviceDto[] }[] {
  const nameById = new Map(zones.map((z) => [z.id, z.name]));
  const byZone = new Map<string, DeviceDto[]>();
  for (const device of devices) {
    const list = byZone.get(device.zoneId) ?? [];
    list.push(device);
    byZone.set(device.zoneId, list);
  }
  return [...byZone.entries()].map(([zoneId, list]) => ({
    zoneId,
    zoneName: nameById.get(zoneId) ?? t("noZone"),
    devices: list,
  }));
}
