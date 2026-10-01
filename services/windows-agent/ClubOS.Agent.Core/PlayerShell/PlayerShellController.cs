using System.Text.Json;
using ClubOS.Contracts;
using Microsoft.Extensions.Logging;

namespace ClubOS.Agent.Core.PlayerShell;

/// <summary>
/// Решает, что показывает Player Shell, по состоянию устройства от Edge (ТЗ §26). Edge — источник истины
/// о сессии; агент ничего не начинает и не завершает сам. Правила при потере связи с Edge:
/// <list type="bullet">
/// <item>идущая сессия продолжается (сбой LAN не должен выгонять клиента);</item>
/// <item>сессия с лимитом закрывается по плановому окончанию и без Edge — Edge завершит её тем же временем;</item>
/// <item>последнее известное состояние хранится на диске: перезапуск агента посреди сессии не блокирует ПК.</item>
/// </list>
/// </summary>
public sealed class PlayerShellController : IShellInput
{
    public const string StateFileName = "shell-state.json";
    private const int MaxPinFailures = 5;
    private static readonly TimeSpan PinFailureWindow = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan OffsetJitter = TimeSpan.FromSeconds(1);

    private readonly ShellOptions _options;
    private readonly IUserPresenter _presenter;
    private readonly TimeProvider _time;
    private readonly ILogger<PlayerShellController> _logger;
    private readonly string _statePath;
    private readonly Lock _gate = new();
    private readonly Queue<DateTimeOffset> _pinFailures = new();
    private readonly SemaphoreSlim _tickLock = new(1, 1);

    private AgentDeviceState? _device;
    private TimeSpan _offset;
    private string? _notice;
    private DateTimeOffset? _lastContactLocal;
    private DateTimeOffset? _maintenanceUntilLocal;
    private ShellState? _pushed;

    public PlayerShellController(AgentOptions options, IUserPresenter presenter, TimeProvider time,
        ILogger<PlayerShellController> logger)
    {
        _options = options.Shell;
        _presenter = presenter;
        _time = time;
        _logger = logger;
        _statePath = Path.Combine(options.DataPath, StateFileName);
        LoadPersisted();
        presenter.AttachShellInput(this);
    }

    public ShellMode Mode => _options.Mode;

    /// <summary>Отпечаток состояния для long-poll: Edge ответит сразу, если его состояние другое. Off — не нужен.</summary>
    public string? KnownStamp
    {
        get
        {
            if (_options.Mode == ShellMode.Off)
            {
                return null;
            }

            lock (_gate)
            {
                // Пока нет подтверждённого ответа Edge — «неизвестно», чтобы получить состояние немедленно.
                return _lastContactLocal is null ? "unknown" : _device?.Stamp;
            }
        }
    }

    public bool InMaintenance
    {
        get
        {
            lock (_gate)
            {
                return _maintenanceUntilLocal is { } until && until > _time.GetUtcNow();
            }
        }
    }

    /// <summary>Ответ Edge с состоянием устройства (heartbeat или long-poll).</summary>
    public void Apply(AgentDeviceState state)
    {
        var localNow = _time.GetUtcNow();
        bool persist;
        lock (_gate)
        {
            var offset = state.ServerTimeUtc - localNow;
            if ((offset - _offset).Duration() > OffsetJitter || _lastContactLocal is null)
            {
                _offset = offset;
            }

            persist = _device?.Stamp != state.Stamp || _device?.LastEnded?.SessionId != state.LastEnded?.SessionId;
            _device = state;
            _lastContactLocal = localNow;
        }

        if (persist)
        {
            Persist(state);
        }
    }

    /// <summary>Подсказка на экране клуба (null — убрать). Показывается со следующим тиком.</summary>
    public void SetNotice(string? notice)
    {
        lock (_gate)
        {
            _notice = notice;
        }
    }

    public ShellState Compute()
    {
        var localNow = _time.GetUtcNow();
        lock (_gate)
        {
            var edgeNow = localNow + _offset;
            var edgeOnline = _lastContactLocal is { } contact &&
                             localNow - contact <= TimeSpan.FromSeconds(_options.EdgeOfflineAfterSeconds);
            var baseState = new ShellState
            {
                Mode = _options.Mode,
                View = ShellView.Hidden,
                DeviceName = _device?.DeviceName ?? Environment.MachineName,
                ClubName = _device?.LocationName,
                ClockOffsetMs = (long)_offset.TotalMilliseconds,
                EdgeOnline = edgeOnline,
                Notice = _notice
            };

            if (_options.Mode == ShellMode.Off)
            {
                return baseState;
            }

            if (_maintenanceUntilLocal is { } until && until > localNow)
            {
                return baseState with { View = ShellView.Maintenance, MaintenanceUntilUtc = until + _offset };
            }

            if (_device?.Session is { } s)
            {
                if (s.PlannedEndAtUtc is { } planned && planned <= edgeNow)
                {
                    // Лимит истёк, а Edge ещё не сообщил о завершении (задержка или нет связи): закрываем ПК
                    // по плановому окончанию и показываем оценку — Edge посчитает то же самое.
                    return baseState with
                    {
                        View = ShellView.Ended,
                        Ended = new ShellEnded
                        {
                            SessionId = s.SessionId,
                            StartedAtUtc = s.StartedAtUtc,
                            EndedAtUtc = planned,
                            TotalMinorUnits = BillingCalculator.CalculateMinorUnits(s.PriceSnapshot, s.StartedAtUtc, planned - s.StartedAtUtc),
                            Currency = s.PriceSnapshot.Currency,
                            Reason = SessionEndReasons.TimeLimit,
                            Estimated = true
                        }
                    };
                }

                return baseState with
                {
                    View = ShellView.Session,
                    Session = new ShellSession
                    {
                        SessionId = s.SessionId,
                        StartedAtUtc = s.StartedAtUtc,
                        PlannedEndAtUtc = s.PlannedEndAtUtc,
                        PriceSnapshot = s.PriceSnapshot
                    }
                };
            }

            if (_device?.LastEnded is { } e && edgeNow - e.EndedAtUtc <= LastEndedVisibleFor)
            {
                return baseState with
                {
                    View = ShellView.Ended,
                    Ended = new ShellEnded
                    {
                        SessionId = e.SessionId,
                        StartedAtUtc = e.StartedAtUtc,
                        EndedAtUtc = e.EndedAtUtc,
                        TotalMinorUnits = e.TotalMinorUnits,
                        Currency = e.Currency,
                        Reason = e.Reason
                    }
                };
            }

            return baseState with { View = _options.Mode == ShellMode.Enforced ? ShellView.Free : ShellView.Hidden };
        }
    }

