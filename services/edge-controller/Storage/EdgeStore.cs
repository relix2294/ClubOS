using System.Text.Json;
using ClubOS.Contracts;
using Microsoft.Data.Sqlite;

namespace ClubOS.EdgeController.Storage;

public sealed record EdgeDevice(
    string DeviceId,
    string DisplayName,
    string ZoneId,
    bool Simulated,
    string CertificatePem,
    bool Online,
    DeviceStatus AgentStatus,
    DateTimeOffset? LastHeartbeatUtc,
    DeviceInventory? Inventory);

public sealed record EdgeSession(
    string SessionId,
    string DeviceId,
    SessionState State,
    string Origin,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? EndedAtUtc,
    PriceSnapshot PriceSnapshot,
    long? TotalMinorUnits,
    string StartedBy,
    string? EndedBy,
    string CorrelationId);

public sealed record OutboxEvent(
    long Sequence,
    string EventId,
    string EventType,
    string AggregateId,
    DateTimeOffset OccurredAtUtc,
    DateTimeOffset RecordedAtUtc,
    string? CorrelationId,
    string PayloadJson,
    int Attempts);

public enum SessionOutcome
{
    Started,
    AlreadyStarted,
    Ended,
    AlreadyEnded,
    Rejected,
    NotFound
}

public sealed record SessionResult(SessionOutcome Outcome, EdgeSession? Session, string? Error = null)
{
    public bool IsSuccess => Outcome is SessionOutcome.Started or SessionOutcome.AlreadyStarted or SessionOutcome.Ended
        or SessionOutcome.AlreadyEnded;
}

/// <summary>
/// Доменное хранилище Edge. Каждое бизнес-изменение и соответствующее событие outbox пишутся
/// одной транзакцией SQLite — событие не может потеряться или появиться без изменения.
/// </summary>
public sealed class EdgeStore(EdgeDatabase database, EdgeSignals signals, TimeProvider time)
{
    // ---------- Конфигурация ----------

    public async Task SaveConfigAsync(EdgeConfigResponse config, CancellationToken ct = default)
    {
        await database.WriteAsync((c, tx) =>
        {
            SetKv(c, tx, "location_name", config.LocationName);
            SetKv(c, tx, "location_timezone", config.Timezone);
            SetKv(c, tx, "currency", config.Currency);
            foreach (var z in config.Zones)
            {
                c.Exec(tx, """
                    INSERT INTO zones (zone_id, name, price_per_hour_minor_units, rounding, rule_version)
                    VALUES ($id, $name, $price, $rounding, $rule)
                    ON CONFLICT(zone_id) DO UPDATE SET name = excluded.name,
                        price_per_hour_minor_units = excluded.price_per_hour_minor_units,
                        rounding = excluded.rounding, rule_version = excluded.rule_version
                    """, ("$id", z.ZoneId), ("$name", z.Name), ("$price", z.PricePerHourMinorUnits),
                    ("$rounding", z.Rounding), ("$rule", z.RuleVersion));
            }

            foreach (var d in config.Devices)
            {
                UpsertDevice(c, tx, d.DeviceId, d.DisplayName, d.ZoneId, d.Simulated, d.CertificatePem);
            }

            return 0;
        }, ct);
    }

    public string? GetKv(string key) =>
        database.Read(c => c.Scalar(null, "SELECT value FROM kv WHERE key = $k", ("$k", key)) as string);

    public PriceSnapshot? GetZonePrice(string zoneId)
    {
        var currency = GetKv("currency");
        if (currency is null)
        {
            return null;
        }

        return database.Read(c => c.Query(null,
            "SELECT price_per_hour_minor_units, rounding, rule_version FROM zones WHERE zone_id = $z",
            r => new PriceSnapshot
            {
                PricePerHourMinorUnits = r.L("price_per_hour_minor_units"),
                Currency = currency,
                Rounding = Enum.Parse<RoundingRule>(r.S("rounding")),
                RuleVersion = (int)r.L("rule_version")
            }, ("$z", zoneId)).FirstOrDefault());
    }

    // ---------- Устройства ----------

