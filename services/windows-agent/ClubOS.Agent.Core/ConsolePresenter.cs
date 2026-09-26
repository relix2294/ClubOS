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

    public Task<PresentResult> SetLockAsync(string commandId, bool locked, string? reason, CancellationToken ct)
    {
        _locked = locked;
        logger.LogInformation("[{Label}] LockTestMode = {Locked} {Reason}", label, locked, reason);
        return Task.FromResult(PresentResult.Success);
    }
}