    /// <summary>Итог сессии виден на экране не дольше этого времени (как на Edge).</summary>
    public static readonly TimeSpan LastEndedVisibleFor = TimeSpan.FromMinutes(10);

    /// <summary>Пересчитывает состояние и отправляет в SessionHost только при изменении.</summary>
    public async Task TickAsync(CancellationToken ct)
    {
        if (_options.Mode == ShellMode.Off)
        {
            return;
        }

        // Тик идёт и из своего цикла, и сразу после ответа Edge — отправки сериализуются.
        await _tickLock.WaitAsync(ct);
        try
        {
            var state = Compute();
            if (state == _pushed)
            {
                return;
            }

            if (_pushed is null || _pushed.View != state.View)
            {
                _logger.LogInformation("Player Shell: {From} → {To}", _pushed?.View.ToString() ?? "—", state.View);
            }

            _pushed = state;
            var result = await _presenter.UpdateShellAsync(state, ct);
            if (!result.Ok)
            {
                // SessionHost не подключён: презентер хранит последнее состояние и отправит его при подключении.
                _logger.LogDebug("Player Shell не обновлён: {Error}", result.Error);
            }
        }
        finally
        {
            _tickLock.Release();
        }
    }

    public async Task RunAsync(CancellationToken ct)
    {
        if (_options.Mode == ShellMode.Off)
        {
            return;
        }

        _logger.LogInformation("Player Shell включён, режим {Mode}", _options.Mode);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await TickAsync(ct);
                await Task.Delay(TimeSpan.FromSeconds(1), _time, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка Player Shell");
            }
        }
    }

    public Task<PresentResult> RequestMaintenanceAsync(string pin, CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        if (_options.Mode == ShellMode.Off)
        {
            return Task.FromResult(PresentResult.Fail("Player Shell выключен."));
        }

        if (string.IsNullOrWhiteSpace(_options.TechnicianPinHash))
        {
            return Task.FromResult(PresentResult.Fail("PIN техника не настроен на этом ПК."));
        }

        lock (_gate)
        {
            while (_pinFailures.TryPeek(out var t) && now - t > PinFailureWindow)
            {
                _pinFailures.Dequeue();
            }

            if (_pinFailures.Count >= MaxPinFailures)
            {
                return Task.FromResult(PresentResult.Fail("Слишком много неверных попыток. Подождите 5 минут."));
            }
        }

        if (!TechnicianPin.Verify(_options.TechnicianPinHash, pin))
        {
            lock (_gate)
            {
                _pinFailures.Enqueue(now);
            }

            _logger.LogWarning("Неверный PIN техника на экране Player Shell");
            return Task.FromResult(PresentResult.Fail("Неверный PIN."));
        }

        lock (_gate)
        {
            _pinFailures.Clear();
            _maintenanceUntilLocal = now.AddMinutes(Math.Clamp(_options.MaintenanceMinutes, 1, 240));
        }

        _logger.LogWarning("Режим обслуживания включён PIN техника на {Minutes} мин", _options.MaintenanceMinutes);
        return Task.FromResult(PresentResult.Success);
    }

    public void EndMaintenance()
    {
        lock (_gate)
        {
            if (_maintenanceUntilLocal is null)
            {
                return;
            }

            _maintenanceUntilLocal = null;
        }

        _logger.LogWarning("Режим обслуживания завершён техником");
    }

    private void LoadPersisted()
    {
        if (_options.Mode == ShellMode.Off || !File.Exists(_statePath))
        {
            return;
        }

        try
        {
            var saved = JsonSerializer.Deserialize<PersistedShellState>(File.ReadAllText(_statePath), ContractJson.Options);
            if (saved?.Device is not null)
            {
                _device = saved.Device;
                _offset = TimeSpan.FromMilliseconds(saved.ClockOffsetMs);
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Сохранённое состояние Player Shell не прочитано: {Error}", ex.Message);
        }
    }

    private void Persist(AgentDeviceState state)
    {
        if (_options.Mode == ShellMode.Off)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
            var tmp = _statePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(
                new PersistedShellState(state, (long)_offset.TotalMilliseconds), ContractJson.Options));
            File.Move(tmp, _statePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Состояние Player Shell не сохранено: {Error}", ex.Message);
        }
    }

    private sealed record PersistedShellState(AgentDeviceState Device, long ClockOffsetMs);
}
