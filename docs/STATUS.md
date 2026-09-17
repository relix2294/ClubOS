# STATUS — ClubOS CA, Milestone 0

**Обновлено:** 2026-09-17
**Фаза:** Реализация M0 начата. Решение по среде: код пишется как готовый к сборке срез, проверка на CI/другой машине (.NET 10 + Docker + Windows). Локально (macOS, .NET 2.1) сборка не выполняется — помечается «не собрано здесь».

---

## 1. Что реально работает сейчас

Пока ничего исполняемого. Создана только структура репозитория и документация
подготовительного прохода. Никакой код ещё не написан и не собран. Это состояние
честно отражает ТЗ §0: «Не выдавать mock/заглушку за завершённую функцию».

## 2. Блокеры среды (реальные, требуют решения)

Проверка окружения на машине разработки (macOS, arm64) выявила блокеры сборки и
приёмки. По правилам стартового промпта (§2.6) о них сообщается явно.

| # | Зависимость | Требуется (ТЗ §7.1, §25.2) | Фактически | Влияние |
|---|-------------|----------------------------|-----------|---------|
| B1 | .NET SDK | **10 LTS** | только **2.1.818** (EOL) | Cloud API, Edge Controller, Windows Agent, Device Simulator не собираются |
| B2 | Docker + compose | нужен для PostgreSQL и `docker compose up` (§25.2, §25.3) | **не установлен** | Приёмочный сценарий шага 1 невыполним локально |
| B3 | ОС Windows | реальный тест Windows Service (§25.3 шаг 2) | macOS arm64 | Agent-служба не может быть проверена здесь — по ТЗ §3.3 это отдельный тест на реальном Windows ПК; код обязан компилироваться на Windows CI runner |

B3 — ожидаемый и предусмотренный ТЗ блокер (не маскируется, инструкция для Windows-теста
будет в `docs/runbooks/`). B1 и B2 — блокеры «обязательной зависимости», требующие решения
до заявления любого шага приёмки выполненным.

## 3. Прогресс по компонентам M0

| Компонент | Статус | Примечание |
|-----------|--------|-----------|
| Monorepo структура | ✅ создана | §7.3 |
| docs (STATUS/DEVIATIONS/ADR/план) | ✅ готово | подготовительный проход |
| Контракты C# + TS | ✅ написано | envelope/DTO/enums, Money, BillingCalculator; сборка на CI |
| Unit-тесты тарифа | ✅ написано | эталон §12.4; прогон на CI |
| CI (GitHub Actions) | 🟡 базовый | dotnet build+test; Windows-джоб ждёт Agent |
| Cloud API | ⬜ следующий | нужен .NET 10 (B1) |
| Edge Controller | ⬜ не начато | нужен .NET 10 (B1) |
| Windows Agent | ⬜ не начато | .NET 10 (B1) + Windows-тест (B3) |
| Device Simulator | ⬜ не начато | нужен .NET 10 (B1) |
| Admin Web | ⬜ не начато | Node есть — можно начинать |
| docker-compose (PostgreSQL) | ⬜ не начато | нужен Docker (B2) |
| Автотесты M0 (§5) | ⬜ не начато | зависят от B1/B2 |

## 4. Команды проверки (зафиксированы, будут работать после снятия B1/B2)

Backend (после установки .NET 10):
```bash
dotnet restore ClubOS.sln
dotnet build ClubOS.sln -c Release
dotnet test tests/unit
dotnet test tests/integration   # требует PostgreSQL (Testcontainers/Docker, B2)
```

Frontend:
```bash
cd apps/admin-web && npm install && npm run lint && npm run build
npx playwright test                # smoke: login → devices → карточка
```

Инфраструктура:
```bash
docker compose up -d               # PostgreSQL + dev cloud deps (B2)
```

Windows Agent (только на реальном Windows ПК, см. runbook):
```powershell
dotnet build services\windows-agent -c Release
# установка службы и ручная проверка — по docs/runbooks/windows-agent-install.md
```

## 5. Следующий шаг

Ожидается решение пользователя по среде (см. вопрос в чате): установить локально
.NET 10 + Docker и собирать/тестировать здесь, либо писать M0 как готовый к сборке
срез с проверкой на другой машине/CI. После этого — реализация Cloud API и контрактов.