    public async Task AddEnrolledDeviceAsync(DeviceEnrollResponse enrolled, DeviceInventory inventory, CancellationToken ct = default)
    {
        await database.WriteAsync((c, tx) =>
        {
            UpsertDevice(c, tx, enrolled.DeviceId, enrolled.DisplayName, enrolled.ZoneId, enrolled.Simulated,
                enrolled.DeviceCertificatePem);
            c.Exec(tx, "UPDATE devices SET inventory_json = $inv WHERE device_id = $id",
                ("$inv", JsonSerializer.Serialize(inventory, ContractJson.Options)), ("$id", enrolled.DeviceId));
            return 0;
        }, ct);
    }

    public EdgeDevice? GetDevice(string deviceId) =>
        database.Read(c => c.Query(null, "SELECT * FROM devices WHERE device_id = $id", MapDevice, ("$id", deviceId))
            .FirstOrDefault());

    public IReadOnlyList<EdgeDevice> ListDevices() =>
        database.Read(c => c.Query(null, "SELECT * FROM devices ORDER BY display_name", MapDevice));

    /// <summary>Heartbeat агента. Переход offline → online пишет durable-событие.</summary>
    public async Task RecordHeartbeatAsync(HeartbeatMessage heartbeat, CancellationToken ct = default)
    {
        var now = time.GetUtcNow();
        var wrote = await database.WriteAsync((c, tx) =>
        {
            var wasOnline = Convert.ToInt64(c.Scalar(tx, "SELECT online FROM devices WHERE device_id = $id",
                ("$id", heartbeat.DeviceId)) ?? 0L) == 1;
            var status = heartbeat.Status is DeviceStatus.Locked ? DeviceStatus.Locked : DeviceStatus.Idle;
            c.Exec(tx, """
                UPDATE devices SET online = 1, agent_status = $status, last_heartbeat_utc = $now,
                    clock_skew_ms = $skew, inventory_json = COALESCE($inv, inventory_json)
                WHERE device_id = $id
                """,
                ("$status", status), ("$now", now), ("$skew", (long)(heartbeat.ClockUtc - now).TotalMilliseconds),
                ("$inv", heartbeat.Inventory is null ? null : JsonSerializer.Serialize(heartbeat.Inventory, ContractJson.Options)),
                ("$id", heartbeat.DeviceId));

            if (!wasOnline)
            {
                AppendEvent(c, tx, EventTypes.DeviceConnectivityChanged, heartbeat.DeviceId,
                    new DeviceConnectivityChangedPayload { DeviceId = heartbeat.DeviceId, Online = true, AtUtc = now }, null);
                return true;
            }

            return false;
        }, ct);

        if (wrote)
        {
            signals.NotifyOutbox();
        }
    }

    /// <summary>Помечает offline устройства без heartbeat дольше окна (ТЗ §10.1).</summary>
    public async Task<int> MarkStaleDevicesOfflineAsync(TimeSpan window, CancellationToken ct = default)
    {
        var now = time.GetUtcNow();
        var threshold = now - window;
        var count = await database.WriteAsync((c, tx) =>
        {
            var stale = c.Query(tx,
                "SELECT device_id FROM devices WHERE online = 1 AND (last_heartbeat_utc IS NULL OR last_heartbeat_utc < $t)",
                r => r.S("device_id"), ("$t", threshold));
            foreach (var id in stale)
            {
                c.Exec(tx, "UPDATE devices SET online = 0, agent_status = 'Offline' WHERE device_id = $id", ("$id", id));
                AppendEvent(c, tx, EventTypes.DeviceConnectivityChanged, id,
                    new DeviceConnectivityChangedPayload { DeviceId = id, Online = false, AtUtc = now }, null);
            }

            return stale.Count;
        }, ct);

        if (count > 0)
        {
            signals.NotifyOutbox();
        }

        return count;
    }

    /// <summary>Эффективный статус для отчёта в Cloud: активная сессия важнее статуса агента.</summary>
    public IReadOnlyList<DeviceStatusEntry> BuildStatusEntries()
    {
        var active = GetActiveSessions().Select(x => x.DeviceId).ToHashSet();
        return ListDevices().Select(d => new DeviceStatusEntry
        {
            DeviceId = d.DeviceId,
            Status = !d.Online ? DeviceStatus.Offline : active.Contains(d.DeviceId) ? DeviceStatus.Active : d.AgentStatus,
            LastHeartbeatUtc = d.LastHeartbeatUtc,
            Inventory = d.Inventory
        }).ToList();
    }

