"use client";

import { useState, type FormEvent } from "react";
import type { StaffMemberView, StaffRole, TemporaryPasswordResponse } from "@clubos/contracts";
import { useShell } from "@/components/AppShell";
import { Button, Card, EmptyState, ErrorState, Field, Loading, inputClass } from "@/components/ui";
import { apiGet, apiPost } from "@/lib/api";
import { formatDateTime } from "@/lib/format";
import { roleLabel, t } from "@/lib/i18n";
import { usePolling } from "@/lib/usePolling";

const ROLES: StaffRole[] = ["Owner", "Admin", "Operator"];

export default function StaffPage() {
  const { me, location, can } = useShell();
  const staff = usePolling((s) => apiGet<StaffMemberView[]>("staff", s), 15_000, [], { topics: ["staff"] });
  const [temp, setTemp] = useState<TemporaryPasswordResponse>();
  const [error, setError] = useState<string>();

  if (!can("staff.manage")) {
    return <EmptyState>Раздел доступен только владельцу.</EmptyState>;
  }

  const act = async (call: () => Promise<unknown>) => {
    setError(undefined);
    try {
      await call();
      staff.refresh();
    } catch (e) {
      setError((e as Error).message);
    }
  };

  return (
    <div className="flex flex-col gap-6">
      <h1 className="text-2xl font-bold text-slate-900">{t.staff.title}</h1>

      <CreateForm
        onCreated={(result) => {
          setTemp(result);
          staff.refresh();
        }}
      />

      {temp && <TemporaryPassword result={temp} onClose={() => setTemp(undefined)} />}
      {error && <ErrorState error={new Error(error)} />}
      {staff.loading && !staff.data && <Loading />}
      {staff.error && <ErrorState error={staff.error} onRetry={staff.refresh} />}

      {staff.data && (
        <Card>
          <div className="overflow-x-auto">
            <table className="w-full text-left text-sm" data-testid="staff-table">
              <thead className="text-xs uppercase text-slate-500">
                <tr>
                  <th className="py-2 pr-4">{t.staff.name}</th>
                  <th className="py-2 pr-4">{t.staff.role}</th>
                  <th className="py-2 pr-4">{t.staff.status}</th>
                  <th className="py-2 pr-4">{t.staff.lastLogin}</th>
                  <th className="py-2" />
                </tr>
              </thead>
              <tbody className="divide-y divide-slate-100">
                {staff.data.map((u) => {
                  const self = u.userId === me.user.userId;
                  return (
                    <tr key={u.userId} data-testid="staff-row">
                      <td className="py-2 pr-4">
                        <div className="font-medium text-slate-900">
                          {u.displayName} {self && <span className="text-xs text-slate-500">({t.staff.you})</span>}
                        </div>
                        <div className="text-xs text-slate-500">{u.email}</div>
                      </td>
                      <td className="py-2 pr-4">
                        <select
                          aria-label={`${t.staff.role}: ${u.displayName}`}
                          className={inputClass}
                          value={u.role}
                          disabled={self}
                          onChange={(e) => act(() => apiPost(`staff/${u.userId}/role`, { role: e.target.value }))}
                        >
                          {ROLES.map((r) => (
                            <option key={r} value={r}>
                              {roleLabel(r)}
                            </option>
                          ))}
                        </select>
                      </td>
                      <td className="py-2 pr-4">
                        <span className={u.isActive ? "text-emerald-700" : "text-slate-500"}>{u.isActive ? t.staff.active : t.staff.inactive}</span>
                        {u.mustChangePassword && <div className="text-xs text-amber-700">{t.staff.temporary}</div>}
                      </td>
                      <td className="py-2 pr-4 whitespace-nowrap text-slate-600">{formatDateTime(u.lastLoginAtUtc, location.timezone)}</td>
                      <td className="py-2">
                        {!self && (
                          <div className="flex flex-wrap justify-end gap-2">
                            <Button
                              variant="secondary"
                              className="py-1"
                              onClick={() =>
                                window.confirm(t.staff.confirmReset) &&
                                act(async () => setTemp(await apiPost<TemporaryPasswordResponse>(`staff/${u.userId}/reset-password`)))
                              }
                            >
                              {t.staff.resetPassword}
                            </Button>
                            {u.isActive ? (
                              <Button
                                variant="danger"
                                className="py-1"
                                onClick={() => window.confirm(t.staff.confirmDeactivate) && act(() => apiPost(`staff/${u.userId}/deactivate`))}
                              >
                                {t.staff.deactivate}
                              </Button>
                            ) : (
                              <Button variant="secondary" className="py-1" onClick={() => act(() => apiPost(`staff/${u.userId}/activate`))}>
                                {t.staff.activate}
                              </Button>
                            )}
                          </div>
                        )}
                      </td>
                    </tr>
                  );
                })}
              </tbody>
            </table>
          </div>
        </Card>
      )}
    </div>
  );
}

function CreateForm({ onCreated }: { onCreated: (result: TemporaryPasswordResponse) => void }) {
  const [email, setEmail] = useState("");
  const [displayName, setDisplayName] = useState("");
  const [role, setRole] = useState<StaffRole>("Operator");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string>();

  const submit = async (e: FormEvent) => {
    e.preventDefault();
    setBusy(true);
    setError(undefined);
    try {
      onCreated(await apiPost<TemporaryPasswordResponse>("staff", { email, displayName, role }));
      setEmail("");
      setDisplayName("");
    } catch (err) {
      setError((err as Error).message);
    } finally {
      setBusy(false);
    }
  };

  return (
    <Card title={t.staff.add}>
      <form onSubmit={submit} className="grid grid-cols-1 gap-3 md:grid-cols-4 md:items-end" aria-label={t.staff.add}>
        <Field label={t.staff.name}>
          <input className={inputClass} required maxLength={80} value={displayName} onChange={(e) => setDisplayName(e.target.value)} />
        </Field>
        <Field label={t.staff.email}>
          <input className={inputClass} type="email" required value={email} onChange={(e) => setEmail(e.target.value)} />
        </Field>
        <Field label={t.staff.role}>
          <select className={inputClass} value={role} onChange={(e) => setRole(e.target.value as StaffRole)}>
            {ROLES.map((r) => (
              <option key={r} value={r}>
                {roleLabel(r)}
              </option>
            ))}
          </select>
        </Field>
        <Button type="submit" disabled={busy}>
          {t.staff.create}
        </Button>
      </form>
      <p className="mt-3 text-xs text-slate-500">{t.staff.roles[role]}</p>
      {error && (
        <p role="alert" className="mt-3 text-sm text-red-700">
          {error}
        </p>
      )}
    </Card>
  );
}

function TemporaryPassword({ result, onClose }: { result: TemporaryPasswordResponse; onClose: () => void }) {
  return (
    <div className="rounded-lg border border-emerald-300 bg-emerald-50 p-4 text-sm" data-testid="temporary-password">
      <div className="font-semibold text-emerald-900">
        {t.staff.tempTitle}: {result.user.displayName} ({result.user.email})
      </div>
      <code className="mt-2 block rounded bg-white p-2 font-mono text-base">{result.temporaryPassword}</code>
      <p className="mt-2 text-emerald-900">{t.staff.tempHint}</p>
      <Button variant="secondary" className="mt-2 py-1" onClick={onClose}>
        OK
      </Button>
    </div>
  );
}
