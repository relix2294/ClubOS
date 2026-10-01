"use client";

import { useState, type FormEvent } from "react";
import { formatMinuteOfDay, type PricePeriod, type TariffPackageInput, type TariffPackageView, type ZoneView } from "@clubos/contracts";
import { apiPost } from "@/lib/api";
import { formatLimit, formatMoney, parseMoney } from "@/lib/format";
import { t } from "@/lib/i18n";
import { Button, Field, inputClass } from "./ui";

const DAYS = [1, 2, 4, 8, 16, 32, 64];

/** «22:00» → 1320; конец «00:00» → 1440 (до полуночи). */
function parseClock(text: string, isEnd: boolean): number | null {
  const m = /^(\d{1,2}):(\d{2})$/.exec(text.trim());
  if (!m) return null;
  const minute = Number(m[1]) * 60 + Number(m[2]);
  if (Number(m[1]) > 23 || Number(m[2]) > 59) return null;
  return isEnd && minute === 0 ? 1440 : minute;
}

const clock = (minute: number) => formatMinuteOfDay(minute === 1440 ? 0 : minute);

/** «Пн–Пт», «Сб, Вс», «ежедневно». */
export function daysLabel(mask: number): string {
  if (mask === 127) return t.tariffs.everyDay;
  if (mask === 31) return t.tariffs.weekdays;
  if (mask === 96) return t.tariffs.weekend;
  return DAYS.filter((d) => (mask & d) !== 0)
    .map((d) => t.tariffs.days[DAYS.indexOf(d)])
    .join(", ");
}

export function periodLabel(p: PricePeriod, currency: string): string {
  return `${daysLabel(p.days)} ${clock(p.startMinute)}–${clock(p.endMinute)}: ${formatMoney(p.pricePerHourMinorUnits, currency)}${t.device.perHour}`;
}

export function windowLabel(p: Pick<TariffPackageView, "availableFromMinute" | "availableToMinute">): string | null {
  return p.availableFromMinute === null || p.availableToMinute === null
    ? null
    : `${t.tariffs.startWindow} ${clock(p.availableFromMinute)}–${clock(p.availableToMinute)}`;
}

interface PeriodDraft {
  key: string;
  days: number;
  start: string;
  end: string;
  price: string;
}

let draftSeq = 0;
const toDraft = (p: PricePeriod): PeriodDraft => ({
  key: `p${draftSeq++}`,
  days: p.days,
  start: clock(p.startMinute),
  end: clock(p.endMinute),
  price: (p.pricePerHourMinorUnits / 100).toFixed(2).replace(".", ","),
});

/** Периоды цены зоны и пакеты (только владелец, locations.manage). */
export function ZoneTariffEditor({ zone, currency, onChanged }: { zone: ZoneView; currency: string; onChanged: () => void }) {
  return (
    <div className="flex flex-col gap-6 rounded-lg bg-slate-50 p-4" data-testid="zone-tariffs">
      <PeriodsEditor zone={zone} currency={currency} onChanged={onChanged} />
      <PackagesEditor zone={zone} currency={currency} onChanged={onChanged} />
    </div>
  );
}

