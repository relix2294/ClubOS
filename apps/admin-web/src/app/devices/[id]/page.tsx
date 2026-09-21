"use client";

import { type FormEvent, useCallback, useEffect, useState } from "react";
import Link from "next/link";
import { useParams } from "next/navigation";
import { ApiError, getDevice, getDeviceCommands, issueShowMessage } from "@/lib/api";
import { useRequireAuth } from "@/components/Guard";
import { CommandStateBadge, DeviceStatusBadge } from "@/components/StatusBadge";
import { SessionPanel } from "@/components/SessionPanel";
import { EmptyView, ErrorView, LoadingView } from "@/components/States";
import { formatTime } from "@/lib/format";
import { t } from "@/lib/i18n";
import type { CommandDto, DeviceDto } from "@/lib/types";

export default function DevicePage() {
  const params = useParams<{ id: string }>();
  const deviceId = params.id;
  const { session, ready } = useRequireAuth();

  const [device, setDevice] = useState<DeviceDto | null>(null);
  const [commands, setCommands] = useState<CommandDto[]>([]);
  const [status, setStatus] = useState<"loading" | "ready" | "error">("loading");
  const [errorMessage, setErrorMessage] = useState("");

  const [title, setTitle] = useState("Внимание");
  const [message, setMessage] = useState("");
  const [sending, setSending] = useState(false);

  const loadDevice = useCallback(async () => {
    if (!session) {
      return;
    }
    setStatus("loading");
    try {
      setDevice(await getDevice(session.accessToken, deviceId));
      setCommands(await getDeviceCommands(session.accessToken, deviceId));
      setStatus("ready");
    } catch (err) {
      setErrorMessage(err instanceof ApiError && err.status === 0 ? t("connectionError") : String(err));
      setStatus("error");
    }
  }, [session, deviceId]);

  const refreshCommands = useCallback(async () => {
    if (!session) {
      return;
    }
    try {
      setCommands(await getDeviceCommands(session.accessToken, deviceId));
    } catch {
      // Тихо — оставляем предыдущий список.
    }
  }, [session, deviceId]);

  useEffect(() => {
    if (ready && session) {
      void loadDevice();
    }
  }, [ready, session, loadDevice]);

  useEffect(() => {
    if (status !== "ready") {
      return;
    }
    const id = setInterval(() => void refreshCommands(), 4000);
    return () => clearInterval(id);
  }, [status, refreshCommands]);

  async function onSend(event: FormEvent) {
    event.preventDefault();
    if (!session || message.trim().length === 0) {
      return;
    }
    setSending(true);
    try {
      await issueShowMessage(session.accessToken, deviceId, title, message);
      setMessage("");
      await refreshCommands();
    } catch {
      // Ошибку покажем как отсутствие новой команды; список не меняем.
    } finally {
      setSending(false);
    }
  }

  if (!ready || !session) {
    return <LoadingView />;
  }

  if (status === "loading") {
    return <LoadingView />;
  }

  if (status === "error" || !device) {
    return <ErrorView message={errorMessage || t("errorTitle")} onRetry={() => void loadDevice()} />;
  }

  return (
    <div className="flex flex-col gap-6">
      <div>
        <Link href="/dashboard" className="text-sm text-[var(--muted)] hover:text-white">
          ← {t("back")}
        </Link>
      </div>

      <section className="flex flex-wrap items-center justify-between gap-3">
        <div className="flex items-center gap-3">
          <h1 className="text-lg font-semibold">{device.displayName}</h1>
          {device.simulated ? (
            <span className="rounded bg-amber-500/15 px-1.5 py-0.5 text-[10px] font-semibold text-amber-300">
              {t("simulated")}
            </span>
          ) : null}
        </div>
        <DeviceStatusBadge status={device.status} />
      </section>

      <div className="grid grid-cols-1 gap-6 lg:grid-cols-2">
        <section className="flex flex-col gap-3 rounded-xl border border-[var(--border)] bg-[var(--surface)] p-4">
          <h2 className="font-medium">{t("inventory")}</h2>
          <dl className="grid grid-cols-1 gap-2 text-sm">
            <Row label={t("hostname")} value={device.hostname} />
            <Row label={t("windows")} value={device.windowsVersion} />
            <Row label={t("cpu")} value={device.cpu} />
            <Row label={t("ram")} value={device.ramMegabytes ? `${device.ramMegabytes} MB` : null} />
            <Row label={t("ipv4")} value={device.ipv4} />
            <Row label={t("agentVersion")} value={device.agentVersion} />
            <Row label={t("lastHeartbeat")} value={formatTime(device.lastHeartbeatUtc)} />
          </dl>
        </section>

        <SessionPanel token={session.accessToken} deviceId={device.id} />

        <section className="flex flex-col gap-3 rounded-xl border border-[var(--border)] bg-[var(--surface)] p-4">
          <h2 className="font-medium">{t("showMessage")}</h2>
          <form onSubmit={onSend} className="flex flex-col gap-3">
            <input
              value={title}
              onChange={(e) => setTitle(e.target.value)}
              placeholder={t("title")}
              className="rounded-lg border border-[var(--border)] bg-[var(--surface-2)] px-3 py-2 text-sm outline-none focus:border-[var(--accent)]"
            />
            <textarea
              value={message}
              onChange={(e) => setMessage(e.target.value)}
              placeholder={t("message")}
              rows={3}
              className="rounded-lg border border-[var(--border)] bg-[var(--surface-2)] px-3 py-2 text-sm outline-none focus:border-[var(--accent)]"
            />
            <button
              type="submit"
              disabled={sending || message.trim().length === 0}
              className="self-start rounded-lg bg-[var(--accent)] px-3 py-1.5 text-sm font-medium text-white disabled:opacity-50"
            >
              {t("send")}
            </button>
          </form>
        </section>

        <section className="flex flex-col gap-3 rounded-xl border border-[var(--border)] bg-[var(--surface)] p-4">
          <h2 className="font-medium">{t("commands")}</h2>
          {commands.length === 0 ? (
            <EmptyView />
          ) : (
            <ul className="flex flex-col gap-2">
              {commands.map((command) => (
                <li
                  key={command.id}
                  className="flex items-center justify-between gap-2 rounded-lg bg-[var(--surface-2)] px-3 py-2 text-sm"
                >
                  <div className="flex flex-col">
                    <span>{command.commandType}</span>
                    <span className="text-xs text-[var(--muted)]">{formatTime(command.issuedAtUtc)}</span>
                  </div>
                  <CommandStateBadge state={command.state} />
                </li>
              ))}
            </ul>
          )}
        </section>
      </div>
    </div>
  );
}

function Row({ label, value }: { label: string; value: string | null }) {
  return (
    <div className="flex items-center justify-between gap-3 border-b border-[var(--border)] pb-1.5 last:border-0">
      <dt className="text-[var(--muted)]">{label}</dt>
      <dd className="text-right">{value ?? t("never")}</dd>
    </div>
  );
}
