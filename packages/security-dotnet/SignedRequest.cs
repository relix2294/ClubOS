using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace ClubOS.Security;

/// <summary>
/// Подписывает исходящий запрос: тело сериализуется заранее, чтобы подпись покрывала ровно те байты, что уйдут
/// по сети (метод, путь с query и SHA-256 тела — <see cref="RequestBinding"/>).
/// </summary>
public static class SignedRequest
{
    public static HttpRequestMessage Create(HttpMethod method, Uri? baseAddress, string relativePath, byte[]? jsonBody,
        string subjectId, ECDsa key, string audience, TimeProvider time)
    {
        var request = new HttpRequestMessage(method, relativePath);
        var absolute = baseAddress is null ? new Uri(new Uri("http://localhost/"), relativePath) : new Uri(baseAddress, relativePath);
        var body = jsonBody ?? [];
        var binding = RequestBinding.For(method.Method, absolute.PathAndQuery, body);
        request.Headers.Authorization = new AuthenticationHeaderValue(SignedToken.Scheme,
            SignedToken.Create(subjectId, key, audience, time, binding));
        if (jsonBody is not null)
        {
            request.Content = new ByteArrayContent(jsonBody);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        }

        return request;
    }
}
