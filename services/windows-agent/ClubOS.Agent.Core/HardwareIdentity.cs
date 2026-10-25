using System.Net.NetworkInformation;
using ClubOS.Contracts;

namespace ClubOS.Agent.Core;

/// <summary>Аппаратная идентичность бездискового ПК (D-018).</summary>
public sealed record HardwareInfo(string HardwareId, IReadOnlyList<string> MacAddresses);

public interface IHardwareIdentity
{
    HardwareInfo Collect();
}

/// <summary>
/// MAC загрузочной сетевой карты — так бездисковые серверы (CCBoot, iCafeCloud и др.) различают клиентов.
/// Берётся активная физическая карта со шлюзом по умолчанию (через неё ПК загрузился и ходит в сеть);
/// при нескольких — самая быстрая, затем по индексу. UUID материнской платы не используется: у одинаковых
/// дешёвых плат он часто совпадает.
/// </summary>
public sealed class NetworkHardwareIdentity(AgentOptions options) : IHardwareIdentity
{
    public HardwareInfo Collect()
    {
        var candidates = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up &&
                        n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
            .Select(n => (Nic: n, Mac: HardwareIds.NormalizeMac(Convert.ToHexString(n.GetPhysicalAddress().GetAddressBytes()))))
            .Where(x => x.Mac is not null)
            .Select(x => (x.Nic, Mac: x.Mac!, HasGateway: HasGateway(x.Nic)))
            .OrderByDescending(x => x.HasGateway)
            .ThenByDescending(x => SafeSpeed(x.Nic))
            .ThenBy(x => x.Nic.Id, StringComparer.Ordinal)
            .ToList();
        var macs = candidates.Select(x => x.Mac).Distinct().ToList();

        var configured = HardwareIds.NormalizeMac(options.HardwareIdOverride);
        var primary = configured ?? macs.FirstOrDefault()
            ?? throw new InvalidOperationException("Не найдена активная сетевая карта с MAC-адресом.");
        if (!macs.Contains(primary))
        {
            macs.Insert(0, primary);
        }

        return new HardwareInfo(primary, macs);
    }

    private static bool HasGateway(NetworkInterface nic)
    {
        try
        {
            return nic.GetIPProperties().GatewayAddresses.Any(g =>
                g.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !g.Address.Equals(System.Net.IPAddress.Any));
        }
        catch (NetworkInformationException)
        {
            return false;
        }
    }

    private static long SafeSpeed(NetworkInterface nic)
    {
        try
        {
            return nic.Speed;
        }
        catch (Exception ex) when (ex is NetworkInformationException or PlatformNotSupportedException)
        {
            return 0;
        }
    }
}
