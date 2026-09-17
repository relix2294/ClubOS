using ClubOS.CloudApi.Common;
using ClubOS.CloudApi.Data;
using ClubOS.CloudApi.Domain;

namespace ClubOS.CloudApi.Api;

/// <summary>
/// Запись иммутабельных событий аудита (ТЗ §27.4). Вызывающий сохраняет изменения
/// (SaveChanges) — так аудит попадает в ту же транзакцию, что и действие.
/// </summary>
public static class AuditLog
{
    public static void Add(
        ClubOsDbContext db,
        Caller caller,
        string action,
        string target,
        string result,
        string? locationId = null,
        string? correlationId = null)
    {
        db.AuditEvents.Add(new AuditEvent
        {
            Id = Ids.New(),
            TenantId = caller.OrganizationId,
            LocationId = locationId,
            OccurredAtUtc = DateTimeOffset.UtcNow,
            Actor = caller.Email.Length > 0 ? caller.Email : caller.UserId,
            Action = action,
            Target = target,
            Result = result,
            CorrelationId = correlationId,
        });
    }
}
