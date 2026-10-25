using ClubOS.Security;
using Microsoft.AspNetCore.Http.Features;

namespace ClubOS.EdgeController.Api;

/// <summary>
/// Для API агентов тело запроса читается целиком до обработчика (не больше 1 МБ) и сохраняется: подпись агента
/// покрывает SHA-256 тела (D-007), а minimal API затем читает тот же буфер.
/// </summary>
public static class SignedBody
{
    private const string ItemKey = "clubos.body";

    public static IApplicationBuilder UseSignedBodyCapture(this IApplicationBuilder app, PathString prefix) =>
        app.Use(async (http, next) =>
        {
            if (http.Request.Path.StartsWithSegments(prefix))
            {
                if (http.Request.ContentLength > RequestBinding.MaxBodyBytes)
                {
                    http.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
                    return;
                }

                using var buffer = new MemoryStream();
                await http.Request.Body.CopyToAsync(buffer, http.RequestAborted);
                if (buffer.Length > RequestBinding.MaxBodyBytes)
                {
                    http.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
                    return;
                }

                var bytes = buffer.ToArray();
                http.Items[ItemKey] = bytes;
                http.Request.Body = new MemoryStream(bytes, writable: false);
            }

            await next(http);
        });

    public static RequestBinding Binding(HttpContext http)
    {
        var body = http.Items.TryGetValue(ItemKey, out var value) && value is byte[] bytes ? bytes : [];
        var raw = http.Features.Get<IHttpRequestFeature>()?.RawTarget;
        return RequestBinding.For(http.Request.Method,
            RequestTarget.From(raw, http.Request.PathBase, http.Request.Path, http.Request.QueryString.Value ?? string.Empty),
            body);
    }
}
