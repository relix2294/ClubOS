using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClubOS.Contracts;

/// <summary>Единые настройки сериализации контрактов (camelCase, enum строками).</summary>
public static class ContractJson
{
    public static JsonSerializerOptions Options { get; } = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
        options.Converters.Add(new JsonStringEnumConverter());
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }

    public static JsonElement ToElement<T>(T value) => JsonSerializer.SerializeToElement(value, Options);

    public static T FromElement<T>(JsonElement element) =>
        element.Deserialize<T>(Options) ?? throw new JsonException($"Пустой payload для {typeof(T).Name}.");
}
