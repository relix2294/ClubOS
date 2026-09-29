using ClubOS.CloudApi.Auth;
using ClubOS.CloudApi.Data;
using ClubOS.CloudApi.Domain;
using ClubOS.CloudApi.Infrastructure;
using ClubOS.CloudApi.Security;
using Microsoft.EntityFrameworkCore;

namespace ClubOS.CloudApi.Api;

/// <summary>
/// Управление персоналом (ТЗ §8): только Owner (право staff.manage), только внутри своей организации.
/// Смена своего пароля — любой сотрудник, в т.ч. с временным паролем.
/// Любое изменение доступа (роль, отключение, сброс/смена пароля) сразу отзывает сессии сотрудника.
/// </summary>
public static class StaffManagementEndpoints
{
    public static void MapStaffManagementEndpoints(this IEndpointRouteBuilder app)
    {
        var staff = app.MapGroup("/api/v1/staff").WithTags("Staff").RequirePermission(Permissions.StaffManage);
        staff.MapGet("", List);
        staff.MapPost("", Create);
        staff.MapPost("/{userId}/role", ChangeRole);
        staff.MapPost("/{userId}/deactivate", (string userId, HttpContext http, ClubOsDbContext db, TokenService tokens,
            AuditWriter audit, CancellationToken ct) => SetActive(userId, false, http, db, tokens, audit, ct));
        staff.MapPost("/{userId}/activate", (string userId, HttpContext http, ClubOsDbContext db, TokenService tokens,
            AuditWriter audit, CancellationToken ct) => SetActive(userId, true, http, db, tokens, audit, ct));
        staff.MapPost("/{userId}/reset-password", ResetPassword);
        staff.MapPost("/{userId}/locations", SetLocations);

        app.MapPost("/api/v1/me/password", ChangeOwnPassword).WithTags("Auth")
            .RequireAuthorization(Policies.Staff).RequireRateLimiting(RateLimits.Auth);
    }

    private static async Task<IResult> List(HttpContext http, ClubOsDbContext db, CancellationToken ct)
    {
        var me = StaffContext.From(http.User);
        var users = await db.Users.AsNoTracking().Where(x => x.OrganizationId == me.TenantId)
            .OrderBy(x => x.DisplayName).ToListAsync(ct);
        var userIds = users.Select(x => x.Id).ToList();
        var access = (await db.StaffLocationAccess.AsNoTracking().Where(x => userIds.Contains(x.UserId)).ToListAsync(ct))
            .ToLookup(x => x.UserId, x => x.LocationId);
        return Results.Ok(users.Select(x => x.ToStaffView(access[x.Id].Order().ToList())).ToList());
    }

    private static async Task<IResult> Create(CreateStaffRequest request, HttpContext http, ClubOsDbContext db,
        AuditWriter audit, TimeProvider time, CancellationToken ct)
    {
        var me = StaffContext.From(http.User);
        var email = request.Email?.Trim().ToLowerInvariant() ?? string.Empty;
        var name = request.DisplayName?.Trim() ?? string.Empty;
        if (email.Length is < 3 or > 254 || !email.Contains('@') || email.StartsWith('@') || email.EndsWith('@'))
        {
            return Problems.Validation("invalid_email", "Укажите корректный email.");
        }

        if (name.Length is 0 or > 80)
        {
            return Problems.Validation("invalid_name", "Имя сотрудника 1–80 символов.");
        }

        if (!Roles.IsValid(request.Role))
        {
            return Problems.Validation("invalid_role", $"Роль: {string.Join(", ", Roles.All)}.");
        }

        if (await db.Users.AnyAsync(x => x.Email == email, ct))
        {
            return Problems.Conflict("email_taken", "Сотрудник с таким email уже существует.");
        }

        var locationIds = request.Role == Roles.Owner ? null : request.LocationIds;
        if (locationIds is not null && await ValidateLocations(db, me.TenantId, locationIds, ct) is { } locationError)
        {
            return Problems.Validation("invalid_locations", locationError);
        }

        var temporary = PasswordPolicy.GenerateTemporary();
        var user = new User
        {
            Id = Ids.New("usr"),
            OrganizationId = me.TenantId,
            Email = email,
            DisplayName = name,
            PasswordHash = PasswordHasher.Hash(temporary),
            Role = request.Role,
            MustChangePassword = true,
            CreatedAtUtc = time.GetUtcNow(),
            AllLocations = locationIds is null
        };
        db.Users.Add(user);
        foreach (var locationId in locationIds?.Distinct() ?? [])
        {
            db.StaffLocationAccess.Add(new StaffLocationAccess { UserId = user.Id, LocationId = locationId });
        }

        audit.Write(me.TenantId, null, me.Actor, "staff.created", $"user:{user.Id}", AuditResults.Success,
            details: new { email, role = user.Role, allLocations = locationIds is null, locationIds });
        await db.SaveChangesAsync(ct);

        return Results.Created($"/api/v1/staff/{user.Id}", new TemporaryPasswordResponse(await ViewAsync(db, user, ct), temporary));
    }

