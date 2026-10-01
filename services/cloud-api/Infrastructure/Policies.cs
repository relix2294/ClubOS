namespace ClubOS.CloudApi.Infrastructure;

public static class Policies
{
    /// <summary>Любой вошедший сотрудник своей организации (в т.ч. с временным паролем).</summary>
    public const string Staff = "staff";

    public const string Edge = "edge";

    /// <summary>Эндпоинт требует права сотрудника (см. <see cref="Security.Permissions"/>).</summary>
    public static TBuilder RequirePermission<TBuilder>(this TBuilder builder, string permission)
        where TBuilder : IEndpointConventionBuilder =>
        builder.RequireAuthorization(Security.Permissions.Policy(permission));
}

public static class RateLimits
{
    /// <summary>Ограничение попыток входа/enrollment с одного IP (защита от перебора).</summary>
    public const string Auth = "auth";
}
