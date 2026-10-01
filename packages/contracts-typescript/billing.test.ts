// Зеркало эталонов BillingCalculatorTests (C#): TS-расчёт для предварительной стоимости в Admin Web
// обязан совпадать с Edge. Запуск: node --experimental-strip-types --test packages/contracts-typescript/
import assert from "node:assert/strict";
import { test } from "node:test";
import { calculateMinorUnits, calculateSessionMinorUnits, dayBit, pricePerHourAt, type PricePeriod, type SessionPricing } from "./index.ts";

const flat: SessionPricing = { pricePerHourMinorUnits: 12_000 };
const nightPeriod = (days = 127): PricePeriod => ({ days, startMinute: 22 * 60, endMinute: 8 * 60, pricePerHourMinorUnits: 6_000 });
const night = (days = 127): SessionPricing => ({ ...flat, utcOffsetMinutes: 300, periods: [nightPeriod(days)] });
/** Местное время Душанбе (UTC+5) → мс UTC. */
const local = (y: number, m: number, d: number, h: number, min: number, s = 0) => Date.UTC(y, m - 1, d, h - 5, min, s);
const minutes = (n: number) => n * 60_000;

test("эталон M0: 60 с = 2,00; 61 с = 4,00; 30 мин = 60,00", () => {
  for (const [seconds, expected] of [
    [60, 200],
    [61, 400],
    [1800, 6000],
    [1, 200],
    [0, 0],
  ]) {
    assert.equal(calculateSessionMinorUnits(flat, local(2026, 10, 1, 3, 0), seconds * 1000), expected);
    assert.equal(calculateMinorUnits(12_000, seconds * 1000), expected);
  }
});

test("переход в ночной тариф делит минуты", () => {
  assert.equal(calculateSessionMinorUnits(night(), local(2026, 10, 1, 21, 50), minutes(20)), 3_000);
  assert.equal(calculateSessionMinorUnits(night(), local(2026, 10, 2, 7, 30), minutes(60)), 9_000);
  assert.equal(calculateSessionMinorUnits(night(), local(2026, 10, 1, 21, 59, 30), 61_000), (12_000 + 6_000) / 60);
  assert.equal(calculateSessionMinorUnits(night(), local(2026, 10, 1, 8, 0), minutes(24 * 60)), 228_000);
});

test("ночь пятницы продолжается в субботу утром", () => {
  const friday = night(dayBit(5));
  assert.equal(pricePerHourAt(friday, local(2026, 10, 3, 3, 0)), 6_000);
  assert.equal(pricePerHourAt(friday, local(2026, 10, 2, 3, 0)), 12_000);
  assert.equal(pricePerHourAt(friday, local(2026, 10, 2, 23, 0)), 6_000);
  assert.equal(pricePerHourAt(friday, local(2026, 10, 3, 23, 0)), 12_000);
});

test("пакет: цена за первые минуты, сверх — по тарифу", () => {
  const pkg: SessionPricing = { ...flat, packageMinutes: 180, packagePriceMinorUnits: 25_000 };
  assert.equal(calculateSessionMinorUnits(pkg, 0, 0), 25_000);
  assert.equal(calculateSessionMinorUnits(pkg, 0, minutes(60)), 25_000);
  assert.equal(calculateSessionMinorUnits(pkg, 0, minutes(200)), 29_000);
  assert.equal(calculateSessionMinorUnits(pkg, 0, minutes(180) + 1000), 25_200);
  const nightPkg: SessionPricing = { ...night(), packageMinutes: 60, packagePriceMinorUnits: 10_000 };
  assert.equal(calculateSessionMinorUnits(nightPkg, local(2026, 10, 1, 21, 0), minutes(90)), 13_000);
});
