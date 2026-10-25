import { expect, test, type Page } from "@playwright/test";

const ownerEmail = process.env.E2E_EMAIL ?? "owner@demo.clubos.local";
const ownerPassword = process.env.E2E_PASSWORD ?? "";

async function login(page: Page, email: string, password: string) {
  await page.goto("/login");
  await page.getByLabel("Email").fill(email);
  await page.getByLabel("Пароль").fill(password);
  await page.getByRole("button", { name: "Войти" }).click();
}

test("владелец добавляет оператора → вход по временному паролю → смена пароля → ограниченные права", async ({ browser }) => {
  test.skip(!ownerPassword, "Задайте E2E_PASSWORD");

  const owner = await browser.newPage();
  await login(owner, ownerEmail, ownerPassword);
  await expect(owner.getByRole("heading", { name: "Устройства" })).toBeVisible();

  await owner.getByRole("link", { name: "Персонал" }).first().click();
  await expect(owner.getByRole("heading", { name: "Персонал" })).toBeVisible();

  const email = `op-${Date.now()}@club.test`;
  const form = owner.getByRole("form", { name: "Добавить сотрудника" });
  await form.getByLabel("Имя").fill("Оператор E2E");
  await form.getByLabel("Email").fill(email);
  await form.getByLabel("Роль").selectOption("Operator");
  await form.getByRole("button", { name: "Создать" }).click();

  const temp = (await owner.getByTestId("temporary-password").locator("code").innerText()).trim();
  expect(temp.length).toBeGreaterThanOrEqual(10);
  await expect(owner.getByTestId("staff-row").filter({ hasText: email })).toContainText("временный пароль");

  // Оператор: первый вход — только смена пароля.
  const context = await browser.newContext();
  const op = await context.newPage();
  await login(op, email, temp);
  await expect(op.getByRole("heading", { name: "Мой пароль" })).toBeVisible();
  await expect(op.getByRole("alert").filter({ hasText: "временному паролю" })).toBeVisible();

  const newPassword = "Operator-Pass-2026!";
  await op.getByLabel("Текущий пароль").fill(temp);
  await op.getByLabel("Новый пароль", { exact: true }).fill(newPassword);
  await op.getByLabel("Повторите новый пароль").fill(newPassword);
  await op.getByRole("button", { name: "Сменить пароль" }).click();
  await expect(op.getByRole("status")).toContainText("Пароль изменён");

  // Оператор видит устройства, но не персонал и не подключение.
  await op.getByRole("link", { name: "Устройства" }).first().click();
  await expect(op.getByRole("heading", { name: "Устройства" })).toBeVisible();
  await expect(op.getByRole("link", { name: "Персонал" })).toHaveCount(0);
  await expect(op.getByRole("link", { name: "Подключение" })).toHaveCount(0);

  // Владелец отключает оператора — доступ пропадает сразу.
  await owner.reload();
  owner.once("dialog", (d) => d.accept());
  await owner.getByTestId("staff-row").filter({ hasText: email }).getByRole("button", { name: "Отключить" }).click();
  await expect(owner.getByTestId("staff-row").filter({ hasText: email })).toContainText("Отключён");

  await op.reload();
  await expect(op).toHaveURL(/\/login/);
  await context.close();
});
