"use client";

import Link from "next/link";
import { usePathname, useRouter } from "next/navigation";
import { createContext, useContext, useEffect, type ReactNode } from "react";
import type { LocationView, MeResponse, Permission } from "@clubos/contracts";
import { UNAUTHORIZED_EVENT, apiGet } from "@/lib/api";
import { roleLabel, t } from "@/lib/i18n";
import { usePolling } from "@/lib/usePolling";
import { ErrorState, Loading } from "./ui";

interface ShellContext {
  me: MeResponse;
  location: LocationView;
  refreshMe: () => void;
  /** Есть ли у текущего сотрудника право. UI только скрывает кнопки — проверка на backend. */
  can: (permission: Permission) => boolean;
}

const Ctx = createContext<ShellContext | null>(null);

export function useShell(): ShellContext {
  const value = useContext(Ctx);
  if (!value) throw new Error("useShell вне AppShell");
  return value;
}

const allNav: { href: string; label: string; permission?: Permission }[] = [
  { href: "/", label: t.nav.dashboard, permission: "devices.view" },
  { href: "/audit", label: t.nav.audit, permission: "audit.view" },
  { href: "/enrollment", label: t.nav.enrollment, permission: "enrollment.manage" },
  { href: "/staff", label: t.nav.staff, permission: "staff.manage" },
  { href: "/account", label: t.nav.account },
];

export function AppShell({ children }: { children: ReactNode }) {
  const pathname = usePathname();
  const router = useRouter();

  useEffect(() => {
    const onUnauthorized = () => router.replace(`/login?next=${encodeURIComponent(window.location.pathname)}`);
    window.addEventListener(UNAUTHORIZED_EVENT, onUnauthorized);
    return () => window.removeEventListener(UNAUTHORIZED_EVENT, onUnauthorized);
  }, [router]);
  // /me включает статус Edge — обновляем раз в 10 секунд.
  const { data: me, error, refresh } = usePolling((signal) => apiGet<MeResponse>("me", signal), 10_000);
  const mustChangePassword = me?.user.mustChangePassword === true;

  // Временный пароль: до смены доступна только страница «Мой пароль» (backend тоже запрещает остальное).
  useEffect(() => {
    if (mustChangePassword && pathname !== "/account") {
      router.replace("/account");
    }
  }, [mustChangePassword, pathname, router]);

  if (error && !me) {
    return (
      <div className="mx-auto max-w-3xl p-8">
        <ErrorState error={error} onRetry={refresh} />
      </div>
    );
  }

  if (!me) {
    return (
      <div className="mx-auto max-w-3xl p-8">
        <Loading />
      </div>
    );
  }

  const location = me.locations[0];
  if (!location) {
    return <div className="p-8 text-slate-600">У организации нет локаций.</div>;
  }

  const logout = async () => {
    await fetch("/api/auth/logout", { method: "POST", headers: { "x-clubos-csrf": "1" } });
    router.replace("/login");
    router.refresh();
  };

  const can = (permission: Permission) => me.user.permissions.includes(permission);
  const nav = allNav.filter((item) => !item.permission || can(item.permission));

  if (mustChangePassword && pathname !== "/account") {
    return (
      <div className="mx-auto max-w-3xl p-8">
        <Loading />
      </div>
    );
  }

  return (
    <Ctx.Provider value={{ me, location, refreshMe: refresh, can }}>
      <div className="flex min-h-screen">
        <aside className="hidden w-60 shrink-0 flex-col border-r border-slate-200 bg-white md:flex">
          <div className="border-b border-slate-100 px-5 py-4">
            <div className="text-lg font-bold text-slate-900">
              {t.appName} <span className="ml-1 rounded bg-brand-50 px-1.5 py-0.5 text-xs font-semibold text-brand-700">{t.milestone}</span>
            </div>
            <div className="mt-1 text-xs text-slate-500">{me.user.organizationName}</div>
          </div>
          <nav className="flex flex-col gap-1 p-3" aria-label="Основная навигация">
            {nav.map((item) => {
              const active = item.href === "/" ? pathname === "/" || pathname.startsWith("/devices") : pathname.startsWith(item.href);
              return (
                <Link
                  key={item.href}
                  href={item.href}
                  aria-current={active ? "page" : undefined}
                  className={`rounded-lg px-3 py-2 text-sm font-medium ${active ? "bg-brand-50 text-brand-700" : "text-slate-700 hover:bg-slate-50"}`}
                >
                  {item.label}
                </Link>
              );
            })}
          </nav>
          <div className="mt-4 px-3">
            <div className="px-3 pb-1 text-xs font-semibold uppercase tracking-wide text-slate-400">{t.future.title}</div>
            {t.future.items.map((label) => (
              <button
                key={label}
                type="button"
                disabled
                title={t.future.notImplemented}
                className="flex w-full cursor-not-allowed flex-col items-start rounded-lg px-3 py-1.5 text-left text-sm text-slate-400"
              >
                {label}
                <span className="text-[10px] leading-tight">{t.future.notImplemented}</span>
              </button>
            ))}
          </div>
        </aside>

        <div className="flex min-w-0 flex-1 flex-col">
          <header className="flex flex-wrap items-center justify-between gap-3 border-b border-slate-200 bg-white px-6 py-3">
            <div>
              <div className="text-base font-semibold text-slate-900">{location.name}</div>
              <div className="text-xs text-slate-500">
                {location.timezone} · {location.currency}
              </div>
            </div>
            <div className="flex items-center gap-3 text-sm">
              <nav className="flex gap-2 md:hidden">
                {nav.map((item) => (
                  <Link key={item.href} href={item.href} className="text-brand-700 underline">
                    {item.label}
                  </Link>
                ))}
              </nav>
              <span className="text-slate-600" data-testid="current-user">
                {me.user.displayName} · {roleLabel(me.user.role)}
              </span>
              <button type="button" onClick={logout} className="rounded-lg border border-slate-300 px-3 py-1.5 text-slate-700 hover:bg-slate-50">
                {t.nav.logout}
              </button>
            </div>
          </header>
          {mustChangePassword && (
            <div role="alert" className="border-b border-amber-300 bg-amber-50 px-6 py-3 text-sm text-amber-900">
              {t.account.mustChangeBanner}
            </div>
          )}
          <main className="flex-1 p-6">{children}</main>
        </div>
      </div>
    </Ctx.Provider>
  );
}