    // ---------- Команды Cloud → Edge ----------

    /// <summary>
    /// Применяет элемент очереди Cloud идемпотентно: inbox (PK = id) + эффект — одна транзакция.
    /// Возвращает false, если элемент уже был обработан ранее (дубль доставки).
    /// </summary>
    public async Task<bool> ApplyCloudCommandAsync(EdgeCommand command, CancellationToken ct = default)
    {
        var now = time.GetUtcNow();
        var applied = await database.WriteAsync((c, tx) =>
        {
            var inserted = c.Exec(tx, "INSERT OR IGNORE INTO inbox (id, kind, received_at_utc) VALUES ($id, $kind, $now)",
                ("$id", command.Id), ("$kind", command.Kind), ("$now", now));
            if (inserted == 0)
            {
                return false;
            }

            switch (command.Kind)
            {
                case EdgeCommandKind.DeviceCommand when command.DeviceCommand is { } env:
                    StoreDeviceCommand(c, tx, env, now);
                    break;
                case EdgeCommandKind.StartSession when command.StartSession is { } start:
                    StartSessionCore(c, tx, start.SessionId, start.DeviceId, start.PriceSnapshot, start.Actor,
                        start.CorrelationId, "cloud", now, emitRejection: true);
                    break;
                case EdgeCommandKind.EndSession when command.EndSession is { } end:
                    EndSessionCore(c, tx, end.SessionId, end.Actor, now);
                    break;
            }

            return true;
        }, ct);

        signals.NotifyOutbox();
        if (command.DeviceCommand is { } e)
        {
            foreach (var deviceId in e.TargetDeviceIds)
            {
                signals.NotifyDevice(deviceId);
            }
        }

        return applied;
    }

    private void StoreDeviceCommand(SqliteConnection c, SqliteTransaction tx, CommandEnvelope env, DateTimeOffset now)
    {
        foreach (var deviceId in env.TargetDeviceIds)
        {
            var known = c.Scalar(tx, "SELECT 1 FROM devices WHERE device_id = $id", ("$id", deviceId)) is not null;
            var expired = env.ExpiresAtUtc <= now;
            var state = !known ? CommandState.Failed : expired ? CommandState.Expired : CommandState.Queued;
            var error = !known ? "Устройство неизвестно Edge." : expired ? "Команда просрочена до доставки." : null;

            c.Exec(tx, """
                INSERT OR IGNORE INTO device_commands
                    (command_id, device_id, command_type, envelope_json, expires_at_utc, state, error, updated_at_utc)
                VALUES ($cid, $did, $type, $env, $exp, $state, $err, $now)
                """, ("$cid", env.CommandId), ("$did", deviceId), ("$type", env.CommandType),
                ("$env", JsonSerializer.Serialize(env, ContractJson.Options)), ("$exp", env.ExpiresAtUtc),
                ("$state", state), ("$err", error), ("$now", now));

            if (state != CommandState.Queued)
            {
                AppendEvent(c, tx, EventTypes.CommandStateChanged, env.CommandId, new CommandStateChangedPayload
                {
                    CommandId = env.CommandId,
                    DeviceId = deviceId,
                    State = state,
                    AtUtc = now,
                    Error = error
                }, env.CorrelationId);
            }
        }
    }

