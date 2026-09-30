import { expect, test, type Page } from "@playwright/test";

const email = process.env.E2E_EMAIL ?? "owner@demo.clubos.local";
const password = process.env.E2E_PASSWORD ?? "";

async function login(page: Page) {
  await page.goto("/login");
  await page.getByLabel("Email").fill(email);
  await page.getByLabel("Пароль").fill(password);
  await page.getByRole("button", { name: "Войти" }).click();
  await expect(page.getByRole("heading", { name: "Устройства" })).toBeVisible();
}

// Бездисковый симулятор (compose-сервис diskless-simulator): ПК с MAC 02:C1:0B:5D:00:0N без токенов.
test("бездисковый ПК: ожидает подтверждения → подтверждение → на связи с меткой", async ({ page }) => {
  test.skip(!password, "Задайте E2E_PASSWORD");
  await login(page);

  const pending = page.getByTestId("diskless-pending");
  const row = pending.getByTestId("diskless-row").first();
  // ПК мог быть подтверждён прошлым прогоном — тогда в списке остаётся второй или список пуст.
  if (!(await row.isVisible({ timeout: 20_000 }).catch(() => false))) {
    await expect(page.getByTestId("device-tile").filter({ hasText: "Бездисковый" }).first()).toBeVisible();
    return;
  }

  const mac = (await row.getAttribute("data-mac"))!;
  expect(mac).toMatch(/^02:C1:0B:5D:00:0\d$/);
  const name = `DL-${mac.slice(-2)}-${Date.now().toString().slice(-4)}`;
  await row.getByLabel("Имя ПК").fill(name);
  await row.getByRole("button", { name: "Подтвердить" }).click();

  await expect(page.locator(`[data-testid="diskless-row"][data-mac="${mac}"]`)).toHaveCount(0);
  const tile = page.getByTestId("device-tile").filter({ hasText: name });
  await expect(tile).toBeVisible({ timeout: 15_000 });
  await expect(tile).toContainText("Бездисковый");
  // Edge получил привязку, ПК загрузился с сертификатом локального CA и шлёт heartbeat.
  await expect(tile.locator('[data-status="Idle"]')).toBeVisible({ timeout: 45_000 });

  await tile.click();
  await expect(page.getByTestId("inventory")).toContainText(`MAC ${mac}`);
});
