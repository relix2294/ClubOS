"use client";

import { useState, type FormEvent } from "react";
import {
  BookingRules,
  type BookingView,
  type ClientView,
  type DeviceView,
} from "@clubos/contracts";
import { useShell } from "@/components/AppShell";
import { ClientPicker } from "@/components/ClientPicker";
import {
  Button,
  Card,
  EmptyState,
  ErrorState,
  Field,
  Loading,
  inputClass,
} from "@/components/ui";
import { apiGet, apiPost } from "@/lib/api";
import {
  addDays,
  formatPhone,
  formatTime,
  localDateMinute,
} from "@/lib/format";
import { t } from "@/lib/i18n";
import { useNow, usePolling } from "@/lib/usePolling";

const DURATIONS = [60, 120, 180, 240, 300, 360];
const HOURS = Array.from({ length: 25 }, (_, i) => i);

/** Положение брони на шкале дня (минуты 0…1440) с обрезкой по границам выбранного дня. */
function span(
  b: BookingView,
  day: string,
  timeZone: string,
): { from: number; to: number } {
  const start = localDateMinute(timeZone, new Date(b.startsAtUtc));
  const end = localDateMinute(timeZone, new Date(b.endsAtUtc));
  const from = start.date < day ? 0 : start.minute;
  const to = end.date > day ? 1440 : end.minute;
  return { from, to: Math.max(to, from + 5) };
}

const statusStyle: Record<BookingView["status"], string> = {
  Booked: "bg-violet-500 text-white",
  Started: "bg-emerald-500 text-white",
  Cancelled: "bg-slate-200 text-slate-500 line-through",
  NoShow: "bg-amber-200 text-amber-900",
};

/** Бронирование ПК: шкала дня по устройствам, новая бронь, начало по брони, отмена. */
export default function BookingsPage() {
  const { location, can } = useShell();
  const today = localDateMinute(location.timezone).date;
  const [day, setDay] = useState(today);
  const [selected, setSelected] = useState<string | null>(null);
  const bookings = usePolling(
    (s) =>
      apiGet<BookingView[]>(
        `locations/${encodeURIComponent(location.locationId)}/bookings?date=${day}`,
        s,
      ),
    15_000,
    [location.locationId, day],
    { topics: ["sessions"], locationId: location.locationId },
  );
  const devices = usePolling(
    (s) =>
      apiGet<DeviceView[]>(
        `locations/${encodeURIComponent(location.locationId)}/devices`,
        s,
      ),
    30_000,
    [location.locationId],
    { topics: ["devices"], locationId: location.locationId },
  );

  if (!can("sessions.manage")) {
    return <EmptyState>Раздел недоступен для вашей роли.</EmptyState>;
  }

  const list = bookings.data ?? [];
  const current = list.find((b) => b.bookingId === selected) ?? null;
  const refresh = () => {
    bookings.refresh();
    devices.refresh();
  };

  return (
    <div className="flex flex-col gap-6">
      <h1 className="text-2xl font-bold text-slate-900">{t.bookings.title}</h1>
      <Card>
        <div className="flex flex-wrap items-end gap-3">
          <Button
            variant="secondary"
            onClick={() => setDay(addDays(day, -1))}
            aria-label={t.bookings.prevDay}
          >
            ←
          </Button>
          <Field label={t.bookings.day}>
            <input
              type="date"
              className={inputClass}
              value={day}
              onChange={(e) => e.target.value && setDay(e.target.value)}
            />
          </Field>
          <Button
            variant="secondary"
            onClick={() => setDay(addDays(day, 1))}
            aria-label={t.bookings.nextDay}
          >
            →
          </Button>
          {day !== today && (
            <Button variant="secondary" onClick={() => setDay(today)}>
              {t.bookings.today}
            </Button>
          )}
          <p className="text-xs text-slate-500">{t.bookings.rules}</p>
        </div>
      </Card>
      {(bookings.loading && !bookings.data) ||
      (devices.loading && !devices.data) ? (
        <Loading />
      ) : null}
      {bookings.error && (
        <ErrorState error={bookings.error} onRetry={bookings.refresh} />
      )}
      {devices.data && bookings.data && (
        <Timeline
          devices={devices.data}
          bookings={list}
          day={day}
          today={today}
          onSelect={setSelected}
          selected={selected}
        />
      )}
      {current && (
        <BookingDetails
          booking={current}
          onDone={refresh}
          onClose={() => setSelected(null)}
        />
      )}
      {devices.data && (
        <NewBooking
          key={day}
          devices={devices.data}
          day={day}
          onCreated={(b) => {
            setDay(
              localDateMinute(location.timezone, new Date(b.startsAtUtc)).date,
            );
            setSelected(b.bookingId);
            refresh();
          }}
        />
      )}
      {bookings.data && <BookingTable bookings={list} onSelect={setSelected} />}
    </div>
  );
}