    /// <summary>
    /// Команды для агента: Queued и Delivered (повторная выдача, если ответ потерялся — агент дедуплицирует).
    /// Первая выдача переводит Queued → Delivered и пишет событие. Просроченные не выдаются.
    /// </summary>
    public async Task<IReadOnlyList<CommandEnvelope>> TakeCommandsForDeviceAsync(string deviceId, CancellationToken ct = default)
    {
        var now = time.GetUtcNow();
        var hasAny = database.Read(c => c.Scalar(null,
            "SELECT 1 FROM device_commands WHERE device_id = $d AND state IN ('Queued','Delivered') AND expires_at_utc > $now LIMIT 1",
            ("$d", deviceId), ("$now", now))) is not null;
        if (!hasAny)
        {
            return [];
        }

        var result = await database.WriteAsync((c, tx) =>
        {
            var rows = c.Query(tx, """
                SELECT command_id, envelope_json, state FROM device_commands
                WHERE device_id = $d AND state IN ('Queued','Delivered') AND expires_at_utc > $now
                ORDER BY updated_at_utc
                """, r => (Id: r.S("command_id"), Env: r.S("envelope_json"), State: r.S("state")),
                ("$d", deviceId), ("$now", now));

            var list = new List<CommandEnvelope>();
            foreach (var row in rows)
            {
                var env = JsonSerializer.Deserialize<CommandEnvelope>(row.Env, ContractJson.Options)!;
                list.Add(env);
                if (row.State == nameof(CommandState.Queued))
                {
                    c.Exec(tx, "UPDATE device_commands SET state = 'Delivered', updated_at_utc = $now WHERE command_id = $id",
                        ("$now", now), ("$id", row.Id));
                    AppendEvent(c, tx, EventTypes.CommandStateChanged, row.Id, new CommandStateChangedPayload
                    {
                        CommandId = row.Id,
                        DeviceId = deviceId,
                        State = CommandState.Delivered,
                        AtUtc = now
                    }, env.CorrelationId);
                }
            }

            return list;
        }, ct);

        signals.NotifyOutbox();
        return result;
    }

    /// <summary>Результат от агента. Переходы только вперёд; повтор/устаревший результат игнорируется.</summary>
    public async Task<bool> ApplyAgentResultAsync(string deviceId, string commandId, CommandState state, string? error,
        CancellationToken ct = default)
    {
        if (state is not (CommandState.Acknowledged or CommandState.Succeeded or CommandState.Failed))
        {
            return false;
        }

        var now = time.GetUtcNow();
        var changed = await database.WriteAsync((c, tx) =>
        {
            var row = c.Query(tx, "SELECT state, envelope_json FROM device_commands WHERE command_id = $id AND device_id = $d",
                r => (State: Enum.Parse<CommandState>(r.S("state")), Env: r.S("envelope_json")),
                ("$id", commandId), ("$d", deviceId)).FirstOrDefault();
            if (row == default || !CommandStateMachine.CanTransition(row.State, state))
            {
                return false;
            }

            var safeError = error is { Length: > 500 } ? error[..500] : error;
            c.Exec(tx, "UPDATE device_commands SET state = $s, error = $e, updated_at_utc = $now WHERE command_id = $id",
                ("$s", state), ("$e", safeError), ("$now", now), ("$id", commandId));
            var env = JsonSerializer.Deserialize<CommandEnvelope>(row.Env, ContractJson.Options)!;
            AppendEvent(c, tx, EventTypes.CommandStateChanged, commandId, new CommandStateChangedPayload
            {
                CommandId = commandId,
                DeviceId = deviceId,
                State = state,
                AtUtc = now,
                Error = safeError
            }, env.CorrelationId);
            return true;
        }, ct);

        if (changed)
        {
            signals.NotifyOutbox();
        }

        return changed;
    }

    /// <summary>Нетерминальные просроченные команды → Expired (ТЗ §10.2 CMD-006).</summary>
    public async Task<int> ExpireCommandsAsync(CancellationToken ct = default)
    {
        var now = time.GetUtcNow();
        var count = await database.WriteAsync((c, tx) =>
        {
            var rows = c.Query(tx, """
                SELECT command_id, device_id, envelope_json FROM device_commands
                WHERE state IN ('Queued','Delivered','Acknowledged') AND expires_at_utc <= $now
                """, r => (Id: r.S("command_id"), Device: r.S("device_id"), Env: r.S("envelope_json")), ("$now", now));
            foreach (var row in rows)
            {
                const string error = "Команда не выполнена до истечения срока.";
                c.Exec(tx, "UPDATE device_commands SET state = 'Expired', error = $e, updated_at_utc = $now WHERE command_id = $id",
                    ("$e", error), ("$now", now), ("$id", row.Id));
                var env = JsonSerializer.Deserialize<CommandEnvelope>(row.Env, ContractJson.Options)!;
                AppendEvent(c, tx, EventTypes.CommandStateChanged, row.Id, new CommandStateChangedPayload
                {
                    CommandId = row.Id,
                    DeviceId = row.Device,
                    State = CommandState.Expired,
                    AtUtc = now,
                    Error = error
                }, env.CorrelationId);
            }

            return rows.Count;
        }, ct);

        if (count > 0)
        {
            signals.NotifyOutbox();
        }

        return count;
    }

