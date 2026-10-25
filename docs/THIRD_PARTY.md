# Сторонние компоненты и лицензии (M0)

Все зависимости — открытые лицензии, совместимые с коммерческим использованием. Версии зафиксированы
в `Directory.Packages.props` (.NET) и `apps/admin-web/package-lock.json` (npm).

## Backend (.NET 10)

| Компонент | Версия | Лицензия | Где используется |
|-----------|--------|----------|------------------|
| .NET 10 / ASP.NET Core (runtime, BCL, System.Text.Json, криптография) | 10.0 | MIT | все сервисы |
| Microsoft.EntityFrameworkCore (+Design, Relational) | 10.0.12 | MIT | Cloud API |
| Npgsql.EntityFrameworkCore.PostgreSQL | 10.0.3 | PostgreSQL License | Cloud API |
| Microsoft.AspNetCore.Authentication.JwtBearer (Microsoft.IdentityModel.*) | 10.0.12 | MIT | Cloud API |
| Microsoft.AspNetCore.OpenApi (Microsoft.OpenApi) | 10.0.12 | MIT | Cloud API |
| Swashbuckle.AspNetCore.SwaggerUI | 10.2.3 | MIT | Cloud API (Swagger UI) |
| Microsoft.Data.Sqlite (SQLitePCLRaw, SQLite) | 10.0.12 | MIT / Apache-2.0 / Public Domain | Edge Controller |
| Microsoft.Extensions.Hosting(.WindowsServices), Http, Logging | 10.0.12 | MIT | Windows Agent, Simulator |
| System.Security.Cryptography.ProtectedData | 10.0.12 | MIT | Windows Agent (DPAPI) |
| dotnet-ef (tool) | 10.0.12 | MIT | миграции |

## Тесты

| Компонент | Лицензия |
|-----------|----------|
| xunit, xunit.runner.visualstudio | Apache-2.0 |
| Microsoft.NET.Test.Sdk | MIT |
| Microsoft.AspNetCore.Mvc.Testing | MIT |
| Testcontainers / Testcontainers.PostgreSql | MIT |
| @playwright/test | Apache-2.0 |

## Admin Web

| Компонент | Версия | Лицензия |
|-----------|--------|----------|
| Next.js | 16.3.6 | MIT |
| React, React DOM | 19.3.0 | MIT |
| qrcode (node-qrcode) | 1.5.4 | MIT |
| Tailwind CSS, @tailwindcss/postcss | 4.3.3 | MIT |
| TypeScript | 5.9.3 | Apache-2.0 |
| ESLint, eslint-config-next | 9.x / 16.3.6 | MIT |
| server-only | 0.0.1 | MIT |

## Инфраструктура (образы)

| Образ | Лицензия |
|-------|----------|
| postgres:18 | PostgreSQL License |
| mcr.microsoft.com/dotnet/{sdk,aspnet,runtime}:10.0 | MIT (.NET), Debian — лицензии пакетов дистрибутива |
| node:22-alpine | MIT (Node.js), Alpine — лицензии пакетов дистрибутива |

Шрифты: системные (Segoe UI / system-ui), внешние шрифты не загружаются. Иконки — собственные inline SVG.
