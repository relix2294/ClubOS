using System.Text.Json;
using ClubOS.CloudApi.Domain;
using ClubOS.Contracts;

namespace ClubOS.CloudApi.Api;

public static class Mapping
{
    /// <summary>Устройство считается offline, если heartbeat не приходил дольше этого окна (3 пропущенных heartbeat по 10 сек).</summary>
    public static readonly TimeSpan HeartbeatWindow = TimeSpan.FromSeconds(30);

    public static readonly TimeSpan EdgeOnlineWindow = TimeSpan.FromSeconds(30);

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
        d.EnrolledAtUtc, activeSession);

    public static CommandView ToView(this DeviceCommand c) => new(
        c.Id, c.CommandType, c.DeviceId, c.State, c.IssuedBy, c.IssuedAtUtc, c.ExpiresAtUtc, c.UpdatedAtUtc, c.Error,
        JsonDocument.Parse(c.PayloadJson).RootElement.Clone());

    public static SessionView ToView(this Session s) => new(
        s.Id, s.DeviceId, s.State, s.Origin, s.RequestedAtUtc, s.StartedAtUtc, s.EndRequestedAtUtc, s.EndedAtUtc,
        s.PricePerHourMinorUnits, s.Currency, s.Rounding, s.TotalMinorUnits, s.FailureReason, s.StartedBy, s.EndedBy);

    public static EdgeView ToView(this Edge e, DateTimeOffset now) => new(
        e.Id, e.Name, e.LastSeenAtUtc is not null && now - e.LastSeenAtUtc <= EdgeOnlineWindow, e.LastSeenAtUtc,
        e.PendingOutboxEvents, e.EnrolledAtUtc, e.CertificateExpiresAtUtc);

    public static PriceSnapshot Snapshot(this Session s) => new()
    {
        PricePerHourMinorUnits = s.PricePerHourMinorUnits,
        Currency = s.Currency,
        Rounding = s.Rounding,
        RuleVersion = s.RuleVersion
    };

    public static bool IsOpen(this Session s) => s.State is SessionState.Created or SessionState.Active;
}
