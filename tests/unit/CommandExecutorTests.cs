using ClubOS.Agent.Core;
using ClubOS.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ClubOS.Unit.Tests;

/// <summary>Исполнение команд агентом: дедупликация, срок, адресат, allow-list (ТЗ §10.2).</summary>
public class CommandExecutorTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly ManualTime _time = new(DateTimeOffset.UtcNow);
    private readonly RecordingPresenter _presenter = new();
    private readonly List<(CommandState State, string? Error)> _reports = [];

    public void Dispose() => _dir.Dispose();

    private CommandExecutor Executor() =>
        new(_presenter, new ExecutedCommandStore(_dir.Path), _time, NullLogger<CommandExecutor>.Instance);

    private Task Run(CommandExecutor executor, CommandEnvelope command, string deviceId = "dev_1") =>
        executor.ExecuteAsync(deviceId, command, (s, e) =>
        {
            _reports.Add((s, e));
            return Task.CompletedTask;
        }, CancellationToken.None);

    private CommandEnvelope Show(string id = "cmd_1", int ttlSeconds = 60) => new()
    {
        CommandId = id,
        CommandType = CommandType.ShowMessage,
        TargetDeviceIds = ["dev_1"],
        IssuedBy = "user:1",
        IssuedAtUtc = _time.GetUtcNow(),
        ExpiresAtUtc = _time.GetUtcNow().AddSeconds(ttlSeconds),
        CorrelationId = "cor",
        Payload = ContractJson.ToElement(new ShowMessagePayload { Title = "Привет", Message = "Тест" })
    };

    [Fact]
    public async Task ShowMessage_is_acknowledged_then_succeeds()
    {
        await Run(Executor(), Show());
        Assert.Equal([CommandState.Acknowledged, CommandState.Succeeded], _reports.Select(r => r.State));
        Assert.Equal(1, _presenter.Shown);
    }

    [Fact]
    public async Task Duplicate_delivery_does_not_execute_twice_even_after_restart()
    {
        await Run(Executor(), Show());
        await Run(Executor(), Show()); // новый экземпляр = перезапуск агента, журнал на диске

        Assert.Equal(1, _presenter.Shown);
        Assert.Equal(CommandState.Succeeded, _reports[^1].State); // итог сообщён повторно
    }

    [Fact]
    public async Task Expired_command_is_not_executed()
    {
        var command = Show(ttlSeconds: 5);
        _time.Advance(TimeSpan.FromSeconds(6));
        await Run(Executor(), command);

        Assert.Equal(0, _presenter.Shown);
        Assert.Equal(CommandState.Failed, Assert.Single(_reports).State);
    }

    [Fact]
    public async Task Command_for_other_device_is_refused()
    {
        await Run(Executor(), Show(), deviceId: "dev_2");
        Assert.Equal(0, _presenter.Shown);
        Assert.Equal(CommandState.Failed, Assert.Single(_reports).State);
    }

    [Fact]
    public async Task Oversized_payload_fails_without_ui()
    {
        var command = Show() with
        {
            Payload = ContractJson.ToElement(new ShowMessagePayload { Title = "t", Message = new string('x', 501) })
        };
        await Run(Executor(), command);
        Assert.Equal(0, _presenter.Shown);
        Assert.Equal(CommandState.Failed, _reports[^1].State);
    }

    [Fact]
    public async Task Presenter_failure_is_reported_as_failed()
    {
        _presenter.FailWith = "AgentSessionHost не подключён.";
        await Run(Executor(), Show());
        Assert.Equal((CommandState.Failed, "AgentSessionHost не подключён."), _reports[^1]);
    }

    [Fact]
    public async Task LockTestMode_toggles_presenter_lock()
    {
        var command = Show("cmd_lock") with
        {
            CommandType = CommandType.LockTestMode,
            Payload = ContractJson.ToElement(new LockTestModePayload { Lock = true })
        };
        await Run(Executor(), command);
        Assert.True(_presenter.IsLocked);
    }

    // ---- Удалённый доступ (D-022) ----

    private readonly List<(CommandState State, string? Error, System.Text.Json.JsonElement? Output)> _full = [];

    private CommandEnvelope Remote(CommandType type, object? payload = null, string id = "cmd_r") => Show(id) with
    {
        CommandType = type,
        Payload = ContractJson.ToElement(payload ?? new { })
    };

    private Task RunRemote(CommandEnvelope command, IRemoteActions? remote) =>
        new CommandExecutor(_presenter, new ExecutedCommandStore(_dir.Path), _time, NullLogger<CommandExecutor>.Instance, remote)
            .ExecuteAsync("dev_1", command, (s, e, o) =>
            {
                _full.Add((s, e, o));
                return Task.CompletedTask;
            }, CancellationToken.None);

    [Fact]
    public async Task Screenshot_returns_image_output_only_with_success()
    {
        await RunRemote(Remote(CommandType.Screenshot), new SimulatedRemoteActions("PC-01", _time));
        Assert.Null(_full[0].Output); // Acknowledged — без данных
        var done = _full[^1];
        Assert.Equal(CommandState.Succeeded, done.State);
        var shot = ContractJson.FromElement<ScreenshotOutput>(done.Output!.Value);
        Assert.Equal("image/svg+xml", shot.Mime);
        Assert.Contains("PC-01", System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(shot.DataBase64)));
    }

    [Fact]
    public async Task Process_list_and_kill_with_protection()
    {
        var remote = new SimulatedRemoteActions("PC-01", _time);
        await RunRemote(Remote(CommandType.ListProcesses, id: "cmd_l"), remote);
        var list = ContractJson.FromElement<ProcessListOutput>(_full[^1].Output!.Value).Processes;
        var game = list.Single(p => p.Name == "cs2");

        await RunRemote(Remote(CommandType.KillProcess, new KillProcessPayload { ProcessId = game.ProcessId, Name = "winlogon" }, "cmd_k1"), remote);
        Assert.Equal(CommandState.Failed, _full[^1].State);
        Assert.Contains("системный", _full[^1].Error);

        await RunRemote(Remote(CommandType.KillProcess, new KillProcessPayload { ProcessId = game.ProcessId, Name = "cs2" }, "cmd_k2"), remote);
        Assert.Equal(CommandState.Succeeded, _full[^1].State);
        Assert.DoesNotContain(ContractJson.FromElement<ProcessListOutput>(remote.ListProcesses().Output!.Value).Processes, p => p.Name == "cs2");

        // PID занят другим именем — отказ.
        await RunRemote(Remote(CommandType.KillProcess, new KillProcessPayload { ProcessId = 4120, Name = "chrome" }, "cmd_k3"), remote);
        Assert.Equal(CommandState.Failed, _full[^1].State);
    }

    [Theory]
    [InlineData("ClubOS.Agent.SessionHost")]
    [InlineData("LSASS")]
    [InlineData("explorer")]
    public void System_and_clubos_processes_are_protected(string name) => Assert.True(ProtectedProcesses.IsProtected(name));

    [Fact]
    public async Task Power_validates_delay_and_works_without_ui()
    {
        var remote = new SimulatedRemoteActions("PC-01", _time);
        await RunRemote(Remote(CommandType.Reboot, new PowerPayload { DelaySeconds = 9999 }, "cmd_p1"), remote);
        Assert.Equal(CommandState.Failed, _full[^1].State);
        await RunRemote(Remote(CommandType.Shutdown, new PowerPayload { DelaySeconds = 30, Message = "Закрываемся" }, "cmd_p2"), remote);
        Assert.Equal(CommandState.Succeeded, _full[^1].State);
        Assert.Equal(0, _presenter.Shown);
    }

    [Fact]
    public async Task Remote_commands_fail_where_remote_access_is_unavailable()
    {
        await RunRemote(Remote(CommandType.Screenshot), remote: null);
        Assert.Equal(CommandState.Failed, _full[^1].State);
        Assert.Contains("недоступен", _full[^1].Error);
    }

    [Fact]
    public async Task Oversized_output_is_not_sent()
    {
        await RunRemote(Remote(CommandType.Screenshot), new HugeScreenshot());
        Assert.Equal(CommandState.Failed, _full[^1].State);
        Assert.Null(_full[^1].Output);
    }

    private sealed class HugeScreenshot : IRemoteActions
    {
        public Task<RemoteResult> ScreenshotAsync(CancellationToken ct) => Task.FromResult(RemoteResult.Success(new ScreenshotOutput
        {
            Mime = "image/jpeg",
            DataBase64 = new string('A', RemoteLimits.MaxOutputChars + 1),
            CapturedAtUtc = DateTimeOffset.UtcNow
        }));

        public RemoteResult ListProcesses() => RemoteResult.Done;
        public RemoteResult KillProcess(int processId, string name) => RemoteResult.Done;
        public RemoteResult Power(bool reboot, int delaySeconds, string? message) => RemoteResult.Done;
    }

    private sealed class RecordingPresenter : IUserPresenter
    {
        public int Shown { get; private set; }
        public string? FailWith { get; set; }
        public bool IsLocked { get; private set; }

        public Task<PresentResult> ShowMessageAsync(string commandId, string title, string message, CancellationToken ct)
        {
            if (FailWith is not null)
            {
                return Task.FromResult(PresentResult.Fail(FailWith));
            }

            Shown++;
            return Task.FromResult(PresentResult.Success);
        }

        public Task<PresentResult> SetLockAsync(string commandId, bool locked, string? reason, CancellationToken ct)
        {
            IsLocked = locked;
            return Task.FromResult(PresentResult.Success);
        }

        public Task<PresentResult> UpdateShellAsync(ClubOS.Agent.Core.PlayerShell.ShellState state, CancellationToken ct) =>
            Task.FromResult(PresentResult.Success);
    }
}