    public CommandState? GetCommandState(string commandId) =>
        database.Read(c => c.Scalar(null, "SELECT state FROM device_commands WHERE command_id = $id", ("$id", commandId)))
            is string s ? Enum.Parse<CommandState>(s) : null;

    // ---------- Сессии ----------

    /// <summary>Локальный старт (edge-cli / offline). Тариф — из кэша конфигурации зоны.</summary>
    public async Task<SessionResult> StartLocalSessionAsync(string deviceId, string actor, CancellationToken ct = default)
    {
        var device = GetDevice(deviceId);
        if (device is null)
        {
            return new SessionResult(SessionOutcome.NotFound, null, "Устройство неизвестно Edge.");
        }

        var price = GetZonePrice(device.ZoneId);
        if (price is null)
        {
            return new SessionResult(SessionOutcome.Rejected, null,
                "Тариф зоны ещё не получен из Cloud (нет кэша конфигурации).");
        }

        var now = time.GetUtcNow();
        var sessionId = $"ses_{Guid.CreateVersion7():N}";
        var result = await database.WriteAsync((c, tx) => StartSessionCore(c, tx, sessionId, deviceId, price, actor,
            $"cor_{Guid.CreateVersion7():N}", "edge", now, emitRejection: false), ct);
        signals.NotifyOutbox();
        return result;
    }

    public async Task<SessionResult> EndSessionAsync(string sessionId, string actor, CancellationToken ct = default)
    {
        var now = time.GetUtcNow();
        var result = await database.WriteAsync((c, tx) => EndSessionCore(c, tx, sessionId, actor, now), ct);
        signals.NotifyOutbox();
        return result;
    }

    public EdgeSession? GetSession(string sessionId) =>
        database.Read(c => c.Query(null, "SELECT * FROM sessions WHERE session_id = $id", MapSession, ("$id", sessionId))
            .FirstOrDefault());

    public IReadOnlyList<EdgeSession> GetActiveSessions() =>
        database.Read(c => c.Query(null, "SELECT * FROM sessions WHERE state = 'Active' ORDER BY started_at_utc", MapSession));

    public IReadOnlyList<EdgeSession> ListRecentSessions(int limit) =>
        database.Read(c => c.Query(null, "SELECT * FROM sessions ORDER BY started_at_utc DESC LIMIT $n", MapSession,
            ("$n", limit)));

    private SessionResult StartSessionCore(SqliteConnection c, SqliteTransaction tx, string sessionId, string deviceId,
        PriceSnapshot price, string actor, string correlationId, string origin, DateTimeOffset now, bool emitRejection)
    {
        var existing = c.Query(tx, "SELECT * FROM sessions WHERE session_id = $id", MapSession, ("$id", sessionId))
            .FirstOrDefault();
        if (existing is not null)
        {
            return new SessionResult(SessionOutcome.AlreadyStarted, existing);
        }

        string? rejection = null;
        if (c.Scalar(tx, "SELECT 1 FROM devices WHERE device_id = $d", ("$d", deviceId)) is null)
        {
            rejection = "Устройство неизвестно Edge.";
        }
        else if (c.Scalar(tx, "SELECT 1 FROM sessions WHERE device_id = $d AND state = 'Active'", ("$d", deviceId)) is not null)
        {
            rejection = "На устройстве уже есть активная сессия.";
        }

        if (rejection is not null)
        {
            if (emitRejection)
            {
                AppendEvent(c, tx, EventTypes.SessionStartRejected, sessionId,
                    new SessionStartRejectedPayload { SessionId = sessionId, DeviceId = deviceId, Reason = rejection },
                    correlationId);
            }

            return new SessionResult(SessionOutcome.Rejected, null, rejection);
        }

        c.Exec(tx, """
            INSERT INTO sessions (session_id, device_id, state, origin, started_at_utc, price_per_hour_minor_units,
                currency, rounding, rule_version, started_by, correlation_id)
            VALUES ($id, $d, 'Active', $origin, $now, $price, $cur, $round, $rule, $actor, $cor)
            """, ("$id", sessionId), ("$d", deviceId), ("$origin", origin), ("$now", now),
            ("$price", price.PricePerHourMinorUnits), ("$cur", price.Currency), ("$round", price.Rounding),
            ("$rule", price.RuleVersion), ("$actor", actor), ("$cor", correlationId));

        AppendEvent(c, tx, EventTypes.SessionStarted, sessionId, new SessionStartedPayload
        {
            SessionId = sessionId,
            DeviceId = deviceId,
            StartedAtUtc = now,
            PriceSnapshot = price,
            Actor = actor,
            Origin = origin
        }, correlationId);

        return new SessionResult(SessionOutcome.Started,
            c.Query(tx, "SELECT * FROM sessions WHERE session_id = $id", MapSession, ("$id", sessionId)).Single());
    }

