import Link from "next/link";
import type { DeviceDto } from "@/lib/types";
import { formatTime } from "@/lib/format";
import { t } from "@/lib/i18n";
import { DeviceStatusBadge } from "./StatusBadge";

export function DeviceCard({ device }: { device: DeviceDto }) {
  return (
    <Link
      href={`/devices/${device.id}`}
      className="flex flex-col gap-3 rounded-xl border border-[var(--border)] bg-[var(--surface)] p-4 transition hover:border-[var(--accent)]"
    >
      <div className="flex items-start justify-between gap-2">
        <span className="font-medium">{device.displayName}</span>
        {device.simulated ? (
          <span className="rounded bg-amber-500/15 px-1.5 py-0.5 text-[10px] font-semibold tracking-wide text-amber-300">
            {t("simulated")}
          </span>
        ) : null}
      </div>
      <DeviceStatusBadge status={device.status} />
      <div className="text-xs text-[var(--muted)]">
        {t("lastHeartbeat")}: {formatTime(device.lastHeartbeatUtc)}
      </div>
    </Link>
  );
}
