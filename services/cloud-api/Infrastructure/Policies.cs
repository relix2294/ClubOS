namespace ClubOS.CloudApi.Infrastructure;

public static class Policies
{
    public const string Staff = "staff";
    public const string Owner = "owner";
    public const string Edge = "edge";
}

public static class RateLimits
{
    /// <summary>Ограничение попыток входа/enrollment с одного IP (защита от перебора).</summary>
    public const string Auth = "auth";
}
