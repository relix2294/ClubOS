using ClubOS.Agent.Core.PlayerShell;

namespace ClubOS.Agent.SessionHost;

/// <summary>Полоса «режим обслуживания» вверху экрана: техник видит, до какого времени снят экран клуба.</summary>
internal sealed class MaintenanceBannerForm : Form
{
    private const int WsExNoActivate = 0x08000000;
    private const int WsExToolWindow = 0x00000080;

    private readonly Label _text = new()
    {
        AutoSize = true,
        ForeColor = Color.White,
        Font = new Font("Segoe UI", 10F),
        Margin = new Padding(0, 8, 12, 0)
    };

    public MaintenanceBannerForm(Action onEnd)
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = Color.FromArgb(146, 64, 14);
        Text = "ClubOS — обслуживание";

        var end = new Button
        {
            Text = "Завершить обслуживание",
            AutoSize = true,
            FlatStyle = FlatStyle.Flat,
            ForeColor = Color.White
        };
        end.Click += (_, _) => onEnd();
        var row = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12, 4, 12, 4), BackColor = BackColor };
        row.Controls.AddRange([_text, end]);
        Controls.Add(row);

        var area = Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1280, 720);
        Bounds = new Rectangle(area.Left + (area.Width - 640) / 2, area.Top, 640, 44);
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WsExNoActivate | WsExToolWindow;
            return cp;
        }
    }

    public void Render(ShellState state)
    {
        var until = state.MaintenanceUntilUtc is { } u
            ? u.AddMilliseconds(-state.ClockOffsetMs).ToLocalTime()
            : DateTimeOffset.Now;
        _text.Text = ShellText.MaintenanceBanner(until);
    }
}
