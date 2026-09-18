namespace ClubOS.WindowsAgent.Config;

/// <summary>
/// Настройки Windows Agent (секция "Agent"). Секреты (enrollment-токен) — из окружения,
/// не из appsettings (ТЗ §27.3). Agent enroll'ится в Cloud, а heartbeat/команды — через Edge.
/// </summary>
public sealed class AgentOptions
{
    public const string SectionName = "Agent";

    /// <summary>Cloud API для enrollment (обмен one-time токена на сертификат).</summary>
    public string CloudBaseUrl { get; set; } = "http://localhost:5000/";

    /// <summary>Локальный Edge Controller для heartbeat и команд.</summary>
    public string EdgeBaseUrl { get; set; } = "http://localhost:5080/";

    /// <summary>Период heartbeat, сек (ТЗ §10.1: каждые 10 сек).</summary>
    public int HeartbeatSeconds { get; set; } = 10;

    /// <summary>Имя named pipe для IPC с session-host (overlay в интерактивной сессии).</summary>
    public string PipeName { get; set; } = "clubos-agent-ui";

    /// <summary>Каталог хранения идентичности устройства (deviceId, ключ, сертификат).</summary>
    public string DataDirectory { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ClubOS", "agent");

    /// <summary>Одноразовый enrollment-токен (обычно из env CLUBOS_ENROLLMENT_TOKEN).</summary>
    public string? EnrollmentToken { get; set; }

    /// <summary>Явно заданное отображаемое имя устройства (иначе — hostname).</summary>
    public string? DisplayName { get; set; }
}
