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

test("тарифы: цена по времени и пакет → сессия по пакету → итог = цена пакета", async ({ page }) => {
  test.skip(!password, "Задайте E2E_PASSWORD");
  await login(page);

  const pkg = `E2E ${String(Date.now()).slice(-6)}`;
  await page.getByRole("link", { name: "Локации и тарифы" }).first().click();
  const card = page.getByTestId("location-card").filter({ has: page.getByRole("form", { name: "Добавить зону: Dushanbe Pilot" }) });
  // Пакет — в каждой зоне локации: ПК симулятора могут быть в любой.
  const zones = card.getByTestId("zone-row");
  await expect(zones.first()).toBeVisible();
  const zoneCount = await zones.count();
  for (let i = 0; i < zoneCount; i++) {
    await zones.nth(i).getByRole("button", { name: "Время и пакеты" }).click();
  }
  const editors = card.getByTestId("zone-tariffs");
  await expect(editors).toHaveCount(zoneCount);
  for (let i = 0; i < zoneCount; i++) {
    const add = editors.nth(i).getByRole("form", { name: /^Добавить пакет:/ });
    await add.getByLabel("Название").fill(pkg);
    await add.getByLabel("Часов").fill("1");
    await add.getByLabel(/^Цена/).fill("50");
    await add.getByRole("button", { name: "Добавить пакет" }).click();
    await expect(editors.nth(i).getByRole("form", { name: `Пакет: ${pkg}` })).toBeVisible();
  }

  // Цена по времени: период с ценой зоны (итоги других тестов не меняются), сохранить и убрать.
  const periods = editors.first().getByRole("form", { name: /^Цена по времени:/ });
  await periods.getByRole("button", { name: "Добавить период" }).click();
  await expect(periods.getByTestId("period-row")).toHaveCount(1);
  await periods.getByRole("button", { name: "Сохранить цены по времени" }).click();
  await expect(periods.getByRole("status")).toContainText("Сохранено");
  await expect(card.getByTestId("zone-row").first()).toContainText("Ежедневно 22:00–08:00");
  await periods.getByRole("button", { name: "Удалить" }).click();
  await periods.getByRole("button", { name: "Сохранить цены по времени" }).click();
  await expect(card.getByTestId("zone-row").first()).not.toContainText("22:00–08:00");

  // Сессия по пакету.
  await page.getByRole("link", { name: "Устройства" }).first().click();
  await page.getByTestId("device-tile").filter({ has: page.locator('[data-status="Idle"]') }).nth(2).click();
  const start = page.getByRole("group", { name: "Начать сессию" });
  const option = start.getByRole("option", { name: new RegExp(pkg) });
  await start.getByLabel("Лимит времени").selectOption((await option.getAttribute("value"))!);
  await start.getByRole("button", { name: "Начать сессию" }).click();
  await expect(page.getByTestId("session-package")).toContainText(pkg, { timeout: 20_000 });
  await expect(page.getByTestId("session-remaining")).toBeVisible({ timeout: 20_000 });
  await page.getByRole("button", { name: "Завершить сессию" }).click();
  const last = page.getByTestId("session-history").locator("li").first();
  await expect(last.locator('[data-state="Ended"]')).toBeVisible({ timeout: 20_000 });
  await expect(last).toContainText(pkg);
  await expect(last).toContainText("50,00 TJS"); // ранний конец — цена пакета

  // Отключаем пакеты, чтобы не копились в списке.
  await page.getByRole("link", { name: "Локации и тарифы" }).first().click();
  for (let i = 0; i < zoneCount; i++) {
    await card.getByTestId("zone-row").nth(i).getByRole("button", { name: "Время и пакеты" }).click();
  }
  const forms = card.getByRole("form", { name: `Пакет: ${pkg}` });
  for (let i = 0; i < zoneCount; i++) {
    await forms.nth(i).getByRole("button", { name: "Отключить" }).click();
    await expect(forms.nth(i).getByRole("button", { name: "Включить" })).toBeVisible();
  }

  await page.getByRole("link", { name: "Журнал аудита" }).first().click();
  await expect(page.getByTestId("audit-table")).toContainText("Добавлен пакет");
});

