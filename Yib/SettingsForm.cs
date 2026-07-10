namespace Yib;

internal sealed class SettingsForm : Form
{
    private static readonly Color BackgroundColor = ColorTranslator.FromHtml("#1B1C1F");
    private static readonly Color TextColor = ColorTranslator.FromHtml("#F5F3EE");
    private static readonly Color AccentColor = ColorTranslator.FromHtml("#FF5A1F");

    private readonly Settings _settings;

    public SettingsForm(Settings settings)
    {
        _settings = settings;

        Text = "ตั้งค่า Yib";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(280, 200);
        BackColor = BackgroundColor;
        ForeColor = TextColor;

        var header = new Label
        {
            Text = "ปุ่มที่แสดงบนวงล้อ",
            AutoSize = true,
            Location = new Point(16, 16),
        };

        CheckBox dlCheckBox = CreatePillCheckBox("แสดงปุ่ม DL (Downloads)", _settings.ShowDlPill, 48);
        dlCheckBox.CheckedChanged += (_, _) =>
        {
            _settings.ShowDlPill = dlCheckBox.Checked;
            _settings.Save();
        };

        CheckBox deskCheckBox = CreatePillCheckBox("แสดงปุ่ม DESK (Desktop)", _settings.ShowDeskPill, 76);
        deskCheckBox.CheckedChanged += (_, _) =>
        {
            _settings.ShowDeskPill = deskCheckBox.Checked;
            _settings.Save();
        };

        CheckBox customCheckBox = CreatePillCheckBox("แสดงปุ่ม Custom", _settings.ShowCustomPill, 104);
        customCheckBox.CheckedChanged += (_, _) =>
        {
            _settings.ShowCustomPill = customCheckBox.Checked;
            _settings.Save();
        };

        var closeButton = new Button
        {
            Text = "ปิด",
            Location = new Point(184, 152),
            Size = new Size(80, 28),
            BackColor = AccentColor,
            ForeColor = Color.FromArgb(0x1A, 0x0E, 0x06),
            FlatStyle = FlatStyle.Flat,
            DialogResult = DialogResult.OK,
        };
        closeButton.FlatAppearance.BorderSize = 0;

        Controls.Add(header);
        Controls.Add(dlCheckBox);
        Controls.Add(deskCheckBox);
        Controls.Add(customCheckBox);
        Controls.Add(closeButton);
        AcceptButton = closeButton;
    }

    private CheckBox CreatePillCheckBox(string label, bool isChecked, int top)
    {
        return new CheckBox
        {
            Text = label,
            AutoSize = true,
            Location = new Point(16, top),
            Checked = isChecked,
            ForeColor = TextColor,
        };
    }
}
