"use client";

import { useCallback, useEffect, useRef, useState } from "react";
import { useLive, type LiveFilter } from "./live";

/** При работающем live-потоке опрос остаётся только страховкой. */
export const LIVE_FALLBACK_MS = 30_000;
const LIVE_DEBOUNCE_MS = 150;

export interface PollingState<T> {
  data: T | undefined;
  error: Error | undefined;
  loading: boolean;
  refresh: () => void;
}

/**
 * Данные Cloud API с обновлением. С фильтром <paramref name="live"/> данные перечитываются сразу по подсказке
 * live-потока, а опрос становится страховочным (раз в {@link LIVE_FALLBACK_MS}). Без потока — обычный опрос
 * раз в <paramref name="intervalMs"/>. Не накладывает запросы друг на друга, отменяет запрос при размонтировании.
 */
export function usePolling<T>(
  fetcher: (signal: AbortSignal) => Promise<T>,
  intervalMs: number,
  deps: unknown[] = [],
  live?: LiveFilter,
): PollingState<T> {
  const [data, setData] = useState<T>();
  const [error, setError] = useState<Error>();
  const [loading, setLoading] = useState(true);
  const [tick, setTick] = useState(0);
  const fetcherRef = useRef(fetcher);

  useEffect(() => {
    fetcherRef.current = fetcher;
  });

  const refresh = useCallback(() => setTick((t) => t + 1), []);
  const liveCtx = useLive();
  const liveKey = live ? JSON.stringify(live) : null;
  const subscribe = liveCtx?.subscribe;
  const effectiveInterval = live && liveCtx?.status === "live" ? Math.max(intervalMs, LIVE_FALLBACK_MS) : intervalMs;

  useEffect(() => {
    if (!liveKey || !subscribe) return;
    let timer: ReturnType<typeof setTimeout> | undefined;
    // Несколько подсказок подряд (команда: Queued → Delivered → Succeeded) — одно перечитывание.
    const unsubscribe = subscribe(JSON.parse(liveKey) as LiveFilter, () => {
      if (timer) clearTimeout(timer);
      timer = setTimeout(refresh, LIVE_DEBOUNCE_MS);
    });
    return () => {
      unsubscribe();
      if (timer) clearTimeout(timer);
    };
  }, [liveKey, subscribe, refresh]);

  useEffect(() => {
    let cancelled = false;
    let timer: ReturnType<typeof setTimeout> | undefined;
    let controller: AbortController | undefined;

    const run = async () => {
      controller = new AbortController();
      try {
        const result = await fetcherRef.current(controller.signal);
        if (!cancelled) {
          setData(result);
          setError(undefined);
        }
      } catch (e) {
        if (!cancelled && !(e instanceof DOMException && e.name === "AbortError")) {
          setError(e as Error);
        }
      } finally {
        if (!cancelled) {
          setLoading(false);
          timer = setTimeout(run, effectiveInterval);
        }
      }
    };

    void run();
    return () => {
      cancelled = true;
      controller?.abort();
      if (timer) clearTimeout(timer);
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [effectiveInterval, tick, ...deps]);

  return { data, error, loading, refresh };
}

/** Текущее время, обновляемое раз в секунду (для таймеров сессий). */
export function useNow(intervalMs = 1000): number {
  const [now, setNow] = useState(() => Date.now());
  useEffect(() => {
    const id = setInterval(() => setNow(Date.now()), intervalMs);
    return () => clearInterval(id);
  }, [intervalMs]);
  return now;
}
