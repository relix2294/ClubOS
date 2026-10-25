using ClubOS.CloudApi.Auth;
using ClubOS.CloudApi.Data;
using ClubOS.CloudApi.Security;
using Microsoft.EntityFrameworkCore;

namespace ClubOS.CloudApi.Infrastructure;

/// <summary>
/// Серверные команды обслуживания (запуск внутри контейнера/на сервере, не через HTTP):
/// <list type="bullet">
/// <item><c>admin reset-password &lt;email&gt;</c> — владелец забыл пароль или он скомпрометирован: временный пароль
/// (смена при входе), все сессии отозваны;</item>
/// <item><c>admin reset-mfa &lt;email&gt;</c> — владелец потерял телефон и коды восстановления: MFA отключена
/// (для обязательных ролей её потребуют настроить при следующем входе), все сессии отозваны.</item>
/// </list>
/// </summary>
public static class AdminCli
{
    public static bool IsAdminCommand(string[] args) => args.Length > 0 && args[0] == "admin";

    public static async Task<int> RunAsync(IServiceProvider services, string[] args)
    {
        if (args.Length != 3 || args[1] is not ("reset-password" or "reset-mfa"))
        {
            Console.Error.WriteLine("Использование: dotnet ClubOS.CloudApi.dll admin reset-password|reset-mfa <email>");
            return 2;
        }

        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ClubOsDbContext>();
        var tokens = scope.ServiceProvider.GetRequiredService<TokenService>();
        var audit = scope.ServiceProvider.GetRequiredService<AuditWriter>();

        var email = args[2].Trim().ToLowerInvariant();
        var user = await db.Users.SingleOrDefaultAsync(x => x.Email == email);
        if (user is null)
        {
            Console.Error.WriteLine($"Пользователь {email} не найден.");
            return 3;
        }

        if (args[1] == "reset-mfa")
        {
            await scope.ServiceProvider.GetRequiredService<MfaService>().DisableAsync(user, CancellationToken.None);
            await tokens.RevokeAllAsync(user, CancellationToken.None);
            audit.Write(user.OrganizationId, null, "system:admin-cli", "staff.mfa_reset", $"user:{user.Id}",
                AuditResults.Success);
            await db.SaveChangesAsync();
            Console.WriteLine($"MFA для {email} сброшена, все сессии отозваны.");
            return 0;
        }

        var temporary = PasswordPolicy.GenerateTemporary();
        user.PasswordHash = PasswordHasher.Hash(temporary);
        user.MustChangePassword = true;
        user.IsActive = true;
        await tokens.RevokeAllAsync(user, CancellationToken.None);
        audit.Write(user.OrganizationId, null, "system:admin-cli", "staff.password_reset", $"user:{user.Id}",
            AuditResults.Success);
        await db.SaveChangesAsync();

        Console.WriteLine($"Временный пароль для {email}: {temporary}");
        Console.WriteLine("При входе потребуется задать новый пароль. Все прежние сессии отозваны.");
        return 0;
    }
}
