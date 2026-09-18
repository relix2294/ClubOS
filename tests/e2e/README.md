# E2E smoke (Playwright)

Проверяет сквозной путь Admin Web: login → дашборд → карточка устройства.

## Предпосылки

Запущены и доступны:
- Cloud API (`http://localhost:5000`) с выполненным seed;
- Admin Web (`http://localhost:3000`), `NEXT_PUBLIC_API_URL` указывает на Cloud;
- в системе есть хотя бы одно устройство (например, через Device Simulator или enrollment).

## Запуск

```bash
cd tests/e2e
npm install
npx playwright install chromium   # локально; в этой среде Chromium предустановлен
E2E_OWNER_PASSWORD=<пароль владельца> npx playwright test
```

Переменные окружения:
- `E2E_BASE_URL` — URL Admin Web (по умолчанию `http://localhost:3000`);
- `E2E_OWNER_EMAIL` / `E2E_OWNER_PASSWORD` — учётные данные dev-владельца.

> В CI не подключён: требует одновременного запуска Cloud API + Admin Web + БД.
> Кандидат на добавление отдельным job'ом в Слое 6+/M1.
