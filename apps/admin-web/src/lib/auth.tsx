"use client";

import { createContext, useContext, useEffect, useMemo, useState, type ReactNode } from "react";
import type { TokenResponse } from "./types";

interface Session {
  accessToken: string;
  refreshToken: string;
  role: string;
}

interface AuthContextValue {
  session: Session | null;
  ready: boolean;
  signIn: (tokens: TokenResponse) => void;
  signOut: () => void;
}

const STORAGE_KEY = "clubos.session";

const AuthContext = createContext<AuthContextValue | null>(null);

export function AuthProvider({ children }: { children: ReactNode }) {
  const [session, setSession] = useState<Session | null>(null);
  const [ready, setReady] = useState(false);

  useEffect(() => {
    try {
      const raw = localStorage.getItem(STORAGE_KEY);
      if (raw) {
        setSession(JSON.parse(raw) as Session);
      }
    } catch {
      // localStorage недоступен — работаем без сохранённой сессии.
    }
    setReady(true);
  }, []);

  const value = useMemo<AuthContextValue>(
    () => ({
      session,
      ready,
      signIn: (tokens) => {
        const next: Session = {
          accessToken: tokens.accessToken,
          refreshToken: tokens.refreshToken,
          role: tokens.role,
        };
        setSession(next);
        try {
          localStorage.setItem(STORAGE_KEY, JSON.stringify(next));
        } catch {
          // Игнорируем — сессия останется только в памяти.
        }
      },
      signOut: () => {
        setSession(null);
        try {
          localStorage.removeItem(STORAGE_KEY);
        } catch {
          // Игнорируем.
        }
      },
    }),
    [session, ready],
  );

  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>;
}

export function useAuth(): AuthContextValue {
  const ctx = useContext(AuthContext);
  if (!ctx) {
    throw new Error("useAuth должен использоваться внутри AuthProvider");
  }
  return ctx;
}
