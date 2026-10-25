using System.Text.Json;
using ClubOS.Contracts;

namespace ClubOS.CloudApi.Domain;

/// <summary>Периоды тарифа в jsonb-колонке: тот же формат, что в снимке для Edge.</summary>
public static class PricingJson
{
    public const int MaxPeriods = 12;

    public static IReadOnlyList<PricePeriod> Read(string? json) =>
        string.IsNullOrEmpty(json) ? [] : JsonSerializer.Deserialize<List<PricePeriod>>(json, ContractJson.Options) ?? [];

    public static string? Write(IReadOnlyList<PricePeriod>? periods) =>
        periods is null || periods.Count == 0 ? null : JsonSerializer.Serialize(periods, ContractJson.Options);

    /// <summary>Смещение местного времени локации от UTC сейчас (минуты); неизвестный пояс — 0.</summary>
    public static int UtcOffsetMinutes(string timezone, DateTimeOffset now) =>
        Api.CashEndpoints.FindZone(timezone) is { } zone ? (int)zone.GetUtcOffset(now).TotalMinutes : 0;

    /// <summary>Минута местного дня и день недели для окна пакета.</summary>
    public static (DayOfWeek Day, int Minute) LocalMinute(string timezone, DateTimeOffset now)
    {
        var local = now.UtcDateTime.AddMinutes(UtcOffsetMinutes(timezone, now));
        return (local.DayOfWeek, (int)local.TimeOfDay.TotalMinutes);
    }

    /// <summary>Попадает ли минута в окно [from, to) (через полночь, если from &gt; to); без окна — всегда.</summary>
    public static bool InWindow(int? from, int? to, int minute) => (from, to) switch
    {
        ({ } f, { } t) when f < t => minute >= f && minute < t,
        ({ } f, { } t) => minute >= f || minute < t,
        _ => true
    };
}
