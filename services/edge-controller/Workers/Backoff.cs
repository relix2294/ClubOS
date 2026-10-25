namespace ClubOS.EdgeController.Workers;

/// <summary>Экспоненциальный backoff с jitter для повторов к Cloud (ТЗ §23.3: retry/backoff).</summary>
public sealed class Backoff(TimeSpan max)
{
    private int _failures;

    public void Reset() => _failures = 0;

    public TimeSpan Next()
    {
        _failures = Math.Min(_failures + 1, 16);
        var seconds = Math.Min(max.TotalSeconds, Math.Pow(2, _failures - 1));
        var jitter = Random.Shared.NextDouble() * 0.25 * seconds;
        return TimeSpan.FromSeconds(seconds + jitter);
    }
}
