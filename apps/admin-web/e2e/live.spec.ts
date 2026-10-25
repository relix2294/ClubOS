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

test("live-поток: изменение из другой вкладки видно без ожидания опроса", async ({ page, context }) => {
  test.skip(!password, "Задайте E2E_PASSWORD");
  await login(page);

  // Индикатор live-потока в шапке.
  await expect(page.getByTestId("live-status")).toHaveAttribute("data-live", "live", { timeout: 15_000 });

  await page.getByTestId("device-tile").filter({ has: page.locator('[data-status="Idle"]') }).first().click();
  await expect(page.getByTestId("device-name")).toBeVisible();
  const deviceUrl = page.url();

  // Та же карточка во второй вкладке (как второй администратор): отправляем сообщение оттуда.
  const other = await context.newPage();
  await other.goto(deviceUrl);
  const title = `Live ${Date.now()}`;
  await other.getByLabel("Заголовок").fill(title);
  await other.getByLabel("Текст сообщения").fill("Проверка live-потока");
  await other.getByRole("button", { name: "Отправить" }).click();

  // Первая вкладка при live-потоке опрашивает раз в 30 с. Команда должна появиться и дойти до «Выполнена»
  // гораздо быстрее, иначе push не работает.
  const row = page.getByTestId("command-list").locator("li").filter({ hasText: `«${title}»` });
  await expect(row.locator('[data-state="Succeeded"]')).toBeVisible({ timeout: 10_000 });
  await other.close();
});
