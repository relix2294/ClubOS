using ClubOS.Agent.Core.PlayerShell;

namespace ClubOS.Agent.SessionHost;

/// <summary>
/// Ввод PIN техника поверх экрана клуба. Проверку делает служба (хэш PIN недоступен процессу пользователя),
/// после 5 неверных попыток служба блокирует ввод на 5 минут.
/// </summary>
internal sealed class TechnicianPinForm : Form
{
    private readonly Func<string, Task<(bool Ok, string? Error)>> _verify;
    private readonly TextBox _pin = new()
    {
        UseSystemPasswordChar = true,
        MaxLength = TechnicianPin.MaxLength,
        Width = 220,
        Font = new Font("Segoe UI", 16F)
    };
    private readonly Label _error = new() { AutoSize = true, ForeColor = Color.FromArgb(220, 38, 38) };
    private readonly Button _ok = new() { Text = "Войти", Width = 100, Height = 34 };

    public TechnicianPinForm(Func<string, Task<(bool Ok, string? Error)>> verify)
    {
        _verify = verify;
        Text = "ClubOS — вход техника";
        TopMost = true;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(320, 190);
        Font = new Font("Segoe UI", 10F);

        var cancel = new Button { Text = "Отмена", Width = 100, Height = 34, DialogResult = DialogResult.Cancel };
        var layout = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, Padding = new Padding(16) };
        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight };
        buttons.Controls.AddRange([_ok, cancel]);
        layout.Controls.AddRange([
            new Label { AutoSize = true, Text = $"PIN техника ({TechnicianPin.MinLength}–{TechnicianPin.MaxLength} цифр)" },
            _pin, _error, buttons
        ]);
        Controls.Add(layout);
        AcceptButton = _ok;
        CancelButton = cancel;

        _pin.KeyPress += (_, e) => e.Handled = !char.IsControl(e.KeyChar) && !char.IsAsciiDigit(e.KeyChar);
        _ok.Click += async (_, _) => await SubmitAsync();
        cancel.Click += (_, _) => Close();
    }

    private async Task SubmitAsync()
    {
        _ok.Enabled = false;
        _error.Text = string.Empty;
        try
        {
            var (ok, error) = await _verify(_pin.Text);
            if (ok)
            {
                DialogResult = DialogResult.OK;
                Close();
                return;
            }

            _error.Text = error ?? "Не удалось проверить PIN.";
            _pin.Clear();
            _pin.Focus();
        }
        finally
        {
            _ok.Enabled = true;
        }
    }
}
