import { expect, test } from "@playwright/test";

// Smoke: login → devices → карточка устройства (ТЗ §25.3, §30).
// Требует поднятого стека и seeded владельца. Пароль — из env E2E_OWNER_PASSWORD.

const email = process.env.E2E_OWNER_EMAIL ?? "owner@demo.clubos";
const password = process.env.E2E_OWNER_PASSWORD ?? "ChangeMe123!";

test("login, открыть дашборд и карточку устройства", async ({ page }) => {
  await page.goto("/");

  // Редирект на форму входа.
  await expect(page).toHaveURL(/\/login$/);
  await page.locator('input[type="email"]').fill(email);
  await page.locator('input[type="password"]').fill(password);
  await page.locator('button[type="submit"]').click();

  // Дашборд.
  await expect(page).toHaveURL(/\/dashboard$/);
  await expect(page.getByRole("heading").first()).toBeVisible();

  // Первая карточка устройства ведёт на страницу устройства.
  const firstDevice = page.locator('a[href^="/devices/"]').first();
  await firstDevice.click();
  await expect(page).toHaveURL(/\/devices\//);
  await expect(page.getByText("Инвентаризация")).toBeVisible();
});
