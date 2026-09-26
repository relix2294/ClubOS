using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using ClubOS.Agent.Core;

namespace ClubOS.Agent.SessionHost;

/// <summary>
/// Невидимый контекст приложения: держит соединение с pipe службы и выполняет UI-запросы
/// в UI-потоке. При разрыве — переподключение каждые 3 секунды.
/// </summary>
internal sealed class HostContext : ApplicationContext
{
    private readonly Control _ui = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly List<LockOverlayForm> _overlays = [];
    private StreamWriter? _writer;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public HostContext()
    {
        _ui.CreateControl(); // привязка к UI-потоку для Invoke
        _ = Task.Run(() => RunPipeAsync(_cts.Token));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _cts.Cancel();
            _ui.Dispose();
        }

        base.Dispose(disposing);
    }

    private async Task RunPipeAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeClientStream(".", SessionHostProtocol.PipeName, PipeDirection.InOut,
                    PipeOptions.Asynchronous);
                await pipe.ConnectAsync(5000, ct);
                using var reader = new StreamReader(pipe, Encoding.UTF8);
                _writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true };

                while (await reader.ReadLineAsync(ct) is { } line)
                {
                    var request = JsonSerializer.Deserialize<HostRequest>(line);
                    if (request is null)
                    {
                        continue;
                    }

                    var reply = (HostMessage)_ui.Invoke(() => Handle(request));
                    await WriteAsync(reply, ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is IOException or TimeoutException or JsonException or UnauthorizedAccessException)
            {
                // Служба не запущена или перезапускается — пробуем снова.
            }
            finally
            {
                _writer = null;
            }

            await Task.Delay(TimeSpan.FromSeconds(3), ct).ContinueWith(_ => { }, TaskScheduler.Default);
        }
    }

    private HostMessage Handle(HostRequest request)
    {
        try
        {
            switch (request.Type)
            {
                case SessionHostProtocol.TypePing:
                    return Ok(request);
                case SessionHostProtocol.TypeShowMessage:
                    var form = new MessageForm(request.Title ?? "ClubOS", request.Message ?? string.Empty);
                    form.Show();
                    form.Activate();
                    return Ok(request);
                case SessionHostProtocol.TypeLock when request.Lock == true:
                    ShowOverlays(request.Reason);
                    return Ok(request);
                case SessionHostProtocol.TypeLock:
                    HideOverlays();
                    return Ok(request);
                default:
                    return new HostMessage { Id = request.Id, Ok = false, Error = $"Неизвестный тип запроса {request.Type}." };
            }
        }
        catch (Exception ex)
        {
            return new HostMessage { Id = request.Id, Ok = false, Error = ex.Message };
        }
    }

    private void ShowOverlays(string? reason)
    {
        HideOverlays();
        foreach (var screen in Screen.AllScreens)
        {
            var overlay = new LockOverlayForm(screen, reason, OnEmergencyUnlock);
            _overlays.Add(overlay);
            overlay.Show();
        }
    }

    private void HideOverlays()
    {
        foreach (var overlay in _overlays)
        {
            overlay.AllowClose = true;
            overlay.Close();
        }

        _overlays.Clear();
    }

    /// <summary>Аварийное снятие overlay (Ctrl+Shift+F12) — тестовый режим не должен «запирать» ПК.</summary>
    private void OnEmergencyUnlock()
    {
        HideOverlays();
        _ = WriteAsync(new HostMessage { Type = SessionHostProtocol.TypeLocalUnlock, Ok = true }, CancellationToken.None);
    }

    private async Task WriteAsync(HostMessage message, CancellationToken ct)
    {
        var writer = _writer;
        if (writer is null)
        {
            return;
        }

        await _writeLock.WaitAsync(ct);
        try
        {
            await writer.WriteLineAsync(JsonSerializer.Serialize(message).AsMemory(), ct);
        }
        catch (IOException)
        {
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private static HostMessage Ok(HostRequest request) => new() { Id = request.Id, Ok = true };
}
