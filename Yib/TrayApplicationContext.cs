using System.Drawing.Drawing2D;
using Microsoft.Win32;

namespace Yib;

internal sealed class TrayApplicationContext : ApplicationContext
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunKeyName = "Yib";

    private readonly NotifyIcon _trayIcon;
    private readonly MouseShakeDetector _shakeDetector;
    private readonly SynchronizationContext _uiContext;
    private readonly Settings _settings;
    private readonly Dictionary<SourceFolder, ToolStripMenuItem> _folderMenuItems = new();
    private SourceFolder _currentFolder;
    private ToolStripMenuItem? _resetFolderItem;

    public TrayApplicationContext()
    {
        _settings = Settings.Load();
        _currentFolder = _settings.SourceFolder;

        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripMenuItem("Yib for Windows") { Enabled = false });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(BuildSensitivityMenu());
        menu.Items.Add(BuildFolderMenu());
        menu.Items.Add(BuildMultiplierMenu());
        menu.Items.Add(BuildSoundMenuItem());
        menu.Items.Add(BuildVolumeMenu());
        menu.Items.Add(BuildOutputDeviceMenu());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(BuildStartupMenuItem());
        menu.Items.Add("ตั้งค่า...", null, (_, _) => new SettingsForm(_settings).ShowDialog());
        menu.Items.Add("ออกจากโปรแกรม", null, OnExit);
        ApplyMenuTheme(menu);
        ApplyRoundedCorners(menu);
        SystemEvents.UserPreferenceChanged += OnSystemThemeChanged;

        _trayIcon = new NotifyIcon
        {
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application,
            Text = "Yib for Windows",
            ContextMenuStrip = menu,
            Visible = true
        };

        // NotifyIcon's hidden window handle above triggers WinForms to install the
        // WindowsFormsSynchronizationContext, so this is safe to capture here.
        _uiContext = SynchronizationContext.Current!;

        _shakeDetector = new MouseShakeDetector { Sensitivity = _settings.Sensitivity };
        _shakeDetector.ShakeDetected += OnShakeDetected;
        try
        {
            _shakeDetector.Start();
        }
        catch (Exception ex)
        {
            // Installing the global mouse hook can fail (e.g. session/permission state). Stay
            // alive with a working tray menu instead of crashing on launch, and tell the user.
            Program.LogError(ex);
            _trayIcon.ShowBalloonTip(3000, "Yib", "ไม่สามารถเริ่มตรวจจับการเขย่าเมาส์ได้", ToolTipIcon.Error);
        }
    }

    private ToolStripMenuItem BuildSensitivityMenu()
    {
        var root = new ToolStripMenuItem("ความไว");
        AddSensitivityItem(root, "หลวม", ShakeSensitivity.Loose);
        AddSensitivityItem(root, "กลาง", ShakeSensitivity.Medium);
        AddSensitivityItem(root, "แน่น", ShakeSensitivity.Tight);
        return root;
    }

    private void AddSensitivityItem(ToolStripMenuItem root, string label, ShakeSensitivity value)
    {
        var item = new ToolStripMenuItem(label) { Checked = _settings.Sensitivity == value };
        item.Click += (_, _) =>
        {
            foreach (ToolStripMenuItem sibling in root.DropDownItems)
            {
                sibling.Checked = false;
            }

            item.Checked = true;
            _settings.Sensitivity = value;
            _shakeDetector.Sensitivity = value;
            _settings.Save();
        };
        root.DropDownItems.Add(item);
    }

    private ToolStripMenuItem BuildFolderMenu()
    {
        var root = new ToolStripMenuItem("โฟลเดอร์");
        AddFolderItem(root, SourceFolder.Downloads);
        AddFolderItem(root, SourceFolder.Desktop);
        AddFolderItem(root, SourceFolder.Custom);

        root.DropDownItems.Add(new ToolStripSeparator());
        root.DropDownItems.Add("เปลี่ยนโฟลเดอร์ของช่องที่เลือก...", null, (_, _) => ChangeActiveFolderPath());
        _resetFolderItem = new ToolStripMenuItem("คืนค่าเริ่มต้น", null, (_, _) => ResetActiveFolderPath());
        root.DropDownItems.Add(_resetFolderItem);

        RefreshFolderMenu();
        return root;
    }

    private void AddFolderItem(ToolStripMenuItem root, SourceFolder slot)
    {
        var item = new ToolStripMenuItem();
        item.Click += (_, _) => SetFolder(slot);
        root.DropDownItems.Add(item);
        _folderMenuItems[slot] = item;
    }

    // The Downloads/Desktop slots show their default name until re-pointed; once a slot has
    // an override (or for the never-defaulted Custom slot) it shows the chosen folder's name.
    private string FolderSlotLabel(SourceFolder slot)
    {
        string? overridePath = _settings.PathFor(slot);
        if (slot == SourceFolder.Custom)
        {
            return string.IsNullOrEmpty(overridePath) ? "เลือกโฟลเดอร์..." : $"กำหนดเอง: {LeafName(overridePath)}";
        }

        string defaultName = slot == SourceFolder.Downloads ? "Downloads" : "Desktop";
        return string.IsNullOrEmpty(overridePath) ? defaultName : $"{defaultName} → {LeafName(overridePath)}";
    }

    private static string LeafName(string path)
    {
        string name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return string.IsNullOrEmpty(name) ? path : name;
    }

    private void RefreshFolderMenu()
    {
        foreach ((SourceFolder slot, ToolStripMenuItem item) in _folderMenuItems)
        {
            item.Text = FolderSlotLabel(slot);
            item.Checked = _settings.SourceFolder == slot;
            item.ToolTipText = FilePicker.GetFolderPath(slot, _settings.DownloadsFolderPath, _settings.DesktopFolderPath, _settings.CustomFolderPath);
        }

        // Reset only makes sense when the active slot has actually been re-pointed.
        if (_resetFolderItem is not null)
        {
            _resetFolderItem.Enabled = !string.IsNullOrEmpty(_settings.PathFor(_currentFolder));
        }
    }

    private void SetFolder(SourceFolder value)
    {
        if (_settings.SourceFolder != value)
        {
            _settings.SourceFolder = value;
            _currentFolder = value;
            _settings.Save();
        }

        RefreshFolderMenu();
    }

    private void ChangeActiveFolderPath()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "เลือกโฟลเดอร์ต้นทาง",
            SelectedPath = FilePicker.GetFolderPath(_currentFolder, _settings.DownloadsFolderPath, _settings.DesktopFolderPath, _settings.CustomFolderPath),
            ShowNewFolderButton = false,
        };

        if (dialog.ShowDialog() != DialogResult.OK)
        {
            return;
        }

        _settings.SetPathFor(_currentFolder, dialog.SelectedPath);
        _settings.Save();
        RefreshFolderMenu();
    }

    private void ResetActiveFolderPath()
    {
        if (string.IsNullOrEmpty(_settings.PathFor(_currentFolder)))
        {
            return;
        }

        _settings.SetPathFor(_currentFolder, null);
        _settings.Save();
        RefreshFolderMenu();
    }

    private ToolStripMenuItem BuildMultiplierMenu()
    {
        var root = new ToolStripMenuItem("ตัวคูณจำนวนไฟล์");
        AddMultiplierItem(root, "x1 (สูงสุด 10 ไฟล์)", 1);
        AddMultiplierItem(root, "x2 (สูงสุด 20 ไฟล์)", 2);
        AddMultiplierItem(root, "x5 (สูงสุด 50 ไฟล์)", 5);
        AddMultiplierItem(root, "x10 (สูงสุด 100 ไฟล์)", 10);
        return root;
    }

    private void AddMultiplierItem(ToolStripMenuItem root, string label, int multiplier)
    {
        var item = new ToolStripMenuItem(label) { Checked = _settings.DialMultiplier == multiplier };
        item.Click += (_, _) =>
        {
            foreach (ToolStripMenuItem sibling in root.DropDownItems)
            {
                sibling.Checked = false;
            }

            item.Checked = true;
            _settings.DialMultiplier = multiplier;
            _settings.Save();
        };
        root.DropDownItems.Add(item);
    }

    private ToolStripMenuItem BuildSoundMenuItem()
    {
        var item = new ToolStripMenuItem("เสียงคลิก") { Checked = _settings.SoundEnabled, CheckOnClick = true };
        item.CheckedChanged += (_, _) =>
        {
            _settings.SoundEnabled = item.Checked;
            _settings.Save();
        };
        return item;
    }

    private ToolStripMenuItem BuildVolumeMenu()
    {
        var root = new ToolStripMenuItem("ระดับเสียง");
        AddVolumeItem(root, "เบา", 30);
        AddVolumeItem(root, "กลาง", 60);
        AddVolumeItem(root, "ดัง", 100);
        return root;
    }

    private void AddVolumeItem(ToolStripMenuItem root, string label, int volume)
    {
        var item = new ToolStripMenuItem(label) { Checked = _settings.SoundVolume == volume };
        item.Click += (_, _) =>
        {
            foreach (ToolStripMenuItem sibling in root.DropDownItems)
            {
                sibling.Checked = false;
            }

            item.Checked = true;
            _settings.SoundVolume = volume;
            _settings.Save();
        };
        root.DropDownItems.Add(item);
    }

    private ToolStripMenuItem BuildOutputDeviceMenu()
    {
        var root = new ToolStripMenuItem("ลำโพงที่ใช้เล่นเสียง");
        AddOutputDeviceItem(root, "ค่าเริ่มต้นของระบบ", AudioPlayer.DefaultDevice);
        foreach ((int index, string name) in AudioPlayer.GetOutputDevices())
        {
            AddOutputDeviceItem(root, name, index);
        }
        return root;
    }

    private void AddOutputDeviceItem(ToolStripMenuItem root, string label, int deviceIndex)
    {
        var item = new ToolStripMenuItem(label) { Checked = _settings.SoundOutputDeviceIndex == deviceIndex };
        item.Click += (_, _) =>
        {
            foreach (ToolStripMenuItem sibling in root.DropDownItems)
            {
                sibling.Checked = false;
            }

            item.Checked = true;
            _settings.SoundOutputDeviceIndex = deviceIndex;
            _settings.Save();
        };
        root.DropDownItems.Add(item);
    }

    private ToolStripMenuItem BuildStartupMenuItem()
    {
        var item = new ToolStripMenuItem("เริ่มทำงานตอนเปิดเครื่อง")
        {
            Checked = IsStartupEnabled(),
            CheckOnClick = true,
        };
        item.CheckedChanged += (_, _) => SetStartupEnabled(item.Checked);
        return item;
    }

    private static bool IsStartupEnabled()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        string? value = key?.GetValue(RunKeyName) as string;
        return !string.IsNullOrWhiteSpace(value) && NormalizeStartupValue(value) == NormalizeStartupValue(CurrentExecutablePath);
    }

    private static void SetStartupEnabled(bool enabled)
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        if (key is null)
        {
            return;
        }

        if (enabled)
        {
            key.SetValue(RunKeyName, BuildStartupValue(), RegistryValueKind.String);
        }
        else
        {
            key.DeleteValue(RunKeyName, throwOnMissingValue: false);
        }
    }

    private static string CurrentExecutablePath => Environment.ProcessPath ?? Application.ExecutablePath;

    private static string BuildStartupValue()
    {
        string path = CurrentExecutablePath;
        return path.Contains(' ') ? $"\"{path}\"" : path;
    }

    private static string NormalizeStartupValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        string normalized = value.Trim();
        if (normalized.Length >= 2 && normalized[0] == '"' && normalized[^1] == '"')
        {
            normalized = normalized[1..^1];
        }

        return normalized.Trim();
    }

    private void OnShakeDetected(Point position)
    {
        // The dial/clipboard work below is too slow (and modal) to run inline in the
        // hook callback (it would lag mouse input system-wide), but DialForm/Clipboard
        // require the STA UI thread. Posting back to the UI thread's own message loop
        // gets both: the hook callback returns immediately, and the work still lands
        // on the right thread a moment later.
        _uiContext.Post(_ => HandleShakeDetected(position), null);
    }

    private void HandleShakeDetected(Point position)
    {
        // Pause shake detection while the dial is open - sliding the mouse around the
        // ring to aim is itself a fast back-and-forth motion that could otherwise be
        // misread as a second shake.
        _shakeDetector.Enabled = false;
        IntPtr previousForeground = NativeMethods.GetForegroundWindow();

        try
        {
            DialogResult result;
            int pickedCount;
            using (var dial = new DialForm(position, _currentFolder, _settings.SoundEnabled, _settings.SoundVolume, _settings.SoundOutputDeviceIndex, _settings.DownloadsFolderPath, _settings.DesktopFolderPath, _settings.CustomFolderPath, _settings.ShowDlPill, _settings.ShowDeskPill, _settings.ShowCustomPill, _settings.DialMultiplier))
            {
                result = dial.ShowDialog();
                pickedCount = dial.PickedCount;
                SetFolder(dial.PickedFolder);
            }

            if (result == DialogResult.OK && pickedCount > 0)
            {
                // previousForeground = whatever window had focus before the overlay stole it.
                // It is restored just before Ctrl+V inside PasteFiles so the paste lands there.
                PasteRecentFiles(pickedCount, previousForeground);
            }
        }
        catch (Exception ex)
        {
            // One failed shake must never take the whole app down.
            Program.LogError(ex);
        }
        finally
        {
            // Re-enabling here (not inline) is critical: if anything above throws, the hook
            // would otherwise stay disabled forever and the app would silently stop reacting.
            _shakeDetector.Enabled = true;
        }
    }

    private void PasteRecentFiles(int count, IntPtr targetWindow)
    {
        string folder = FilePicker.GetFolderPath(_currentFolder, _settings.DownloadsFolderPath, _settings.DesktopFolderPath, _settings.CustomFolderPath);
        List<string> files = FilePicker.GetRecentFiles(folder, count);

        if (files.Count == 0)
        {
            _trayIcon.ShowBalloonTip(1500, "Yib", "No recent files found", ToolTipIcon.Warning);
            return;
        }

        ClipboardPaster.PasteFiles(files, targetWindow);
    }

    private void OnExit(object? sender, EventArgs e)
    {
        SystemEvents.UserPreferenceChanged -= OnSystemThemeChanged;
        _shakeDetector.ShakeDetected -= OnShakeDetected;
        _shakeDetector.Dispose();
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        Application.Exit();
    }

    private void OnSystemThemeChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category != UserPreferenceCategory.General)
        {
            return;
        }

        ContextMenuStrip? menu = _trayIcon.ContextMenuStrip;
        if (menu is not null)
        {
            _uiContext.Post(_ => ApplyMenuTheme(menu), null);
        }
    }

    // Applies Windows 11 DWM rounded corners to the menu popup and all submenus.
    // Submenu handles are created lazily (on first open), so we hook HandleCreated
    // on each DropDown rather than forcing it now.
    private static void ApplyRoundedCorners(ToolStrip strip)
    {
        RoundStrip(strip);
        foreach (ToolStripItem item in strip.Items)
        {
            if (item is ToolStripMenuItem { HasDropDownItems: true } mi)
            {
                mi.DropDown.HandleCreated += (_, _) => RoundStrip(mi.DropDown);
            }
        }
    }

    private static void RoundStrip(ToolStrip strip)
    {
        _ = strip.Handle;
        int pref = NativeMethods.DWMWCP_ROUND;
        NativeMethods.DwmSetWindowAttribute(strip.Handle, NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int));
    }

    private static void ApplyMenuTheme(ToolStrip menu)
    {
        bool dark = IsDarkTheme();
        ToolStripManager.Renderer = dark
            ? new YibMenuRenderer(new DarkMenuColorTable())
            : new ToolStripProfessionalRenderer(new ProfessionalColorTable());

        Color back = dark ? DarkMenuColors.Background : SystemColors.Menu;
        Color fore = dark ? DarkMenuColors.Text : SystemColors.MenuText;
        ApplyMenuColorsRecursive(menu, back, fore);
    }

    private static void ApplyMenuColorsRecursive(ToolStrip strip, Color back, Color fore)
    {
        strip.BackColor = back;
        strip.ForeColor = fore;

        foreach (ToolStripItem item in strip.Items)
        {
            item.BackColor = back;
            item.ForeColor = fore;

            if (item is ToolStripMenuItem { HasDropDownItems: true } menuItem)
            {
                ApplyMenuColorsRecursive(menuItem.DropDown, back, fore);
            }
        }
    }

    private static bool IsDarkTheme()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", writable: false);
        return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
    }

    private sealed class YibMenuRenderer : ToolStripProfessionalRenderer
    {
        public YibMenuRenderer(ProfessionalColorTable table) : base(table) { }

        // Draws the "Yib for Windows" header in accent colour instead of the default
        // dimmed/greyed text that WinForms uses for Enabled=false items.
        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            if (!e.Item.Enabled && e.Item.Text == "Yib for Windows")
            {
                e.TextColor = DarkMenuColors.Accent;
            }
            base.OnRenderItemText(e);
        }

        // Replaces the default OS check box with a filled accent-coloured rounded square
        // and a white tick drawn with anti-aliased lines.
        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            Graphics g = e.Graphics;
            Rectangle ir = e.ImageRectangle;
            if (ir.IsEmpty)
            {
                return;
            }

            g.SmoothingMode = SmoothingMode.AntiAlias;

            // Erase whatever the base would have drawn (border highlight, etc.)
            using (var bg = new SolidBrush(DarkMenuColors.Background))
            {
                g.FillRectangle(bg, ir);
            }

            // Filled rounded square in accent colour
            const int pad = 3;
            var box = new Rectangle(ir.X + pad, ir.Y + pad, ir.Width - pad * 2, ir.Height - pad * 2);
            using (var path = RoundedBoxPath(box, 3))
            using (var fill = new SolidBrush(DarkMenuColors.Accent))
            {
                g.FillPath(fill, path);
            }

            // White tick: two line segments forming a check mark
            using var pen = new Pen(Color.White, 1.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            float x = box.X, y = box.Y, w = box.Width, h = box.Height;
            g.DrawLines(pen, new PointF[]
            {
                new(x + w * 0.18f, y + h * 0.50f),
                new(x + w * 0.42f, y + h * 0.76f),
                new(x + w * 0.82f, y + h * 0.22f),
            });
        }

        // Draws a clean single-pixel border that matches DWM's rounded clipping instead
        // of the layered / double-line border the default renderer produces.
        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            if (e.ToolStrip is ContextMenuStrip or ToolStripDropDownMenu)
            {
                e.Graphics.SmoothingMode = SmoothingMode.Default;
                using var pen = new Pen(DarkMenuColors.Border, 1f);
                e.Graphics.DrawRectangle(pen, 0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
            }
            else
            {
                base.OnRenderToolStripBorder(e);
            }
        }

        private static GraphicsPath RoundedBoxPath(Rectangle r, int radius)
        {
            int d = radius * 2;
            var p = new GraphicsPath();
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }

    private static class DarkMenuColors
    {
        public static readonly Color Background = ColorTranslator.FromHtml("#1B1C1F");
        public static readonly Color Border = ColorTranslator.FromHtml("#34353A");
        public static readonly Color Text = ColorTranslator.FromHtml("#F5F3EE");
        public static readonly Color Accent = ColorTranslator.FromHtml("#FF5A1F");
    }

    private sealed class DarkMenuColorTable : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => DarkMenuColors.Background;
        public override Color ImageMarginGradientBegin => DarkMenuColors.Background;
        public override Color ImageMarginGradientMiddle => DarkMenuColors.Background;
        public override Color ImageMarginGradientEnd => DarkMenuColors.Background;
        public override Color MenuBorder => DarkMenuColors.Border;
        public override Color MenuItemBorder => DarkMenuColors.Accent;
        public override Color MenuItemSelected => DarkMenuColors.Accent;
        public override Color MenuItemSelectedGradientBegin => DarkMenuColors.Accent;
        public override Color MenuItemSelectedGradientEnd => DarkMenuColors.Accent;
        public override Color MenuItemPressedGradientBegin => DarkMenuColors.Accent;
        public override Color MenuItemPressedGradientEnd => DarkMenuColors.Accent;
        public override Color SeparatorDark => DarkMenuColors.Border;
        public override Color SeparatorLight => DarkMenuColors.Border;
    }
}
