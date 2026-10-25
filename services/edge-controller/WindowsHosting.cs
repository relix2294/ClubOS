using System.Runtime.Versioning;
using Microsoft.Extensions.Logging.EventLog;

namespace ClubOS.EdgeController;

/// <summary>Параметры работы Edge как Windows-службы на сервере клуба.</summary>
public static class WindowsHosting
{
    public const string ServiceName = "ClubOSEdge";

    /// <summary>Источник Event Log = имя службы (регистрируется install-edge.ps1).</summary>
    [SupportedOSPlatform("windows")]
    public static void UseServiceEventSource(EventLogSettings settings) => settings.SourceName = ServiceName;
}
