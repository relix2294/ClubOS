using ClubOS.Agent.Core.PlayerShell;

namespace ClubOS.Agent.SessionHost;

/// <summary>
/// Индикатор сессии в углу экрана: остаток/прошедшее время и стоимость. Окно не забирает фокус
/// (WS_EX_NOACTIVATE) и не мешает игре; поверх полноэкранных эксклюзивных игр Windows его не покажет —
/// поэтому предупреждения о конце времени дублируются цветом и строкой, а не диалогами.
/// Щелчок сворачивает индикатор до одной строки.
/// </summary>
internal sealed class SessionHudForm : Form
{
    private const int WsExNoActivate = 0x08000000;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExTopmost = 0x00000008;

    private static readonly Color Normal = Color.FromArgb(15, 23, 42);
    private static readonly Color Soon = Color.FromArgb(180, 83, 9);
    private static readonly Color Critical = Color.FromArgb(185, 28, 28);

    private readonly Label _device = NewLabel(9F, Color.FromArgb(203, 213, 225));
    private readonly Label _time = NewLabel(16F, Color.White, bold: true);
    private readonly Label _cost = NewLabel(10F, Color.White);
    private readonly Label _warning = NewLabel(9F, Color.White);
    private bool _compact;
    private ShellState? _state;

    public SessionHudForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = Normal;
        Opacity = 0.92;
        Padding = new Padding(12, 8, 12, 8);
        Text = "ClubOS";

        var stack = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            BackColor = Color.Transparent
        };
        stack.Controls.AddRange([_device, _time, _cost, _warning]);
        Controls.Add(stack);

        foreach (Control c in new Control[] { this, stack, _device, _time, _cost, _warning })
        {
            c.Click += (_, _) => ToggleCompact();
        }
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WsExNoActivate | WsExToolWindow | WsExTopmost;
            return cp;
        }
    }

    public void Render(ShellState state)
    {
        _state = state;
        _device.Text = state.ClubName is null ? state.DeviceName : $"{state.DeviceName} · {state.ClubName}";
        Tick();
    }

    public void Tick()
    {
        if (_state?.Session is not { } session)
        {
            return;
        }

        var text = ShellText.Hud(session, ShellClock.EdgeNow(_state, DateTimeOffset.UtcNow));
        _time.Text = text.Time;
        _cost.Text = text.Cost;
        _warning.Text = text.Warning ?? string.Empty;
        _warning.Visible = text.Warning is not null;
        _device.Visible = !_compact;
        _cost.Visible = !_compact;
        BackColor = text.Level switch
        {
            ShellWarning.Critical => Critical,
            ShellWarning.Soon => Soon,
            _ => Normal
        };
        PlaceInCorner();
    }

    private void ToggleCompact()
    {
        _compact = !_compact;
        Tick();
    }

    private void PlaceInCorner()
    {
        var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 720);
        var width = _warning.Visible ? 360 : 240;
        var height = (_compact ? 44 : 92) + (_warning.Visible ? 22 : 0);
        var bounds = new Rectangle(area.Right - width - 16, area.Bottom - height - 16, width, height);
        if (Bounds != bounds)
        {
            Bounds = bounds;
        }
    }

    private static Label NewLabel(float size, Color color, bool bold = false) => new()
    {
        AutoSize = true,
        ForeColor = color,
        BackColor = Color.Transparent,
        Font = new Font(bold ? "Segoe UI Semibold" : "Segoe UI", size),
        Margin = new Padding(0, 1, 0, 1)
    };
}
