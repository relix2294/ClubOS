import type { CommandState, DeviceStatus } from "@/lib/types";

// Статус передаётся цветом И текстом И иконкой (ТЗ §9: не только цветом).

const deviceStatus: Record<DeviceStatus, { label: string; icon: string; className: string }> = {
  Idle: { label: "В сети", icon: "●", className: "bg-emerald-500/15 text-emerald-300" },
  Active: { label: "Активно", icon: "▶", className: "bg-sky-500/15 text-sky-300" },
  Locked: { label: "Заблокировано", icon: "🔒", className: "bg-amber-500/15 text-amber-300" },
  Reserved: { label: "Резерв", icon: "◆", className: "bg-violet-500/15 text-violet-300" },
  Maintenance: { label: "Обслуживание", icon: "🛠", className: "bg-amber-500/15 text-amber-300" },
  Updating: { label: "Обновление", icon: "↻", className: "bg-sky-500/15 text-sky-300" },
  Error: { label: "Ошибка", icon: "✕", className: "bg-rose-500/15 text-rose-300" },
  Offline: { label: "Оффлайн", icon: "○", className: "bg-slate-500/15 text-slate-300" },
};

const commandState: Record<CommandState, { label: string; className: string }> = {
  Queued: { label: "В очереди", className: "bg-slate-500/15 text-slate-300" },
  Delivered: { label: "Доставлена", className: "bg-sky-500/15 text-sky-300" },
  Acknowledged: { label: "Принята", className: "bg-sky-500/15 text-sky-300" },
  Succeeded: { label: "Выполнена", className: "bg-emerald-500/15 text-emerald-300" },
  Failed: { label: "Ошибка", className: "bg-rose-500/15 text-rose-300" },
  Expired: { label: "Истекла", className: "bg-amber-500/15 text-amber-300" },
  Cancelled: { label: "Отменена", className: "bg-slate-500/15 text-slate-300" },
};

export function DeviceStatusBadge({ status }: { status: DeviceStatus }) {
  const s = deviceStatus[status];
  return (
    <span className={`inline-flex items-center gap-1.5 rounded-full px-2.5 py-1 text-xs font-medium ${s.className}`}>
      <span aria-hidden>{s.icon}</span>
      {s.label}
    </span>
  );
}

export function CommandStateBadge({ state }: { state: CommandState }) {
  const s = commandState[state];
  return (
    <span className={`inline-flex items-center rounded-full px-2.5 py-1 text-xs font-medium ${s.className}`}>
      {s.label}
    </span>
  );
}
