"use client";

import { Fragment, useState, type FormEvent } from "react";
import type { CreateLocationRequest, LocationView, ZoneView } from "@clubos/contracts";
import { useShell } from "@/components/AppShell";
import { Button, Card, EmptyState, Field, inputClass } from "@/components/ui";
import { apiPost } from "@/lib/api";
import { formatMoney, parseMoney } from "@/lib/format";
import { t } from "@/lib/i18n";
import { ZoneTariffEditor, periodLabel } from "@/components/ZoneTariffEditor";

/** Локации, зоны и тарифы (только владелец). Цена меняет версию правила; идущие сессии считаются по старой цене. */
export default function LocationsPage() {
  const { me, can, refreshMe } = useShell();
  if (!can("locations.manage")) {
    return <EmptyState>{t.locations.ownerOnly}</EmptyState>;
  }

  return (
    <div className="flex flex-col gap-6">
      <h1 className="text-2xl font-bold text-slate-900">{t.nav.locations}</h1>
      <p className="text-sm text-slate-600">{t.locations.intro}</p>
      {me.locations.map((l) => (
        <LocationCard key={l.locationId} location={l} onChanged={refreshMe} />
      ))}
      <CreateLocation onCreated={refreshMe} />
    </div>
  );
}

function LocationCard({ location, onChanged }: { location: LocationView; onChanged: () => void }) {
  const [name, setName] = useState("");
  const [price, setPrice] = useState("");
  const [error, setError] = useState<string>();

  const addZone = async (e: FormEvent) => {
    e.preventDefault();
    const minor = parseMoney(price);
    if (minor === null || minor <= 0) {
      setError(t.locations.badPrice);
      return;
    }
    setError(undefined);
    try {
      await apiPost(`locations/${location.locationId}/zones`, { name, pricePerHourMinorUnits: minor });
      setName("");
      setPrice("");
      onChanged();
    } catch (err) {
      setError((err as Error).message);
    }
  };

  return (
    <Card title={`${location.name} · ${location.timezone} · ${location.currency}`}>
      <div className="flex flex-col gap-4" data-testid="location-card">
        <table className="w-full text-left text-sm">
          <thead className="text-xs uppercase text-slate-500">
            <tr>
              <th className="py-2 pr-4">{t.locations.zone}</th>
              <th className="py-2 pr-4">{t.locations.price}</th>
              <th className="py-2" />
            </tr>
          </thead>
          <tbody className="divide-y divide-slate-100">
            {location.zones.map((z) => (
              <ZoneRow key={z.zoneId} zone={z} currency={location.currency} onChanged={onChanged} />
            ))}
          </tbody>
        </table>
        <form onSubmit={addZone} className="grid grid-cols-1 gap-3 md:grid-cols-3 md:items-end" aria-label={`${t.locations.addZone}: ${location.name}`}>
          <Field label={t.locations.zoneName}>
            <input className={inputClass} required maxLength={40} value={name} onChange={(e) => setName(e.target.value)} />
          </Field>
          <Field label={`${t.locations.price}, ${location.currency}`}>
            <input className={inputClass} required inputMode="decimal" value={price} onChange={(e) => setPrice(e.target.value)} />
          </Field>
          <Button type="submit" variant="secondary">
            {t.locations.addZone}
          </Button>
        </form>
        {error && (
          <p role="alert" className="text-sm text-red-700">
            {error}
          </p>
        )}
      </div>
    </Card>
  );
}

