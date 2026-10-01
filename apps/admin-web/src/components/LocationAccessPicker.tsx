"use client";

import type { LocationView } from "@clubos/contracts";
import { t } from "@/lib/i18n";

export interface LocationAccessValue {
  all: boolean;
  ids: string[];
}

/** Доступ сотрудника к локациям: все (включая будущие) или выбранные. */
export function LocationAccessPicker({
  locations,
  value,
  onChange,
  idPrefix,
}: {
  locations: LocationView[];
  value: LocationAccessValue;
  onChange: (value: LocationAccessValue) => void;
  idPrefix: string;
}) {
  return (
    <fieldset className="flex flex-col gap-1 text-sm" data-testid="location-access">
      <legend className="mb-1 text-sm font-medium text-slate-700">{t.staff.locations}</legend>
      <label className="flex items-center gap-2">
        <input type="radio" name={`${idPrefix}-scope`} checked={value.all} onChange={() => onChange({ ...value, all: true })} />
        {t.staff.allLocations}
      </label>
      <label className="flex items-center gap-2">
        <input type="radio" name={`${idPrefix}-scope`} checked={!value.all} onChange={() => onChange({ ...value, all: false })} />
        {t.staff.selectedLocations}
      </label>
      {!value.all && (
        <div className="ml-6 flex flex-col gap-1">
          {locations.map((l) => (
            <label key={l.locationId} className="flex items-center gap-2">
              <input
                type="checkbox"
                checked={value.ids.includes(l.locationId)}
                onChange={(e) =>
                  onChange({
                    all: false,
                    ids: e.target.checked ? [...value.ids, l.locationId] : value.ids.filter((x) => x !== l.locationId),
                  })
                }
              />
              {l.name}
            </label>
          ))}
        </div>
      )}
    </fieldset>
  );
}
