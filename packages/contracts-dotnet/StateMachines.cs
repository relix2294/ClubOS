namespace ClubOS.Contracts;

/// <summary>
/// Правила переходов жизненного цикла команды (ТЗ §10.2 CMD-003).
/// Переходы только вперёд: Queued → Delivered → Acknowledged → терминальное состояние.
/// Терминальные состояния (Succeeded/Failed/Expired/Cancelled) не меняются никогда.
/// </summary>
public static class CommandStateMachine
{
    public static bool IsTerminal(CommandState state) =>
        state is CommandState.Succeeded or CommandState.Failed or CommandState.Expired or CommandState.Cancelled;

    /// <summary>Можно ли перейти из <paramref name="from"/> в <paramref name="to"/>.</summary>
    public static bool CanTransition(CommandState from, CommandState to)
    {
        if (IsTerminal(from) || from == to)
        {
            return false;
        }

        return Rank(to) > Rank(from);
    }

    private static int Rank(CommandState state) => state switch
    {
        CommandState.Queued => 0,
        CommandState.Delivered => 1,
        CommandState.Acknowledged => 2,
        _ => 3 // любое терминальное
    };
}

/// <summary>
/// Базовый путь сессии M0 (ТЗ §12.2.1): Created → Active → Ended; Created → Failed.
/// Ended нельзя вернуть в Active.
/// </summary>
public static class SessionStateMachine
{
    public static bool CanTransition(SessionState from, SessionState to) => (from, to) switch
    {
        (SessionState.Created, SessionState.Active) => true,
        (SessionState.Created, SessionState.Failed) => true,
        (SessionState.Created, SessionState.Ended) => true, // старт и конец пришли одним batch
        (SessionState.Active, SessionState.Ended) => true,
        _ => false
    };
}
