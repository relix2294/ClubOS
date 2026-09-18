using ClubOS.CloudApi.Api;
using ClubOS.Contracts;
using Xunit;

namespace ClubOS.Integration.Tests;

/// <summary>Чистые тесты машины состояний команды (ТЗ §10.2) — без БД/Docker.</summary>
public sealed class CommandStateMachineTests
{
    [Fact]
    public void ForwardTransition_IsAllowed()
    {
        Assert.True(CommandStateMachine.CanAdvance(CommandState.Queued, CommandState.Delivered));
        Assert.True(CommandStateMachine.CanAdvance(CommandState.Delivered, CommandState.Succeeded));
    }

    [Fact]
    public void BackwardTransition_IsBlocked()
    {
        Assert.False(CommandStateMachine.CanAdvance(CommandState.Acknowledged, CommandState.Queued));
    }

    [Fact]
    public void TerminalState_DoesNotAdvance()
    {
        Assert.False(CommandStateMachine.CanAdvance(CommandState.Succeeded, CommandState.Failed));
        Assert.False(CommandStateMachine.CanAdvance(CommandState.Expired, CommandState.Succeeded));
    }

    [Theory]
    [InlineData(CommandState.Succeeded)]
    [InlineData(CommandState.Failed)]
    [InlineData(CommandState.Expired)]
    [InlineData(CommandState.Cancelled)]
    public void TerminalStates_AreTerminal(CommandState state)
    {
        Assert.True(CommandStateMachine.IsTerminal(state));
    }
}
