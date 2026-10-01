namespace ClubOS.Contracts;

/// <summary>
/// Демо-зал на VPS (профиль compose <c>demo</c>): одноразовые enrollment-токены Edge и симулированных ПК выводятся
/// из одного случайного секрета в .env. Seed Cloud создаёт их хэши, Edge и Device Simulator знают секрет —
/// демо поднимается без входа сотрудника (на VPS вход Owner/Admin требует 2FA).
/// </summary>
public static class DemoEnrollment
{
    public const int DeviceCount = 5;
    public const int MinSecretLength = 24;

    public static string EdgeToken(string secret) => $"{secret.Trim()}-edge";

    public static string DeviceToken(string secret, int index) => $"{secret.Trim()}-pc{index:D2}";

    public static string DeviceName(int index) => $"SIM-PC-{index:D2}";
}