function Timeline({
  devices,
  bookings,
  day,
  today,
  selected,
  onSelect,
}: {
  devices: DeviceView[];
  bookings: BookingView[];
  day: string;
  today: string;
  selected: string | null;
  onSelect: (id: string) => void;
}) {
  const { location } = useShell();
  const now = useNow(60_000);
  const nowMinute =
    day === today
      ? localDateMinute(location.timezone, new Date(now)).minute
      : null;
  const pct = (minute: number) => `${(minute / 1440) * 100}%`;

  return (
    <Card title={t.bookings.timeline}>
      {devices.length === 0 ? (
        <EmptyState>{t.dashboard.empty}</EmptyState>
      ) : (
        <div className="overflow-x-auto">
          <div className="min-w-[720px]" data-testid="booking-timeline">
            <div className="ml-28 flex text-[10px] text-slate-400">
              {HOURS.slice(0, 24).map((h) => (
                <div
                  key={h}
                  className="flex-1 border-l border-slate-200 pl-0.5"
                >
                  {String(h).padStart(2, "0")}
                </div>
              ))}
            </div>
            {devices.map((d) => (
              <div
                key={d.deviceId}
                className="flex items-center border-t border-slate-100 py-1"
                data-testid="timeline-row"
                data-device={d.deviceId}
              >
                <div
                  className="w-28 shrink-0 truncate pr-2 text-sm font-medium text-slate-800"
                  title={d.zoneName}
                >
                  {d.displayName}
                  <span className="block text-[10px] font-normal text-slate-500">
                    {d.zoneName}
                  </span>
                </div>
                <div className="relative h-8 flex-1 rounded bg-slate-50">
                  {HOURS.slice(1, 24).map((h) => (
                    <div
                      key={h}
                      className="absolute top-0 h-full border-l border-slate-200/70"
                      style={{ left: pct(h * 60) }}
                    />
                  ))}
                  {nowMinute !== null && (
                    <div
                      className="absolute top-0 z-10 h-full w-0.5 bg-red-500"
                      style={{ left: pct(nowMinute) }}
                      title={t.bookings.now}
                    />
                  )}
                  {bookings
                    .filter((b) => b.deviceId === d.deviceId)
                    .map((b) => {
                      const { from, to } = span(b, day, location.timezone);
                      return (
                        <button
                          key={b.bookingId}
                          type="button"
                          data-testid="timeline-booking"
                          data-status={b.status}
                          onClick={() => onSelect(b.bookingId)}
                          title={`${b.guestName} ${formatTime(b.startsAtUtc, location.timezone)}–${formatTime(b.endsAtUtc, location.timezone)}`}
                          className={`absolute top-1 h-6 overflow-hidden truncate rounded px-1 text-left text-[11px] ${statusStyle[b.status]} ${selected === b.bookingId ? "ring-2 ring-brand-500" : ""}`}
                          style={{ left: pct(from), width: pct(to - from) }}
                        >
                          {b.guestName}
                        </button>
                      );
                    })}
                </div>
              </div>
            ))}
          </div>
        </div>
      )}
      <div className="mt-3 flex flex-wrap gap-3 text-xs text-slate-600">
        {(Object.keys(statusStyle) as BookingView["status"][]).map((s) => (
          <span key={s} className="flex items-center gap-1">
            <span
              className={`inline-block h-3 w-3 rounded ${statusStyle[s]}`}
            />{" "}
            {t.bookings.statuses[s]}
          </span>
        ))}
      </div>
    </Card>
  );
}

