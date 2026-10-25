namespace ClubOS.EdgeController.Api;

/// <summary>Страница кассы Edge (D-023): без внешних ресурсов — работает в сети клуба без интернета.</summary>
internal static class OfflineCashPage
{
    public const string Html = """
<!doctype html>
<html lang="ru">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>ClubOS — касса клуба без интернета</title>
<style>
  :root { color-scheme: light; --brand: #2f6fed; --bad: #b91c1c; --ok: #047857; --muted: #64748b; }
  * { box-sizing: border-box; }
  body { margin: 0; font: 15px/1.4 system-ui, -apple-system, "Segoe UI", sans-serif; background: #f1f5f9; color: #0f172a; }
  header { background: #0f172a; color: #e2e8f0; padding: 12px 16px; display: flex; gap: 12px; align-items: center; flex-wrap: wrap; }
  header h1 { font-size: 17px; margin: 0; }
  header .who { margin-left: auto; font-size: 14px; }
  main { max-width: 980px; margin: 0 auto; padding: 16px; display: flex; flex-direction: column; gap: 16px; }
  section { background: #fff; border: 1px solid #e2e8f0; border-radius: 12px; padding: 16px; }
  h2 { font-size: 15px; margin: 0 0 12px; }
  table { width: 100%; border-collapse: collapse; }
  th { text-align: left; font-size: 12px; text-transform: uppercase; color: var(--muted); padding: 6px 8px 6px 0; }
  td { padding: 8px 8px 8px 0; border-top: 1px solid #f1f5f9; vertical-align: middle; }
  .num { text-align: right; font-variant-numeric: tabular-nums; white-space: nowrap; }
  .due { color: var(--bad); font-weight: 600; }
  input, select { font: inherit; padding: 8px 10px; border: 1px solid #cbd5e1; border-radius: 8px; background: #fff; }
  input.amount { width: 96px; text-align: right; }
  button { font: inherit; padding: 8px 14px; border-radius: 8px; border: 1px solid #cbd5e1; background: #fff; cursor: pointer; }
  button.primary { background: var(--brand); border-color: var(--brand); color: #fff; }
  button:disabled { opacity: .5; cursor: not-allowed; }
  .row { display: flex; gap: 8px; align-items: center; flex-wrap: wrap; }
  .banner { padding: 10px 14px; border-radius: 10px; font-size: 14px; }
  .banner.warn { background: #fef3c7; color: #92400e; }
  .banner.ok { background: #d1fae5; color: #065f46; }
  .error { color: var(--bad); font-size: 14px; min-height: 1em; }
  .muted { color: var(--muted); font-size: 13px; }
  .login { max-width: 360px; margin: 48px auto; }
  .login label { display: block; font-size: 13px; color: #334155; margin: 12px 0 4px; }
  .login input, .login select { width: 100%; }
  .login button { width: 100%; margin-top: 16px; }
</style>
</head>
<body>
<header><h1>ClubOS · касса клуба</h1><span id="location" class="muted"></span><span class="who" id="who"></span></header>
<main id="app"></main>
<script>
"use strict";
const app = document.getElementById("app");
const H = { "content-type": "application/json", "X-ClubOS-Cash": "1" };
const money = (m, cur) => (m / 100).toFixed(2).replace(".", ",") + (cur ? " " + cur : "");
const time = (iso) => iso ? new Date(iso).toLocaleString("ru-RU", { day: "2-digit", month: "2-digit", hour: "2-digit", minute: "2-digit" }) : "—";
const key = () => (crypto.randomUUID ? crypto.randomUUID() : Date.now().toString(36) + Math.random().toString(36).slice(2));
const el = (tag, attrs = {}, ...kids) => { const e = document.createElement(tag); for (const [k, v] of Object.entries(attrs)) { if (k === "on") for (const [ev, fn] of Object.entries(v)) e.addEventListener(ev, fn); else if (v !== undefined && v !== null) e.setAttribute(k, v); } for (const k of kids) e.append(k); return e; };
const parseMoney = (t) => { const n = t.trim().replace(/\s/g, "").replace(",", "."); if (!/^\d{1,7}(\.\d{1,2})?$/.test(n)) return null; const [w, f = ""] = n.split("."); return Number(w) * 100 + Number(f.padEnd(2, "0")); };
let timer;

async function api(path, init) {
  const r = await fetch("/cash/api/" + path, { credentials: "same-origin", ...init });
  const text = await r.text();
  const data = text ? JSON.parse(text) : null;
  if (!r.ok) { const e = new Error((data && data.detail) || "Ошибка " + r.status); e.status = r.status; throw e; }
  return data;
}

async function start() {
  try { const me = await api("me"); showMain(me); } catch { showLogin(); }
}

async function showLogin() {
  clearInterval(timer);
  document.getElementById("who").textContent = "";
  let staff = [];
  try { staff = await api("staff"); } catch {}
  const error = el("p", { class: "error", role: "alert" });
  const select = el("select", { id: "user", "aria-label": "Сотрудник" }, ...staff.map(s => el("option", { value: s.userId }, s.displayName)));
  const pin = el("input", { id: "pin", type: "password", inputmode: "numeric", autocomplete: "off", maxlength: "8", "aria-label": "PIN" });
  const form = el("form", { class: "login", "aria-label": "Вход в кассу" },
    el("section", {},
      el("h2", {}, "Вход в кассу клуба"),
      el("p", { class: "muted" }, staff.length ? "PIN офлайн-кассы задаётся в Admin Web → «Мой пароль»." : "Нет сотрудников с PIN офлайн-кассы. Задайте PIN в Admin Web → «Мой пароль», пока есть интернет."),
      el("label", { for: "user" }, "Сотрудник"), select,
      el("label", { for: "pin" }, "PIN"), pin,
      el("button", { class: "primary", type: "submit" }, "Войти"), error));
  form.addEventListener("submit", async (e) => {
    e.preventDefault();
    error.textContent = "";
    try { const me = await api("login", { method: "POST", headers: H, body: JSON.stringify({ userId: select.value, pin: pin.value }) }); showMain(me); }
    catch (err) { error.textContent = err.message; pin.value = ""; }
  });
  app.replaceChildren(form);
  (staff.length ? pin : select).focus();
}

function showMain(me) {
  const logout = el("button", { on: { click: async () => { await api("logout", { method: "POST", headers: H }); showLogin(); } } }, "Выйти");
  document.getElementById("who").replaceChildren(me.displayName + " ", logout);
  const state = el("div");
  app.replaceChildren(state);
  const load = async () => {
    try { render(state, await api("payable")); }
    catch (err) { if (err.status === 401) showLogin(); else state.prepend(el("p", { class: "error" }, err.message)); }
  };
  clearInterval(timer);
  timer = setInterval(load, 10000);
  load();
}

function render(root, data) {
  document.getElementById("location").textContent = data.location || "";
  const cur = data.currency || "";
  const banner = data.pendingEvents > 0
    ? el("div", { class: "banner warn", role: "status" }, `Нет связи с облаком или идёт отправка: ${data.pendingEvents} событий ждут отправки. Оплаты сохранены на сервере клуба и уйдут сами.`)
    : el("div", { class: "banner ok", role: "status" }, "Все оплаты отправлены в облако.");
  const rows = data.payable.map(p => {
    const amount = el("input", { class: "amount", inputmode: "decimal", value: money(p.dueMinorUnits), "aria-label": "Сумма" });
    const err = el("div", { class: "error" });
    const k = key();
    const pay = async (method, buttons) => {
      const minor = parseMoney(amount.value);
      if (minor === null || minor <= 0) { err.textContent = "Сумма, например 25 или 25,50."; return; }
      buttons.forEach(b => b.disabled = true);
      try { await api("payments", { method: "POST", headers: H, body: JSON.stringify({ sessionId: p.sessionId, amountMinorUnits: minor, method, idempotencyKey: k }) }); render(root, await api("payable")); }
      catch (e) { err.textContent = e.message; buttons.forEach(b => b.disabled = false); }
    };
    const cash = el("button", { class: "primary" }, "Наличные");
    const card = el("button", {}, "Карта");
    cash.addEventListener("click", () => pay("Cash", [cash, card]));
    card.addEventListener("click", () => pay("Card", [cash, card]));
    return el("tr", { "data-session": p.sessionId },
      el("td", {}, el("strong", {}, p.deviceName), el("div", { class: "muted" }, p.state === "Ended" ? "завершена " + time(p.endedAtUtc) : "идёт, предоплата до " + time(p.plannedEndAtUtc))),
      el("td", { class: "num" }, money(p.chargeMinorUnits, cur)),
      el("td", { class: "num" }, money(p.cloudPaidMinorUnits + p.offlinePaidMinorUnits, cur)),
      el("td", { class: "num due" }, money(p.dueMinorUnits, cur)),
      el("td", {}, el("div", { class: "row" }, amount, cash, card), err));
  });
  const payable = el("section", {}, el("h2", {}, "К оплате"),
    rows.length ? el("table", {}, el("thead", {}, el("tr", {}, el("th", {}, "ПК"), el("th", { class: "num" }, "Начислено"), el("th", { class: "num" }, "Оплачено"), el("th", { class: "num" }, "Долг"), el("th", {}, ""))), el("tbody", {}, ...rows))
      : el("p", { class: "muted" }, "Долгов по сессиям нет."));
  const pays = el("section", {}, el("h2", {}, "Принято на этой кассе"),
    data.payments.length ? el("table", {}, el("tbody", {}, ...data.payments.map(x => el("tr", {},
      el("td", {}, time(x.recordedAtUtc)), el("td", {}, x.deviceName), el("td", {}, x.method === "Cash" ? "Наличные" : "Карта"),
      el("td", { class: "num" }, money(x.amountMinorUnits, cur)), el("td", {}, x.userName),
      el("td", { class: "muted" }, x.sent ? "в облаке" : "ждёт отправки")))))
      : el("p", { class: "muted" }, "Оплат на кассе Edge ещё не было."));
  const hint = el("p", { class: "muted" }, "Касса клуба работает и без интернета: долг считается по данным сервера клуба и оплатам, известным облаку. Возвраты, пополнения балансов и бар — только в Admin Web.");
  root.replaceChildren(banner, payable, pays, hint);
}

start();
</script>
</body>
</html>
""";
}
