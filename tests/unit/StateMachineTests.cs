using ClubOS.Contracts;
using Xunit;

namespace ClubOS.Unit.Tests;

public class StateMachineTests
{
    [Theory]
    [InlineData(CommandState.Queued, CommandState.Delivered, true)]
    [InlineData(CommandState.Queued, CommandState.Succeeded, true)]
    [InlineData(CommandState.Delivered, CommandState.Acknowledged, true)]
    [InlineData(CommandState.Acknowledged, CommandState.Failed, true)]
    [InlineData(CommandState.Delivered, CommandState.Expired, true)]
    [InlineData(CommandState.Acknowledged, CommandState.Delivered, false)] // назад нельзя
    [InlineData(CommandState.Succeeded, CommandState.Failed, false)]       // терминальное неизменно
    [InlineData(CommandState.Expired, CommandState.Succeeded, false)]
    [InlineData(CommandState.Delivered, CommandState.Delivered, false)]    // повтор не переход
    public void Command_transitions_only_forward(CommandState from, CommandState to, bool allowed) =>
        Assert.Equal(allowed, CommandStateMachine.CanTransition(from, to));

    [Theory]
    [InlineData(SessionState.Created, SessionState.Active, true)]
    [InlineData(SessionState.Active, SessionState.Ended, true)]
    [InlineData(SessionState.Created, SessionState.Failed, true)]
    [InlineData(SessionState.Ended, SessionState.Active, false)] // ended нельзя вернуть в active (ТЗ §12.2.1)
    [InlineData(SessionState.Ended, SessionState.Ended, false)]
    [InlineData(SessionState.Failed, SessionState.Active, false)]
    public void Session_transitions(SessionState from, SessionState to, bool allowed) =>
        Assert.Equal(allowed, SessionStateMachine.CanTransition(from, to));
}
