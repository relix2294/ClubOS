namespace ClubOS.EdgeController;

public sealed class EdgeOptions
{
    public const string Section = "Edge";

    /// <summary>Каталог данных: edge.db (SQLite WAL), identity, ключ, local admin token.</summary>
    public string DataPath { get; set; } = "edge-data";

    /// <summary>Базовый URL Cloud API. Соединение всегда исходящее (ТЗ §7.2).</summary>
    public string CloudUrl { get; set; } = "http://localhost:5080";

    /// <summary>Одноразовый enrollment-токен Edge (только для первого запуска; из env, не из git).</summary>
    public string? EnrollmentToken { get; set; }

    /// <summary>Порт API для агентов в LAN клуба.</summary>
    public int AgentApiPort { get; set; } = 7070;

    /// <summary>Порт локального admin API (только loopback) для edge-cli.</summary>
    public int LocalApiPort { get; set; } = 7071;

    /// <summary>
    /// Принимать локальный admin API только на порту <see cref="LocalApiPort"/> с loopback-адреса.
    /// Отключается только в интеграционных тестах (TestServer не имеет сокета).
    /// </summary>
    public bool LocalApiEnforceLoopback { get; set; } = true;

    /// <summary>Устройство считается offline без heartbeat дольше этого окна.</summary>
    public int HeartbeatTimeoutSeconds { get; set; } = 30;

    public int StatusReportSeconds { get; set; } = 5;
    public int ConfigRefreshSeconds { get; set; } = 60;
    public int CommandLongPollSeconds { get; set; } = 20;
    public int MaxBackoffSeconds { get; set; } = 30;

    /// <summary>За сколько дней до истечения Edge продлевает свой сертификат (D-011).</summary>
    public int CertificateRenewBeforeDays { get; set; } = 30;

    /// <summary>Как часто проверять срок сертификата.</summary>
    public int CertificateCheckMinutes { get; set; } = 360;
}
