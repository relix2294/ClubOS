using ClubOS.Contracts;

namespace ClubOS.EdgeController.Data;

/// <summary>Переходы жизненного цикла команды строго вперёд (ТЗ §10.2 CMD-003), как в Cloud.</summary>
public static class CommandStateMachine
{
    public static bool IsTerminal(CommandState state) => state is
        CommandState.Succeeded or CommandState.Failed or CommandState.Expired or CommandState.Cancelled;

    private static int Rank(CommandState state) => state switch
    {
        CommandState.Queued => 0,
        CommandState.Delivered => 1,
        CommandState.Acknowledged => 2,
        _ => 3,
    };

    public static bool CanAdvance(CommandState current, CommandState next) =>
        !IsTerminal(current) && Rank(next) >= Rank(current);
}