function ZoneRow({ zone, currency, onChanged }: { zone: ZoneView; currency: string; onChanged: () => void }) {
  const [editing, setEditing] = useState(false);
  const [tariffsOpen, setTariffsOpen] = useState(false);
  const [name, setName] = useState(zone.name);
  const [price, setPrice] = useState("");
  const [error, setError] = useState<string>();

  const save = async (e: FormEvent) => {
    e.preventDefault();
    const minor = parseMoney(price);
    if (minor === null || minor <= 0) {
      setError(t.locations.badPrice);
      return;
    }
    try {
      await apiPost(`zones/${zone.zoneId}`, { name, pricePerHourMinorUnits: minor });
      setEditing(false);
      onChanged();
    } catch (err) {
      setError((err as Error).message);
    }
  };

  if (!editing) {
    const activePackages = zone.packages.filter((p) => p.isActive).length;
    return (
      <Fragment>
      <tr data-testid="zone-row">
        <td className="py-2 pr-4 font-medium text-slate-900">{zone.name}</td>
        <td className="py-2 pr-4">
          {formatMoney(zone.pricePerHourMinorUnits, currency)}
          {t.device.perHour}
          {zone.periods.map((p, i) => (
            <span key={i} className="block text-xs text-slate-500">
              {periodLabel(p, currency)}
            </span>
          ))}
          {activePackages > 0 && (
            <span className="block text-xs text-slate-500">
              {t.tariffs.packagesTitle}: {activePackages}
            </span>
          )}
        </td>
        <td className="py-2 text-right whitespace-nowrap">
          <Button variant="secondary" className="mr-2 py-1" onClick={() => setTariffsOpen((v) => !v)} aria-expanded={tariffsOpen}>
            {tariffsOpen ? t.tariffs.hide : t.tariffs.configure}
          </Button>
          <Button
            variant="secondary"
            className="py-1"
            onClick={() => {
              setName(zone.name);
              setPrice((zone.pricePerHourMinorUnits / 100).toFixed(2).replace(".", ","));
              setError(undefined);
              setEditing(true);
            }}
          >
            {t.locations.edit}
          </Button>
        </td>
      </tr>
      {tariffsOpen && (
        <tr>
          <td colSpan={3} className="pb-4">
            <ZoneTariffEditor zone={zone} currency={currency} onChanged={onChanged} />
          </td>
        </tr>
      )}
      </Fragment>
    );
  }

  return (
    <tr data-testid="zone-row">
      <td colSpan={3} className="py-2">
        <form onSubmit={save} className="grid grid-cols-1 gap-3 md:grid-cols-4 md:items-end" aria-label={`${t.locations.edit}: ${zone.name}`}>
          <Field label={t.locations.zoneName}>
            <input className={inputClass} required maxLength={40} value={name} onChange={(e) => setName(e.target.value)} />
          </Field>
          <Field label={`${t.locations.price}, ${currency}`}>
            <input className={inputClass} required inputMode="decimal" value={price} onChange={(e) => setPrice(e.target.value)} />
          </Field>
          <Button type="submit">{t.locations.save}</Button>
          <Button variant="secondary" onClick={() => setEditing(false)}>
            {t.mfa.cancel}
          </Button>
        </form>
        <p className="mt-2 text-xs text-slate-500">{t.locations.priceHint}</p>
        {error && (
          <p role="alert" className="mt-2 text-sm text-red-700">
            {error}
          </p>
        )}
      </td>
    </tr>
  );
}

function CreateLocation({ onCreated }: { onCreated: () => void }) {
  const [name, setName] = useState("");
  const [timezone, setTimezone] = useState("Asia/Dushanbe");
  const [currency, setCurrency] = useState("TJS");
  const [zoneName, setZoneName] = useState("Standard");
  const [price, setPrice] = useState("");
  const [error, setError] = useState<string>();
  const [done, setDone] = useState(false);

  const submit = async (e: FormEvent) => {
    e.preventDefault();
    setDone(false);
    const minor = parseMoney(price);
    if (minor === null || minor <= 0) {
      setError(t.locations.badPrice);
      return;
    }
    setError(undefined);
    const body: CreateLocationRequest = {
      name,
      timezone: timezone.trim(),
      currency: currency.trim().toUpperCase(),
      zones: [{ name: zoneName, pricePerHourMinorUnits: minor }],
    };
    try {
      await apiPost("locations", body);
      setName("");
      setPrice("");
      setDone(true);
      onCreated();
    } catch (err) {
      setError((err as Error).message);
    }
  };

  return (
    <Card title={t.locations.create}>
      <form onSubmit={submit} className="grid grid-cols-1 gap-3 md:grid-cols-3" aria-label={t.locations.create}>
        <Field label={t.locations.name}>
          <input className={inputClass} required maxLength={80} value={name} onChange={(e) => setName(e.target.value)} />
        </Field>
        <Field label={t.locations.timezone}>
          <input className={inputClass} required value={timezone} onChange={(e) => setTimezone(e.target.value)} />
        </Field>
        <Field label={t.locations.currency}>
          <input className={inputClass} required maxLength={3} value={currency} onChange={(e) => setCurrency(e.target.value)} />
        </Field>
        <Field label={t.locations.firstZone}>
          <input className={inputClass} required maxLength={40} value={zoneName} onChange={(e) => setZoneName(e.target.value)} />
        </Field>
        <Field label={t.locations.price}>
          <input className={inputClass} required inputMode="decimal" value={price} onChange={(e) => setPrice(e.target.value)} />
        </Field>
        <div className="flex items-end">
          <Button type="submit">{t.locations.createButton}</Button>
        </div>
      </form>
      <p className="mt-3 text-xs text-slate-500">{t.locations.createHint}</p>
      {done && (
        <p role="status" className="mt-3 text-sm text-emerald-700">
          {t.locations.created}
        </p>
      )}
      {error && (
        <p role="alert" className="mt-3 text-sm text-red-700">
          {error}
        </p>
      )}
    </Card>
  );
}
