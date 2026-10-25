"use client";

import { useState } from "react";
import {
  TerminalCommandStates,
  type CommandView,
  type DeviceView,
  type ProcessListOutput,
  type ScreenshotOutput,
} from "@clubos/contracts";
import { ApiError, apiGet, apiPost } from "@/lib/api";
import { formatDateTime } from "@/lib/format";
import { t } from "@/lib/i18n";
import { useShell } from "./AppShell";
import { Button, Card, Field, inputClass } from "./ui";

const POLL_MS = 1000;
const WAIT_MS = 90_000;

/** Отправить команду и дождаться итога (агент → Edge → Cloud; результат — через GET /commands/{id}). */
async function runCommand(deviceId: string, body: object): Promise<CommandView> {
  const created = await apiPost<CommandView>(`devices/${deviceId}/commands`, body);
  const deadline = Date.now() + WAIT_MS;
  while (Date.now() < deadline) {
    await new Promise((r) => setTimeout(r, POLL_MS));
    const current = await apiGet<CommandView>(`commands/${created.commandId}`);
    if (TerminalCommandStates.includes(current.state)) {
      if (current.state !== "Succeeded") throw new Error(current.error ?? t.remote.failed);
      return current;
    }
  }
  throw new Error(t.remote.timeout);
}

/**
 * Удалённый доступ к ПК (D-022, права devices.remote): снимок экрана, процессы пользователя с завершением,
 * перезагрузка и выключение. Живого управления мышью нет — для него RDP/VNC в LAN клуба.
 */
