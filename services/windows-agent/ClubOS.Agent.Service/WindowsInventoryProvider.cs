using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using ClubOS.Agent.Core;
using ClubOS.Contracts;
using Microsoft.Win32;

namespace ClubOS.Agent.Service;

/// <summary>Инвентаризация Windows (ТЗ §3.3): только несекретные поля, из реестра и WinAPI.</summary>
[SupportedOSPlatform("windows")]
public sealed partial class WindowsInventoryProvider : IInventoryProvider
{
    public DeviceInventory Collect() => new()
    {
        Hostname = Environment.MachineName,
        WindowsVersion = WindowsVersion(),
        Cpu = ReadString(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString")?.Trim()
              ?? $"{RuntimeInformation.ProcessArchitecture}, {Environment.ProcessorCount} ядер",
        RamMegabytes = TotalRamMegabytes(),
        Ipv4 = BasicInventoryProvider.PrimaryIpv4(),
        AgentVersion = BasicInventoryProvider.AgentVersion()
    };

    private static string WindowsVersion()
    {
        const string key = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";
        var product = ReadString(key, "ProductName") ?? "Windows";
        var display = ReadString(key, "DisplayVersion");
        var build = ReadString(key, "CurrentBuildNumber") ?? Environment.OSVersion.Version.Build.ToString();
        var ubr = Registry.LocalMachine.OpenSubKey(key)?.GetValue("UBR") is int u ? $".{u}" : string.Empty;

        // С Windows 11 ProductName в реестре по-прежнему «Windows 10 …» — уточняем по номеру сборки.
        if (int.TryParse(build, out var b) && b >= 22000)
        {
            product = product.Replace("Windows 10", "Windows 11", StringComparison.Ordinal);
        }

        return $"{product} {display} (build {build}{ubr})".Replace("  ", " ", StringComparison.Ordinal);
    }

    private static string? ReadString(string key, string name) =>
        Registry.LocalMachine.OpenSubKey(key)?.GetValue(name) as string;

    private static int TotalRamMegabytes()
    {
        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        return GlobalMemoryStatusEx(ref status) ? (int)(status.TotalPhys / (1024 * 1024)) : 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);
}
