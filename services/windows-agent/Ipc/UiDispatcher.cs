using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using ClubOS.WindowsAgent.Config;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClubOS.WindowsAgent.Ipc;

/// <summary>
/// Отправка UI-запросов из сервиса (session 0) в session-host интерактивной сессии по
/// named pipe с ACL (ТЗ §3.3): доступ только локальным пользователям, сеть — запрещена.
/// На каждый запрос — отдельное соединение; ответ session-host подтверждает исполнение.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class UiDispatcher(IOptions<AgentOptions> options, ILogger<UiDispatcher> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);

    private readonly AgentOptions _options = options.Value;

    public async Task<UiResponse> SendAsync(UiRequest request, CancellationToken ct)
    {
        using var server = NamedPipeServerStreamAcl.Create(
            _options.PipeName,
            PipeDirection.InOut,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            inBufferSize: 0,
            outBufferSize: 0,
            BuildSecurity());

        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        connectCts.CancelAfter(ConnectTimeout);

        try
        {
            await server.WaitForConnectionAsync(connectCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning("session-host не подключён за {Timeout}s — UI не показан.", ConnectTimeout.TotalSeconds);
            return new UiResponse { Ok = false, Error = "session-host не подключён" };
        }

        using var reader = new StreamReader(server, Encoding.UTF8, false, 1024, leaveOpen: true);
        await using var writer = new StreamWriter(server, Encoding.UTF8, 1024, leaveOpen: true) { AutoFlush = true };

        await writer.WriteLineAsync(JsonSerializer.Serialize(request, JsonOptions));

        var line = await reader.ReadLineAsync(ct);
        if (line is null)
        {
            return new UiResponse { Ok = false, Error = "нет ответа от session-host" };
        }

        return JsonSerializer.Deserialize<UiResponse>(line, JsonOptions)
            ?? new UiResponse { Ok = false, Error = "некорректный ответ session-host" };
    }

    private static PipeSecurity BuildSecurity()
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
            PipeAccessRights.ReadWrite,
            AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));
        // Явный запрет удалённого доступа к каналу.
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.NetworkSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Deny));
        return security;
    }
}
