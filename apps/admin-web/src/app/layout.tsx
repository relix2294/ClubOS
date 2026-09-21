import type { Metadata } from "next";
import type { ReactNode } from "react";
import "./globals.css";
import { AuthProvider } from "@/lib/auth";
import { AppHeader } from "@/components/AppHeader";

export const metadata: Metadata = {
  title: "ClubOS — Панель управления",
  description: "ClubOS Admin Web (M0)",
};

export default function RootLayout({ children }: Readonly<{ children: ReactNode }>) {
  return (
    <html lang="ru">
      <body>
        <AuthProvider>
          <AppHeader />
          <main className="mx-auto max-w-6xl px-4 py-6">{children}</main>
        </AuthProvider>
      </body>
    </html>
  );
}
