using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using ClubOS.Agent.Core;
using ClubOS.Agent.Core.PlayerShell;

namespace ClubOS.Agent.SessionHost;

/// <summary>
/// Невидимый контекст приложения: держит соединение с pipe службы и выполняет UI-запросы
/// в UI-потоке. При разрыве — переподключение каждые 3 секунды. Отрисовывает Player Shell:
/// экран клуба (ПК свободен / сессия завершена), индикатор сессии, режим обслуживания.
/// </summary>
internal sealed class HostContext : ApplicationContext
{
    private static readonly TimeSpan MaintenanceReplyTimeout = TimeSpan.FromSeconds(10);

    private readonly Control _ui = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly List<LockOverlayForm> _overlays = [];
    private readonly List<ShellScreenForm> _screens = [];
    private readonly ConcurrentDictionary<string, TaskCompletionSource<HostRequest>> _pendingReplies = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly System.Windows.Forms.Timer _tick = new() { Interval = 1000 };
    private SessionHudForm? _hud;
    private MaintenanceBannerForm? _banner;
    private TechnicianPinForm? _pinForm;
    private ShellState? _shell;
    private string? _summaryShownFor;
    private StreamWriter? _writer;

    public HostContext()
    {
        _ui.CreateControl(); // привязка к UI-потоку для Invoke
        _tick.Tick += (_, _) => OnTick();
        _tick.Start();
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += (_, _) => _ui.BeginInvoke(RebuildScreens);
        _ = Task.Run(() => RunPipeAsync(_cts.Token));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            KeyboardGuard.Disable();
            _tick.Dispose();
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
                    if (line.Length > SessionHostProtocol.MaxLineLength)
                    {
                        break;
                    }

                    var request = JsonSerializer.Deserialize<HostRequest>(line);
                    if (request is null)
                    {
                        continue;
                    }

                    // Ответ службы на наш запрос (PIN техника) — не требует ответа.
                    if (request.Type == SessionHostProtocol.TypeMaintenanceResult)
                    {
                        if (_pendingReplies.TryRemove(request.Id, out var tcs))
                        {
                            tcs.TrySetResult(request);
                        }

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
                // Служба не запущена или перезапускается — пробуем снова. Экран клуба остаётся как был:
                // потеря связи со службой не должна открывать свободный ПК.
            }
            finally
            {
                _writer = null;
                foreach (var pending in _pendingReplies.Values)
                {
                    pending.TrySetResult(new HostRequest
                    {
                        Id = string.Empty,
                        Type = SessionHostProtocol.TypeMaintenanceResult,
                        Ok = false,
                        Error = "Нет связи со службой ClubOS."
                    });
                }

                _pendingReplies.Clear();
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
                case SessionHostProtocol.TypeShell when request.Shell is { } shell:
                    ApplyShell(shell);
                    return Ok(request);
                case SessionHostProtocol.TypeScreenshot:
                    return ScreenCapture.Capture(request.Id);
                default:
                    return new HostMessage { Id = request.Id, Ok = false, Error = $"Неизвестный тип запроса {request.Type}." };
            }
        }
        catch (Exception ex)
        {
            return new HostMessage { Id = request.Id, Ok = false, Error = ex.Message };
        }
    }

    // ---------- Player Shell ----------

    private void ApplyShell(ShellState state)
    {
        _shell = state;
        var locked = state.Mode == ShellMode.Enforced && state.View is ShellView.Free or ShellView.Ended;

        if (locked)
        {
            ShowScreens(state);
        }
        else
        {
            HideScreens();
        }

        if (state.View == ShellView.Session)
        {
            _hud ??= new SessionHudForm();
            _hud.Render(state);
            if (!_hud.Visible)
            {
                _hud.Show();
            }
        }
        else
        {
            _hud?.Close();
            _hud = null;
        }

        if (state.View == ShellView.Maintenance)
        {
            _banner ??= new MaintenanceBannerForm(EndMaintenance);
            _banner.Render(state);
            if (!_banner.Visible)
            {
                _banner.Show();
            }
        }
        else
        {
            _banner?.Close();
            _banner = null;
        }

        // В режиме Hud ПК не закрывается — итог показываем один раз обычным сообщением.
        if (state is { Mode: ShellMode.Hud, View: ShellView.Ended, Ended: { Estimated: false } ended } &&
            _summaryShownFor != ended.SessionId)
        {
            _summaryShownFor = ended.SessionId;
            new MessageForm(ShellText.EndedTitle, $"{ShellText.EndReason(ended.Reason)}\n{ShellText.EndedSummary(ended)}").Show();
        }
    }

    private void ShowScreens(ShellState state)
    {
        if (_screens.Count == 0)
        {
            foreach (var screen in Screen.AllScreens)
            {
                var form = new ShellScreenForm(screen, screen.Primary, OpenTechnicianLogin);
                _screens.Add(form);
                form.Show();
            }
        }

        foreach (var form in _screens)
        {
            form.Render(state);
        }

        KeyboardGuard.Enable();
        BringScreensToFront();
    }

    private void HideScreens()
    {
        KeyboardGuard.Disable();
        _pinForm?.Close();
        foreach (var form in _screens)
        {
            form.AllowClose = true;
            form.Close();
        }

        _screens.Clear();
    }

    private void RebuildScreens()
    {
        if (_screens.Count == 0 || _shell is null)
        {
            return;
        }

        HideScreens();
        ApplyShell(_shell);
    }

    /// <summary>Раз в секунду: таймер индикатора, часы экрана и удержание экрана клуба поверх окон.</summary>
    private void OnTick()
    {
        _hud?.Tick();
        foreach (var form in _screens)
        {
            form.Tick();
        }

        if (_screens.Count > 0 && _pinForm is null && _overlays.Count == 0)
        {
            BringScreensToFront();
        }
    }

    private void BringScreensToFront()
    {
        var active = Form.ActiveForm;
        if (active is ShellScreenForm || active is TechnicianPinForm)
        {
            return;
        }

        foreach (var form in _screens)
        {
            form.TopMost = true;
            form.BringToFront();
        }

        _screens.FirstOrDefault()?.Activate();
    }

    private void OpenTechnicianLogin()
    {
        if (_pinForm is not null)
        {
            _pinForm.Activate();
            return;
        }

        KeyboardGuard.Disable(); // диалогу нужен обычный ввод
        _pinForm = new TechnicianPinForm(RequestMaintenanceAsync);
        _pinForm.FormClosed += (_, _) =>
        {
            _pinForm = null;
            if (_screens.Count > 0)
            {
                KeyboardGuard.Enable();
            }
        };
        _pinForm.Show(_screens.FirstOrDefault());
        _pinForm.Activate();
    }

    private async Task<(bool Ok, string? Error)> RequestMaintenanceAsync(string pin)
    {
        var id = $"mnt-{Guid.NewGuid():N}";
        var tcs = new TaskCompletionSource<HostRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingReplies[id] = tcs;
        try
        {
            if (!await WriteAsync(new HostMessage { Id = id, Type = SessionHostProtocol.TypeMaintenanceRequest, Pin = pin },
                    _cts.Token))
            {
                return (false, "Нет связи со службой ClubOS.");
            }

            var reply = await tcs.Task.WaitAsync(MaintenanceReplyTimeout);
            return (reply.Ok == true, reply.Error);
        }
        catch (TimeoutException)
        {
            return (false, "Служба ClubOS не ответила.");
        }
        finally
        {
            _pendingReplies.TryRemove(id, out _);
        }
    }

    private void EndMaintenance() =>
        _ = WriteAsync(new HostMessage { Type = SessionHostProtocol.TypeMaintenanceEnd, Ok = true }, CancellationToken.None);

    // ---------- LockTestMode ----------

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

    private async Task<bool> WriteAsync(HostMessage message, CancellationToken ct)
    {
        var writer = _writer;
        if (writer is null)
        {
            return false;
        }

        await _writeLock.WaitAsync(ct);
        try
        {
            await writer.WriteLineAsync(JsonSerializer.Serialize(message).AsMemory(), ct);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private static HostMessage Ok(HostRequest request) => new() { Id = request.Id, Ok = true };
}
