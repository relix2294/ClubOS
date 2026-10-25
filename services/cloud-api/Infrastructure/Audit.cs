using System.Text.Json;
using ClubOS.CloudApi.Data;
using ClubOS.CloudApi.Domain;
using ClubOS.Contracts;

namespace ClubOS.CloudApi.Infrastructure;

/// <summary>
/// Запись иммутабельного аудита (ТЗ §27.4). Запись добавляется в тот же DbContext, что и
/// бизнес-изменение, и сохраняется одной транзакцией SaveChanges — действие без аудита невозможно.
/// </summary>
public sealed class AuditWriter(ClubOsDbContext db, TimeProvider time)
{
    public void Write(string tenantId, string? locationId, string actor, string action, string target, string result,
        string? correlationId = null, object? details = null)
    {
        db.AuditEvents.Add(new AuditEvent
        {
            Id = Ids.New("aud"),
            TenantId = tenantId,
            LocationId = locationId,
            OccurredAtUtc = time.GetUtcNow(),
            Actor = actor,
            Action = action,
            Target = target,
            Result = result,
            CorrelationId = correlationId,
            DetailsJson = details is null ? null : JsonSerializer.Serialize(details, ContractJson.Options)
        });
    }
}

public static class AuditResults
{
    public const string Success = "success";
    public const string Denied = "denied";
    public const string Failed = "failed";
    public const string Requested = "requested";
}