    /// <summary>Идемпотентно: повторное завершение возвращает тот же итог без второго события (ТЗ §5.1).</summary>
    private SessionResult EndSessionCore(SqliteConnection c, SqliteTransaction tx, string sessionId, string actor,
        DateTimeOffset now)
    {
        var session = c.Query(tx, "SELECT * FROM sessions WHERE session_id = $id", MapSession, ("$id", sessionId))
            .FirstOrDefault();
        if (session is null)
        {
            return new SessionResult(SessionOutcome.NotFound, null, "Сессия не найдена на Edge.");
        }

        if (session.State == SessionState.Ended)
        {
            return new SessionResult(SessionOutcome.AlreadyEnded, session);
        }

        var total = BillingCalculator.CalculateMinorUnits(session.PriceSnapshot, now - session.StartedAtUtc);
        c.Exec(tx, """
            UPDATE sessions SET state = 'Ended', ended_at_utc = $now, total_minor_units = $total, ended_by = $actor
            WHERE session_id = $id AND state = 'Active'
            """, ("$now", now), ("$total", total), ("$actor", actor), ("$id", sessionId));

        AppendEvent(c, tx, EventTypes.SessionEnded, sessionId, new SessionEndedPayload
        {
            SessionId = sessionId,
            DeviceId = session.DeviceId,
            StartedAtUtc = session.StartedAtUtc,
            EndedAtUtc = now,
            PriceSnapshot = session.PriceSnapshot,
            TotalMinorUnits = total,
            Actor = actor,
            Origin = session.Origin
        }, session.CorrelationId);

        return new SessionResult(SessionOutcome.Ended,
            c.Query(tx, "SELECT * FROM sessions WHERE session_id = $id", MapSession, ("$id", sessionId)).Single());
    }

    // ---------- Outbox ----------

    public IReadOnlyList<OutboxEvent> GetPendingEvents(int limit) =>
        database.Read(c => c.Query(null, """
            SELECT seq, event_id, event_type, aggregate_id, occurred_at_utc, recorded_at_utc, correlation_id,
                   payload_json, attempts
            FROM outbox WHERE sent_at_utc IS NULL ORDER BY seq LIMIT $n
            """, r => new OutboxEvent(r.L("seq"), r.S("event_id"), r.S("event_type"), r.S("aggregate_id"),
            r.T("occurred_at_utc"), r.T("recorded_at_utc"), r.Str("correlation_id"), r.S("payload_json"),
            (int)r.L("attempts")), ("$n", limit)));

    public int CountPendingEvents() =>
        Convert.ToInt32(database.Read(c => c.Scalar(null, "SELECT COUNT(*) FROM outbox WHERE sent_at_utc IS NULL")));

