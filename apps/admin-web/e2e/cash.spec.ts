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

  // Смена могла остаться от прошлого прогона — закрываем без расхождения. Сначала дождаться загрузки кассы:
  // isVisible() не ждёт.
  const leftover = page.getByRole("form", { name: "Закрыть смену" });
  await expect(leftover.or(page.getByRole("form", { name: "Открыть смену" }))).toBeVisible();
  if (await leftover.isVisible()) {
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

// В том же файле, что и касса: тесты файла идут последовательно и не делят открытую смену с параллельными.
test("клиенты: регистрация → пополнение в кассе → сессия на клиента → оплата с баланса → журнал", async ({ page }) => {
  test.skip(!password, "Задайте E2E_PASSWORD");
  await login(page);

  // Смена открыта (или открываем): пополнение баланса — кассовая операция.
  await page.getByRole("link", { name: "Касса" }).first().click();
  const openForm = page.getByRole("form", { name: "Открыть смену" });
  await expect(page.getByTestId("shift-open").or(openForm)).toBeVisible();
  if (await openForm.isVisible()) {
    await openForm.getByLabel(/Наличные в кассе на начало смены/).fill("0");
    await openForm.getByRole("button", { name: "Открыть смену" }).click();
    await expect(page.getByTestId("shift-open")).toBeVisible();
  }
  const cashBefore = minor(await page.getByTestId("expected-cash").innerText());

  // Новый клиент с уникальным телефоном.
  await page.getByRole("link", { name: "Клиенты" }).first().click();
  await expect(page.getByRole("heading", { name: "Клиенты" })).toBeVisible();
  const phone = `99290${String(Date.now()).slice(-7)}`;
  const name = `E2E Клиент ${phone.slice(-4)}`;
  const create = page.getByRole("form", { name: "Новый клиент" });
  await create.getByLabel("Телефон").fill(`+${phone}`);
  await create.getByLabel("Имя").fill(name);
  await create.getByRole("button", { name: "Добавить клиента" }).click();
  const card = page.getByTestId("client-card");
  await expect(card).toContainText(name);
  await expect(page.getByTestId("client-balance")).toHaveText(/0,00/);

  // Повтор телефона — отказ.
  await create.getByLabel("Телефон").fill(phone);
  await create.getByLabel("Имя").fill("Дубль");
  await create.getByRole("button", { name: "Добавить клиента" }).click();
  await expect(create.getByRole("alert")).toContainText("уже есть");

  // Пополнение 50,00 наличными.
  const topUp = page.getByRole("form", { name: "Пополнить баланс" });
  await topUp.getByLabel(/Сумма/).fill("50");
  await topUp.getByRole("button", { name: "Наличными" }).click();
  await expect(topUp.getByRole("status")).toContainText("+50,00");
  await expect(page.getByTestId("client-balance")).toHaveText(/50,00/);
  await expect(page.getByTestId("client-ledger")).toContainText("Пополнение");

  // Сессия на клиента на свободном ПК.
  await page.getByRole("link", { name: "Устройства" }).first().click();
  await page.getByTestId("device-tile").filter({ has: page.locator('[data-status="Idle"]') }).nth(3).click();
  const deviceName = (await page.getByTestId("device-name").innerText()).trim();
  const start = page.getByRole("group", { name: "Начать сессию" });
  await start.getByLabel("Лимит времени").selectOption("30");
  await start.getByLabel("Поиск: телефон или имя").fill(phone.slice(-7));
  await start.getByTestId("client-results").getByRole("button", { name: new RegExp(name) }).click();
  await expect(start.getByTestId("picked-client")).toContainText(name);
  await start.getByRole("button", { name: "Начать сессию" }).click();
  await expect(page.getByRole("button", { name: "Завершить сессию" })).toBeVisible({ timeout: 20_000 });

  // Касса: строка с клиентом, оплата с баланса (предоплата 30 мин по тарифу зоны).
  await page.getByRole("link", { name: "Касса" }).first().click();
  const row = page.getByTestId("payable-row").filter({ hasText: deviceName }).filter({ hasText: name }).first();
  await expect(row).toBeVisible({ timeout: 15_000 });
  const due = minor(await row.getByTestId("due").innerText());
  expect(due).toBeGreaterThan(0);
  // Баланса может не хватить на всю сумму: платим сколько есть, остаток — наличными.
  const fromBalance = Math.min(due, 5_000);
  await row.getByLabel("Сумма").fill((fromBalance / 100).toFixed(2).replace(".", ","));
  await row.getByRole("button", { name: "С баланса" }).click();
  if (fromBalance < due) {
    await expect(row.getByTestId("due")).toHaveText(new RegExp(((due - fromBalance) / 100).toFixed(2).replace(".", ",")));
    await row.getByRole("button", { name: "Наличные" }).click();
  }
  await expect(page.getByTestId("payable-row").filter({ hasText: name })).toHaveCount(0);
  // Наличные в кассе: +50,00 пополнения (+ доплата наличными); оплата с баланса кассу не трогает.
  await expect
    .poll(async () => minor(await page.getByTestId("expected-cash").innerText()))
    .toBe(cashBefore + 5_000 + (due - fromBalance));
  expect(minor(await page.getByTestId("balance-payments").innerText())).toBeGreaterThanOrEqual(fromBalance);
  await expect(page.getByTestId("operations-table")).toContainText("Пополнение баланса");

  // Журнал клиента: оплата сессии, остаток уменьшился.
  await page.getByRole("link", { name: "Клиенты" }).first().click();
  await page.getByLabel("Поиск: телефон или имя").fill(phone);
  await page.getByTestId("clients-list").getByRole("button", { name: new RegExp(name) }).click();
  await expect(page.getByTestId("client-ledger")).toContainText("Оплата сессии");
  await expect(page.getByTestId("client-balance")).toHaveText(new RegExp(((5_000 - fromBalance) / 100).toFixed(2).replace(".", ",")));

  // Сессию завершаем, чтобы ПК был свободен для следующих прогонов.
  await page.getByRole("link", { name: "Устройства" }).first().click();
  await page.getByTestId("device-tile").filter({ hasText: deviceName }).first().click();
  await page.getByRole("button", { name: "Завершить сессию" }).click();
  await expect(page.getByTestId("session-history").locator("li").first().locator('[data-state="Ended"]')).toBeVisible({
    timeout: 20_000,
  });

  await page.getByRole("link", { name: "Журнал аудита" }).first().click();
  await expect(page.getByTestId("audit-table")).toContainText("Пополнен баланс клиента");
});
