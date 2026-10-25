using ClubOS.Agent.Core;
using ClubOS.Agent.Core.PlayerShell;
using ClubOS.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ClubOS.Unit.Tests;

/// <summary>Player Shell: что видит клиент на ПК при разных состояниях сессии и связи с Edge (M1, ТЗ §26).</summary>
public class PlayerShellTests : IDisposable
{
    private static readonly PriceSnapshot Price = new()
    {
        PricePerHourMinorUnits = 12_000,
        Currency = "TJS",
        Rounding = RoundingRule.CeilingPerMinute,
        RuleVersion = 1
    };

    private readonly TempDir _dir = new();
    private readonly ManualTime _time = new(new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero));
    private readonly ShellPresenter _presenter = new();

    public void Dispose() => _dir.Dispose();

    private PlayerShellController Controller(ShellMode mode, string? pinHash = null) =>
        new(new AgentOptions
        {
            DataPath = _dir.Path,
            Shell = new ShellOptions { Mode = mode, TechnicianPinHash = pinHash, MaintenanceMinutes = 15 }
        }, _presenter, _time, NullLogger<PlayerShellController>.Instance);

    private AgentDeviceState State(AgentSessionInfo? session = null, AgentEndedSessionInfo? ended = null,
        DateTimeOffset? serverTime = null) => new()
        {
            ServerTimeUtc = serverTime ?? _time.Now,
            Stamp = AgentDeviceState.StampFor(session?.SessionId, session?.PlannedEndAtUtc),
            DeviceName = "PC-07",
            LocationName = "Dushanbe Pilot",
            Session = session,
            LastEnded = ended
        };

    private AgentSessionInfo Session(TimeSpan? limit = null, TimeSpan? startedAgo = null) => new()
    {
        SessionId = "ses_1",
        StartedAtUtc = _time.Now - (startedAgo ?? TimeSpan.Zero),
        PlannedEndAtUtc = limit is { } l ? _time.Now - (startedAgo ?? TimeSpan.Zero) + l : null,
        PriceSnapshot = Price
    };

    [Fact]
    public async Task Off_mode_keeps_m0_behaviour()
    {
        var shell = Controller(ShellMode.Off);
        shell.Apply(State(Session()));

        Assert.Null(shell.KnownStamp);
        Assert.Equal(ShellView.Hidden, shell.Compute().View);
        await shell.TickAsync(CancellationToken.None);
        Assert.Empty(_presenter.Updates);
        Assert.False((await shell.RequestMaintenanceAsync("123456", CancellationToken.None)).Ok);
    }

    [Fact]
    public void Enforced_pc_is_closed_until_edge_confirms_a_session()
    {
        var shell = Controller(ShellMode.Enforced);

        var state = shell.Compute();
        Assert.Equal(ShellView.Free, state.View);
        Assert.False(state.EdgeOnline);
        Assert.Equal("unknown", shell.KnownStamp); // Edge ответит на long-poll сразу

        shell.Apply(State());
        state = shell.Compute();
        Assert.Equal(ShellView.Free, state.View);
        Assert.True(state.EdgeOnline);
        Assert.Equal("PC-07", state.DeviceName);
        Assert.Equal("Dushanbe Pilot", state.ClubName);
        Assert.Equal(AgentDeviceState.NoSessionStamp, shell.KnownStamp);
    }

    [Fact]
    public void Hud_mode_does_not_close_a_free_pc()
    {
        var shell = Controller(ShellMode.Hud);
        shell.Apply(State());
        Assert.Equal(ShellView.Hidden, shell.Compute().View);

        shell.Apply(State(Session(TimeSpan.FromHours(1))));
        Assert.Equal(ShellView.Session, shell.Compute().View);
    }

    [Fact]
    public void Session_opens_the_pc_and_carries_timer_data()
    {
        var shell = Controller(ShellMode.Enforced);
        var session = Session(TimeSpan.FromMinutes(30));
        shell.Apply(State(session));

        var state = shell.Compute();
        Assert.Equal(ShellView.Session, state.View);
        Assert.Equal(session.PlannedEndAtUtc, state.Session!.PlannedEndAtUtc);
        Assert.Equal(session.Stamp(), shell.KnownStamp);
    }

    [Fact]
    public void Timer_uses_edge_clock_not_pc_clock()
    {
        var shell = Controller(ShellMode.Enforced);
        // Часы ПК отстают от Edge на 10 минут.
        var edgeNow = _time.Now.AddMinutes(10);
        shell.Apply(State(new AgentSessionInfo
        {
            SessionId = "ses_1",
            StartedAtUtc = edgeNow,
            PlannedEndAtUtc = edgeNow.AddMinutes(30),
            PriceSnapshot = Price
        }, serverTime: edgeNow));

        var state = shell.Compute();
        Assert.Equal(600_000, state.ClockOffsetMs);
        var now = ShellClock.EdgeNow(state, _time.Now);
        Assert.Equal(TimeSpan.FromMinutes(30), ShellClock.Remaining(state.Session!, now));
    }

    [Fact]
    public void Expired_limit_closes_pc_even_without_edge()
    {
        var shell = Controller(ShellMode.Enforced);
        shell.Apply(State(Session(TimeSpan.FromMinutes(30))));

        _time.Advance(TimeSpan.FromMinutes(31)); // Edge молчит (нет связи)
        var state = shell.Compute();

        Assert.Equal(ShellView.Ended, state.View);
        Assert.True(state.Ended!.Estimated);
        Assert.Equal(6_000, state.Ended.TotalMinorUnits);
        Assert.Equal(SessionEndReasons.TimeLimit, state.Ended.Reason);
        Assert.False(state.EdgeOnline);
    }

    [Fact]
    public void Open_session_continues_when_edge_is_unreachable()
    {
        var shell = Controller(ShellMode.Enforced);
        shell.Apply(State(Session()));

        _time.Advance(TimeSpan.FromHours(3));
        var state = shell.Compute();
        Assert.Equal(ShellView.Session, state.View);
        Assert.False(state.EdgeOnline);
    }

    [Fact]
    public void Ended_summary_is_shown_then_pc_returns_to_free()
    {
        var shell = Controller(ShellMode.Enforced);
        shell.Apply(State(ended: new AgentEndedSessionInfo
        {
            SessionId = "ses_1",
            StartedAtUtc = _time.Now.AddHours(-1),
            EndedAtUtc = _time.Now,
            TotalMinorUnits = 12_000,
            Currency = "TJS",
            Reason = SessionEndReasons.Staff
        }));

        var state = shell.Compute();
        Assert.Equal(ShellView.Ended, state.View);
        Assert.False(state.Ended!.Estimated);
        Assert.Equal("Время: 1:00:00   ·   К оплате: 120,00 TJS", ShellText.EndedSummary(state.Ended));

        _time.Advance(PlayerShellController.LastEndedVisibleFor + TimeSpan.FromSeconds(1));
        Assert.Equal(ShellView.Free, shell.Compute().View);
    }

    [Fact]
    public void Last_state_survives_agent_restart()
    {
        Controller(ShellMode.Enforced).Apply(State(Session(TimeSpan.FromHours(2))));

        var restarted = Controller(ShellMode.Enforced);
        var state = restarted.Compute();
        Assert.Equal(ShellView.Session, state.View); // перезапуск службы не выгоняет клиента
        Assert.False(state.EdgeOnline);
        Assert.Equal("unknown", restarted.KnownStamp);
    }

    [Fact]
    public async Task Tick_pushes_only_changes()
    {
        var shell = Controller(ShellMode.Enforced);
        shell.Apply(State());
        await shell.TickAsync(CancellationToken.None);
        await shell.TickAsync(CancellationToken.None);
        Assert.Single(_presenter.Updates);

        shell.Apply(State(Session(TimeSpan.FromMinutes(30))));
        _time.Advance(TimeSpan.FromSeconds(5)); // время идёт, но снимок тот же — таймер считает SessionHost
        await shell.TickAsync(CancellationToken.None);
        await shell.TickAsync(CancellationToken.None);
        Assert.Equal(2, _presenter.Updates.Count);
        Assert.Equal(ShellView.Session, _presenter.Updates[^1].View);
    }

    [Fact]
    public void Small_clock_jitter_does_not_change_the_snapshot()
    {
        var shell = Controller(ShellMode.Enforced);
        shell.Apply(State(Session(TimeSpan.FromMinutes(30))));
        var first = shell.Compute();

        shell.Apply(State(Session(TimeSpan.FromMinutes(30)), serverTime: _time.Now.AddMilliseconds(300)));
        Assert.Equal(first, shell.Compute());
    }

    [Fact]
    public async Task Technician_pin_opens_maintenance_with_lockout()
    {
        var shell = Controller(ShellMode.Enforced, TechnicianPin.Hash("246810"));
        shell.Apply(State());
        Assert.Same(shell, _presenter.Input);

        for (var i = 0; i < 5; i++)
        {
            Assert.False((await shell.RequestMaintenanceAsync("000000", CancellationToken.None)).Ok);
        }

        var locked = await shell.RequestMaintenanceAsync("246810", CancellationToken.None);
        Assert.False(locked.Ok); // 5 ошибок → блокировка даже для верного PIN
        Assert.Contains("5 минут", locked.Error);

        _time.Advance(TimeSpan.FromMinutes(6));
        Assert.True((await shell.RequestMaintenanceAsync("246810", CancellationToken.None)).Ok);
        Assert.True(shell.InMaintenance);
        var state = shell.Compute();
        Assert.Equal(ShellView.Maintenance, state.View);
        Assert.Equal(_time.Now.AddMinutes(15), state.MaintenanceUntilUtc);

        shell.EndMaintenance();
        Assert.False(shell.InMaintenance);
        Assert.Equal(ShellView.Free, shell.Compute().View);
    }

    [Fact]
    public async Task Maintenance_expires_by_itself()
    {
        var shell = Controller(ShellMode.Enforced, TechnicianPin.Hash("246810"));
        Assert.True((await shell.RequestMaintenanceAsync("246810", CancellationToken.None)).Ok);
        _time.Advance(TimeSpan.FromMinutes(15) + TimeSpan.FromSeconds(1));
        Assert.False(shell.InMaintenance);
        Assert.Equal(ShellView.Free, shell.Compute().View);
    }

    [Fact]
    public async Task Maintenance_is_unavailable_without_configured_pin()
    {
        var shell = Controller(ShellMode.Enforced);
        var result = await shell.RequestMaintenanceAsync("246810", CancellationToken.None);
        Assert.False(result.Ok);
        Assert.Contains("не настроен", result.Error);
    }

    private sealed class ShellPresenter : IUserPresenter
    {
        public List<ShellState> Updates { get; } = [];
        public IShellInput? Input { get; private set; }
        public bool IsLocked => false;

        public Task<PresentResult> ShowMessageAsync(string commandId, string title, string message, CancellationToken ct) =>
            Task.FromResult(PresentResult.Success);

        public Task<PresentResult> SetLockAsync(string commandId, bool locked, string? reason, CancellationToken ct) =>
            Task.FromResult(PresentResult.Success);

        public Task<PresentResult> UpdateShellAsync(ShellState state, CancellationToken ct)
        {
            Updates.Add(state);
            return Task.FromResult(PresentResult.Success);
        }

        public void AttachShellInput(IShellInput input) => Input = input;
    }
}

internal static class AgentSessionInfoExtensions
{
    public static string Stamp(this AgentSessionInfo session) =>
        AgentDeviceState.StampFor(session.SessionId, session.PlannedEndAtUtc);
}