function PeriodsEditor({ zone, currency, onChanged }: { zone: ZoneView; currency: string; onChanged: () => void }) {
  const [drafts, setDrafts] = useState<PeriodDraft[]>(() => zone.periods.map(toDraft));
  const [error, setError] = useState<string>();
  const [saved, setSaved] = useState(false);
  const [busy, setBusy] = useState(false);

  const update = (key: string, patch: Partial<PeriodDraft>) => {
    setSaved(false);
    setDrafts((list) => list.map((d) => (d.key === key ? { ...d, ...patch } : d)));
  };
  const move = (index: number, delta: number) => {
    setSaved(false);
    setDrafts((list) => {
      const next = [...list];
      const [item] = next.splice(index, 1);
      next.splice(index + delta, 0, item);
      return next;
    });
  };

  const save = async (e: FormEvent) => {
    e.preventDefault();
    const periods: PricePeriod[] = [];
    for (const [i, d] of drafts.entries()) {
      const start = parseClock(d.start, false);
      const end = parseClock(d.end, true);
      const price = parseMoney(d.price);
      if (start === null || end === null) return setError(`${t.tariffs.period} ${i + 1}: ${t.tariffs.badTime}`);
      if (price === null || price <= 0) return setError(`${t.tariffs.period} ${i + 1}: ${t.locations.badPrice}`);
      if (d.days === 0) return setError(`${t.tariffs.period} ${i + 1}: ${t.tariffs.noDays}`);
      periods.push({ days: d.days, startMinute: start, endMinute: end, pricePerHourMinorUnits: price });
    }
    setError(undefined);
    setBusy(true);
    try {
      await apiPost(`zones/${zone.zoneId}/periods`, { periods });
      setSaved(true);
      onChanged();
    } catch (err) {
      setError((err as Error).message);
    } finally {
      setBusy(false);
    }
  };

  return (
    <form onSubmit={save} className="flex flex-col gap-3" aria-label={`${t.tariffs.periodsTitle}: ${zone.name}`}>
      <div>
        <h3 className="text-sm font-semibold text-slate-800">{t.tariffs.periodsTitle}</h3>
        <p className="text-xs text-slate-500">{t.tariffs.periodsHint}</p>
      </div>
      {drafts.length === 0 && <p className="text-sm text-slate-500">{t.tariffs.noPeriods}</p>}
      {drafts.map((d, i) => (
        <div key={d.key} className="flex flex-wrap items-end gap-3 rounded-lg border border-slate-200 bg-white p-3" data-testid="period-row">
          <fieldset className="flex flex-wrap gap-1">
            <legend className="mb-1 text-xs font-medium text-slate-700">{t.tariffs.daysLabel}</legend>
            {DAYS.map((bit, di) => (
              <label key={bit} className={`cursor-pointer rounded border px-2 py-1 text-xs ${d.days & bit ? "border-brand-500 bg-brand-50 text-brand-700" : "border-slate-300 text-slate-500"}`}>
                <input
                  type="checkbox"
                  className="sr-only"
                  checked={(d.days & bit) !== 0}
                  onChange={(e) => update(d.key, { days: e.target.checked ? d.days | bit : d.days & ~bit })}
                />
                {t.tariffs.days[di]}
              </label>
            ))}
          </fieldset>
          <Field label={t.tariffs.from}>
            <input className={`${inputClass} w-24`} type="time" required value={d.start} onChange={(e) => update(d.key, { start: e.target.value })} />
          </Field>
          <Field label={t.tariffs.to}>
            <input className={`${inputClass} w-24`} type="time" required value={d.end} onChange={(e) => update(d.key, { end: e.target.value })} />
          </Field>
          <Field label={`${t.locations.price}, ${currency}`}>
            <input className={`${inputClass} w-28`} inputMode="decimal" required value={d.price} onChange={(e) => update(d.key, { price: e.target.value })} />
          </Field>
          <div className="flex gap-1">
            <Button variant="secondary" className="px-2 py-1" disabled={i === 0} onClick={() => move(i, -1)} aria-label={t.tariffs.up}>
              ↑
            </Button>
            <Button variant="secondary" className="px-2 py-1" disabled={i === drafts.length - 1} onClick={() => move(i, 1)} aria-label={t.tariffs.down}>
              ↓
            </Button>
            <Button
              variant="secondary"
              className="px-2 py-1"
              onClick={() => {
                setSaved(false);
                setDrafts((list) => list.filter((x) => x.key !== d.key));
              }}
              aria-label={t.tariffs.remove}
            >
              ✕
            </Button>
          </div>
        </div>
      ))}
      <div className="flex flex-wrap gap-2">
        <Button
          variant="secondary"
          disabled={drafts.length >= 12}
          onClick={() => {
            setSaved(false);
            setDrafts((list) => [...list, toDraft({ days: 127, startMinute: 22 * 60, endMinute: 8 * 60, pricePerHourMinorUnits: zone.pricePerHourMinorUnits })]);
          }}
        >
          {t.tariffs.addPeriod}
        </Button>
        <Button type="submit" disabled={busy}>
          {t.tariffs.savePeriods}
        </Button>
      </div>
      {saved && (
        <p role="status" className="text-sm text-emerald-700">
          {t.tariffs.saved}
        </p>
      )}
      {error && (
        <p role="alert" className="text-sm text-red-700">
          {error}
        </p>
      )}
    </form>
  );
}

function PackagesEditor({ zone, currency, onChanged }: { zone: ZoneView; currency: string; onChanged: () => void }) {
  return (
    <div className="flex flex-col gap-3">
      <div>
        <h3 className="text-sm font-semibold text-slate-800">{t.tariffs.packagesTitle}</h3>
        <p className="text-xs text-slate-500">{t.tariffs.packagesHint}</p>
      </div>
      {zone.packages.length === 0 && <p className="text-sm text-slate-500">{t.tariffs.noPackages}</p>}
      {zone.packages.map((p) => (
        <PackageForm key={p.packageId} zone={zone} currency={currency} existing={p} onChanged={onChanged} />
      ))}
      <PackageForm zone={zone} currency={currency} onChanged={onChanged} />
    </div>
  );
}

