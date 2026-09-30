using ClubOS.Agent.Core.PlayerShell;

namespace ClubOS.Agent.SessionHost;

/// <summary>
/// Экран клуба (Player Shell) на один монитор: ПК свободен или сессия завершена. Полноэкранное окно
/// поверх всех, Alt+F4 не закрывает; Winlogon Shell, GPO и политики не меняются (D-003, D-016).
/// На основном мониторе — информация и вход техника (Ctrl+Shift+F12), на остальных — только фон.
/// </summary>
internal sealed class ShellScreenForm : Form
{
    private static readonly Color Background = Color.FromArgb(11, 15, 25);
    private static readonly Color Muted = Color.FromArgb(148, 163, 184);
    private static readonly Color Accent = Color.FromArgb(56, 189, 248);

    private readonly bool _primary;
    private readonly Action _onTechnician;
    private readonly Label _club = NewLabel(18F, Muted);
    private readonly Label _device = NewLabel(44F, Color.White, bold: true);
    private readonly Label _title = NewLabel(30F, Accent, bold: true);
    private readonly Label _detail = NewLabel(18F, Color.White);
    private readonly Label _summary = NewLabel(22F, Color.White, bold: true);
    private readonly Label _clock = NewLabel(28F, Color.White);
    private readonly Label _status = NewLabel(12F, Color.FromArgb(251, 191, 36));
    private readonly Label _hint = NewLabel(10F, Color.FromArgb(71, 85, 105));

    public ShellScreenForm(Screen screen, bool primary, Action onTechnician)
    {
        _primary = primary;
        _onTechnician = onTechnician;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Bounds = screen.Bounds;
        TopMost = true;
        ShowInTaskbar = false;
        BackColor = Background;
        ForeColor = Color.White;
        KeyPreview = true;
        Text = "ClubOS";

        if (primary)
        {
            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 10,
                BackColor = Background,
                Padding = new Padding(48)
            };
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 22)); // отступ сверху
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // club
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // device
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // title
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // detail
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // summary
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 78));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // clock + status
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // hint

            layout.Controls.Add(_club, 0, 1);
            layout.Controls.Add(_device, 0, 2);
            layout.Controls.Add(_title, 0, 4);
            layout.Controls.Add(_detail, 0, 5);
            layout.Controls.Add(_summary, 0, 6);
            var footer = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.TopDown,
                AutoSize = true,
                Anchor = AnchorStyles.None,
                BackColor = Background
            };
            footer.Controls.Add(_clock);
            footer.Controls.Add(_status);
            layout.Controls.Add(footer, 0, 8);
            layout.Controls.Add(_hint, 0, 9);
            Controls.Add(layout);
        }

        KeyDown += (_, e) =>
        {
            if (e.Control && e.Shift && e.KeyCode == Keys.F12)
            {
                e.Handled = true;
                _onTechnician();
            }
        };
    }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool AllowClose { get; set; }

    public void Render(ShellState state)
    {
        if (!_primary)
        {
            return;
        }

        _club.Text = state.ClubName ?? "ClubOS";
        _device.Text = state.DeviceName;
        if (state is { View: ShellView.Ended, Ended: { } ended })
        {
            _title.Text = ShellText.EndedTitle;
            _detail.Text = ShellText.EndReason(ended.Reason);
            _summary.Text = ShellText.EndedSummary(ended);
            _summary.Visible = true;
        }
        else
        {
            _title.Text = ShellText.FreeTitle;
            _detail.Text = ShellText.FreeHint;
            _summary.Visible = false;
        }

        _status.Text = state.Notice ?? (state.EdgeOnline ? string.Empty : ShellText.EdgeOffline);
        _hint.Text = ShellText.TechnicianHint;
        Tick();
    }

    /// <summary>Часы на экране (локальное время ПК).</summary>
    public void Tick()
    {
        if (_primary)
        {
            _clock.Text = DateTime.Now.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!AllowClose && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true; // Alt+F4 не снимает экран клуба
        }

        base.OnFormClosing(e);
    }

    private static Label NewLabel(float size, Color color, bool bold = false) => new()
    {
        AutoSize = true,
        Anchor = AnchorStyles.None,
        TextAlign = ContentAlignment.MiddleCenter,
        ForeColor = color,
        BackColor = Background,
        Font = new Font(bold ? "Segoe UI Semibold" : "Segoe UI", size),
        Padding = new Padding(0, 4, 0, 4)
    };
}