    private static async Task<IResult> ChangeRole(string userId, ChangeRoleRequest request, HttpContext http,
        ClubOsDbContext db, TokenService tokens, AuditWriter audit, CancellationToken ct)
    {
        var me = StaffContext.From(http.User);
        if (!Roles.IsValid(request.Role))
        {
            return Problems.Validation("invalid_role", $"Роль: {string.Join(", ", Roles.All)}.");
        }

        var user = await FindInTenant(db, me, userId, ct);
        if (user is null)
        {
            return Problems.NotFound("Сотрудник");
        }

        if (user.Role == request.Role)
        {
            return Results.Ok(await ViewAsync(db, user, ct));
        }

        if (user.Role == Roles.Owner && await IsLastActiveOwner(db, user, ct))
        {
            return Problems.Conflict("last_owner", "Нельзя снять роль с последнего активного владельца.");
        }

        var before = user.Role;
        user.Role = request.Role;
        await tokens.RevokeAllAsync(user, ct);
        audit.Write(me.TenantId, null, me.Actor, "staff.role_changed", $"user:{user.Id}", AuditResults.Success,
            details: new { from = before, to = user.Role });
        await db.SaveChangesAsync(ct);
        return Results.Ok(await ViewAsync(db, user, ct));
    }

    private static async Task<IResult> SetActive(string userId, bool active, HttpContext http, ClubOsDbContext db,
        TokenService tokens, AuditWriter audit, CancellationToken ct)
    {
        var me = StaffContext.From(http.User);
        var user = await FindInTenant(db, me, userId, ct);
        if (user is null)
        {
            return Problems.NotFound("Сотрудник");
        }

        if (user.IsActive == active)
        {
            return Results.Ok(await ViewAsync(db, user, ct));
        }

        if (!active && user.Id == me.UserId)
        {
            return Problems.Conflict("self_deactivate", "Нельзя отключить самого себя.");
        }

        if (!active && user.Role == Roles.Owner && await IsLastActiveOwner(db, user, ct))
        {
            return Problems.Conflict("last_owner", "Нельзя отключить последнего активного владельца.");
        }

        user.IsActive = active;
        await tokens.RevokeAllAsync(user, ct);
        audit.Write(me.TenantId, null, me.Actor, active ? "staff.activated" : "staff.deactivated", $"user:{user.Id}",
            AuditResults.Success);
        await db.SaveChangesAsync(ct);
        return Results.Ok(await ViewAsync(db, user, ct));
    }

    private static async Task<IResult> ResetPassword(string userId, HttpContext http, ClubOsDbContext db,
        TokenService tokens, AuditWriter audit, CancellationToken ct)
    {
        var me = StaffContext.From(http.User);
        var user = await FindInTenant(db, me, userId, ct);
        if (user is null)
        {
            return Problems.NotFound("Сотрудник");
        }

        var temporary = PasswordPolicy.GenerateTemporary();
        user.PasswordHash = PasswordHasher.Hash(temporary);
        user.MustChangePassword = true;
        await tokens.RevokeAllAsync(user, ct);
        audit.Write(me.TenantId, null, me.Actor, "staff.password_reset", $"user:{user.Id}", AuditResults.Success);
        await db.SaveChangesAsync(ct);
        return Results.Ok(new TemporaryPasswordResponse(await ViewAsync(db, user, ct), temporary));
    }