function BookingDetails({
  booking,
  onDone,
  onClose,
}: {
  booking: BookingView;
  onDone: () => void;
  onClose: () => void;
}) {
  const { location } = useShell();
  const now = useNow(15_000);
  const [reason, setReason] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string>();
  const [done, setDone] = useState<string>();
  const startsIn = Date.parse(booking.startsAtUtc) - now;
  const canStart =
    booking.status === "Booked" &&
    startsIn <= BookingRules.holdMinutes * 60_000;

  const run = async (call: () => Promise<unknown>, text: string) => {
    setBusy(true);
    setError(undefined);
    try {
      await call();
      setDone(text);
      onDone();
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setBusy(false);
    }
  };

  return (
    <Card
      title={
        <span data-testid="booking-details">
          {booking.guestName} · {booking.deviceName} ·{" "}
          {formatTime(booking.startsAtUtc, location.timezone)}–
          {formatTime(booking.endsAtUtc, location.timezone)}
        </span>
      }
      actions={
        <Button variant="secondary" className="py-1" onClick={onClose}>
          ✕
        </Button>
      }
    >
      <div className="flex flex-col gap-3 text-sm">
        <p className="text-slate-700">
          {t.bookings.status}:{" "}
          <strong data-testid="booking-status">
            {t.bookings.statuses[booking.status]}
          </strong>
          {booking.guestPhone && ` · ${formatPhone(booking.guestPhone)}`}
          {booking.note && ` · ${booking.note}`}
          {booking.cancelReason && ` · ${booking.cancelReason}`}
          <span className="block text-xs text-slate-500">
            {t.bookings.createdBy} {booking.createdByName}
          </span>
        </p>
        {booking.status === "Booked" && (
          <div className="flex flex-wrap items-end gap-2">
            <Button
              disabled={busy || !canStart}
              onClick={() =>
                run(
                  () => apiPost(`bookings/${booking.bookingId}/start`),
                  t.bookings.started,
                )
              }
              title={canStart ? undefined : t.bookings.tooEarly}
            >
              {t.bookings.start}
            </Button>
            <Field label={t.bookings.cancelReason}>
              <input
                className={`${inputClass} py-1`}
                maxLength={200}
                value={reason}
                onChange={(e) => setReason(e.target.value)}
              />
            </Field>
            <Button
              variant="danger"
              disabled={busy}
              onClick={() => {
                if (window.confirm(t.bookings.cancelConfirm))
                  void run(
                    () =>
                      apiPost(`bookings/${booking.bookingId}/cancel`, {
                        reason: reason || null,
                      }),
                    t.bookings.cancelled,
                  );
              }}
            >
              {t.bookings.cancel}
            </Button>
          </div>
        )}
        {booking.status === "Booked" && !canStart && (
          <p className="text-xs text-slate-500">{t.bookings.tooEarly}</p>
        )}
        {done && (
          <p role="status" className="text-emerald-700">
            {done}
          </p>
        )}
        {error && (
          <p
            role="alert"
            className="rounded-lg bg-red-50 px-3 py-2 text-red-800"
          >
            {error}
          </p>
        )}
      </div>
    </Card>
  );
}

