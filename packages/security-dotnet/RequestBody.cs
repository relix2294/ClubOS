namespace ClubOS.Security;

/// <summary>Точный адрес запроса для <see cref="RequestBinding"/>: путь с query, как их отправил клиент.</summary>
public static class RequestTarget
{
    /// <param name="rawTarget">Сырой request-target из HTTP (если сервер его знает).</param>
    public static string From(string? rawTarget, string pathBase, string path, string query) =>
        !string.IsNullOrEmpty(rawTarget) && rawTarget.StartsWith('/') ? rawTarget : pathBase + path + query;
}