test("бронирование: бронь на завтра → шкала → отмена; бронь сейчас → начать по брони", async ({ page }) => {
  test.skip(!password, "Задайте E2E_PASSWORD");
  await login(page);
  await page.getByRole("link", { name: "Бронирования" }).first().click();
  await expect(page.getByRole("heading", { name: "Бронирования" })).toBeVisible();

  // Бронь на завтра 18:00 на 2 часа.
  const form = page.getByRole("form", { name: "Новая бронь" });
  const guest = `E2E Гость ${String(Date.now()).slice(-5)}`;
  const tomorrow = await page.evaluate(() => {
    const d = new Date(Date.now() + 5 * 3600_000 + 24 * 3600_000); // Душанбе UTC+5
    return d.toISOString().slice(0, 10);
  });
  await form.getByLabel("Дата").fill(tomorrow);
  await form.getByLabel("Время").fill("18:00");
  await form.getByLabel("Длительность").selectOption("120");
  await form.getByLabel("Имя гостя").fill(guest);
  await form.getByRole("button", { name: "Забронировать" }).click();
  await expect(page.getByTestId("booking-details")).toContainText(guest);
  await expect(page.getByTestId("booking-details")).toContainText("18:00–20:00");
  await expect(page.getByTestId("timeline-booking").filter({ hasText: guest })).toHaveAttribute("data-status", "Booked");

  // Повтор на то же время и ПК — отказ БД.
  await form.getByLabel("Дата").fill(tomorrow);
  await form.getByLabel("Время").fill("19:00");
  await form.getByLabel("Имя гостя").fill("Дубль");
  await form.getByRole("button", { name: "Забронировать" }).click();
  await expect(form.getByRole("alert")).toContainText("уже забронирован");

  // Отмена.
  page.once("dialog", (d) => void d.accept());
  await page.getByRole("button", { name: "Отменить бронь" }).click();
  await expect(page.getByTestId("booking-status")).toHaveText("Отменена");

  // Бронь «сейчас» на свободный ПК → плитка показывает бронь → начать по брони.
  await page.getByRole("link", { name: "Устройства" }).first().click();
  const tile = page.getByTestId("device-tile").filter({ has: page.locator('[data-status="Idle"]') }).nth(1);
  const deviceName = (await tile.getByTestId("device-tile-name").innerText()).trim();
  await page.getByRole("link", { name: "Бронирования" }).first().click();
  const now = await page.evaluate(() => {
    const d = new Date(Date.now() + 5 * 3600_000 + 60_000);
    return { date: d.toISOString().slice(0, 10), time: d.toISOString().slice(11, 16) };
  });
  const nowGuest = `${guest} сейчас`;
  const option = form.getByLabel("ПК").locator("option", { hasText: `${deviceName} ·` }).first();
  await form.getByLabel("ПК").selectOption((await option.getAttribute("value"))!);
  await form.getByLabel("Дата").fill(now.date);
  await form.getByLabel("Время").fill(now.time);
  await form.getByLabel("Длительность").selectOption("60");
  await form.getByLabel("Имя гостя").fill(nowGuest);
  await form.getByRole("button", { name: "Забронировать" }).click();
  await expect(page.getByTestId("booking-details")).toContainText(nowGuest);

  await page.getByRole("link", { name: "Устройства" }).first().click();
  await expect(page.getByTestId("device-tile").filter({ hasText: deviceName }).getByTestId("tile-booking")).toContainText(nowGuest);
  await page.getByRole("link", { name: "Бронирования" }).first().click();
  await page.getByTestId("booking-row").filter({ hasText: nowGuest }).getByRole("button", { name: "Открыть" }).click();
  await page.getByRole("button", { name: "Начать по брони" }).click();
  await expect(page.getByTestId("booking-status")).toHaveText("Гость пришёл");

  // Сессия идёт на этом ПК — завершаем, чтобы ПК был свободен.
  await page.getByRole("link", { name: "Устройства" }).first().click();
  await page.getByTestId("device-tile").filter({ hasText: deviceName }).first().click();
  await expect(page.getByTestId("session-remaining")).toBeVisible({ timeout: 20_000 });
  await page.getByRole("button", { name: "Завершить сессию" }).click();
  await expect(page.getByTestId("session-history").locator("li").first().locator('[data-state="Ended"]')).toBeVisible({ timeout: 20_000 });
});

