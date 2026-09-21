namespace ClubOS.CloudApi.Common;

/// <summary>
/// Генерация идентификаторов. На M0 — GUID в формате "N" (32 hex, без дефисов),
/// совместимо со строковыми ID контрактов (ТЗ §23.1). При переходе на ULID
/// достаточно заменить реализацию здесь.
/// </summary>
public static class Ids
{
    public static string New() => Guid.NewGuid().ToString("N");
}
