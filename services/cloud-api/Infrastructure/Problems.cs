namespace ClubOS.CloudApi.Infrastructure;

/// <summary>Единый формат ошибок API (RFC 9457 ProblemDetails) с машинно-читаемым кодом.</summary>
public static class Problems
{
    public static IResult Validation(string code, string detail) =>
        Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Validation failed", detail: detail,
            extensions: new Dictionary<string, object?> { ["code"] = code });

    public static IResult NotFound(string what) =>
        Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Not found", detail: $"{what} не найден.",
            extensions: new Dictionary<string, object?> { ["code"] = "not_found" });

    public static IResult Conflict(string code, string detail) =>
        Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "Conflict", detail: detail,
            extensions: new Dictionary<string, object?> { ["code"] = code });

    public static IResult Unauthorized(string detail) =>
        Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Unauthorized", detail: detail,
            extensions: new Dictionary<string, object?> { ["code"] = "unauthorized" });
}
