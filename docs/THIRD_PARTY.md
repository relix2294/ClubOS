# Сторонние зависимости и лицензии (M0)

Все перечисленные пакеты распространяются под разрешительными лицензиями (MIT,
Apache-2.0, PostgreSQL License), совместимыми с проектом. Список отражает прямые
зависимости M0; транзитивные наследуют совместимые лицензии.

## .NET (Cloud API, Edge, Agent, Simulator, тесты)

| Пакет | Назначение | Лицензия |
|-------|-----------|----------|
| Microsoft.EntityFrameworkCore (+ Sqlite) | ORM, миграции | MIT |
| Npgsql.EntityFrameworkCore.PostgreSQL | Провайдер PostgreSQL | PostgreSQL License |
| Microsoft.AspNetCore.Authentication.JwtBearer | JWT-аутентификация | MIT |
| Microsoft.AspNetCore.OpenApi | OpenAPI-документ | MIT |
| Microsoft.Extensions.Hosting(.WindowsServices) | Хостинг / Windows Service | MIT |
| Microsoft.Extensions.Http | HttpClientFactory | MIT |
| System.Management | WMI-инвентаризация (Windows) | MIT |
| Microsoft.NET.Test.Sdk, xunit, xunit.runner.visualstudio | Тесты | MIT / Apache-2.0 |
| Microsoft.AspNetCore.Mvc.Testing | Интеграционные тесты API | MIT |
| Testcontainers.PostgreSql | PostgreSQL в тестах | MIT |

## Frontend (Admin Web)

| Пакет | Назначение | Лицензия |
|-------|-----------|----------|
| next | Фреймворк | MIT |
| react, react-dom | UI | MIT |
| tailwindcss, @tailwindcss/postcss | Стили | MIT |
| typescript | Типы | Apache-2.0 |
| eslint, typescript-eslint, @next/eslint-plugin-next | Линтинг | MIT |

## Инфраструктура

| Компонент | Лицензия |
|-----------|----------|
| PostgreSQL 18 (docker image) | PostgreSQL License |

> Перед релизом M1 сформировать полный SBOM (например, `dotnet list package --include-transitive`
> и `npm ls --all`) и приложить тексты лицензий.
