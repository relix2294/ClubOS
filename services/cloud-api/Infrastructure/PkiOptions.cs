namespace ClubOS.CloudApi.Infrastructure;

/// <summary>Срок сертификатов Edge и устройств (dev CA, D-002). Продление — автоматически за 30 дней до конца.</summary>
public sealed class PkiOptions
{
    public const string Section = "Pki";

    public int CertificateDays { get; set; } = 90;

    /// <summary>
    /// Отклонять запросы Edge без подписи тела (D-007). Edge M1 подписывает всегда; false — только если в клубах
    /// остались Edge версии M0 (обновляйте Edge раньше Cloud).
    /// </summary>
    public bool RequireEdgeRequestBinding { get; set; } = true;

    /// <summary>
    /// Срок действия CRL (nextUpdate). Edge обновляет список каждые несколько минут; просроченный список Edge
    /// продолжает применять (клуб без интернета не должен терять проверку отзыва), но пишет предупреждение.
    /// </summary>
    public int CrlDays { get; set; } = 7;

    public TimeSpan CrlValidity => TimeSpan.FromDays(Math.Clamp(CrlDays, 1, 30));

    public TimeSpan CertificateValidity => TimeSpan.FromDays(Math.Clamp(CertificateDays, 1, 397));
}
