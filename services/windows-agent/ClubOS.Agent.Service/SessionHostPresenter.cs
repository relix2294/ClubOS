using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using ClubOS.Agent.Core;

namespace ClubOS.Agent.Service;

/// <summary>
/// Мост Service (Session 0) → AgentSessionHost (интерактивная сессия) через Named Pipe с ACL (ТЗ §11.1):
/// SYSTEM и Administrators — полный доступ, Interactive users — чтение/запись; сеть — запрещена
/// (PipeOptions.CurrentUserOnly не применим: клиент — другой пользователь). Дополнительно проверяется,
/// что клиент — исполняемый файл AgentSessionHost из каталога установки.
/// Если SessionHost не подключён, UI-команда честно завершается Failed.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class SessionHostPresenter(ILogger<SessionHostPresenter> logger) : BackgroundService, IUserPresenter
{
    private static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(15);
    private const string SessionHostExe = "ClubOS.Agent.SessionHost.exe";

    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly Dictionary<string, TaskCompletionSource<HostMessage>> _pending = new();
    private readonly Lock _pendingGate = new();
    private StreamWriter? _writer;
    private volatile bool _locked;
    private string? _lockReason;

    public bool IsLocked => _locked;

    public Task<PresentResult> ShowMessageAsync(string commandId, string title, string message, CancellationToken ct) =>
        SendAsync(new HostRequest
        {
            Id = commandId,
            Type = SessionHostProtocol.TypeShowMessage,
            Title = title,
            Message = message
        }, ct);

    public async Task<PresentResult> SetLockAsync(string commandId, bool locked, string? reason, CancellationToken ct)
    {
        var result = await SendAsync(new HostRequest
        {
            Id = commandId,
            Type = SessionHostProtocol.TypeLock,
            Lock = locked,
            Reason = reason
        }, ct);
        if (result.Ok)
        {
            _locked = locked;
            _lockReason = reason;
        }

        return result;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var pipe = CreatePipe();
                await pipe.WaitForConnectionAsync(stoppingToken);

                if (!IsTrustedClient(pipe))
                {
                    logger.LogWarning("Отклонено подключение к pipe: клиент не является {Exe}", SessionHostExe);
                    continue;
                }

                logger.LogInformation("AgentSessionHost подключён");
                using var reader = new StreamReader(pipe, Encoding.UTF8);
                _writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true };

                // После переподключения (перелогин пользователя) восстанавливаем overlay блокировки.
                if (_locked)
                {
                    _ = SendAsync(new HostRequest
                    {
                        Id = $"relock-{Guid.NewGuid():N}",
                        Type = SessionHostProtocol.TypeLock,
                        Lock = true,
                        Reason = _lockReason
                    }, stoppingToken);
                }

                while (await reader.ReadLineAsync(stoppingToken) is { } line)
                {
                    HandleIncoming(line);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (IOException ex)
            {
                logger.LogInformation("Соединение с AgentSessionHost разорвано: {Error}", ex.Message);
            }
            finally
            {
                _writer = null;
                FailPending("AgentSessionHost отключился до ответа.");
            }
        }
    }

    private void HandleIncoming(string line)
    {
        HostMessage? message;
        try
        {
            message = JsonSerializer.Deserialize<HostMessage>(line);
        }
        catch (JsonException)
        {
            logger.LogWarning("Некорректное сообщение от SessionHost");
            return;
        }

        if (message is null)
        {
            return;
        }

        if (message.Id is null && message.Type == SessionHostProtocol.TypeLocalUnlock)
        {
            _locked = false;
            logger.LogWarning("Overlay LockTestMode снят локально аварийной комбинацией клавиш");
            return;
        }

        lock (_pendingGate)
        {
            if (message.Id is not null && _pending.Remove(message.Id, out var tcs))
            {
                tcs.TrySetResult(message);
            }
        }
    }

    private async Task<PresentResult> SendAsync(HostRequest request, CancellationToken ct)
    {
        var writer = _writer;
        if (writer is null)
        {
            return PresentResult.Fail("Нет активной пользовательской сессии: AgentSessionHost не подключён.");
        }

        var tcs = new TaskCompletionSource<HostMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_pendingGate)
        {
            _pending[request.Id] = tcs;
        }

        try
        {
            await _sendLock.WaitAsync(ct);
            try
            {
                await writer.WriteLineAsync(JsonSerializer.Serialize(request).AsMemory(), ct);
            }
            finally
            {
                _sendLock.Release();
            }

            var reply = await tcs.Task.WaitAsync(ReplyTimeout, ct);
            return reply.Ok ? PresentResult.Success : PresentResult.Fail(reply.Error ?? "SessionHost сообщил об ошибке.");
        }
        catch (TimeoutException)
        {
            return PresentResult.Fail("AgentSessionHost не ответил вовремя.");
        }
        catch (IOException ex)
        {
            return PresentResult.Fail($"Ошибка канала SessionHost: {ex.Message}");
        }
        finally
        {
            lock (_pendingGate)
            {
                _pending.Remove(request.Id);
            }
        }
    }

    private void FailPending(string error)
    {
        lock (_pendingGate)
        {
            foreach (var tcs in _pending.Values)
            {
                tcs.TrySetResult(new HostMessage { Ok = false, Error = error });
            }

            _pending.Clear();
        }
    }

    private static NamedPipeServerStream CreatePipe()
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.InteractiveSid, null),
            PipeAccessRights.ReadWrite, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null),
            PipeAccessRights.FullControl, AccessControlType.Deny));

        return NamedPipeServerStreamAcl.Create(SessionHostProtocol.PipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security);
    }

    /// <summary>Клиент pipe должен быть AgentSessionHost из того же каталога установки, что и служба.</summary>
    private bool IsTrustedClient(NamedPipeServerStream pipe)
    {
        if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out var pid))
        {
            return false;
        }

        try
        {
            using var process = Process.GetProcessById((int)pid);
            var path = process.MainModule?.FileName;
            var expected = Path.Combine(AppContext.BaseDirectory, SessionHostExe);
            return path is not null && string.Equals(Path.GetFullPath(path), Path.GetFullPath(expected),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            logger.LogWarning("Не удалось проверить клиента pipe: {Error}", ex.Message);
            return false;
        }
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeClientProcessId(IntPtr pipe, out uint clientProcessId);
}
