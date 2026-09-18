import type { PriceSnapshot } from "./types";

/** Формат денег из минорных единиц: 12000 → "120,00 TJS" (как Money.ToDisplayString). */
export function formatMoney(minorUnits: number, currency: string): string {
  const whole = Math.trunc(minorUnits / 100);
  const frac = Math.abs(minorUnits % 100).toString().padStart(2, "0");
  return `${whole},${frac} ${currency}`;
}

/** Длительность в секундах → "чч:мм:сс". */
export function formatDuration(totalSeconds: number): string {
  const s = Math.max(0, Math.floor(totalSeconds));
  const hh = Math.floor(s / 3600);
  const mm = Math.floor((s % 3600) / 60);
  const ss = s % 60;
  const pad = (n: number) => n.toString().padStart(2, "0");
  return `${pad(hh)}:${pad(mm)}:${pad(ss)}`;
}

/**
 * Оценка стоимости на клиенте, идентична серверному BillingCalculator
 * (CeilingPerMinute): минуты вверх, цена = минуты * цена_час / 60.
 */
export function estimateCostMinorUnits(snapshot: PriceSnapshot, elapsedSeconds: number): number {
  const seconds = Math.max(0, Math.ceil(elapsedSeconds));
  const minutes = Math.ceil(seconds / 60);
  return Math.trunc((minutes * snapshot.pricePerHourMinorUnits) / 60);
}

/** Локальное время из ISO-строки UTC. */
export function formatTime(iso: string | null): string {
  if (!iso) {
    return "—";
  }
  const date = new Date(iso);
  return Number.isNaN(date.getTime()) ? "—" : date.toLocaleString("ru-RU");
}
