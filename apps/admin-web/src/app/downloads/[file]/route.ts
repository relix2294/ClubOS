import { NextRequest, NextResponse } from "next/server";

// Пакеты для Windows. На VPS /downloads/* отдаёт Caddy напрямую, сюда запрос не доходит.
// В dev-стеке Admin Web проксирует на сервис downloads (CLUBOS_DOWNLOADS_URL), чтобы ссылки работали одинаково.
const FILES = /^(ClubOS-(Agent|Edge)-win-x64\.zip|SHA256SUMS\.txt|VERSION\.txt)$/;

export async function GET(_req: NextRequest, ctx: { params: Promise<{ file: string }> }) {
  const { file } = await ctx.params;
  const base = process.env.CLUBOS_DOWNLOADS_URL;
  if (!base || !FILES.test(file)) {
    return NextResponse.json({ detail: "Пакет не найден." }, { status: 404 });
  }

  let upstream: Response;
  try {
    upstream = await fetch(`${base.replace(/\/$/, "")}/${file}`, { cache: "no-store" });
  } catch {
    return NextResponse.json({ detail: "Сервис пакетов недоступен." }, { status: 502 });
  }

  if (!upstream.ok || !upstream.body) {
    return NextResponse.json({ detail: "Пакет не найден." }, { status: upstream.status === 404 ? 404 : 502 });
  }

  return new NextResponse(upstream.body, {
    headers: {
      "content-type": upstream.headers.get("content-type") ?? "application/octet-stream",
      ...(upstream.headers.get("content-length") ? { "content-length": upstream.headers.get("content-length")! } : {}),
      ...(file.endsWith(".zip") ? { "content-disposition": `attachment; filename="${file}"` } : {}),
    },
  });
}