test("бар: товар и приход → чек наличными → итоги смены → возврат чека", async ({ page }) => {
  test.skip(!password, "Задайте E2E_PASSWORD");
  await login(page);

  // Смена нужна для продажи.
  await page.getByRole("link", { name: "Касса" }).first().click();
  const openForm = page.getByRole("form", { name: "Открыть смену" });
  await expect(page.getByTestId("shift-open").or(openForm)).toBeVisible();
  if (await openForm.isVisible()) {
    await openForm.getByLabel(/Наличные в кассе на начало смены/).fill("0");
    await openForm.getByRole("button", { name: "Открыть смену" }).click();
    await expect(page.getByTestId("shift-open")).toBeVisible();
  }
  const barBefore = minor(await page.getByTestId("bar-sales").innerText());

  // Товар и приход.
  await page.getByRole("link", { name: "Бар" }).first().click();
  await page.getByRole("tab", { name: "Товары и склад" }).click();
  const name = `E2E Кола ${String(Date.now()).slice(-5)}`;
  const create = page.getByRole("form", { name: "Новый товар" });
  await create.getByLabel("Название").fill(name);
  await create.getByLabel("Категория").fill("E2E");
  await create.getByLabel(/^Цена/).fill("12,50");
  await create.getByRole("button", { name: "Добавить товар" }).click();
  const row = page.getByTestId("catalog-row").filter({ hasText: name });
  await expect(row).toBeVisible();
  const stock = row.getByRole("form", { name: `Склад: ${name}` });
  await stock.getByLabel("Количество").fill("5");
  await stock.getByRole("button", { name: "Провести" }).click();
  await expect(row.getByTestId("catalog-stock")).toHaveText("Остаток: 5");

  // Чек: 2 шт. наличными.
  await page.getByRole("tab", { name: "Продажа" }).click();
  const tile = page.getByTestId("bar-product").filter({ hasText: name });
  await tile.click();
  await tile.click();
  await expect(page.getByTestId("cart-total")).toHaveText(/25,00/);
  await page.getByTestId("bar-cart").getByRole("button", { name: "Наличные" }).click();
  await expect(page.getByTestId("sale-done")).toContainText("25,00");
  await expect(tile).toContainText("Остаток: 3");
  const sale = page.getByTestId("sale-row").filter({ hasText: name }).first();
  await expect(sale).toHaveAttribute("data-status", "Paid");

  // Касса: бар в итогах смены.
  await page.getByRole("link", { name: "Касса" }).first().click();
  await expect.poll(async () => minor(await page.getByTestId("bar-sales").innerText())).toBe(barBefore + 2_500);
  await expect(page.getByTestId("operations-table")).toContainText(`${name} × 2`);

  // Возврат чека целиком.
  await page.getByRole("link", { name: "Бар" }).first().click();
  await sale.getByRole("button", { name: "Вернуть" }).click();
  await sale.getByLabel("Причина возврата").fill("e2e: возврат");
  await sale.getByRole("button", { name: "Вернуть чек" }).click();
  await expect(sale).toHaveAttribute("data-status", "Refunded");
  await expect(tile).toContainText("Остаток: 5");

  // Снять тестовый товар с продажи.
  await page.getByRole("tab", { name: "Товары и склад" }).click();
  await row.getByRole("button", { name: "Снять с продажи" }).click();
  await expect(row.getByRole("button", { name: "Вернуть в продажу" })).toBeVisible();
});
