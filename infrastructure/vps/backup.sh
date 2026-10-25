#!/usr/bin/env bash
# Бэкап ClubOS Cloud: дамп PostgreSQL + dev CA (без CA все Edge и ПК придётся перерегистрировать).
# Запуск: ./backup.sh [каталог]   · cron: 0 3 * * * /opt/ClubOS/infrastructure/vps/backup.sh /var/backups/clubos
set -euo pipefail
cd "$(dirname "$0")"

DEST="${1:-/var/backups/clubos}"
KEEP_DAYS="${KEEP_DAYS:-14}"
STAMP="$(date -u +%Y%m%dT%H%M%SZ)"
mkdir -p "$DEST"
chmod 700 "$DEST"

docker compose exec -T postgres pg_dump -U clubos -d clubos --format=custom > "$DEST/clubos-db-$STAMP.dump"
docker compose exec -T cloud-api tar -C /data -czf - dev-ca > "$DEST/clubos-devca-$STAMP.tar.gz"
chmod 600 "$DEST"/clubos-*-"$STAMP".*

find "$DEST" -name 'clubos-*' -mtime "+$KEEP_DAYS" -delete
echo "OK: $DEST/clubos-db-$STAMP.dump, $DEST/clubos-devca-$STAMP.tar.gz"
