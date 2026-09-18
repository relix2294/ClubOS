import { defineConfig, devices } from "@playwright/test";

// Smoke-конфиг E2E (ТЗ §30). Требует запущенных Cloud API и Admin Web.
// Базовый URL панели — из env E2E_BASE_URL (по умолчанию http://localhost:3000).
export default defineConfig({
  testDir: "./tests",
  timeout: 30_000,
  retries: 0,
  use: {
    baseURL: process.env.E2E_BASE_URL ?? "http://localhost:3000",
    trace: "on-first-retry",
  },
  projects: [
    {
      name: "chromium",
      use: { ...devices["Desktop Chrome"] },
    },
  ],
});
