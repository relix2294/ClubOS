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

/** "1 234,50 TJS" → 123450. */
function minor(text: string): number {
  const match = text.replace(/\s/g, "").match(/(-?\d+),(\d{2})/);
  if (!match) throw new Error(`Нет суммы в «${text}»`);
  const [, whole, frac] = match;
  return Number(whole) * 100 + (whole.startsWith("-") ? -1 : 1) * Number(frac);
}

test("касса: смена → оплата завершённой сессии → закрытие с недостачей → отчёт", async ({ page }) => {
  test.skip(!password, "Задайте E2E_PASSWORD");
  await login(page);

  // Сессия на свободном ПК: старт и завершение (итог считает Edge).
  await page.getByTestId("device-tile").filter({ has: page.locator('[data-status="Idle"]') }).nth(2).click();
  const deviceName = (await page.getByTestId("device-name").innerText()).trim();
  const start = page.getByRole("group", { name: "Начать сессию" });
  await start.getByLabel("Лимит времени").selectOption("open");
  await start.getByRole("button", { name: "Начать сессию" }).click();
  await expect(page.getByRole("button", { name: "Завершить сессию" })).toBeVisible({ timeout: 20_000 });
  await page.getByRole("button", { name: "Завершить сессию" }).click();
  await expect(page.getByTestId("session-history").locator("li").first().locator('[data-state="Ended"]')).toBeVisible({
    timeout: 20_000,
  });

  await page.getByRole("link", { name: "Касса" }).first().click();
  await expect(page.getByRole("heading", { name: "Касса" })).toBeVisible();

  // Смена могла остаться от прошлого прогона — закрываем без расхождения.
  const leftover = page.getByRole("form", { name: "Закрыть смену" });
  if (await leftover.isVisible({ timeout: 3_000 }).catch(() => false)) {
    const expected = minor(await page.getByTestId("expected-cash").innerText());
    await leftover.getByLabel(/Пересчитано наличных/).fill((expected / 100).toFixed(2).replace(".", ","));
    page.once("dialog", (d) => void d.accept());
    await leftover.getByRole("button", { name: "Закрыть смену" }).click();
    await page.getByTestId("shift-closed").getByRole("button", { name: "OK" }).click();
  }

  const open = page.getByRole("form", { name: "Открыть смену" });
  await open.getByLabel(/Наличные в кассе на начало смены/).fill("100");
  await open.getByRole("button", { name: "Открыть смену" }).click();
  await expect(page.getByTestId("shift-open")).toBeVisible();
  await expect(page.getByTestId("expected-cash")).toHaveText(/100,00/);

  // Самая свежая неоплаченная сессия этого ПК — наша.
  const row = page.getByTestId("payable-row").filter({ hasText: deviceName }).first();
  await expect(row).toBeVisible({ timeout: 15_000 });
  const sessionId = await row.getAttribute("data-session");
  const due = minor(await row.getByTestId("due").innerText());
  expect(due).toBeGreaterThan(0);
  await row.getByRole("button", { name: "Наличные" }).click();

  // Оплата прошла: строки больше нет, выручка и наличные выросли на сумму долга.
  await expect(page.locator(`[data-testid="payable-row"][data-session="${sessionId}"]`)).toHaveCount(0);
  await expect.poll(async () => minor(await page.getByTestId("shift-revenue").innerText())).toBe(due);
  const expectedCash = 10_000 + due;
  await expect.poll(async () => minor(await page.getByTestId("expected-cash").innerText())).toBe(expectedCash);
  await expect(page.getByTestId("operations-table")).toContainText(deviceName);

  // Пересчитали на 1,00 меньше: закрытие показывает недостачу.
  const close = page.getByRole("form", { name: "Закрыть смену" });
  await close.getByLabel(/Пересчитано наличных/).fill(((expectedCash - 100) / 100).toFixed(2).replace(".", ","));
  await close.getByLabel("Комментарий").fill("e2e: не хватает 1,00");
  page.once("dialog", (d) => void d.accept());
  await close.getByRole("button", { name: "Закрыть смену" }).click();
  await expect(page.getByTestId("discrepancy")).toContainText("1,00 TJS — недостача");
  await expect(page.getByRole("form", { name: "Открыть смену" })).toBeVisible();
  await expect(page.getByTestId("shift-history")).toContainText("e2e: не хватает 1,00");

  // Отчёт: оплата видна в итогах за сегодня.
  await page.getByRole("link", { name: "Отчёты" }).first().click();
  await expect(page.getByRole("heading", { name: "Выручка" })).toBeVisible();
  await expect(page.getByTestId("revenue-total")).toBeVisible();
  expect(minor(await page.getByTestId("revenue-net").innerText())).toBeGreaterThanOrEqual(due);

  // Оплата записана в аудит.
  await page.getByRole("link", { name: "Журнал аудита" }).first().click();
  await expect(page.getByTestId("audit-table")).toContainText("Оплата сессии");
  await expect(page.getByTestId("audit-table")).toContainText("Закрыта кассовая смена");
});