    /// <summary>
    /// Отмечает события доставленными (accepted/duplicates) или отклонёнными. Записи остаются в журнале
    /// (event log), но больше не отправляются — outbox «очищается».
    /// </summary>
    public async Task MarkEventsDeliveredAsync(IEnumerable<string> delivered, IEnumerable<RejectedEvent> rejected,
        CancellationToken ct = default)
    {
        var now = time.GetUtcNow();
        await database.WriteAsync((c, tx) =>
        {
            foreach (var id in delivered)
            {
                c.Exec(tx, "UPDATE outbox SET sent_at_utc = $now WHERE event_id = $id", ("$now", now), ("$id", id));
            }

            foreach (var r in rejected)
            {
                c.Exec(tx, "UPDATE outbox SET sent_at_utc = $now, rejected_reason = $reason WHERE event_id = $id",
                    ("$now", now), ("$reason", r.Reason), ("$id", r.EventId));
            }

            return 0;
        }, ct);
    }

    public async Task RecordSendFailureAsync(IEnumerable<string> eventIds, string error, CancellationToken ct = default)
    {
        var safe = error.Length > 300 ? error[..300] : error;
        await database.WriteAsync((c, tx) =>
        {
            foreach (var id in eventIds)
            {
                c.Exec(tx, "UPDATE outbox SET attempts = attempts + 1, last_error = $e WHERE event_id = $id",
                    ("$e", safe), ("$id", id));
            }

            return 0;
        }, ct);
    }

    private void AppendEvent<T>(SqliteConnection c, SqliteTransaction tx, string eventType, string aggregateId, T payload,
        string? correlationId)
    {
        var now = time.GetUtcNow();
        c.Exec(tx, """
            INSERT INTO outbox (event_id, event_type, aggregate_id, occurred_at_utc, recorded_at_utc, correlation_id, payload_json)
            VALUES ($id, $type, $agg, $occ, $rec, $cor, $payload)
            """, ("$id", $"evt_{Guid.CreateVersion7():N}"), ("$type", eventType), ("$agg", aggregateId),
            ("$occ", now), ("$rec", now), ("$cor", correlationId),
            ("$payload", JsonSerializer.Serialize(payload, ContractJson.Options)));
    }

    // ---------- Вспомогательное ----------

    private static void SetKv(SqliteConnection c, SqliteTransaction tx, string key, string value) =>
        c.Exec(tx, "INSERT INTO kv (key, value) VALUES ($k, $v) ON CONFLICT(key) DO UPDATE SET value = excluded.value",
            ("$k", key), ("$v", value));

    private static void UpsertDevice(SqliteConnection c, SqliteTransaction tx, string id, string name, string zoneId,
        bool simulated, string certPem) =>
        c.Exec(tx, """
            INSERT INTO devices (device_id, display_name, zone_id, simulated, certificate_pem)
            VALUES ($id, $name, $zone, $sim, $cert)
            ON CONFLICT(device_id) DO UPDATE SET display_name = excluded.display_name, zone_id = excluded.zone_id,
                simulated = excluded.simulated, certificate_pem = excluded.certificate_pem
            """, ("$id", id), ("$name", name), ("$zone", zoneId), ("$sim", simulated), ("$cert", certPem));

    private static EdgeDevice MapDevice(SqliteDataReader r) => new(
        r.S("device_id"), r.S("display_name"), r.S("zone_id"), r.L("simulated") == 1, r.S("certificate_pem"),
        r.L("online") == 1, Enum.Parse<DeviceStatus>(r.S("agent_status")), r.TN("last_heartbeat_utc"),
        r.Str("inventory_json") is { } inv ? JsonSerializer.Deserialize<DeviceInventory>(inv, ContractJson.Options) : null);

    private static EdgeSession MapSession(SqliteDataReader r) => new(
        r.S("session_id"), r.S("device_id"), Enum.Parse<SessionState>(r.S("state")), r.S("origin"),
        r.T("started_at_utc"), r.TN("ended_at_utc"),
        new PriceSnapshot
        {
            PricePerHourMinorUnits = r.L("price_per_hour_minor_units"),
            Currency = r.S("currency"),
            Rounding = Enum.Parse<RoundingRule>(r.S("rounding")),
            RuleVersion = (int)r.L("rule_version")
        },
        r.LN("total_minor_units"), r.S("started_by"), r.Str("ended_by"), r.S("correlation_id"));
}
