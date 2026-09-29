using System.Collections.Concurrent;
using System.Text.Json.Serialization;
using System.Threading.Channels;

namespace ClubOS.CloudApi.Live;

/// <summary>Виды изменений, о которых Admin Web узнаёт мгновенно (DEVIATIONS D-008).</summary>
public static class LiveTopics
{
    /// <summary>Устройство: статус, инвентаризация, имя/зона, новое устройство.</summary>
    public const string Devices = "devices";

    public const string Commands = "commands";
    public const string Sessions = "sessions";
    public const string Audit = "audit";

    /// <summary>Edge: на связи/нет, очередь outbox.</summary>
    public const string Edges = "edges";

    public const string Staff = "staff";

    /// <summary>Подписчик отстал (переполнение буфера) — клиенту нужно перечитать всё.</summary>
    public const string Resync = "resync";
}

/// <summary>
/// Подсказка «что-то изменилось» без данных: клиент перечитывает нужный ресурс через REST, где действуют
/// обычные проверки прав. Поэтому push не может раскрыть больше, чем разрешает API.
/// </summary>
public sealed record LiveEvent(
    [property: JsonPropertyName("topic")] string Topic,
    [property: JsonIgnore] string TenantId,
    [property: JsonPropertyName("locationId")] string? LocationId,
    [property: JsonPropertyName("deviceId")] string? DeviceId = null,
    [property: JsonPropertyName("id")] string? Id = null);

/// <summary>
/// Внутрипроцессная рассылка событий подписчикам одного tenant. У каждого подписчика ограниченный буфер:
/// медленный клиент не держит память сервера — при переполнении он получает <see cref="LiveTopics.Resync"/>.
/// Для нескольких экземпляров Cloud API брокер заменяется на PostgreSQL LISTEN/NOTIFY или Redis (D-008).
/// </summary>
public sealed class LiveBroker
{
    public const int BufferSize = 256;

    private readonly ConcurrentDictionary<Guid, Subscription> _subscribers = new();

    public int SubscriberCount => _subscribers.Count;

    public Subscription Subscribe(string tenantId)
    {
        var id = Guid.NewGuid();
        var subscription = new Subscription(tenantId, () => _subscribers.TryRemove(id, out _));
        _subscribers[id] = subscription;
        return subscription;
    }

    public void Publish(LiveEvent evt)
    {
        foreach (var subscription in _subscribers.Values)
        {
            if (subscription.TenantId == evt.TenantId) // tenant isolation
            {
                subscription.Offer(evt);
            }
        }
    }

    public void Publish(IEnumerable<LiveEvent> events)
    {
        foreach (var evt in events.Distinct())
        {
            Publish(evt);
        }
    }
}

public sealed class Subscription : IDisposable
{
    // FullMode.Wait: TryWrite при полном буфере возвращает false (DropWrite «успешно» выбросил бы событие молча),
    // и подписчик узнаёт о потере через флаг переполнения.
    private readonly Channel<LiveEvent> _channel = Channel.CreateBounded<LiveEvent>(
        new BoundedChannelOptions(LiveBroker.BufferSize) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly Action _unsubscribe;
    private int _overflowed;

    internal Subscription(string tenantId, Action unsubscribe)
    {
        TenantId = tenantId;
        _unsubscribe = unsubscribe;
    }

    public string TenantId { get; }

    public ChannelReader<LiveEvent> Reader => _channel.Reader;

    internal void Offer(LiveEvent evt)
    {
        if (!_channel.Writer.TryWrite(evt))
        {
            Interlocked.Exchange(ref _overflowed, 1);
        }
    }

    /// <summary>true — с прошлой проверки были потерянные события: клиенту нужен полный resync.</summary>
    public bool TakeOverflow() => Interlocked.Exchange(ref _overflowed, 0) == 1;

    public void Dispose() => _unsubscribe();
}
