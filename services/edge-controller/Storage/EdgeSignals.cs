using System.Collections.Concurrent;

namespace ClubOS.EdgeController.Storage;

/// <summary>
/// Внутрипроцессные сигналы «появились данные»: будят outbox publisher и long-poll агентов
/// без ожидания таймера. Потеря сигнала безопасна — все циклы имеют таймаут-фолбэк.
/// </summary>
public sealed class EdgeSignals
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource> _devices = new();
    private TaskCompletionSource _outbox = NewTcs();

    public void NotifyOutbox() => Interlocked.Exchange(ref _outbox, NewTcs()).TrySetResult();

    public Task WaitOutboxAsync(TimeSpan timeout, CancellationToken ct) =>
        Task.WhenAny(Volatile.Read(ref _outbox).Task, Task.Delay(timeout, ct));

    public void NotifyDevice(string deviceId)
    {
        if (_devices.TryRemove(deviceId, out var tcs))
        {
            tcs.TrySetResult();
        }
    }

    public Task WaitDeviceAsync(string deviceId, TimeSpan timeout, CancellationToken ct) =>
        Task.WhenAny(_devices.GetOrAdd(deviceId, _ => NewTcs()).Task, Task.Delay(timeout, ct));

    private static TaskCompletionSource NewTcs() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
