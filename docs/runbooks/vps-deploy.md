# Runbook: Cloud-часть ClubOS на VPS

На VPS работают Cloud API, PostgreSQL 18 и Admin Web за Caddy с автоматическим HTTPS (Let's Encrypt).
Edge Controller ставится **в клубе** на Windows-сервер ([edge-windows-install.md](edge-windows-install.md)),
агенты — на игровые ПК ([windows-agent-install.md](windows-agent-install.md)).

```
Админские ПК (браузер) ──HTTPS──▶ VPS: Caddy ─▶ Admin Web ─▶ Cloud API ─▶ PostgreSQL
Сервер клуба: Edge (Windows-служба) ──HTTPS, исходящее──▶ VPS: Caddy ─▶ Cloud API
Игровые ПК: Agent ──LAN :7070──▶ Edge
```

## 1. Что нужно

| Что | Требование |
|-----|-----------|
| VPS | Ubuntu 24.04 LTS, 2 vCPU, 4 ГБ RAM, 30 ГБ SSD; ближе к Душанбе (задержка Edge↔Cloud и админки) |
| Домен | A-запись `clubos.<ваш-домен>` → публичный IP VPS (до запуска, иначе Let's Encrypt не выдаст сертификат) |
| Порты | входящие 22 (SSH), 80 и 443 (HTTP/HTTPS). Больше ничего открывать не нужно |

## 2. Подготовка сервера (один раз)

```bash
# Docker Engine + compose plugin (официальный скрипт Docker)
curl -fsSL https://get.docker.com | sh

# Firewall: только SSH и веб
ufw allow OpenSSH && ufw allow 80/tcp && ufw allow 443/tcp && ufw allow 443/udp && ufw --force enable

# Код
git clone https://github.com/relix2294/ClubOS.git /opt/ClubOS
cd /opt/ClubOS/infrastructure/vps
cp .env.example .env
chmod 600 .env
nano .env        # домен, email, пароли и ключ: команды генерации — в комментариях файла
```

## 3. Запуск

```bash
cd /opt/ClubOS/infrastructure/vps
docker compose up -d --build        # первая сборка 5–10 минут
docker compose ps                   # все сервисы Up, cloud-api (healthy)
docker compose logs -f caddy        # «certificate obtained successfully» для вашего домена
```

Откройте `https://clubos.<ваш-домен>`, войдите как `CLUBOS_OWNER_EMAIL` / `CLUBOS_OWNER_PASSWORD`.

Что опубликовано наружу (Caddyfile):
- `/` — Admin Web;
- `/api/v1/*` — Cloud API: Edge по подписанным запросам, staff API по JWT;
- Swagger, OpenAPI и health снаружи возвращают 404.

PostgreSQL и Cloud API напрямую из интернета недоступны.

## 4. Подключение клуба

1. Admin Web → **Подключение** → «Токен для Edge Controller» → скопировать (одноразовый, 24 часа).
2. На сервере клуба: [edge-windows-install.md](edge-windows-install.md), `-CloudUrl https://clubos.<ваш-домен>`.
3. На дашборде появится «Edge на связи». Дальше — агенты на игровые ПК.

## 5. Бэкап (обязательно)

```bash
/opt/ClubOS/infrastructure/vps/backup.sh /var/backups/clubos      # вручную
crontab -e   # ежедневно в 03:00 UTC:
# 0 3 * * * /opt/ClubOS/infrastructure/vps/backup.sh /var/backups/clubos >> /var/log/clubos-backup.log 2>&1
```

Скрипт сохраняет дамп PostgreSQL и **dev CA** (том `devca`) и хранит их 14 дней (`KEEP_DAYS`).
Без CA все Edge и ПК придётся перерегистрировать. Копируйте `/var/backups/clubos` за пределы VPS:
на другой сервер или в объектное хранилище.

Восстановление:

```bash
docker compose exec -T postgres pg_restore -U clubos -d clubos --clean < clubos-db-<время>.dump
docker compose exec -T cloud-api tar -C /data -xzf - < clubos-devca-<время>.tar.gz && docker compose restart cloud-api
```

## 6. Обновление

```bash
cd /opt/ClubOS && git pull
cd infrastructure/vps && docker compose up -d --build
```

Миграции БД применяются автоматически при старте Cloud API. Перед обновлением сделайте бэкап.

**Обновление до M1 (2FA):** для Owner и Admin двухфакторная аутентификация обязательна. После обновления
при входе владелец увидит только «Мой пароль» с настройкой 2FA. Нужен телефон с приложением-аутентификатором
(Google Authenticator, Microsoft Authenticator, Aegis). Сохраните коды восстановления.

**Порядок обновления:** сначала Edge в клубах ([edge-windows-install.md](edge-windows-install.md), п. 6),
затем Cloud на VPS. Новый Edge совместим со старым Cloud. Если Cloud оказался новее Edge, команды неизвестного
вида (например, «Продлить» из M1) Edge пропускает с предупреждением в журнале, остальная очередь работает.

## 7. Диагностика

| Симптом | Где смотреть |
|---------|-------------|
| Сайт не открывается / ошибка сертификата | `docker compose logs caddy`: DNS A-запись, открыт ли 80/443 |
| Забыт или скомпрометирован пароль владельца | `docker compose exec cloud-api dotnet ClubOS.CloudApi.dll admin reset-password <email>`: временный пароль, при входе потребуется задать новый |
| Владелец потерял телефон с 2FA и коды восстановления | `docker compose exec cloud-api dotnet ClubOS.CloudApi.dll admin reset-mfa <email>`: 2FA отключена, при входе потребуется настроить заново. Сотруднику 2FA сбрасывает владелец: «Персонал» → «Сбросить 2FA» |
| Сменили `CLUBOS_AUTH_SIGNING_KEY` — 2FA перестала принимать коды | секреты TOTP зашифрованы ключом, выведенным из ключа подписи. Задайте отдельный `CLUBOS_Auth__MfaEncryptionKey` до смены ключа подписи, иначе сбросьте 2FA всем (`admin reset-mfa`) |
| Не входит в Admin Web | `docker compose logs admin-web cloud-api`; 429 — сработал лимит попыток входа (20 в минуту с IP) |
| «Edge не на связи» | на сервере клуба `edge-cli status` и Event Viewer (источник ClubOSEdge); `docker compose logs cloud-api` |
| Место на диске | `docker system df`; старые образы — `docker image prune` |
