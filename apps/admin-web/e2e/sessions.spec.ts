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

/** "MM:SS" или "HH:MM:SS" → секунды. */
function seconds(text: string): number {
  return text
    .trim()
    .split(":")
    .map(Number)
    .reduce((acc, part) => acc * 60 + part, 0);
}

test("сессия с лимитом: старт → остаток → продление → завершение с итогом", async ({ page }) => {
  test.skip(!password, "Задайте E2E_PASSWORD");
  await login(page);

  // Свободное устройство в сети (симулятор).
  await page.getByTestId("device-tile").filter({ has: page.locator('[data-status="Idle"]') }).last().click();
  await expect(page.getByTestId("device-name")).toBeVisible();

  const start = page.getByRole("group", { name: "Начать сессию" });
  await start.getByLabel("Лимит времени").selectOption("30");
  await start.getByRole("button", { name: "Начать сессию" }).click();

  // Edge подтвердил старт: виден остаток ~30 минут и время окончания.
  const remaining = page.getByTestId("session-remaining");
  await expect(remaining).toBeVisible({ timeout: 20_000 });
  const before = seconds(await remaining.locator(".font-mono").innerText());
  expect(before).toBeGreaterThan(29 * 60);
  expect(before).toBeLessThanOrEqual(30 * 60);

  // Продление на 15 минут: Edge сдвигает окончание, остаток растёт.
  await page.getByRole("group", { name: "Продлить" }).getByRole("button", { name: "+15 мин" }).click();
  await expect
    .poll(async () => seconds(await remaining.locator(".font-mono").innerText()), { timeout: 20_000 })
    .toBeGreaterThan(44 * 60);

  // Завершение: итог в истории, с лимитом и причиной.
  await page.getByRole("button", { name: "Завершить сессию" }).click();
  const last = page.getByTestId("session-history").locator("li").first();
  await expect(last.locator('[data-state="Ended"]')).toBeVisible({ timeout: 20_000 });
  await expect(last).toContainText("лимит 45 мин"); // 30 + продление 15
  await expect(last).toContainText("TJS");
  await expect(last).toContainText("сотрудником");

  await expect(page.getByTestId("device-audit")).toContainText("Сессия продлена");
});