    /// <summary>
    /// Смена своего пароля. Все прочие сессии пользователя отзываются; текущему клиенту выдаются
    /// новые токены (BFF Admin Web обновляет cookie), поэтому повторный вход не нужен.
    /// </summary>
    private static async Task<IResult> ChangeOwnPassword(ChangePasswordRequest request, HttpContext http,
        ClubOsDbContext db, TokenService tokens, AuditWriter audit, TimeProvider time, CancellationToken ct)
    {
        var me = StaffContext.From(http.User);
        var user = await db.Users.SingleAsync(x => x.Id == me.UserId && x.OrganizationId == me.TenantId, ct);

        if (!PasswordHasher.Verify(request.CurrentPassword ?? string.Empty, user.PasswordHash))
        {
            audit.Write(me.TenantId, null, me.Actor, "auth.password_changed", $"user:{user.Id}", AuditResults.Denied);
            await db.SaveChangesAsync(ct);
            return Problems.Validation("wrong_password", "Текущий пароль указан неверно.");
        }

        if (PasswordPolicy.Validate(request.NewPassword, user.Email) is { } error)
        {
            return Problems.Validation("weak_password", error);
        }

        if (request.NewPassword == request.CurrentPassword)
        {
            return Problems.Validation("same_password", "Новый пароль должен отличаться от текущего.");
        }

        user.PasswordHash = PasswordHasher.Hash(request.NewPassword);
        user.MustChangePassword = false;
        user.PasswordChangedAtUtc = time.GetUtcNow();
        await tokens.RevokeAllAsync(user, ct);
        var issued = await tokens.IssueAsync(user, ct);
        audit.Write(me.TenantId, null, me.Actor, "auth.password_changed", $"user:{user.Id}", AuditResults.Success);
        await db.SaveChangesAsync(ct);

        return Results.Ok(await AuthEndpoints.BuildResponse(db, tokens, user, issued, ct));
    }

    public static async Task<StaffMemberView> ViewAsync(ClubOsDbContext db, User user, CancellationToken ct) =>
        user.ToStaffView(await db.StaffLocationAccess.AsNoTracking().Where(x => x.UserId == user.Id)
            .Select(x => x.LocationId).OrderBy(x => x).ToListAsync(ct));

    /// <summary>Проверяет список локаций организации: null — ошибки нет; иначе текст ошибки.</summary>
    private static async Task<string?> ValidateLocations(ClubOsDbContext db, string tenantId, IReadOnlyList<string> ids,
        CancellationToken ct)
    {
        if (ids.Count == 0)
        {
            return "Выберите хотя бы одну локацию или «Все локации».";
        }

        var distinct = ids.Distinct().ToList();
        var known = await db.Locations.CountAsync(x => x.OrganizationId == tenantId && distinct.Contains(x.Id), ct);
        return known == distinct.Count ? null : "Локация не найдена.";
    }

    private static async Task ReplaceLocations(ClubOsDbContext db, User user, IReadOnlyList<string>? ids, CancellationToken ct)
    {
        await db.StaffLocationAccess.Where(x => x.UserId == user.Id).ExecuteDeleteAsync(ct);
        user.AllLocations = ids is null;
        foreach (var id in ids?.Distinct() ?? [])
        {
            db.StaffLocationAccess.Add(new StaffLocationAccess { UserId = user.Id, LocationId = id });
        }
    }

    /// <summary>Доступ сотрудника к локациям. Owner всегда видит все локации. Действует со следующего запроса.</summary>
    private static async Task<IResult> SetLocations(string userId, StaffLocationsRequest request, HttpContext http,
        ClubOsDbContext db, AuditWriter audit, CancellationToken ct)
    {
        var me = StaffContext.From(http.User);
        var user = await FindInTenant(db, me, userId, ct);
        if (user is null)
        {
            return Problems.NotFound("Сотрудник");
        }

        if (user.Role == Roles.Owner)
        {
            return Problems.Conflict("owner_all_locations", "Владелец всегда имеет доступ ко всем локациям.");
        }

        var ids = request.AllLocations ? null : request.LocationIds ?? [];
        if (ids is not null && await ValidateLocations(db, me.TenantId, ids, ct) is { } error)
        {
            return Problems.Validation("invalid_locations", error);
        }

        await ReplaceLocations(db, user, ids, ct);
        audit.Write(me.TenantId, null, me.Actor, "staff.locations_changed", $"user:{user.Id}", AuditResults.Success,
            details: new { allLocations = ids is null, locationIds = ids });
        await db.SaveChangesAsync(ct);
        return Results.Ok(await ViewAsync(db, user, ct));
    }

    private static Task<User?> FindInTenant(ClubOsDbContext db, StaffContext me, string userId, CancellationToken ct) =>
        db.Users.SingleOrDefaultAsync(x => x.Id == userId && x.OrganizationId == me.TenantId, ct);

    private static async Task<bool> IsLastActiveOwner(ClubOsDbContext db, User user, CancellationToken ct) =>
        !await db.Users.AnyAsync(x => x.OrganizationId == user.OrganizationId && x.Id != user.Id &&
                                      x.Role == Roles.Owner && x.IsActive, ct);
}
