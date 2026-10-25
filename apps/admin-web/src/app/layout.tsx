import type { Metadata } from "next";
import "./globals.css";

export const metadata: Metadata = {
  title: "ClubOS — панель клуба",
  description: "ClubOS CA · Milestone 0",
};

export default function RootLayout({ children }: Readonly<{ children: React.ReactNode }>) {
  return (
    <html lang="ru">
      <body className="min-h-screen font-sans antialiased">{children}</body>
    </html>
  );
}
