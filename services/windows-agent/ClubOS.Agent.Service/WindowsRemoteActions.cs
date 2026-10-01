using System.Diagnostics;
using System.Runtime.Versioning;
using ClubOS.Agent.Core;
using ClubOS.Contracts;

namespace ClubOS.Agent.Service;

/// <summary>
/// Удалённый доступ на Windows (D-022). Процессы — только интерактивных сессий (SessionId ≠ 0) и не системные;
/// питание — штатный shutdown.exe с предупреждением пользователю; снимок экрана — через AgentSessionHost.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsRemoteActions(SessionHostPresenter host, TimeProvider time, ILogger<WindowsRemoteActions> logger)
    : IRemoteActions
{
    public async Task<RemoteResult> ScreenshotAsync(CancellationToken ct)
    {
        var (result, reply) = await host.CaptureScreenAsync(ct);
        if (!result.Ok || reply?.Data is null)
        {
            return RemoteResult.Fail(result.Error ?? "Снимок экрана не получен.");
        }

        return RemoteResult.Success(new ScreenshotOutput
        {
            Mime = "image/jpeg",
            DataBase64 = reply.Data,
            Width = reply.Width ?? 0,
            Height = reply.Height ?? 0,
            CapturedAtUtc = time.GetUtcNow()
        });
    }

    public RemoteResult ListProcesses()
    {
        var list = new List<ProcessInfo>();
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try
                {
                    if (p.SessionId == 0 || ProtectedProcesses.IsProtected(p.ProcessName))
                    {
                        continue;
                    }

                    list.Add(new ProcessInfo
                    {
                        ProcessId = p.Id,
                        Name = p.ProcessName,
                        MemoryMb = p.WorkingSet64 / (1024 * 1024),
                        WindowTitle = string.IsNullOrEmpty(p.MainWindowTitle) ? null : p.MainWindowTitle
                    });
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    // Процесс завершился или недоступен — пропускаем.
                }
            }
        }

        return RemoteResult.Success(new ProcessListOutput
        {
            Processes = list.OrderByDescending(p => p.WindowTitle is not null).ThenByDescending(p => p.MemoryMb)
                .Take(RemoteLimits.MaxProcesses).ToList()
        });
    }

    public RemoteResult KillProcess(int processId, string name)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            if (!string.Equals(process.ProcessName, name, StringComparison.OrdinalIgnoreCase))
            {
                return RemoteResult.Fail("PID занят другим процессом — обновите список.");
            }

            if (process.SessionId == 0 || ProtectedProcesses.IsProtected(process.ProcessName))
            {
                return RemoteResult.Fail($"Процесс «{name}» системный — завершать нельзя.");
            }

            process.Kill(entireProcessTree: true);
            process.WaitForExit(5000);
            logger.LogWarning("Удалённо завершён процесс {Name} ({Pid})", name, processId);
            return RemoteResult.Done;
        }
        catch (ArgumentException)
        {
            return RemoteResult.Fail("Процесс уже завершён.");
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return RemoteResult.Fail($"Не удалось завершить процесс: {ex.Message}");
        }
    }

    public RemoteResult Power(bool reboot, int delaySeconds, string? message)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "shutdown.exe")) { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add(reboot ? "/r" : "/s");
        start.ArgumentList.Add("/f");
        start.ArgumentList.Add("/t");
        start.ArgumentList.Add(delaySeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
        start.ArgumentList.Add("/c");
        start.ArgumentList.Add(string.IsNullOrWhiteSpace(message)
            ? reboot ? "Администратор клуба перезагружает компьютер." : "Администратор клуба выключает компьютер."
            : message);
        start.ArgumentList.Add("/d");
        start.ArgumentList.Add("p:0:0");
        using var process = Process.Start(start);
        if (process is null || !process.WaitForExit(10_000) || process.ExitCode != 0)
        {
            return RemoteResult.Fail($"shutdown.exe завершился с кодом {process?.ExitCode.ToString() ?? "?"}.");
        }

        logger.LogWarning("Удалённо: {Action} через {Delay} с", reboot ? "перезагрузка" : "выключение", delaySeconds);
        return RemoteResult.Done;
    }
}
