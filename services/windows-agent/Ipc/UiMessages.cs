namespace ClubOS.WindowsAgent.Ipc;

/// <summary>Запрос на отображение UI, передаётся сервис → session-host по named pipe.</summary>
public sealed record UiRequest
{
    /// <summary>"ShowMessage" или "LockTestMode".</summary>
    public required string Kind { get; init; }
    public string? Title { get; init; }
    public string? Message { get; init; }

    /// <summary>Для LockTestMode: true — включить overlay, false — снять.</summary>
    public bool Lock { get; init; }

    /// <summary>Для ShowMessage: авто-закрытие через N секунд (0 — не закрывать автоматически).</summary>
    public int AutoCloseSeconds { get; init; } = 10;
}

/// <summary>Ответ session-host сервису.</summary>
public sealed record UiResponse
{
    public bool Ok { get; init; }
    public string? Error { get; init; }
}