function NewBooking({
  devices,
  day,
  onCreated,
}: {
  devices: DeviceView[];
  day: string;
  onCreated: (b: BookingView) => void;
}) {
  const { location, can } = useShell();
  const nowLocal = localDateMinute(location.timezone);
  // По умолчанию — ближайший целый час выбранного дня (сегодня — после текущего времени).
  const defaultHour =
    day === nowLocal.date
      ? Math.min(23, Math.floor(nowLocal.minute / 60) + 1)
      : 18;
  const [deviceId, setDeviceId] = useState("");
  const [date, setDate] = useState(day);
  const [time, setTime] = useState(
    `${String(defaultHour).padStart(2, "0")}:00`,
  );
  const [duration, setDuration] = useState("120");
  const [client, setClient] = useState<ClientView | null>(null);
  const [name, setName] = useState("");
  const [phone, setPhone] = useState("");
  const [note, setNote] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string>();

  const submit = async (e: FormEvent) => {
    e.preventDefault();
    setBusy(true);
    setError(undefined);
    try {
      const created = await apiPost<BookingView>(
        `locations/${encodeURIComponent(location.locationId)}/bookings`,
        {
          deviceId: deviceId || devices[0]?.deviceId,
          startsAt: `${date}T${time}`,
          durationMinutes: Number(duration),
          clientId: client?.clientId ?? null,
          guestName: name || null,
          guestPhone: phone || null,
          note: note || null,
        },
      );
      setName("");
      setPhone("");
      setNote("");
      setClient(null);
      onCreated(created);
    } catch (err) {
      setError((err as Error).message);
    } finally {
      setBusy(false);
    }
  };

  return (
    <Card title={t.bookings.newTitle}>
      <form
        onSubmit={submit}
        className="flex flex-col gap-3"
        aria-label={t.bookings.newTitle}
      >
        <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-4">
          <Field label={t.bookings.device}>
            <select
              className={inputClass}
              value={deviceId || devices[0]?.deviceId || ""}
              onChange={(e) => setDeviceId(e.target.value)}
            >
              {devices.map((d) => (
                <option key={d.deviceId} value={d.deviceId}>
                  {d.displayName} · {d.zoneName}
                </option>
              ))}
            </select>
          </Field>
          <Field label={t.bookings.date}>
            <input
              type="date"
              className={inputClass}
              required
              value={date}
              onChange={(e) => setDate(e.target.value)}
            />
          </Field>
          <Field label={t.bookings.time}>
            <input
              type="time"
              className={inputClass}
              required
              value={time}
              onChange={(e) => setTime(e.target.value)}
            />
          </Field>
          <Field label={t.bookings.duration}>
            <select
              className={inputClass}
              value={duration}
              onChange={(e) => setDuration(e.target.value)}
            >
              {DURATIONS.map((m) => (
                <option key={m} value={m}>
                  {m / 60} {t.bookings.hoursShort}
                </option>
              ))}
              <option value="30">30 {t.bookings.minutesShort}</option>
              <option value="600">10 {t.bookings.hoursShort}</option>
            </select>
          </Field>
        </div>
        {can("cash.operate") && (
          <ClientPicker value={client} onChange={setClient} />
        )}
        <div className="grid gap-3 sm:grid-cols-3">
          <Field
            label={client ? t.bookings.guestNameOptional : t.bookings.guestName}
          >
            <input
              className={inputClass}
              required={!client}
              maxLength={80}
              value={name}
              onChange={(e) => setName(e.target.value)}
            />
          </Field>
          <Field label={t.bookings.phone}>
            <input
              className={inputClass}
              type="tel"
              placeholder="+992 90 123 45 67"
              value={phone}
              onChange={(e) => setPhone(e.target.value)}
            />
          </Field>
          <Field label={t.bookings.note}>
            <input
              className={inputClass}
              maxLength={200}
              value={note}
              onChange={(e) => setNote(e.target.value)}
            />
          </Field>
        </div>
        <div>
          <Button type="submit" disabled={busy || devices.length === 0}>
            {t.bookings.create}
          </Button>
        </div>
        {error && (
          <p
            role="alert"
            className="rounded-lg bg-red-50 px-3 py-2 text-sm text-red-800"
          >
            {error}
          </p>
        )}
      </form>
    </Card>
  );
}

function BookingTable({
  bookings,
  onSelect,
}: {
  bookings: BookingView[];
  onSelect: (id: string) => void;
}) {
  const { location } = useShell();
  return (
    <Card title={t.bookings.list}>
      {bookings.length === 0 ? (
        <EmptyState>{t.bookings.empty}</EmptyState>
      ) : (
        <div className="overflow-x-auto">
          <table
            className="w-full text-left text-sm"
            data-testid="bookings-table"
          >
            <thead className="text-xs uppercase text-slate-500">
              <tr>
                <th className="py-2 pr-4">{t.bookings.time}</th>
                <th className="py-2 pr-4">{t.bookings.device}</th>
                <th className="py-2 pr-4">{t.bookings.guestName}</th>
                <th className="py-2 pr-4">{t.bookings.status}</th>
                <th className="py-2" />
              </tr>
            </thead>
            <tbody className="divide-y divide-slate-100">
              {bookings.map((b) => (
                <tr
                  key={b.bookingId}
                  data-testid="booking-row"
                  data-status={b.status}
                >
                  <td className="py-2 pr-4 whitespace-nowrap">
                    {formatTime(b.startsAtUtc, location.timezone)}–
                    {formatTime(b.endsAtUtc, location.timezone)}
                  </td>
                  <td className="py-2 pr-4">{b.deviceName}</td>
                  <td className="py-2 pr-4">
                    {b.guestName}
                    {b.guestPhone && (
                      <span className="block text-xs text-slate-500">
                        {formatPhone(b.guestPhone)}
                      </span>
                    )}
                  </td>
                  <td className="py-2 pr-4">{t.bookings.statuses[b.status]}</td>
                  <td className="py-2 text-right">
                    <Button
                      variant="secondary"
                      className="py-1"
                      onClick={() => onSelect(b.bookingId)}
                    >
                      {t.clients.open}
                    </Button>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </Card>
  );
}