export function RemoteCard({ device }: { device: DeviceView }) {
  const { location } = useShell();
  const [busy, setBusy] = useState<string | null>(null);
  const [error, setError] = useState<string>();
  const [done, setDone] = useState<string>();
  const [shot, setShot] = useState<ScreenshotOutput>();
  const [large, setLarge] = useState(false);
  const [processes, setProcesses] = useState<ProcessListOutput["processes"]>();
  const [delay, setDelay] = useState("60");
  const offline = device.status === "Offline";

  const run = async (key: string, action: () => Promise<void>) => {
    setBusy(key);
    setError(undefined);
    setDone(undefined);
    try {
      await action();
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setBusy(null);
    }
  };

  const screenshot = () =>
    run("shot", async () => {
      const result = await runCommand(device.deviceId, { commandType: "Screenshot", ttlSeconds: 60 });
      setShot(result.result as ScreenshotOutput);
    });

  const listProcesses = () =>
    run("ps", async () => {
      const result = await runCommand(device.deviceId, { commandType: "ListProcesses", ttlSeconds: 60 });
      setProcesses((result.result as ProcessListOutput).processes);
    });

  const kill = (processId: number, name: string) => {
    if (!window.confirm(`${t.remote.killConfirm} «${name}»?`)) return;
    void run(`kill-${processId}`, async () => {
      await runCommand(device.deviceId, { commandType: "KillProcess", processId, processName: name, ttlSeconds: 60 });
      setProcesses((list) => list?.filter((p) => p.processId !== processId));
      setDone(`${t.remote.killed}: ${name}`);
    });
  };

  const power = (reboot: boolean) => {
    const text = reboot ? t.remote.rebootConfirm : t.remote.shutdownConfirm;
    if (!window.confirm(text)) return;
    void run(reboot ? "reboot" : "shutdown", async () => {
      const body = { commandType: reboot ? "Reboot" : "Shutdown", delaySeconds: Number(delay), ttlSeconds: 120 };
      try {
        await runCommand(device.deviceId, body);
      } catch (e) {
        // Идёт сессия — переспросить и отправить принудительно.
        if (e instanceof ApiError && e.code === "session_active" && window.confirm(t.remote.forceConfirm)) {
          await runCommand(device.deviceId, { ...body, force: true });
        } else {
          throw e;
        }
      }
      setDone(reboot ? t.remote.rebootSent : t.remote.shutdownSent);
    });
  };

  return (
    <Card title={t.remote.title}>
      <div className="flex flex-col gap-4" data-testid="remote-card">
        <p className="text-xs text-slate-500">{t.remote.hint}</p>
        <div className="flex flex-wrap gap-2">
          <Button disabled={!!busy || offline} onClick={screenshot}>
            {busy === "shot" ? t.remote.waiting : t.remote.screenshot}
          </Button>
          <Button variant="secondary" disabled={!!busy || offline} onClick={listProcesses}>
            {busy === "ps" ? t.remote.waiting : t.remote.processes}
          </Button>
        </div>

        {shot && (
          <figure className="flex flex-col gap-1" data-testid="remote-screenshot">
            {/* eslint-disable-next-line @next/next/no-img-element -- data: URI снимка, оптимизация Next не нужна */}
            <img
              src={`data:${shot.mime};base64,${shot.dataBase64}`}
              alt={t.remote.screenshotAlt}
              onClick={() => setLarge((v) => !v)}
              className={`cursor-zoom-in rounded-lg border border-slate-200 ${large ? "w-full" : "max-h-64 w-auto"}`}
            />
            <figcaption className="text-xs text-slate-500">
              {formatDateTime(shot.capturedAtUtc, location.timezone)}
              {shot.width > 0 && ` · ${shot.width}×${shot.height}`}
            </figcaption>
          </figure>
        )}

        {processes && (
          <div className="overflow-x-auto">
            {processes.length === 0 ? (
              <p className="text-sm text-slate-500">{t.remote.noProcesses}</p>
            ) : (
              <table className="w-full text-left text-sm" data-testid="remote-processes">
                <thead className="text-xs uppercase text-slate-500">
                  <tr>
                    <th className="py-1 pr-3">{t.remote.process}</th>
                    <th className="py-1 pr-3 text-right">{t.remote.memory}</th>
                    <th className="py-1" />
                  </tr>
                </thead>
                <tbody className="divide-y divide-slate-100">
                  {processes.map((p) => (
                    <tr key={p.processId} data-testid="remote-process" data-name={p.name}>
                      <td className="py-1 pr-3">
                        <span className="font-medium">{p.name}</span>
                        {p.windowTitle && <span className="block text-xs text-slate-500">{p.windowTitle}</span>}
                      </td>
                      <td className="py-1 pr-3 text-right tabular-nums">{p.memoryMb} МБ</td>
                      <td className="py-1 text-right">
                        <Button variant="secondary" className="py-0.5" disabled={!!busy} onClick={() => kill(p.processId, p.name)}>
                          {t.remote.kill}
                        </Button>
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            )}
          </div>
        )}

        <div className="flex flex-wrap items-end gap-2 border-t border-slate-100 pt-4">
          <Field label={t.remote.delay}>
            <select className={`${inputClass} py-1`} value={delay} onChange={(e) => setDelay(e.target.value)}>
              <option value="0">{t.remote.now}</option>
              <option value="60">1 мин</option>
              <option value="300">5 мин</option>
            </select>
          </Field>
          <Button variant="secondary" disabled={!!busy || offline} onClick={() => power(true)}>
            {busy === "reboot" ? t.remote.waiting : t.remote.reboot}
          </Button>
          <Button variant="danger" disabled={!!busy || offline} onClick={() => power(false)}>
            {busy === "shutdown" ? t.remote.waiting : t.remote.shutdown}
          </Button>
        </div>

        {offline && <p className="text-sm text-amber-700">{t.remote.offline}</p>}
        {done && (
          <p role="status" className="text-sm text-emerald-700">
            {done}
          </p>
        )}
        {error && (
          <p role="alert" className="rounded-lg bg-red-50 px-3 py-2 text-sm text-red-800">
            {error}
          </p>
        )}
      </div>
    </Card>
  );
}
