using System.Text.Json;
using ClubOS.CloudApi.Domain;
using ClubOS.Contracts;

namespace ClubOS.CloudApi.Api;

public static class Mapping
{
    /// <summary>Устройство считается offline, если heartbeat не приходил дольше этого окна (3 пропущенных heartbeat по 10 сек).</summary>
    public static readonly TimeSpan HeartbeatWindow = TimeSpan.FromSeconds(30);

    public static readonly TimeSpan EdgeOnlineWindow = TimeSpan.FromSeconds(30);

    /// <param name="mfaSetupRequired">Роль требует MFA, а она не включена: прав нет до настройки.</param>
    public static UserView ToView(this User u, string organizationName, bool mfaSetupRequired) => new(
        u.Id, u.Email, u.DisplayName, u.Role, u.OrganizationId, organizationName,
        u.MustChangePassword || mfaSetupRequired ? new List<string>() : Security.Permissions.For(u.Role).Order().ToList(),
        u.MustChangePassword, u.MfaEnabled, mfaSetupRequired);

    /// <param name="locationIds">Назначенные локации (для сотрудника с ограниченным доступом).</param>
    public static StaffMemberView ToStaffView(this User u, IReadOnlyList<string> locationIds) => new(
        u.Id, u.Email, u.DisplayName, u.Role, u.IsActive, u.MustChangePassword, u.CreatedAtUtc, u.LastLoginAtUtc,
        u.MfaEnabled, u.AllLocations || u.Role == Roles.Owner,
        u.AllLocations || u.Role == Roles.Owner ? [] : locationIds);

    public static DeviceStatus EffectiveStatus(Device d, DateTimeOffset now) =>
        d.LastHeartbeatUtc is null || now - d.LastHeartbeatUtc > HeartbeatWindow ? DeviceStatus.Offline : d.Status;

    public static DeviceView ToView(this Device d, string zoneName, SessionView? activeSession, DateTimeOffset now) => new(
        d.Id, d.DisplayName, d.LocationId, d.ZoneId, zoneName, EffectiveStatus(d, now), d.Simulated, d.LastHeartbeatUtc,
        d.Hostname is null
            ? null
            : new DeviceInventory
            {
                Hostname = d.Hostname,
                WindowsVersion = d.WindowsVersion ?? string.Empty,
                Cpu = d.Cpu ?? string.Empty,
                RamMegabytes = d.RamMegabytes ?? 0,
                Ipv4 = d.Ipv4 ?? string.Empty,
                AgentVersion = d.AgentVersion ?? string.Empty
            },
        d.EnrolledAtUtc, activeSession, d.CertificateExpiresAtUtc,
        d.HardwareId is null ? null : ClubOS.Contracts.HardwareIds.FormatMac(d.HardwareId));

    public static CommandView ToView(this DeviceCommand c) => new(
        c.Id, c.CommandType, c.DeviceId, c.State, c.IssuedBy, c.IssuedAtUtc, c.ExpiresAtUtc, c.UpdatedAtUtc, c.Error,
        JsonDocument.Parse(c.PayloadJson).RootElement.Clone(), c.ResultJson is not null);

    /// <summary>Команда с результатом (снимок экрана может весить сотни КБ — только по запросу одной команды).</summary>
    public static CommandView ToViewWithResult(this DeviceCommand c) => c.ToView() with
    {
        Result = c.ResultJson is null ? null : JsonDocument.Parse(c.ResultJson).RootElement.Clone()
    };

    public static SessionView ToView(this Session s) => new(
        s.Id, s.DeviceId, s.State, s.Origin, s.RequestedAtUtc, s.StartedAtUtc, s.EndRequestedAtUtc, s.EndedAtUtc,
        s.PricePerHourMinorUnits, s.Currency, s.Rounding, s.TotalMinorUnits, s.FailureReason, s.StartedBy, s.EndedBy,
        s.DurationMinutes, s.PlannedEndAtUtc, s.EndReason, s.ClientId, PricingJson.Read(s.PeriodsJson), s.UtcOffsetMinutes,
        s.PackageName, s.PackageMinutes, s.PackagePriceMinorUnits);

    public static EdgeView ToView(this Edge e, DateTimeOffset now) => new(
        e.Id, e.Name, e.LastSeenAtUtc is not null && now - e.LastSeenAtUtc <= EdgeOnlineWindow, e.LastSeenAtUtc,
        e.PendingOutboxEvents, e.EnrolledAtUtc, e.CertificateExpiresAtUtc);

    public static PriceSnapshot Snapshot(this Session s) => new()
    {
        PricePerHourMinorUnits = s.PricePerHourMinorUnits,
        Currency = s.Currency,
        Rounding = s.Rounding,
        RuleVersion = s.RuleVersion,
        Periods = PricingJson.Read(s.PeriodsJson),
        UtcOffsetMinutes = s.UtcOffsetMinutes,
        PackageId = s.PackageId,
        PackageName = s.PackageName,
        PackageMinutes = s.PackageMinutes,
        PackagePriceMinorUnits = s.PackagePriceMinorUnits
    };

    /// <summary>Записать снимок тарифа из события Edge в сессию Cloud.</summary>
    public static void ApplySnapshot(this Session s, PriceSnapshot p)
    {
        s.PricePerHourMinorUnits = p.PricePerHourMinorUnits;
        s.Currency = p.Currency;
        s.Rounding = p.Rounding;
        s.RuleVersion = p.RuleVersion;
        s.PeriodsJson = PricingJson.Write(p.Periods);
        s.UtcOffsetMinutes = p.UtcOffsetMinutes;
        s.PackageId = p.PackageId;
        s.PackageName = p.PackageName;
        s.PackageMinutes = p.PackageMinutes;
        s.PackagePriceMinorUnits = p.PackagePriceMinorUnits;
    }

    public static ZoneView ToView(this Zone z, IEnumerable<TariffPackage> packages) => new(
        z.Id, z.Name, z.PricePerHourMinorUnits, PricingJson.Read(z.PeriodsJson),
        packages.Where(p => p.ZoneId == z.Id).OrderBy(p => p.DurationMinutes).ThenBy(p => p.Name).Select(p => p.ToView()).ToList());

    public static TariffPackageView ToView(this TariffPackage p) => new(
        p.Id, p.ZoneId, p.Name, p.DurationMinutes, p.PriceMinorUnits, p.AvailableFromMinute, p.AvailableToMinute, p.IsActive);

    public static bool IsOpen(this Session s) => s.State is SessionState.Created or SessionState.Active;
}
