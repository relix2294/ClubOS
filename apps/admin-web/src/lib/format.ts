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

/** «120», «120,5», «120.50» → минимальные единицы (12000, 12050, 12050); иначе null. */
export function parseMoney(text: string): number | null {
  const normalized = text.trim().replace(/\s/g, "").replace(",", ".");
  if (!/^\d{1,7}(\.\d{1,2})?$/.test(normalized)) return null;
  const [whole, frac = ""] = normalized.split(".");
  return Number(whole) * 100 + Number(frac.padEnd(2, "0"));
}

/** Телефон из цифр: 992901234567 → «+992 90 123 45 67»; другие длины — «+» и цифры как есть. */
export function formatPhone(digits: string): string {
  const m = /^(\d{3})(\d{2})(\d{3})(\d{2})(\d{2})$/.exec(digits);
  return m ? `+${m[1]} ${m[2]} ${m[3]} ${m[4]} ${m[5]}` : `+${digits}`;
}

/** Местный день недели (0 — вс, как getDay) и минута суток в часовом поясе локации. */
export function localDayMinute(timeZone: string, at: Date = new Date()): { day: number; minute: number } {
  const parts = new Intl.DateTimeFormat("en-US", { timeZone, weekday: "short", hour: "2-digit", minute: "2-digit", hourCycle: "h23" }).formatToParts(at);
  const get = (type: string) => parts.find((p) => p.type === type)?.value ?? "";
  const day = ["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"].indexOf(get("weekday"));
  return { day, minute: Number(get("hour")) * 60 + Number(get("minute")) };
}

/** Окно начала пакета [from, to) по местной минуте; через полночь, если from > to. */
export function inStartWindow(from: number | null, to: number | null, minute: number): boolean {
  if (from === null || to === null) return true;
  return from < to ? minute >= from && minute < to : minute >= from || minute < to;
}
