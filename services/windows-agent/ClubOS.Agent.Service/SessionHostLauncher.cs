using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using ClubOS.Agent.Core;
using ClubOS.Agent.Core.PlayerShell;

namespace ClubOS.Agent.Service;

/// <summary>
/// Сторож AgentSessionHost для Player Shell (M1). Обычно SessionHost запускает задача планировщика при входе
/// пользователя. Если его закрыли (Диспетчер задач) или он упал, экран клуба пропал бы до следующего входа.
/// Служба (LocalSystem) раз в несколько секунд проверяет подключение и при его отсутствии запускает
/// SessionHost в активной консольной сессии от имени вошедшего пользователя (WTSQueryUserToken +
/// CreateProcessAsUser) — без повышения прав SessionHost. Winlogon Shell, GPO и политики не меняются.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class SessionHostLauncher(
    SessionHostPresenter presenter,
    AgentOptions options,
    ILogger<SessionHostLauncher> logger) : BackgroundService
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(3);

    /// <summary>Сколько ждать подключения, прежде чем считать SessionHost незапущенным (вход пользователя, старт).</summary>
    private static readonly TimeSpan GracePeriod = TimeSpan.FromSeconds(10);

    /// <summary>Не чаще одного запуска за интервал: защита от цикла запусков, если SessionHost не может подключиться.</summary>
    private static readonly TimeSpan MinRelaunchInterval = TimeSpan.FromSeconds(20);

    private const uint NoSession = 0xFFFFFFFF;
    private const int ErrorNoToken = 1008;
    private const uint CreateUnicodeEnvironment = 0x00000400;

    private DateTimeOffset? _disconnectedSince;
    private DateTimeOffset _lastLaunch = DateTimeOffset.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (options.Shell.Mode == ShellMode.Off)
        {
            return;
        }

        var exe = Path.Combine(AppContext.BaseDirectory, "ClubOS.Agent.SessionHost.exe");
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(CheckInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            var now = DateTimeOffset.UtcNow;
            if (presenter.IsConnected)
            {
                _disconnectedSince = null;
                continue;
            }

            _disconnectedSince ??= now;
            if (now - _disconnectedSince < GracePeriod || now - _lastLaunch < MinRelaunchInterval)
            {
                continue;
            }

            if (!File.Exists(exe))
            {
                logger.LogError("Не найден {Exe}: Player Shell не может быть показан", exe);
                _lastLaunch = now;
                continue;
            }

            _lastLaunch = now;
            TryLaunchInActiveSession(exe);
        }
    }

    private void TryLaunchInActiveSession(string exe)
    {
        var sessionId = WTSGetActiveConsoleSessionId();
        if (sessionId == NoSession)
        {
            return; // консоль не подключена (переключение пользователей)
        }

        if (!WTSQueryUserToken(sessionId, out var token))
        {
            var error = Marshal.GetLastPInvokeError();
            if (error != ErrorNoToken)
            {
                logger.LogWarning("WTSQueryUserToken({Session}) не удался: {Error}", sessionId, new Win32Exception(error).Message);
            }

            return; // ERROR_NO_TOKEN — никто не вошёл: экран входа Windows, Player Shell не нужен
        }

        var environment = IntPtr.Zero;
        var desktop = Marshal.StringToHGlobalUni(@"winsta0\default");
        var commandLine = Marshal.StringToHGlobalUni($"\"{exe}\"");
        try
        {
            if (!CreateEnvironmentBlock(out environment, token, false))
            {
                environment = IntPtr.Zero;
            }

            var startup = new StartupInfo { Cb = Marshal.SizeOf<StartupInfo>(), Desktop = desktop };
            if (!CreateProcessAsUser(token, exe, commandLine, IntPtr.Zero, IntPtr.Zero, false,
                    environment == IntPtr.Zero ? 0 : CreateUnicodeEnvironment, environment,
                    Path.GetDirectoryName(exe), ref startup, out var process))
            {
                logger.LogWarning("Не удалось запустить AgentSessionHost в сессии {Session}: {Error}", sessionId,
                    new Win32Exception(Marshal.GetLastPInvokeError()).Message);
                return;
            }

            CloseHandle(process.Thread);
            CloseHandle(process.Process);
            logger.LogWarning("AgentSessionHost не был подключён — перезапущен службой в сессии {Session} (PID {Pid})",
                sessionId, process.ProcessId);
        }
        finally
        {
            if (environment != IntPtr.Zero)
            {
                DestroyEnvironmentBlock(environment);
            }

            Marshal.FreeHGlobal(commandLine);
            Marshal.FreeHGlobal(desktop);
            CloseHandle(token);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        public int Cb;
        public IntPtr Reserved;
        public IntPtr Desktop;
        public IntPtr Title;
        public int X;
        public int Y;
        public int XSize;
        public int YSize;
        public int XCountChars;
        public int YCountChars;
        public int FillAttribute;
        public int Flags;
        public short ShowWindow;
        public short Reserved2Size;
        public IntPtr Reserved2;
        public IntPtr StdInput;
        public IntPtr StdOutput;
        public IntPtr StdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr Process;
        public IntPtr Thread;
        public int ProcessId;
        public int ThreadId;
    }

    [LibraryImport("kernel32.dll")]
    private static partial uint WTSGetActiveConsoleSessionId();

    [LibraryImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WTSQueryUserToken(uint sessionId, out IntPtr token);

    [LibraryImport("userenv.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateEnvironmentBlock(out IntPtr environment, IntPtr token,
        [MarshalAs(UnmanagedType.Bool)] bool inherit);

    [LibraryImport("userenv.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyEnvironmentBlock(IntPtr environment);

    [LibraryImport("advapi32.dll", EntryPoint = "CreateProcessAsUserW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateProcessAsUser(IntPtr token, string applicationName, IntPtr commandLine,
        IntPtr processAttributes, IntPtr threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
        uint creationFlags, IntPtr environment, string? currentDirectory, ref StartupInfo startupInfo,
        out ProcessInformation processInformation);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr handle);
}
