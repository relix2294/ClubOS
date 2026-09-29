import { expect, test, type Page } from "@playwright/test";
import { totp } from "./totp";

const ownerEmail = process.env.E2E_EMAIL ?? "owner@demo.clubos.local";
const ownerPassword = process.env.E2E_PASSWORD ?? "";

async function login(page: Page, email: string, password: string) {
  await page.goto("/login");
  await page.getByLabel("Email").fill(email);
  await page.getByLabel("Пароль").fill(password);
  await page.getByRole("button", { name: "Войти" }).click();
}

test("2FA: администратор включает TOTP → вход с кодом → владелец сбрасывает", async ({ browser }) => {
  test.skip(!ownerPassword, "Задайте E2E_PASSWORD");

  // Владелец создаёт администратора.
  const owner = await browser.newPage();
  await login(owner, ownerEmail, ownerPassword);
  await expect(owner.getByRole("heading", { name: "Устройства" })).toBeVisible();
  await owner.getByRole("link", { name: "Персонал" }).first().click();
  const email = `adm-${Date.now()}@club.test`;
  const form = owner.getByRole("form", { name: "Добавить сотрудника" });
  await form.getByLabel("Имя").fill("Админ 2FA");
  await form.getByLabel("Email").fill(email);
  await form.getByLabel("Роль").selectOption("Admin");
  await form.getByRole("button", { name: "Создать" }).click();
  const temp = (await owner.getByTestId("temporary-password").locator("code").innerText()).trim();

  // Администратор: смена временного пароля, затем настройка 2FA.
  const context = await browser.newContext();
  const admin = await context.newPage();
  await login(admin, email, temp);
  const password = "Admin-2FA-Pass-2026!";
  await admin.getByLabel("Текущий пароль").fill(temp);
  await admin.getByLabel("Новый пароль", { exact: true }).fill(password);
  await admin.getByLabel("Повторите новый пароль").fill(password);
  await admin.getByRole("button", { name: "Сменить пароль" }).click();
  await expect(admin.getByTestId("mfa-status")).toHaveText("Выключена");

  await admin.getByRole("button", { name: "Настроить 2FA" }).click();
  await expect(admin.getByRole("img", { name: /QR-код/ })).toBeVisible();
  const secret = (await admin.getByTestId("mfa-secret").innerText()).trim();
  await admin.getByRole("form", { name: "Настроить 2FA" }).getByLabel("Код из приложения").fill(totp(secret));
  await admin.getByRole("button", { name: "Включить 2FA" }).click();

  const codes = admin.getByTestId("recovery-codes").locator("li");
  await expect(codes).toHaveCount(10);
  await admin.getByRole("button", { name: "Я сохранил коды" }).click();
  await expect(admin.getByTestId("mfa-status")).toHaveText("Включена");

  // Новый вход: пароль → код. Код текущего шага уже использован — берём следующий (часы телефона ±30 с).
  await admin.getByRole("button", { name: "Выйти" }).click();
  await login(admin, email, password);
  await expect(admin.getByRole("heading", { name: "Подтверждение входа" })).toBeVisible();
  await admin.getByLabel("Код из приложения").fill("000000" === totp(secret, 1) ? "111111" : "000000");
  await admin.getByRole("button", { name: "Подтвердить" }).click();
  await expect(admin.getByRole("form", { name: "Подтверждение входа" }).getByRole("alert")).toContainText("Неверный код");
  await admin.getByLabel("Код из приложения").fill(totp(secret, 1));
  await admin.getByRole("button", { name: "Подтвердить" }).click();
  await expect(admin.getByRole("heading", { name: "Устройства" })).toBeVisible();

  // Владелец видит 2FA у сотрудника и сбрасывает её.
  await owner.reload();
  const row = owner.getByTestId("staff-row").filter({ hasText: email });
  await expect(row.getByTestId("staff-mfa")).toBeVisible();
  owner.once("dialog", (d) => d.accept());
  await row.getByRole("button", { name: "Сбросить 2FA" }).click();
  await expect(row.getByTestId("staff-mfa")).toHaveCount(0);

  // Сессии администратора завершены сбросом.
  await admin.reload();
  await expect(admin).toHaveURL(/\/login/);
  await context.close();
});
