namespace ClubOS.Agent.SessionHost;

/// <summary>
/// LockTestMode (ТЗ §25.2.5, DEVIATIONS D-003): управляемый полноэкранный overlay поверх рабочего стола.
/// НЕ меняет Winlogon Shell, GPO или политики; Ctrl+Alt+Del работает. Снимается командой из Admin Web
/// или аварийно сочетанием Ctrl+Shift+F12.
/// </summary>
internal sealed class LockOverlayForm : Form
{
    private readonly Action _onEmergencyUnlock;

    public LockOverlayForm(Screen screen, string? reason, Action onEmergencyUnlock)
    {
        _onEmergencyUnlock = onEmergencyUnlock;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Bounds = screen.Bounds;
        TopMost = true;
        ShowInTaskbar = false;
        BackColor = Color.FromArgb(17, 24, 39);
        ForeColor = Color.White;
        KeyPreview = true;

        var text = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font("Segoe UI", 28F),
            Text = "Компьютер временно недоступен\n\n" +
                   (string.IsNullOrWhiteSpace(reason) ? string.Empty : reason + "\n\n") +
                   "ClubOS · тестовый режим блокировки (M0)"
        };
        Controls.Add(text);

        KeyDown += (_, e) =>
        {
            if (e.Control && e.Shift && e.KeyCode == Keys.F12)
            {
                _onEmergencyUnlock();
            }
        };
    }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool AllowClose { get; set; }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // Alt+F4 не снимает overlay; закрытие — только по команде или аварийной комбинации.
        if (!AllowClose && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
        }

        base.OnFormClosing(e);
    }
}
