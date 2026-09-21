import { t } from "@/lib/i18n";

export function LoadingView({ label }: { label?: string }) {
  return (
    <div className="flex items-center justify-center rounded-xl border border-[var(--border)] bg-[var(--surface)] p-10 text-[var(--muted)]">
      {label ?? t("loading")}
    </div>
  );
}

export function EmptyView({ label }: { label?: string }) {
  return (
    <div className="flex items-center justify-center rounded-xl border border-dashed border-[var(--border)] bg-[var(--surface)] p-10 text-[var(--muted)]">
      {label ?? t("empty")}
    </div>
  );
}

export function ErrorView({ message, onRetry }: { message: string; onRetry?: () => void }) {
  return (
    <div className="flex flex-col items-center gap-3 rounded-xl border border-rose-500/30 bg-rose-500/10 p-10 text-rose-200">
      <div className="font-medium">{t("errorTitle")}</div>
      <div className="text-sm text-rose-200/80">{message}</div>
      {onRetry ? (
        <button
          onClick={onRetry}
          className="rounded-lg border border-rose-400/40 px-3 py-1.5 text-sm hover:bg-rose-500/20"
        >
          {t("retry")}
        </button>
      ) : null}
    </div>
  );
}
