using ClubOS.Agent.Core.PlayerShell;
using ClubOS.Contracts;

namespace ClubOS.Agent.Core;

public sealed class AgentOptions
{
    public const string Section = "Agent";

    /// <summary>URL Edge Controller в LAN клуба, напр. http://edge.local:7070.</summary>
    public string EdgeUrl { get; set; } = "http://localhost:7070";

    /// <summary>
    /// SHA-256 отпечаток dev CA (Admin Web → Подключение) для первого подключения к Edge по HTTPS, пока CA ещё
    /// не закреплён при регистрации. Без него агент не доверяет TLS-сертификату Edge (D-007).
    /// </summary>
    public string? EdgeCaFingerprint { get; set; }

    /// <summary>Одноразовый enrollment-токен (нужен только до первой регистрации).</summary>
    public string? EnrollmentToken { get; set; }

    /// <summary>
    /// Бездисковый ПК (D-018): диск сбрасывается при каждой загрузке, один образ на все ПК. Токен не нужен:
    /// при запуске агент создаёт новый ключ и получает сертификат у Edge по MAC загрузочной карты;
    /// новый ПК ждёт подтверждения в Admin Web. Сохранённая identity (например, попавшая в образ) не используется.
    /// </summary>
    public bool Diskless { get; set; }

    /// <summary>Аппаратный идентификатор вместо MAC сетевой карты (симулятор, отладка).</summary>
    public string? HardwareIdOverride { get; set; }

    /// <summary>Device Simulator: бездисковый ПК после подтверждения получит метку SIMULATED.</summary>
    public bool SimulatedDevice { get; set; }

    /// <summary>Каталог identity/ключа/журнала исполненных команд.</summary>
    public string DataPath { get; set; } = "agent-data";

    public int HeartbeatSeconds { get; set; } = 10;
    public int CommandPollSeconds { get; set; } = 20;
    public int MaxBackoffSeconds { get; set; } = 30;

    /// <summary>За сколько дней до истечения сертификата устройства агент его продлевает (D-011).</summary>
    public int CertificateRenewBeforeDays { get; set; } = 30;

    public int CertificateCheckMinutes { get; set; } = 360;

    /// <summary>Player Shell (M1): экран клуба и индикатор сессии. По умолчанию выключен (поведение M0).</summary>
    public ShellOptions Shell { get; set; } = new();
}

/// <summary>Защита приватного ключа устройства на диске (Windows: DPAPI).</summary>
public interface IKeyProtector
{
    byte[] Protect(byte[] plaintext);
    byte[] Unprotect(byte[] protectedData);
}

/// <summary>Сбор безопасной инвентаризации (без секретов, ТЗ §3.3).</summary>
public interface IInventoryProvider
{
    DeviceInventory Collect();
}

/// <summary>
/// Показ UI пользователю. Служба Windows работает в Session 0 и НЕ рисует UI сама —
/// реализация передаёт запросы в AgentSessionHost через Named Pipe (ТЗ §11.1).
/// </summary>
public interface IUserPresenter
{
    bool IsLocked { get; }

    Task<PresentResult> ShowMessageAsync(string commandId, string title, string message, CancellationToken ct);

    Task<PresentResult> SetLockAsync(string commandId, bool locked, string? reason, CancellationToken ct);

    /// <summary>Новое состояние Player Shell. Реализация хранит последнее и восстанавливает его после переподключения UI.</summary>
    Task<PresentResult> UpdateShellAsync(ShellState state, CancellationToken ct);

    /// <summary>Куда передавать ввод техника из UI (PIN режима обслуживания).</summary>
    void AttachShellInput(IShellInput input)
    {
    }
}

public sealed record PresentResult(bool Ok, string? Error)
{
    public static PresentResult Success { get; } = new(true, null);
    public static PresentResult Fail(string error) => new(false, error);
}
