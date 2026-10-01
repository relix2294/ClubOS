using System.Net.Http.Json;
using System.Text.Json;

namespace ClubOS.Integration.Tests;

public static class Api
{
    public static async Task<JsonElement> JsonAsync(this HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"{(int)response.StatusCode}: {text}");
        }

        return JsonDocument.Parse(text).RootElement.Clone();
    }

    public static async Task<JsonElement> GetJsonAsync(this HttpClient client, string url) =>
        await (await client.GetAsync(url)).JsonAsync();

    public static async Task<JsonElement> PostJsonAsync(this HttpClient client, string url, object? body = null) =>
        await (await client.PostAsJsonAsync(url, body ?? new { })).JsonAsync();

    public static async Task<string> EdgeTokenAsync(this HttpClient owner, TestLocation location) =>
        (await owner.PostJsonAsync("/api/v1/enrollment-tokens/edge", new { locationId = location.LocationId, name = "Edge-IT" }))
        .GetProperty("enrollmentToken").GetString()!;

    public static async Task<string> DeviceTokenAsync(this HttpClient owner, TestLocation location, string name = "PC-IT") =>
        (await owner.PostJsonAsync("/api/v1/enrollment-tokens/device",
            new { locationId = location.LocationId, zoneId = location.ZoneId, displayName = name, simulated = true }))
        .GetProperty("enrollmentToken").GetString()!;

    public static string Str(this JsonElement e, string name) => e.GetProperty(name).GetString()!;
}