function PackageForm({
  zone,
  currency,
  existing,
  onChanged,
}: {
  zone: ZoneView;
  currency: string;
  existing?: TariffPackageView;
  onChanged: () => void;
}) {
  const [name, setName] = useState(existing?.name ?? "");
  const [hours, setHours] = useState(existing ? String(existing.durationMinutes / 60).replace(".", ",") : "");
  const [price, setPrice] = useState(existing ? (existing.priceMinorUnits / 100).toFixed(2).replace(".", ",") : "");
  const [from, setFrom] = useState(existing?.availableFromMinute != null ? clock(existing.availableFromMinute) : "");
  const [to, setTo] = useState(existing?.availableToMinute != null ? clock(existing.availableToMinute) : "");
  const [error, setError] = useState<string>();
  const [busy, setBusy] = useState(false);

  const body = (isActive: boolean): TariffPackageInput | null => {
    const minutes = Math.round(Number(hours.replace(",", ".")) * 60);
    const minor = parseMoney(price);
    if (!Number.isFinite(minutes) || minutes < 1) {
      setError(t.tariffs.badDuration);
      return null;
    }
    if (minor === null) {
      setError(t.tariffs.badPackagePrice);
      return null;
    }
    const windowFrom = from ? parseClock(from, false) : null;
    const windowTo = to ? parseClock(to, true) : null;
    if ((from && windowFrom === null) || (to && windowTo === null) || (windowFrom === null) !== (windowTo === null)) {
      setError(t.tariffs.badWindow);
      return null;
    }
    return { name, durationMinutes: minutes, priceMinorUnits: minor, availableFromMinute: windowFrom, availableToMinute: windowTo, isActive };
  };

  const submit = async (isActive: boolean) => {
    const payload = body(isActive);
    if (!payload) return;
    setError(undefined);
    setBusy(true);
    try {
      await apiPost(existing ? `packages/${existing.packageId}` : `zones/${zone.zoneId}/packages`, payload);
      if (!existing) {
        setName("");
        setHours("");
        setPrice("");
        setFrom("");
        setTo("");
      }
      onChanged();
    } catch (err) {
      setError((err as Error).message);
    } finally {
      setBusy(false);
    }
  };

  const label = existing ? `${t.tariffs.package}: ${existing.name}` : `${t.tariffs.addPackage}: ${zone.name}`;
  return (
    <form
      onSubmit={(e) => {
        e.preventDefault();
        void submit(existing?.isActive ?? true);
      }}
      aria-label={label}
      data-testid={existing ? "package-row" : undefined}
      className={`flex flex-col gap-2 rounded-lg border p-3 ${existing ? (existing.isActive ? "border-slate-200 bg-white" : "border-dashed border-slate-300 bg-slate-100") : "border-dashed border-brand-500/40 bg-white"}`}
    >
      {existing && (
        <p className="text-xs text-slate-600">
          <strong>{existing.name}</strong> · {formatLimit(existing.durationMinutes)} · {formatMoney(existing.priceMinorUnits, currency)}
          {windowLabel(existing) && ` · ${windowLabel(existing)}`}
          {!existing.isActive && ` · ${t.tariffs.inactive}`}
        </p>
      )}
      <div className="flex flex-wrap items-end gap-3">
        <Field label={t.tariffs.packageName}>
          <input className={`${inputClass} w-36`} required maxLength={40} value={name} onChange={(e) => setName(e.target.value)} />
        </Field>
        <Field label={t.tariffs.hours}>
          <input className={`${inputClass} w-20`} inputMode="decimal" required value={hours} onChange={(e) => setHours(e.target.value)} />
        </Field>
        <Field label={`${t.tariffs.packagePrice}, ${currency}`}>
          <input className={`${inputClass} w-28`} inputMode="decimal" required value={price} onChange={(e) => setPrice(e.target.value)} />
        </Field>
        <Field label={t.tariffs.windowFrom}>
          <input className={`${inputClass} w-24`} type="time" value={from} onChange={(e) => setFrom(e.target.value)} />
        </Field>
        <Field label={t.tariffs.windowTo}>
          <input className={`${inputClass} w-24`} type="time" value={to} onChange={(e) => setTo(e.target.value)} />
        </Field>
        <Button type="submit" variant={existing ? "secondary" : "primary"} disabled={busy}>
          {existing ? t.locations.save : t.tariffs.addPackage}
        </Button>
        {existing && (
          <Button variant="secondary" disabled={busy} onClick={() => void submit(!existing.isActive)}>
            {existing.isActive ? t.tariffs.deactivate : t.tariffs.activate}
          </Button>
        )}
      </div>
      {error && (
        <p role="alert" className="text-sm text-red-700">
          {error}
        </p>
      )}
    </form>
  );
}
