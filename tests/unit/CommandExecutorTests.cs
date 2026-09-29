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
