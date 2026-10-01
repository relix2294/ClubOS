using Microsoft.Data.Sqlite;

namespace ClubOS.EdgeController.Storage;

/// <summary>
/// SQLite в режиме WAL (ТЗ §7.1, DEVIATIONS D-001). Все записи сериализуются через один
/// семафор (один процесс Edge) — без SQLITE_BUSY; чтения идут параллельно благодаря WAL.
/// Схема версионируется через PRAGMA user_version.
/// </summary>
public sealed class EdgeDatabase : IDisposable
{
    private readonly string _connectionString;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public EdgeDatabase(string dataPath)
    {
        Directory.CreateDirectory(dataPath);
        FilePath = Path.Combine(dataPath, "edge.db");
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = FilePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = true,
            DefaultTimeout = 30
        }.ToString();
        Migrate();
    }

    public string FilePath { get; }

    public SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000; PRAGMA synchronous = FULL;";
        cmd.ExecuteNonQuery();
        return connection;
    }

    /// <summary>Выполняет запись в транзакции под глобальной блокировкой записи.</summary>
    public async Task<T> WriteAsync<T>(Func<SqliteConnection, SqliteTransaction, T> work, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            using var connection = Open();
            using var tx = connection.BeginTransaction();
            var result = work(connection, tx);
            tx.Commit();
            return result;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public T Read<T>(Func<SqliteConnection, T> work)
    {
        using var connection = Open();
        return work(connection);
    }

    public void Dispose()
    {
        _writeLock.Dispose();
        SqliteConnection.ClearAllPools();
    }

    private void Migrate()
    {
        using var connection = Open();
        using (var wal = connection.CreateCommand())
        {
            wal.CommandText = "PRAGMA journal_mode = WAL;";
            wal.ExecuteNonQuery();
        }

        var version = Convert.ToInt32(Scalar(connection, "PRAGMA user_version;"));
        if (version < 1)
        {
            using var tx = connection.BeginTransaction();
            Exec(connection, tx, Schema.V1);
            Exec(connection, tx, "PRAGMA user_version = 1;");
            tx.Commit();
        }

        if (version < 2)
        {
            using var tx = connection.BeginTransaction();
            Exec(connection, tx, Schema.V2);
            Exec(connection, tx, "PRAGMA user_version = 2;");
            tx.Commit();
        }

        if (version < 3)
        {
            using var tx = connection.BeginTransaction();
            Exec(connection, tx, Schema.V3);
            Exec(connection, tx, "PRAGMA user_version = 3;");
            tx.Commit();
        }

        if (version < 4)
        {
            using var tx = connection.BeginTransaction();
            Exec(connection, tx, Schema.V4);
            Exec(connection, tx, "PRAGMA user_version = 4;");
            tx.Commit();
        }

        if (version < 5)
        {
            using var tx = connection.BeginTransaction();
            Exec(connection, tx, Schema.V5);
            Exec(connection, tx, "PRAGMA user_version = 5;");
            tx.Commit();
        }

        if (version < 6)
        {
            using var tx = connection.BeginTransaction();
            Exec(connection, tx, Schema.V6);
            Exec(connection, tx, "PRAGMA user_version = 6;");
            tx.Commit();
        }
    }

    public int SchemaVersion => Convert.ToInt32(Read(c => Scalar(c, "PRAGMA user_version;")));

    private static object? Scalar(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }

    private static void Exec(SqliteConnection c, SqliteTransaction tx, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static class Schema
    {
        public const string V1 = """
            CREATE TABLE kv (
                key   TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );

            -- Кэш конфигурации локации (нужен для offline-сессий).
            CREATE TABLE zones (
                zone_id                    TEXT PRIMARY KEY,
                name                       TEXT NOT NULL,
                price_per_hour_minor_units INTEGER NOT NULL,
                rounding                   TEXT NOT NULL,
                rule_version               INTEGER NOT NULL
            );

            CREATE TABLE devices (
                device_id          TEXT PRIMARY KEY,
                display_name       TEXT NOT NULL,
                zone_id            TEXT NOT NULL,
                simulated          INTEGER NOT NULL DEFAULT 0,
                certificate_pem    TEXT NOT NULL,
                online             INTEGER NOT NULL DEFAULT 0,
                agent_status       TEXT NOT NULL DEFAULT 'Offline',
                last_heartbeat_utc TEXT NULL,
                clock_skew_ms      INTEGER NULL,
                inventory_json     TEXT NULL
            );

            -- Сессии. Edge — источник истины для активной сессии (ТЗ §23.3).
            CREATE TABLE sessions (
                session_id                 TEXT PRIMARY KEY,
                device_id                  TEXT NOT NULL,
                state                      TEXT NOT NULL,
                origin                     TEXT NOT NULL,
                started_at_utc             TEXT NOT NULL,
                ended_at_utc               TEXT NULL,
                price_per_hour_minor_units INTEGER NOT NULL,
                currency                   TEXT NOT NULL,
                rounding                   TEXT NOT NULL,
                rule_version               INTEGER NOT NULL,
                total_minor_units          INTEGER NULL,
                started_by                 TEXT NOT NULL,
                ended_by                   TEXT NULL,
                correlation_id             TEXT NOT NULL
            );
            -- Не больше одной активной сессии на устройство — гарантия на уровне БД.
            CREATE UNIQUE INDEX ux_sessions_active_device ON sessions(device_id) WHERE state = 'Active';

            -- Durable outbox событий Edge → Cloud (at-least-once). seq = монотонная sequence локации.
            CREATE TABLE outbox (
                seq             INTEGER PRIMARY KEY AUTOINCREMENT,
                event_id        TEXT NOT NULL UNIQUE,
                event_type      TEXT NOT NULL,
                aggregate_id    TEXT NOT NULL,
                occurred_at_utc TEXT NOT NULL,
                recorded_at_utc TEXT NOT NULL,
                correlation_id  TEXT NULL,
                payload_json    TEXT NOT NULL,
                sent_at_utc     TEXT NULL,
                rejected_reason TEXT NULL,
                attempts        INTEGER NOT NULL DEFAULT 0,
                last_error      TEXT NULL
            );
            CREATE INDEX ix_outbox_pending ON outbox(sent_at_utc, seq);

            -- Идемпотентный inbox команд Cloud → Edge.
            CREATE TABLE inbox (
                id              TEXT PRIMARY KEY,
                kind            TEXT NOT NULL,
                received_at_utc TEXT NOT NULL
            );

            CREATE TABLE device_commands (
                command_id      TEXT PRIMARY KEY,
                device_id       TEXT NOT NULL,
                command_type    TEXT NOT NULL,
                envelope_json   TEXT NOT NULL,
                expires_at_utc  TEXT NOT NULL,
                state           TEXT NOT NULL,
                error           TEXT NULL,
                updated_at_utc  TEXT NOT NULL
            );
            CREATE INDEX ix_device_commands_device ON device_commands(device_id, state);
            """;

        /// <summary>M1: лимит времени сессии (Player Shell), причина завершения.</summary>
        public const string V2 = """
            ALTER TABLE sessions ADD COLUMN planned_end_at_utc TEXT NULL;
            ALTER TABLE sessions ADD COLUMN end_reason TEXT NULL;
            CREATE INDEX ix_sessions_planned_end ON sessions(planned_end_at_utc) WHERE state = 'Active';
            CREATE INDEX ix_sessions_device_ended ON sessions(device_id, ended_at_utc);
            """;

        /// <summary>
        /// M1: бездисковые ПК (D-018). hardware_id — MAC загрузочной карты (из конфигурации Cloud);
        /// local_certificate_pem — сертификат локального CA Edge, выданный при последней загрузке ПК.
        /// </summary>
        public const string V3 = """
            ALTER TABLE devices ADD COLUMN hardware_id TEXT NULL;
            ALTER TABLE devices ADD COLUMN local_certificate_pem TEXT NULL;
            CREATE UNIQUE INDEX ix_devices_hardware ON devices(hardware_id) WHERE hardware_id IS NOT NULL;

            CREATE TABLE diskless_candidates (
                hardware_id    TEXT PRIMARY KEY,
                macs           TEXT NOT NULL,
                hostname       TEXT NOT NULL,
                ipv4           TEXT NULL,
                simulated      INTEGER NOT NULL DEFAULT 0,
                first_seen_utc TEXT NOT NULL,
                last_seen_utc  TEXT NOT NULL
            );
            """;

        /// <summary>
        /// M1: тарифы по времени и пакеты. periods_json — периоды цены зоны из конфигурации Cloud;
        /// price_snapshot_json — полный снимок тарифа сессии (периоды, смещение времени, пакет). Колонки цены
        /// остаются для отчётов и старых строк: если снимка нет — одна цена.
        /// </summary>
        public const string V4 = """
            ALTER TABLE zones ADD COLUMN periods_json TEXT NULL;
            ALTER TABLE sessions ADD COLUMN price_snapshot_json TEXT NULL;
            """;

        /// <summary>
        /// M2: касса Edge без интернета (D-023). offline_staff — кассиры с PIN из конфигурации Cloud;
        /// session_cloud_paid — оплачено по сессии в Cloud (команда CashSync); offline_payments — оплаты, принятые
        /// на Edge (иммутабельны, в Cloud уходят событием OfflinePaymentRecorded).
        /// </summary>
        public const string V5 = """
            CREATE TABLE offline_staff (
                user_id      TEXT PRIMARY KEY,
                display_name TEXT NOT NULL,
                pin_hash     TEXT NOT NULL
            );

            CREATE TABLE session_cloud_paid (
                session_id        TEXT PRIMARY KEY,
                paid_minor_units  INTEGER NOT NULL,
                updated_at_utc    TEXT NOT NULL
            );

            CREATE TABLE offline_payments (
                payment_id        TEXT PRIMARY KEY,
                session_id        TEXT NOT NULL,
                device_id         TEXT NOT NULL,
                amount_minor_units INTEGER NOT NULL,
                method            TEXT NOT NULL,
                user_id           TEXT NOT NULL,
                user_name         TEXT NOT NULL,
                recorded_at_utc   TEXT NOT NULL,
                idempotency_key   TEXT NULL UNIQUE
            );
            CREATE INDEX ix_offline_payments_session ON offline_payments(session_id);
            CREATE INDEX ix_offline_payments_time ON offline_payments(recorded_at_utc);
            """;

        /// <summary>
        /// M2: ротация ключа устройства при продлении (D-011). previous_certificate_pem — сертификат прежнего ключа,
        /// принимается, пока агент не подпишет запрос новым ключом.
        /// </summary>
        public const string V6 = """
            ALTER TABLE devices ADD COLUMN previous_certificate_pem TEXT NULL;
            """;
    }
}
