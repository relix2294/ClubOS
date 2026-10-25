using ClubOS.CloudApi.Domain;

namespace ClubOS.CloudApi.Security;

/// <summary>
/// Права персонала (ТЗ §8). Проверяются на backend для каждого эндпоинта; UI лишь скрывает недоступное.
/// </summary>
public static class Permissions
{
    public const string DevicesView = "devices.view";
    public const string DevicesCommand = "devices.command";

    /// <summary>Удалённый доступ (D-022): снимок экрана, процессы, перезагрузка/выключение ПК.</summary>
    public const string DevicesRemote = "devices.remote";
    public const string SessionsManage = "sessions.manage";
    public const string AuditView = "audit.view";
    public const string EnrollmentManage = "enrollment.manage";
    public const string StaffManage = "staff.manage";

    /// <summary>Локации, зоны и тарифы (деньги) — только владелец.</summary>
    public const string LocationsManage = "locations.manage";

    /// <summary>Касса: открыть/закрыть смену, принять оплату, вернуть переплату, внесение и изъятие.</summary>
    public const string CashOperate = "cash.operate";

    /// <summary>Возврат оплаченных денег сверх переплаты (решение администратора).</summary>
    public const string CashRefund = "cash.refund";

    /// <summary>Отчёты по выручке и история смен всех кассиров.</summary>
    public const string ReportsView = "reports.view";

    public static readonly IReadOnlyList<string> All =
    [
        DevicesView, DevicesCommand, SessionsManage, AuditView, EnrollmentManage, StaffManage, LocationsManage,
        CashOperate, CashRefund, ReportsView, DevicesRemote
    ];

    private static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> ByRole =
        new Dictionary<string, IReadOnlySet<string>>
        {
            [Roles.Owner] = All.ToHashSet(),
            [Roles.Admin] = new HashSet<string>
            {
                DevicesView, DevicesCommand, SessionsManage, AuditView, EnrollmentManage, CashOperate, CashRefund, ReportsView,
                DevicesRemote
            },
            [Roles.Operator] = new HashSet<string> { DevicesView, DevicesCommand, SessionsManage, AuditView, CashOperate }
        };

    public static IReadOnlySet<string> For(string role) =>
        ByRole.TryGetValue(role, out var set) ? set : new HashSet<string>();

    public static bool Has(string role, string permission) => For(role).Contains(permission);

    /// <summary>Имя политики авторизации для права.</summary>
    public static string Policy(string permission) => "perm:" + permission;
}

/// <summary>Требования к паролю персонала.</summary>
public static class PasswordPolicy
{
    public const int MinLength = 10;
    public const int MaxLength = 128;

    /// <summary>Возвращает текст ошибки или null, если пароль подходит.</summary>
    public static string? Validate(string? password, string email)
    {
        if (string.IsNullOrEmpty(password) || password.Length < MinLength || password.Length > MaxLength)
        {
            return $"Пароль должен быть от {MinLength} до {MaxLength} символов.";
        }

        if (string.Equals(password, email, StringComparison.OrdinalIgnoreCase) ||
            password.Contains(email.Split('@')[0], StringComparison.OrdinalIgnoreCase) && email.Split('@')[0].Length >= 4)
        {
            return "Пароль не должен содержать email.";
        }

        if (password.Distinct().Count() < 5)
        {
            return "Пароль слишком простой: используйте больше разных символов.";
        }

        return null;
    }

    /// <summary>Временный пароль для выдачи сотруднику (показывается один раз).</summary>
    public static string GenerateTemporary()
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789";
        return System.Security.Cryptography.RandomNumberGenerator.GetString(alphabet, 14);
    }
}
