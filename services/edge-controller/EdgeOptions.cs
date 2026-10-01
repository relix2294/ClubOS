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

    /// <summary>
    /// HTTPS-порт API агентов (D-007); 0 — выключить. Сертификат выпускает CA Cloud после регистрации Edge,
    /// агенты доверяют только этому CA.
    /// </summary>
    public int AgentTlsPort { get; set; } = 7443;

    /// <summary>
    /// Открытый HTTP-порт API агентов (<see cref="AgentApiPort"/>). Оставлен на время перевода ПК на HTTPS;
    /// после перевода всех агентов выключите (false), тогда LAN-трафик Edge↔Agent только в TLS.
    /// </summary>
    public bool AgentHttpEnabled { get; set; } = true;

    /// <summary>Дополнительные имена/IP Edge для TLS-сертификата через запятую (DNS-имя сервера клуба, внешний IP).</summary>
    public string TlsHostNames { get; set; } = string.Empty;

    /// <summary>
    /// Требовать клиентский сертификат устройства (mTLS, D-002) для API агентов. false — сертификат проверяется,
    /// если агент его предъявил (агенты M2+ предъявляют всегда); true — запросы без сертификата (в том числе по
    /// HTTP-порту) отклоняются. Регистрация ПК, загрузка бездискового ПК и касса /cash работают без сертификата.
    /// Включайте после обновления всех агентов клуба.
    /// </summary>
    public bool RequireAgentClientCertificate { get; set; }

    /// <summary>Как часто забирать список отзыва (CRL) у Cloud, секунд.</summary>
    public int CrlRefreshSeconds { get; set; } = 300;

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

    /// <summary>
    /// Бездисковый ПК (D-018): если ПК с тем же MAC присылал heartbeat не позже стольких секунд назад, новая загрузка
    /// отклоняется (второй экземпляр или подмена MAC). Перезагрузка настоящего ПК обычно дольше.
    /// </summary>
    public int DisklessConflictSeconds { get; set; } = 20;
    public int CommandLongPollSeconds { get; set; } = 20;
    public int MaxBackoffSeconds { get; set; } = 30;

    /// <summary>
    /// Отклонять запросы агентов без подписи тела (D-007). false на время обновления агентов старых версий;
    /// подписанный запрос проверяется всегда.
    /// </summary>
    public bool RequireAgentRequestBinding { get; set; }

    /// <summary>За сколько дней до истечения Edge продлевает свой сертификат (D-011).</summary>
    public int CertificateRenewBeforeDays { get; set; } = 30;

    /// <summary>Как часто проверять срок сертификата.</summary>
    public int CertificateCheckMinutes { get; set; } = 360;
}
