"use client";

import { useEffect } from "react";
import { useRouter } from "next/navigation";
import { useAuth } from "@/lib/auth";
import { LoadingView } from "@/components/States";

export default function Home() {
  const { session, ready } = useAuth();
  const router = useRouter();

  useEffect(() => {
    if (!ready) {
      return;
    }
    router.replace(session ? "/dashboard" : "/login");
  }, [ready, session, router]);

  return <LoadingView />;
}
