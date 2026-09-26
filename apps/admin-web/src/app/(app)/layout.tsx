import { cookies } from "next/headers";
import { redirect } from "next/navigation";
import { AppShell } from "@/components/AppShell";

export default async function AppLayout({ children }: { children: React.ReactNode }) {
  const jar = await cookies();
  if (!jar.get("clubos_at") && !jar.get("clubos_rt")) {
    redirect("/login");
  }

  return <AppShell>{children}</AppShell>;
}
