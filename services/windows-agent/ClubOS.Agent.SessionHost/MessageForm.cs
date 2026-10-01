namespace ClubOS.Agent.SessionHost;

/// <summary>Видимое сообщение от администратора клуба (ShowMessage). Поверх всех окон, закрывается кнопкой.</summary>
internal sealed class MessageForm : Form
{
    public MessageForm(string title, string message)
    {
        Text = $"ClubOS — {title}";
        TopMost = true;
        ShowInTaskbar = true;
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(480, 220);
        Font = new Font("Segoe UI", 11F);

        var header = new Label
        {
            Text = title,
            Dock = DockStyle.Top,
            Height = 44,
            Font = new Font("Segoe UI Semibold", 14F),
            Padding = new Padding(16, 12, 16, 0)
        };
        var body = new Label
        {
            Text = message,
            Dock = DockStyle.Fill,
            Padding = new Padding(16, 8, 16, 8)
        };
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Width = 100, Height = 34 };
        var footer = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 52,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(12, 8, 12, 8)
        };
        footer.Controls.Add(ok);
        ok.Click += (_, _) => Close();
        AcceptButton = ok;

        Controls.Add(body);
        Controls.Add(footer);
        Controls.Add(header);
    }
}
