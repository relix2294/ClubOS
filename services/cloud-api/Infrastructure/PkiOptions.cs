namespace ClubOS.CloudApi.Infrastructure;

/// <summary>Срок сертификатов Edge и устройств (dev CA, D-002). Продление — автоматически за 30 дней до конца.</summary>
public sealed class PkiOptions
{
    public const string Section = "Pki";

    public int CertificateDays { get; set; } = 90;

    public TimeSpan CertificateValidity => TimeSpan.FromDays(Math.Clamp(CertificateDays, 1, 397));
}
