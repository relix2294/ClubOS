using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using ClubOS.Contracts;

namespace ClubOS.Agent.Core;

/// <summary>Кроссплатформенная инвентаризация (dev/Linux). На Windows используется WindowsInventoryProvider.</summary>
public sealed class BasicInventoryProvider : IInventoryProvider
{
    public DeviceInventory Collect() => new()
    {
        Hostname = Environment.MachineName,
        WindowsVersion = RuntimeInformation.OSDescription,
        Cpu = CpuName(),
        RamMegabytes = (int)(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024 * 1024)),
        Ipv4 = PrimaryIpv4(),
        AgentVersion = AgentVersion()
    };

    public static string AgentVersion() =>
        typeof(BasicInventoryProvider).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion.Split('+')[0] ?? "0.0.0";

    public static string PrimaryIpv4()
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
            {
                continue;
            }

            foreach (var address in nic.GetIPProperties().UnicastAddresses)
            {
                if (address.Address.AddressFamily == AddressFamily.InterNetwork)
                {
                    return address.Address.ToString();
                }
            }
        }

        return "0.0.0.0";
    }

    private static string CpuName()
    {
        try
        {
            if (OperatingSystem.IsLinux() && File.Exists("/proc/cpuinfo"))
            {
                var line = File.ReadLines("/proc/cpuinfo").FirstOrDefault(l => l.StartsWith("model name", StringComparison.Ordinal));
                if (line is not null)
                {
                    return line[(line.IndexOf(':') + 1)..].Trim();
                }
            }
        }
        catch (IOException)
        {
        }

        return $"{RuntimeInformation.ProcessArchitecture}, {Environment.ProcessorCount} ядер";
    }
}
