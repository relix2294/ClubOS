import { cookies } from "next/headers";
import { redirect } from "next/navigation";
import { AppShell } from "@/components/AppShell";
import { LiveProvider } from "@/lib/live";

export default async function AppLayout({ children }: { children: React.ReactNode }) {
  const jar = await cookies();
  if (!jar.get("clubos_at") && !jar.get("clubos_rt")) {
    redirect("/login");
  }

  return (
    <LiveProvider>
      <AppShell>{children}</AppShell>
    </LiveProvider>
  );
}
