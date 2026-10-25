import { expect, test, type Page } from "@playwright/test";

const ownerEmail = process.env.E2E_EMAIL ?? "owner@demo.clubos.local";
const ownerPassword = process.env.E2E_PASSWORD ?? "";

async function login(page: Page, email: string, password: string) {
  await page.goto("/login");
  await page.getByLabel("Email").fill(email);
  await page.getByLabel("Пароль").fill(password);
  await page.getByRole("button", { name: "Войти" }).click();
}

test("владелец создаёт локацию и тариф → переключение локаций → оператор видит только свою", async ({ browser }) => {
  test.skip(!ownerPassword, "Задайте E2E_PASSWORD");

  const owner = await browser.newPage();
  await login(owner, ownerEmail, ownerPassword);
  await expect(owner.getByRole("heading", { name: "Устройства" })).toBeVisible();

  // Новая локация (имя в конце алфавита — по умолчанию остаётся демо-локация).
  const name = `Zz E2E ${Date.now()}`;
  await owner.getByRole("link", { name: "Локации и тарифы" }).first().click();
  const create = owner.getByRole("form", { name: "Новая локация (клуб)" });
  await create.getByLabel("Название").fill(name);
  await create.getByLabel("Цена за час").fill("90");
  await create.getByRole("button", { name: "Создать локацию" }).click();
  await expect(owner.getByRole("status")).toContainText("Локация создана");

  const card = owner.getByTestId("location-card").filter({ has: owner.getByRole("form", { name: `Добавить зону: ${name}` }) });
  await expect(card.getByTestId("zone-row")).toContainText("90,00 TJS");

  // Тариф: 90 → 100 TJS/час.
  await card.getByRole("button", { name: "Изменить" }).click();
  const edit = card.getByRole("form", { name: "Изменить: Standard" });
  await edit.getByLabel("Цена за час, TJS").fill("100");
  await edit.getByRole("button", { name: "Сохранить" }).click();
  await expect(card.getByTestId("zone-row")).toContainText("100,00 TJS");

  // Переключатель локаций в шапке: в новой локации устройств нет.
  const select = owner.getByTestId("location-select");
  await select.selectOption({ label: name });
  await owner.getByRole("link", { name: "Устройства" }).first().click();
  await expect(owner.getByTestId("device-tile")).toHaveCount(0);
  await select.selectOption({ label: "Dushanbe Pilot" });
  await expect(owner.getByTestId("device-tile").first()).toBeVisible();

  // Оператор только для новой локации.
  await owner.getByRole("link", { name: "Персонал" }).first().click();
  const email = `loc-${Date.now()}@club.test`;
  const form = owner.getByRole("form", { name: "Добавить сотрудника" });
  await form.getByLabel("Имя").fill("Оператор локации");
  await form.getByLabel("Email").fill(email);
  await form.getByLabel("Роль").selectOption("Operator");
  await owner.getByTestId("location-access").first().getByLabel("Только выбранные").check();
  await owner.getByTestId("location-access").first().getByLabel(name).check();
  await form.getByRole("button", { name: "Создать" }).click();
  const temp = (await owner.getByTestId("temporary-password").locator("code").innerText()).trim();
  await expect(owner.getByTestId("staff-row").filter({ hasText: email }).getByTestId("staff-locations")).toHaveText(name);

  const context = await browser.newContext();
  const op = await context.newPage();
  await login(op, email, temp);
  const password = "Location-Op-Pass-2026!";
  await op.getByLabel("Текущий пароль").fill(temp);
  await op.getByLabel("Новый пароль", { exact: true }).fill(password);
  await op.getByLabel("Повторите новый пароль").fill(password);
  await op.getByRole("button", { name: "Сменить пароль" }).click();
  await op.getByRole("link", { name: "Устройства" }).first().click();

  // Одна локация — без переключателя, демо-ПК не видны.
  await expect(op.getByTestId("location-select")).toHaveCount(0);
  await expect(op.getByText(name)).toBeVisible();
  await expect(op.getByTestId("device-tile")).toHaveCount(0);
  await context.close();
});
