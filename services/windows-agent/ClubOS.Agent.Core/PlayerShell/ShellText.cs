using ClubOS.Contracts;

namespace ClubOS.Agent.Core.PlayerShell;

/// <summary>Тексты Player Shell (RU). Вынесены из WinForms, чтобы проверяться unit-тестами.</summary>
public static class ShellText
{
    public const string FreeTitle = "Компьютер свободен";
    public const string FreeHint = "Чтобы начать игру, обратитесь к администратору клуба";
    public const string EndedTitle = "Сессия завершена";
    public const string EdgeOffline = "Нет связи с сервером клуба — идущая сессия продолжается";
    public const string TechnicianHint = "Ctrl+Shift+F12 — вход техника";

    public sealed record HudText(string Time, string Cost, string? Warning, ShellWarning Level);

    public static HudText Hud(ShellSession session, DateTimeOffset edgeNow)
    {
        var cost = $"Стоимость {ShellClock.FormatMoney(ShellClock.CurrentCost(session, edgeNow), session.PriceSnapshot.Currency)}";
        var remaining = ShellClock.Remaining(session, edgeNow);
        var level = ShellClock.Warning(session, edgeNow);
        var time = remaining is { } r
            ? $"Осталось {ShellClock.FormatDuration(r)}"
            : $"Идёт {ShellClock.FormatDuration(ShellClock.Elapsed(session, edgeNow))}";
        var warning = level switch
        {
            ShellWarning.Critical => "Меньше минуты! Сохраните игру — ПК будет закрыт",
            ShellWarning.Soon => "Скоро конец времени — продлите у администратора",
            _ => null
        };
        return new HudText(time, cost, warning, level);
    }

    public static string EndReason(string? reason) => reason switch
    {
        SessionEndReasons.TimeLimit => "Оплаченное время закончилось",
        SessionEndReasons.Staff => "Сессию завершил администратор",
        _ => "Сессия закрыта"
    };

    public static string EndedSummary(ShellEnded ended)
    {
        var duration = ShellClock.FormatDuration(ended.EndedAtUtc - ended.StartedAtUtc);
        var total = ShellClock.FormatMoney(ended.TotalMinorUnits, ended.Currency);
        return $"Время: {duration}   ·   К оплате: {total}" + (ended.Estimated ? " (предварительно)" : string.Empty);
    }

    public static string MaintenanceBanner(DateTimeOffset untilLocal) =>
        $"Режим обслуживания до {untilLocal:HH:mm}. Экран клуба вернётся автоматически.";
}
