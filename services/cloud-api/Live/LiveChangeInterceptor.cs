using System.Data.Common;
using System.Runtime.CompilerServices;
using ClubOS.CloudApi.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace ClubOS.CloudApi.Live;

/// <summary>
/// Превращает сохранённые изменения сущностей в <see cref="LiveEvent"/>. Один перехватчик покрывает все пути
/// записи (staff API, sync от Edge, отчёты статусов, истечение команд), поэтому публикацию нельзя «забыть».
/// Событие уходит только после фиксации: без транзакции — после SaveChanges, внутри транзакции — после Commit.
/// Иначе клиент мог бы перечитать ресурс раньше, чем изменение стало видимым.
/// </summary>
public sealed class LiveChangeInterceptor(LiveBroker broker) : ISaveChangesInterceptor, IDbTransactionInterceptor
{
    /// <summary>Поля, изменение которых само по себе не интересно UI (меняются каждые несколько секунд).</summary>
    private static readonly HashSet<string> NoisyDeviceFields = [nameof(Device.LastHeartbeatUtc)];

    private static readonly HashSet<string> NoisyEdgeFields = [nameof(Edge.LastSeenAtUtc), nameof(Edge.LastEdgeClockUtc)];

    private readonly ConditionalWeakTable<DbContext, List<LiveEvent>> _pending = new();

    // ---------- SaveChanges ----------

    public InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Collect(eventData.Context);
        return result;
    }

    public ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Collect(eventData.Context);
        return ValueTask.FromResult(result);
    }

    public int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        FlushIfNoTransaction(eventData.Context);
        return result;
    }

    public ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result,
        CancellationToken cancellationToken = default)
    {
        FlushIfNoTransaction(eventData.Context);
        return ValueTask.FromResult(result);
    }

    public void SaveChangesFailed(DbContextErrorEventData eventData) => DropIfNoTransaction(eventData.Context);

    public Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        DropIfNoTransaction(eventData.Context);
        return Task.CompletedTask;
    }

    // ---------- Транзакции ----------

    public void TransactionCommitted(DbTransaction transaction, TransactionEndEventData eventData) => Flush(eventData.Context);

    public Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        Flush(eventData.Context);
        return Task.CompletedTask;
    }

    public void TransactionRolledBack(DbTransaction transaction, TransactionEndEventData eventData) => Drop(eventData.Context);

    public Task TransactionRolledBackAsync(DbTransaction transaction, TransactionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        Drop(eventData.Context);
        return Task.CompletedTask;
    }

    public void TransactionFailed(DbTransaction transaction, TransactionErrorEventData eventData) => Drop(eventData.Context);

    public Task TransactionFailedAsync(DbTransaction transaction, TransactionErrorEventData eventData,
        CancellationToken cancellationToken = default)
    {
        Drop(eventData.Context);
        return Task.CompletedTask;
    }

    // ---------- Сбор ----------

    /// <summary>Какие события порождает изменение одной записи. Публично для unit-тестов.</summary>
    public static IEnumerable<LiveEvent> EventsFor(object entity, EntityState state, IReadOnlyCollection<string> modified)
    {
        var changed = state is EntityState.Added or EntityState.Deleted;
        switch (entity)
        {
            case Device d when changed || modified.Any(p => !NoisyDeviceFields.Contains(p)):
                yield return new LiveEvent(LiveTopics.Devices, d.TenantId, d.LocationId, d.Id);
                break;
            case DeviceCommand c:
                yield return new LiveEvent(LiveTopics.Commands, c.TenantId, c.LocationId, c.DeviceId, c.Id);
                break;
            case Session s:
                yield return new LiveEvent(LiveTopics.Sessions, s.TenantId, s.LocationId, s.DeviceId, s.Id);
                // Плитка устройства показывает активную сессию и таймер.
                yield return new LiveEvent(LiveTopics.Devices, s.TenantId, s.LocationId, s.DeviceId);
                break;
            case AuditEvent a when state == EntityState.Added:
                yield return new LiveEvent(LiveTopics.Audit, a.TenantId, a.LocationId, TargetDevice(a.Target));
                break;
            case Edge e when changed || modified.Any(p => !NoisyEdgeFields.Contains(p)):
                yield return new LiveEvent(LiveTopics.Edges, e.TenantId, e.LocationId, null, e.Id);
                break;
            // Бездисковый ПК ждёт подтверждения: показать/убрать в Admin Web. Обновления «последний раз видели» не шумят.
            case PendingDisklessDevice p when changed:
                yield return new LiveEvent(LiveTopics.Devices, p.TenantId, p.LocationId);
                break;
            case Client cl:
                yield return new LiveEvent(LiveTopics.Cash, cl.TenantId, null, null, cl.Id);
                break;
            case CashShift sh:
                yield return new LiveEvent(LiveTopics.Cash, sh.TenantId, sh.LocationId, null, sh.Id);
                break;
            case CashOperation op:
                yield return new LiveEvent(LiveTopics.Cash, op.TenantId, op.LocationId, op.DeviceId, op.ShiftId);
                break;
            case User u:
                yield return new LiveEvent(LiveTopics.Staff, u.OrganizationId, null, null, u.Id);
                break;
        }
    }

    private static string? TargetDevice(string target) =>
        target.StartsWith("device:", StringComparison.Ordinal) ? target["device:".Length..] : null;

    private void Collect(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        var events = context.ChangeTracker.Entries()
            .Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            .SelectMany(e => EventsFor(e.Entity, e.State, ModifiedProperties(e)))
            .ToList();
        if (events.Count == 0)
        {
            return;
        }

        lock (_pending)
        {
            _pending.GetOrCreateValue(context).AddRange(events);
        }
    }

    private static List<string> ModifiedProperties(EntityEntry entry) =>
        entry.State == EntityState.Modified
            ? entry.Properties.Where(p => p.IsModified).Select(p => p.Metadata.Name).ToList()
            : [];

    private void FlushIfNoTransaction(DbContext? context)
    {
        if (context?.Database.CurrentTransaction is null)
        {
            Flush(context);
        }
    }

    private void DropIfNoTransaction(DbContext? context)
    {
        if (context?.Database.CurrentTransaction is null)
        {
            Drop(context);
        }
    }

    private void Flush(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        List<LiveEvent>? events;
        lock (_pending)
        {
            if (!_pending.TryGetValue(context, out events))
            {
                return;
            }

            _pending.Remove(context);
        }

        broker.Publish(events);
    }

    private void Drop(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        lock (_pending)
        {
            _pending.Remove(context);
        }
    }
}
