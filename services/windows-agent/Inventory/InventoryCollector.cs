using System.Management;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using ClubOS.Contracts;

namespace ClubOS.WindowsAgent.Inventory;

/// <summary>
/// Безопасная инвентаризация ПК (ТЗ §3.3, §9 LOC-007): только несекретные поля.
/// Windows-специфично (WMI) — помечено SupportedOSPlatform.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class InventoryCollector
{
    public DeviceInventory Collect()
    {
        return new DeviceInventory
        {
            Hostname = Environment.MachineName,
            WindowsVersion = RuntimeInformation.OSDescription,
            Cpu = QueryScalar("SELECT Name FROM Win32_Processor") ?? "unknown",
            RamMegabytes = (int)(ReadTotalPhysicalMemoryBytes() / (1024 * 1024)),
            Ipv4 = FirstIpv4() ?? "0.0.0.0",
            AgentVersion = AgentVersion(),
        };
    }

    private static long ReadTotalPhysicalMemoryBytes()
    {
        var raw = QueryScalar("SELECT TotalPhysicalMemory FROM Win32_ComputerSystem");
        return long.TryParse(raw, out var bytes) ? bytes : 0;
    }

    private static string? QueryScalar(string wql)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(wql);
            foreach (var item in searcher.Get())
            {
                using (item)
                {
                    foreach (var prop in item.Properties)
                    {
                        return prop.Value?.ToString();
                    }
                }
            }
        }
        catch (ManagementException)
        {
            return null;
        }

        return null;
    }

    private static string? FirstIpv4()
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up
                || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
            {
                continue;
            }

            foreach (var addr in nic.GetIPProperties().UnicastAddresses)
            {
                if (addr.Address.AddressFamily == AddressFamily.InterNetwork
                    && !IPAddress.IsLoopback(addr.Address))
                {
                    return addr.Address.ToString();
                }
            }
        }

        return null;
    }

    private static string AgentVersion() =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0";
}
