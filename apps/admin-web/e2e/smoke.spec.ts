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

test.beforeAll(() => {
  if (!password) throw new Error("Задайте E2E_PASSWORD (пароль dev Owner, как CLUBOS_Seed__OwnerPassword).");
});

test("неверный пароль не пускает в панель", async ({ page }) => {
  await page.goto("/login");
  await page.getByLabel("Email").fill(email);
  await page.getByLabel("Пароль").fill("wrong-password-123");
  await page.getByRole("button", { name: "Войти" }).click();
  await expect(page.getByRole("form").getByRole("alert")).toContainText("Неверный email или пароль");
});

test("login → устройства → карточка устройства", async ({ page }) => {
  await login(page);

  const tiles = page.getByTestId("device-tile");
  await expect(tiles.first()).toBeVisible();
  await expect(page.getByTestId("edge-status").first()).toBeVisible();

  // Статус показан текстом + иконкой, не только цветом.
  await expect(tiles.first().locator("[data-status]")).toHaveText(/Свободен|Сессия|Не в сети|Заблокирован/);

  await tiles.first().click();
  await expect(page.getByTestId("device-name")).toBeVisible();
  const inventory = page.getByTestId("inventory");
  await expect(inventory).toContainText("Имя ПК");
  await expect(inventory).toContainText("IPv4");
  await expect(inventory).toContainText("Последний heartbeat");
  await expect(inventory).toContainText("Сертификат до");
});

test("ShowMessage проходит жизненный цикл до «Выполнена»", async ({ page }) => {
  await login(page);
  // Берём устройство в сети.
  await page.getByTestId("device-tile").filter({ has: page.locator('[data-status="Idle"]') }).first().click();

  const text = `E2E ${Date.now()}`;
  await page.getByLabel("Текст сообщения").fill(text);
  await page.getByRole("button", { name: "Отправить" }).click();

  const first = page.getByTestId("command-list").locator("li").first();
  await expect(first.locator('[data-state="Succeeded"]')).toBeVisible();
  await expect(page.getByTestId("device-audit")).toContainText("Команда: сообщение");
});
