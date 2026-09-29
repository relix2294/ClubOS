using ClubOS.Agent.Core.PlayerShell;
using Microsoft.Extensions.Logging;

namespace ClubOS.Agent.Core;

/// <summary>Презентер для dev-режима вне Windows: вместо UI пишет в лог. Явно помечен как dev.</summary>
public sealed class ConsolePresenter(ILogger<ConsolePresenter> logger, string label = "DEV CONSOLE") : IUserPresenter
{
    private volatile bool _locked;

    public bool IsLocked => _locked;

    public Task<PresentResult> ShowMessageAsync(string commandId, string title, string message, CancellationToken ct)
    {
        logger.LogInformation("[{Label}] ShowMessage «{Title}»: {Message}", label, title, message);
        return Task.FromResult(PresentResult.Success);
    }

    public ShellState? Shell { get; private set; }

    public Task<PresentResult> UpdateShellAsync(ShellState state, CancellationToken ct)
    {
        if (Shell?.View != state.View)
        {
            var details = state.View switch
            {
                ShellView.Session when state.Session?.PlannedEndAtUtc is { } end => $"лимит до {end:HH:mm:ss} UTC",
                ShellView.Session => "без лимита",
                ShellView.Ended when state.Ended is { } e => $"итог {ShellClock.FormatMoney(e.TotalMinorUnits, e.Currency)}",
                _ => string.Empty
            };
            logger.LogInformation("[{Label}] Player Shell: {View} {Details}", label, state.View, details);
        }

        Shell = state;
        return Task.FromResult(PresentResult.Success);
    }

    public Task<PresentResult> SetLockAsync(string commandId, bool locked, string? reason, CancellationToken ct)
    {
        _locked = locked;
        logger.LogInformation("[{Label}] LockTestMode = {Locked} {Reason}", label, locked, reason);
        return Task.FromResult(PresentResult.Success);
    }
}
