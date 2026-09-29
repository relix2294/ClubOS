import { formatMinorUnits } from "@clubos/contracts";

// Время хранится в UTC, отображается в часовом поясе локации (ТЗ: Asia/Dushanbe).

export function formatDateTime(utc: string | null | undefined, timeZone: string): string {
  if (!utc) return "—";
  return new Intl.DateTimeFormat("ru-RU", {
    timeZone,
    day: "2-digit",
    month: "2-digit",
    year: "numeric",
    hour: "2-digit",
    minute: "2-digit",
    second: "2-digit",
  }).format(new Date(utc));
}

export function formatDuration(ms: number): string {
  const total = Math.max(0, Math.floor(ms / 1000));
  const h = Math.floor(total / 3600);
  const m = Math.floor((total % 3600) / 60);
  const s = total % 60;
  return `${String(h).padStart(2, "0")}:${String(m).padStart(2, "0")}:${String(s).padStart(2, "0")}`;
}

export function formatMoney(minorUnits: number, currency: string): string {
  return `${formatMinorUnits(minorUnits)} ${currency}`;
}

export function formatAgo(utc: string | null, now: number): string {
  if (!utc) return "никогда";
  const seconds = Math.max(0, Math.round((now - Date.parse(utc)) / 1000));
  if (seconds < 60) return `${seconds} с назад`;
  const minutes = Math.round(seconds / 60);
  if (minutes < 60) return `${minutes} мин назад`;
  return `${Math.round(minutes / 60)} ч назад`;
}

/** Лимит сессии: 30 → «30 мин», 90 → «1 ч 30 мин», 120 → «2 ч». */
export function formatLimit(minutes: number): string {
  const h = Math.floor(minutes / 60);
  const m = minutes % 60;
  if (h === 0) return `${m} мин`;
  return m === 0 ? `${h} ч` : `${h} ч ${m} мин`;
}

export function formatTime(utc: string | null | undefined, timeZone: string): string {
  if (!utc) return "—";
  return new Intl.DateTimeFormat("ru-RU", { timeZone, hour: "2-digit", minute: "2-digit" }).format(new Date(utc));
}
