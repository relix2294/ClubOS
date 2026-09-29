using System.Globalization;
using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Text.Json;
using ClubOS.CloudApi.Auth;
using ClubOS.CloudApi.Data;
using ClubOS.CloudApi.Infrastructure;
using ClubOS.CloudApi.Security;

namespace ClubOS.CloudApi.Live;

/// <summary>
/// <c>GET /api/v1/live</c> — поток Server-Sent Events для Admin Web (DEVIATIONS D-008). Вместо опроса раз в 2–3 с
/// клиент получает подсказки «изменилось устройство/команда/сессия…» и перечитывает только нужное через REST.
/// Поток живёт не дольше access-токена и перепроверяет сотрудника по БД раз в <see cref="RevalidateEvery"/>:
/// отключённый сотрудник или сменивший роль теряет поток. Тогда сервер шлёт <c>reauth</c> и закрывает поток,
/// а BFF при переподключении обновляет токен.
/// </summary>
public static class LiveEndpoints
{
    public static readonly TimeSpan PingEvery = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan RevalidateEvery = TimeSpan.FromSeconds(30);

    public static void MapLiveEndpoints(this IEndpointRouteBuilder app) =>
        app.MapGet("/api/v1/live", Stream)
            .WithTags("Live")
            .RequirePermission(Permissions.DevicesView);

    private static IResult Stream(HttpContext http, LiveBroker broker, IServiceScopeFactory scopes, TimeProvider time,
        CancellationToken ct)
    {
        var staff = StaffContext.From(http.User);
        var principal = http.User;
        var expires = ExpiresAt(principal) ?? time.GetUtcNow().AddMinutes(15);
        http.Response.Headers.CacheControl = "no-cache, no-transform";
        http.Response.Headers["X-Accel-Buffering"] = "no"; // nginx/прокси: не буферизовать поток
        return TypedResults.ServerSentEvents(Events(staff, principal, expires, broker, scopes, time, ct));
    }

    private static async IAsyncEnumerable<SseItem<string>> Events(StaffContext staff, ClaimsPrincipal principal,
        DateTimeOffset expires, LiveBroker broker, IServiceScopeFactory scopes, TimeProvider time,
        [EnumeratorCancellation] CancellationToken ct)
    {
        using var subscription = broker.Subscribe(staff.TenantId);
        var nextRevalidation = time.GetUtcNow() + RevalidateEvery;
        var access = await LoadAccessAsync(scopes, staff, ct);
        yield return new SseItem<string>("{}", "ready") { ReconnectionInterval = TimeSpan.FromSeconds(3) };

        while (!ct.IsCancellationRequested)
        {
            var hasEvents = await WaitAsync(subscription, ct);
            if (ct.IsCancellationRequested)
            {
                yield break;
            }

            if (subscription.TakeOverflow())
            {
                yield return new SseItem<string>("{}", LiveTopics.Resync);
            }

            if (hasEvents)
            {
                while (subscription.Reader.TryRead(out var evt))
                {
                    if (Allowed(staff.Role, access, evt))
                    {
                        yield return new SseItem<string>(JsonSerializer.Serialize(evt), "change");
                    }
                }
            }
            else
            {
                yield return new SseItem<string>("{}", "ping"); // держит соединение через прокси
            }

            var now = time.GetUtcNow();
            if (now >= expires)
            {
                yield return new SseItem<string>("""{"reason":"expired"}""", "reauth");
                yield break;
            }

            if (now >= nextRevalidation)
            {
                nextRevalidation = now + RevalidateEvery;
                if (!await StillValidAsync(scopes, principal, ct))
                {
                    yield return new SseItem<string>("""{"reason":"revoked"}""", "reauth");
                    yield break;
                }

                access = await LoadAccessAsync(scopes, staff, ct); // доступ к локациям мог измениться
            }
        }
    }

    /// <summary>
    /// Подсказки только о доступных локациях (события без локации — только при доступе ко всей организации),
    /// об аудите и персонале — только тем, кто может их читать.
    /// </summary>
    public static bool Allowed(string role, LocationAccess access, LiveEvent evt) =>
        access.Contains(evt.LocationId) && evt.Topic switch
        {
            LiveTopics.Audit => Permissions.Has(role, Permissions.AuditView),
            LiveTopics.Staff => Permissions.Has(role, Permissions.StaffManage),
            _ => true
        };

    private static async Task<LocationAccess> LoadAccessAsync(IServiceScopeFactory scopes, StaffContext staff,
        CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ClubOsDbContext>();
        return await LocationScope.LoadAsync(db, staff.UserId, staff.TenantId, ct);
    }

    private static async Task<bool> WaitAsync(Subscription subscription, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(PingEvery);
        try
        {
            return await subscription.Reader.WaitToReadAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private static async Task<bool> StillValidAsync(IServiceScopeFactory scopes, ClaimsPrincipal principal,
        CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ClubOsDbContext>();
        try
        {
            return await StaffTokenValidation.IsCurrentAsync(db, principal, ct);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private static DateTimeOffset? ExpiresAt(ClaimsPrincipal principal) =>
        long.TryParse(principal.FindFirst("exp")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var exp)
            ? DateTimeOffset.FromUnixTimeSeconds(exp)
            : null;
}
