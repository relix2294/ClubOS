import type { CommandState, DeviceStatus, SessionState } from "@clubos/contracts";
import { commandStateLabel, deviceStatusLabel, sessionStateLabel } from "@/lib/i18n";
import { IconActive, IconClock, IconIdle, IconLock, IconOffline, IconWarning } from "./icons";

// Статус передаётся не только цветом: иконка + текст (доступность, ТЗ §25.2.6).

const deviceStyles: Record<DeviceStatus, { cls: string; Icon: typeof IconIdle }> = {
  Offline: { cls: "bg-slate-100 text-slate-700 border-slate-300", Icon: IconOffline },
  Idle: { cls: "bg-emerald-50 text-emerald-800 border-emerald-300", Icon: IconIdle },
  Active: { cls: "bg-blue-50 text-blue-800 border-blue-300", Icon: IconActive },
  Locked: { cls: "bg-amber-50 text-amber-900 border-amber-300", Icon: IconLock },
  Reserved: { cls: "bg-indigo-50 text-indigo-800 border-indigo-300", Icon: IconClock },
  Maintenance: { cls: "bg-orange-50 text-orange-800 border-orange-300", Icon: IconWarning },
  Updating: { cls: "bg-sky-50 text-sky-800 border-sky-300", Icon: IconClock },
  Error: { cls: "bg-red-50 text-red-800 border-red-300", Icon: IconWarning },
};

export function DeviceStatusBadge({ status }: { status: DeviceStatus }) {
  const { cls, Icon } = deviceStyles[status];
  return (
    <span data-status={status} className={`inline-flex items-center gap-1.5 rounded-full border px-2.5 py-0.5 text-xs font-semibold ${cls}`}>
      <Icon className="h-3.5 w-3.5" />
      {deviceStatusLabel[status]}
    </span>
  );
}

const commandStyles: Record<CommandState, string> = {
  Queued: "bg-slate-100 text-slate-700 border-slate-300",
  Delivered: "bg-sky-50 text-sky-800 border-sky-300",
  Acknowledged: "bg-indigo-50 text-indigo-800 border-indigo-300",
  Succeeded: "bg-emerald-50 text-emerald-800 border-emerald-300",
  Failed: "bg-red-50 text-red-800 border-red-300",
  Expired: "bg-orange-50 text-orange-800 border-orange-300",
  Cancelled: "bg-slate-100 text-slate-600 border-slate-300",
};

const commandIcons: Record<CommandState, typeof IconIdle> = {
  Queued: IconClock,
  Delivered: IconClock,
  Acknowledged: IconClock,
  Succeeded: IconIdle,
  Failed: IconWarning,
  Expired: IconWarning,
  Cancelled: IconOffline,
};

export function CommandStateBadge({ state }: { state: CommandState }) {
  const Icon = commandIcons[state];
  return (
    <span data-state={state} className={`inline-flex items-center gap-1.5 rounded-full border px-2.5 py-0.5 text-xs font-semibold ${commandStyles[state]}`}>
      <Icon className="h-3.5 w-3.5" />
      {commandStateLabel[state]}
    </span>
  );
}

export function SessionStateBadge({ state }: { state: SessionState }) {
  const cls =
    state === "Active"
      ? "bg-blue-50 text-blue-800 border-blue-300"
      : state === "Ended"
        ? "bg-slate-100 text-slate-700 border-slate-300"
        : state === "Failed"
          ? "bg-red-50 text-red-800 border-red-300"
          : "bg-amber-50 text-amber-900 border-amber-300";
  return <span className={`inline-flex rounded-full border px-2.5 py-0.5 text-xs font-semibold ${cls}`}>{sessionStateLabel[state]}</span>;
}
