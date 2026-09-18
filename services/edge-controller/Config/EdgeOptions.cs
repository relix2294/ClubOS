namespace ClubOS.EdgeController.Config;

/// <summary>
/// Настройки Edge Controller (секция "Edge"). Идентичность клуба (Tenant/Location) и
/// параметры связи с Cloud. Секреты (токен) — из окружения, не из appsettings (ТЗ §27.3).
/// </summary>
public sealed class EdgeOptions
{
    public const string SectionName = "Edge";

    /// <summary>Организация-арендатор (ТЗ §6.1).</summary>
    public string TenantId { get; set; } = "demo-tenant";

    /// <summary>Локация (клуб), которую обслуживает этот Edge.</summary>
    public string LocationId { get; set; } = "demo-location";

    /// <summary>Базовый URL Cloud API для sync (напр. https://cloud.example/).</summary>
    public string CloudBaseUrl { get; set; } = "http://localhost:5000/";

    /// <summary>Bearer-токен для авторизации sync-запросов в Cloud (из env EDGE_CLOUD_TOKEN).</summary>
    public string? CloudToken { get; set; }

    /// <summary>Путь к локальной SQLite-БД (WAL).</summary>
    public string DatabasePath { get; set; } = "edge-data/edge.db";

    /// <summary>Период попытки отправки outbox, сек.</summary>
    public int SyncIntervalSeconds { get; set; } = 5;

    /// <summary>Размер батча outbox за одну отправку.</summary>
    public int SyncBatchSize { get; set; } = 100;

    /// <summary>Тариф по умолчанию (минорные единицы за час) при отсутствии кэша конфигурации.</summary>
    public long DefaultPricePerHourMinorUnits { get; set; } = 12_000; // 120,00 TJS/час

    /// <summary>Валюта по умолчанию.</summary>
    public string DefaultCurrency { get; set; } = "TJS";
}
