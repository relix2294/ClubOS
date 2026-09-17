namespace ClubOS.Contracts;

/// <summary>
/// Централизованные номера версий схем контрактов (ТЗ §24: у envelope есть schemaVersion).
/// Повышаются при несовместимом изменении полей payload.
/// </summary>
public static class SchemaVersions
{
    public const int Event = 1;
    public const int Command = 1;
    public const int Heartbeat = 1;
    public const int Enrollment = 1;
}
