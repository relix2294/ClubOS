"use client";

import { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState, type ReactNode } from "react";
import type { LiveEvent, LiveTopic } from "@clubos/contracts";

// Live-обновления (DEVIATIONS D-008): один EventSource на вкладку. Сервер присылает подсказки
// «изменилось X», подписанные хуки перечитывают свои данные через REST. Если поток недоступен,
// usePolling продолжает обычный опрос, так что страница никогда не «замерзает».

export type LiveStatus = "connecting" | "live" | "offline";

export interface LiveFilter {
  topics: LiveTopic[];
  deviceId?: string;
  locationId?: string;
}

type Listener = { filter: LiveFilter; callback: () => void };

interface LiveContextValue {
  status: LiveStatus;
  subscribe: (filter: LiveFilter, callback: () => void) => () => void;
}

const LiveCtx = createContext<LiveContextValue | null>(null);

export function useLive(): LiveContextValue | null {
  return useContext(LiveCtx);
}

export function matches(filter: LiveFilter, evt: LiveEvent): boolean {
  if (!filter.topics.includes(evt.topic)) return false;
  if (filter.deviceId && evt.deviceId !== filter.deviceId) return false;
  if (filter.locationId && evt.locationId && evt.locationId !== filter.locationId) return false;
  return true;
}

const RECONNECT_MIN_MS = 2_000;
const RECONNECT_MAX_MS = 30_000;

export function LiveProvider({ children }: { children: ReactNode }) {
  const [status, setStatus] = useState<LiveStatus>(() => (typeof EventSource === "undefined" ? "offline" : "connecting"));
  const listeners = useRef(new Set<Listener>());

  useEffect(() => {
    if (typeof EventSource === "undefined") return;

    let source: EventSource | null = null;
    let retry: ReturnType<typeof setTimeout> | undefined;
    let delay = RECONNECT_MIN_MS;
    let disposed = false;

    const notifyAll = () => listeners.current.forEach((l) => l.callback());

    const connect = () => {
      if (disposed) return;
      setStatus("connecting");
      source = new EventSource("/api/live");

      source.addEventListener("ready", () => {
        setStatus("live");
        delay = RECONNECT_MIN_MS;
        notifyAll(); // пока не было потока, изменения могли пройти мимо
      });
      source.addEventListener("change", (e) => {
        let evt: LiveEvent;
        try {
          evt = JSON.parse((e as MessageEvent<string>).data) as LiveEvent;
        } catch {
          return;
        }
        listeners.current.forEach((l) => {
          if (matches(l.filter, evt)) l.callback();
        });
      });
      source.addEventListener("resync", notifyAll);
      source.addEventListener("reauth", () => {
        // Токен истёк или доступ изменился: переподключаемся, BFF обновит токен (или REST уведёт на вход).
        source?.close();
        schedule(RECONNECT_MIN_MS);
      });
      source.onerror = () => {
        if (source?.readyState === EventSource.CLOSED) {
          // Ответ не 200 (например, 401/502): EventSource сам не переподключается.
          schedule(delay);
          delay = Math.min(delay * 2, RECONNECT_MAX_MS);
        }
        setStatus("offline");
      };
    };

    const schedule = (ms: number) => {
      source?.close();
      source = null;
      if (retry) clearTimeout(retry);
      retry = setTimeout(connect, ms);
    };

    connect();
    return () => {
      disposed = true;
      if (retry) clearTimeout(retry);
      source?.close();
    };
  }, []);

  const subscribe = useCallback((filter: LiveFilter, callback: () => void) => {
    const listener = { filter, callback };
    listeners.current.add(listener);
    return () => {
      listeners.current.delete(listener);
    };
  }, []);

  const value = useMemo<LiveContextValue>(() => ({ status, subscribe }), [status, subscribe]);

  return <LiveCtx.Provider value={value}>{children}</LiveCtx.Provider>;
}
