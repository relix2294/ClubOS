"use client";

import Link from "next/link";
import { usePathname, useRouter } from "next/navigation";
import { useAuth } from "@/lib/auth";
import { t } from "@/lib/i18n";

const nav: { href: string; label: string }[] = [
  { href: "/dashboard", label: t("dashboard") },
  { href: "/audit", label: t("audit") },
];

export function AppHeader() {
  const { session, signOut } = useAuth();
  const pathname = usePathname();
  const router = useRouter();

  if (!session) {
    return null;
  }

  return (
    <header className="border-b border-[var(--border)] bg-[var(--surface)]">
      <div className="mx-auto flex max-w-6xl items-center justify-between gap-4 px-4 py-3">
        <div className="flex items-center gap-6">
          <span className="text-sm font-semibold tracking-wide">{t("appName")}</span>
          <nav className="flex items-center gap-1">
            {nav.map((item) => {
              const active = pathname.startsWith(item.href);
              return (
                <Link
                  key={item.href}
                  href={item.href}
                  className={`rounded-lg px-3 py-1.5 text-sm ${
                    active ? "bg-[var(--surface-2)] text-white" : "text-[var(--muted)] hover:text-white"
                  }`}
                >
                  {item.label}
                </Link>
              );
            })}
          </nav>
        </div>
        <button
          onClick={() => {
            signOut();
            router.push("/login");
          }}
          className="rounded-lg border border-[var(--border)] px-3 py-1.5 text-sm text-[var(--muted)] hover:text-white"
        >
          {t("signOut")}
        </button>
      </div>
    </header>
  );
}
