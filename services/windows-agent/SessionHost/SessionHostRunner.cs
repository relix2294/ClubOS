using System.Drawing;
using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;
using ClubOS.WindowsAgent.Ipc;

namespace ClubOS.WindowsAgent.SessionHost;

/// <summary>
/// Session-host: процесс в интерактивной сессии пользователя, показывающий overlay/сообщения
/// (ТЗ §3.3, §25.2.5). Подключается к named pipe сервиса как клиент и исполняет UI-запросы.
/// LockTestMode — управляемый полноэкранный overlay, БЕЗ подмены системного Shell (D-003).
/// </summary>
[SupportedOSPlatform("windows")]
public static class SessionHostRunner
{
    public static void Run(string pipeName)
    {
        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        using var context = new SessionHostContext(pipeName);
        Application.Run(context);
    }
}

[SupportedOSPlatform("windows")]
internal sealed class SessionHostContext : ApplicationContext
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly string _pipeName;
    private readonly Form _pump;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;
    private OverlayForm? _overlay;

    public SessionHostContext(string pipeName)
    {
        _pipeName = pipeName;
        _pump = new Form
        {
            ShowInTaskbar = false,
            FormBorderStyle = FormBorderStyle.None,
            WindowState = FormWindowState.Minimized,
            Opacity = 0,
        };
        _ = _pump.Handle; // форсируем создание хэндла для Invoke с UI-потока
        _loop = Task.Run(() => PipeLoopAsync(_cts.Token));
    }

    private async Task PipeLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                await client.ConnectAsync(1000, ct);

                using var reader = new StreamReader(client, Encoding.UTF8, false, 1024, leaveOpen: true);
                await using var writer = new StreamWriter(client, Encoding.UTF8, 1024, leaveOpen: true) { AutoFlush = true };

                var line = await reader.ReadLineAsync(ct);
                if (line is null)
                {
                    continue;
                }

                var request = JsonSerializer.Deserialize<UiRequest>(line, JsonOptions);
                var response = request is null
                    ? new UiResponse { Ok = false, Error = "некорректный запрос" }
                    : Handle(request);

                await writer.WriteLineAsync(JsonSerializer.Serialize(response, JsonOptions));
            }
            catch (TimeoutException)
            {
                // Сервис ещё не открыл канал — просто повторим.
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (IOException)
            {
                // Обрыв канала — повторим соединение.
            }

            try
            {
                await Task.Delay(300, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private UiResponse Handle(UiRequest request)
    {
        try
        {
            UiResponse result = new() { Ok = false };
            _pump.Invoke(() => { result = Apply(request); });
            return result;
        }
        catch (Exception ex)
        {
            return new UiResponse { Ok = false, Error = ex.Message };
        }
    }

    private UiResponse Apply(UiRequest request)
    {
        switch (request.Kind)
        {
            case "ShowMessage":
                new MessageForm(
                    request.Title ?? "Сообщение",
                    request.Message ?? string.Empty,
                    request.AutoCloseSeconds).Show();
                return new UiResponse { Ok = true };

            case "LockTestMode":
                if (request.Lock)
                {
                    _overlay ??= new OverlayForm();
                    _overlay.SetReason(request.Message);
                    _overlay.Show();
                    _overlay.BringToFront();
                }
                else
                {
                    _overlay?.Close();
                    _overlay?.Dispose();
                    _overlay = null;
                }

                return new UiResponse { Ok = true };

            default:
                return new UiResponse { Ok = false, Error = $"неизвестный тип UI: {request.Kind}" };
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _cts.Cancel();
            try
            {
                _loop.Wait(TimeSpan.FromSeconds(2));
            }
            catch (AggregateException)
            {
                // Игнорируем — завершаемся.
            }

            _overlay?.Dispose();
            _pump.Dispose();
            _cts.Dispose();
        }

        base.Dispose(disposing);
    }
}

/// <summary>Полноэкранный overlay тестового режима (управляемый, не подмена Shell — D-003).</summary>
[SupportedOSPlatform("windows")]
internal sealed class OverlayForm : Form
{
    private readonly Label _label;

    public OverlayForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        WindowState = FormWindowState.Maximized;
        TopMost = true;
        ShowInTaskbar = false;
        BackColor = Color.Black;
        StartPosition = FormStartPosition.Manual;
        Bounds = Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 800, 600);

        _label = new Label
        {
            Dock = DockStyle.Fill,
            ForeColor = Color.White,
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font("Segoe UI", 28, FontStyle.Bold),
        };
        Controls.Add(_label);
        SetReason(null);
    }

    public void SetReason(string? reason) =>
        _label.Text = string.IsNullOrWhiteSpace(reason)
            ? "ТЕСТОВЫЙ РЕЖИМ"
            : $"ТЕСТОВЫЙ РЕЖИМ\n\n{reason}";
}

/// <summary>Видимое сообщение пользователю (ShowMessage) с авто-закрытием.</summary>
[SupportedOSPlatform("windows")]
internal sealed class MessageForm : Form
{
    public MessageForm(string title, string message, int autoCloseSeconds)
    {
        Text = title;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = true;
        ShowInTaskbar = false;
        MinimizeBox = false;
        MaximizeBox = false;
        ClientSize = new Size(440, 200);

        var label = new Label
        {
            Dock = DockStyle.Top,
            Height = 150,
            Padding = new Padding(16),
            Text = message,
            Font = new Font("Segoe UI", 12),
            TextAlign = ContentAlignment.MiddleCenter,
        };
        var okButton = new Button
        {
            Text = "OK",
            Dock = DockStyle.Bottom,
            Height = 40,
        };
        okButton.Click += (_, _) => Close();

        Controls.Add(label);
        Controls.Add(okButton);

        if (autoCloseSeconds > 0)
        {
            var timer = new System.Windows.Forms.Timer { Interval = autoCloseSeconds * 1000 };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                Close();
            };
            FormClosed += (_, _) => timer.Dispose();
            timer.Start();
        }
    }
}
