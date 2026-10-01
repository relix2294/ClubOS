using System.Text.Json;
using ClubOS.CloudApi.Data;
using ClubOS.CloudApi.Domain;
using ClubOS.Contracts;

namespace ClubOS.CloudApi.Edges;

/// <summary>Постановка элемента в очередь Cloud → Edge в рамках текущей транзакции DbContext.</summary>
public static class EdgeQueue
{
    public static void Enqueue(ClubOsDbContext db, string tenantId, string locationId, EdgeCommand command) =>
        db.EdgeOutbox.Add(new EdgeOutboxItem
        {
            Id = command.Id,
            TenantId = tenantId,
            LocationId = locationId,
            Kind = command.Kind,
            PayloadJson = JsonSerializer.Serialize(command, ContractJson.Options),
            CreatedAtUtc = command.IssuedAtUtc,
            ExpiresAtUtc = command.ExpiresAtUtc
        });
}
