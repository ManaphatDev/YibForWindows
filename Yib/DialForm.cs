using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Yib;

internal sealed class DialForm : Form
{
    private const float MaxCardSize = 360f;
    private const float CardWidthScreenFraction = 0.86f;
    private const float CardHeightScreenFraction = 0.70f;
    private const float CornerRadiusFraction = 0.19f;
    private const float ButtonDiameterFraction = 0.135f;
    private const float RingInsetFraction = 0.07f;
    private const float TickInsetFraction = 0.035f;
    private const float DeadZoneFraction = 0.10f;
    private readonly int _maxPickCount;
    private const int ScreenEdgeMargin = 8;
    private const int ThumbPixelSize = 36;
    private const float PillsTopFraction = 0.30f;
    private const double OpenAnimationDurationMs = 150;
    private const double HighlightAnimationDurationMs = 120;
    private const double LiftAnimationDurationMs = 220;
    private const double FireAnimationDurationMs = 260;
    private const float LiftDistanceFraction = 0.18f;
    private const float LiftScaleBoost = 0.2f;
    private const float IdleBreatheFraction = 0.09f;
    private const float IdleSwayFraction = 0.07f;

    // A lifted/scaled/glowing active button reaches further toward the card's center than
    // its resting radius - both the pill row and the thumbnail row need this much extra
    // clearance from the ring to avoid colliding with it.
    private const float ActiveButtonClearance = 26f;

    // Rotary-dial order: 1..9 then 0, starting at 12 o'clock and going clockwise.
    private static readonly int[] DialOrder = { 1, 2, 3, 4, 5, 6, 7, 8, 9, 0 };

    private static readonly Color CardColorTop = ColorTranslator.FromHtml("#1d1e22");
    private static readonly Color CardColorBottom = ColorTranslator.FromHtml("#15161a");
    private static readonly Color BorderColor = ColorTranslator.FromHtml("#2E2F33");
    private static readonly Color ButtonColor = ColorTranslator.FromHtml("#34353A");
    private static readonly Color ButtonDimColor = ColorTranslator.FromHtml("#26272B");
    private static readonly Color AccentColor = ColorTranslator.FromHtml("#FF5A1F");
    private static readonly Color TextMutedColor = ColorTranslator.FromHtml("#8C8C90");

    private readonly float _cardSize;
    private readonly float _half;
    private readonly float _buttonDiameter;
    private readonly float _ringRadius;
    private readonly float _tickRadius;
    private readonly float[] _numberAngles = new float[10];
    private readonly PointF[] _numberCenters = new PointF[10];
    private readonly GraphicsPath _cardPath;

    private RectangleF _dlPillRect;
    private RectangleF _deskPillRect;
    private RectangleF _customPillRect;
    private float _pillsRowBottom;
    private readonly string? _dlPillLabel;
    private readonly string? _deskPillLabel;
    private readonly string? _customPillLabel;

    private SourceFolder _folder;
    private List<string> _availableFiles = new();
    private readonly Image?[] _thumbnails = new Image?[3];
    private int _activeIndex = -1;
    private int _previousActiveIndex = -1;
    private float _highlightBlend = 1f;
    private float _liftProgress = 1f;
    private float _fireProgress = -1f;
    private int _fireIndex = -1;

    private readonly System.Windows.Forms.Timer _animationTimer;
    private long _openStartTick = -1;
    private long _highlightStartTick = -1;
    private long _fireStartTick = -1;

    private readonly bool _soundEnabled;
    private readonly int _soundVolume;
    private readonly int _soundOutputDeviceIndex;
    private readonly string? _downloadsFolderPath;
    private readonly string? _desktopFolderPath;
    private readonly string? _customFolderPath;

    public int PickedCount { get; private set; }
    public SourceFolder PickedFolder { get; private set; }

    private bool IsFiring => _fireStartTick >= 0;

    private readonly int _multiplier;

    public DialForm(Point cursorPosition, SourceFolder initialFolder, bool soundEnabled, int soundVolume, int soundOutputDeviceIndex, string? downloadsFolderPath, string? desktopFolderPath, string? customFolderPath, bool showDlPill, bool showDeskPill, bool showCustomPill, int multiplier)
    {
        _multiplier = multiplier;
        _maxPickCount = 10 * multiplier;
        _folder = initialFolder;
        PickedFolder = initialFolder;
        _soundEnabled = soundEnabled;
        _soundVolume = soundVolume;
        _soundOutputDeviceIndex = soundOutputDeviceIndex;
        _downloadsFolderPath = downloadsFolderPath;
        _desktopFolderPath = desktopFolderPath;
        _customFolderPath = customFolderPath;

        // A re-pointed slot shows its folder's name instead of the generic DL/DESK tag, so
        // the dial reflects where each pill actually pulls from. Downloads/Desktop keep their
        // short default tag until overridden; Custom has no default folder, so it only shows
        // when a path has been chosen.
        _dlPillLabel = !showDlPill ? null : string.IsNullOrEmpty(downloadsFolderPath) ? "DL" : BuildPillLabel(downloadsFolderPath);
        _deskPillLabel = !showDeskPill ? null : string.IsNullOrEmpty(desktopFolderPath) ? "DESK" : BuildPillLabel(desktopFolderPath);
        _customPillLabel = string.IsNullOrEmpty(customFolderPath) || !showCustomPill ? null : BuildPillLabel(customFolderPath);

        Rectangle workingArea = Screen.FromPoint(cursorPosition).WorkingArea;
        _cardSize = Math.Min(MaxCardSize, Math.Min(workingArea.Width * CardWidthScreenFraction, workingArea.Height * CardHeightScreenFraction));
        _half = _cardSize / 2f;
        _buttonDiameter = _cardSize * ButtonDiameterFraction;
        _ringRadius = _half - _buttonDiameter / 2f - _cardSize * RingInsetFraction;
        _tickRadius = _half - _cardSize * TickInsetFraction;

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        KeyPreview = true;

        int size = (int)Math.Ceiling(_cardSize);
        Size = new Size(size, size);

        int left = Math.Max(workingArea.Left + ScreenEdgeMargin,
            Math.Min(workingArea.Right - size - ScreenEdgeMargin, cursorPosition.X - size / 2));
        int top = Math.Max(workingArea.Top + ScreenEdgeMargin,
            Math.Min(workingArea.Bottom - size - ScreenEdgeMargin, cursorPosition.Y - size / 2));
        Location = new Point(left, top);

        // Rendered through a layered window (see Render/PushLayeredBitmap) instead of a hard
        // Region clip, so the rounded corners come out anti-aliased instead of pixelated. The
        // path is still kept around to hit-test clicks against the visible (rounded) shape.
        _cardPath = RoundedRectPath(new RectangleF(0, 0, _cardSize, _cardSize), _cardSize * CornerRadiusFraction);

        ComputeNumberLayout();
        ComputePillRects();
        RefreshAvailableFiles();

        _animationTimer = new System.Windows.Forms.Timer { Interval = 16 };
        _animationTimer.Tick += (_, _) => Render();

        MouseMove += OnMouseMove;
        MouseDown += OnMouseDown;
        KeyDown += OnKeyDown;
        Deactivate += (_, _) => CancelAndClose();
        Shown += (_, _) => PlayClickSound();
    }

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            cp.ExStyle |= NativeMethods.WS_EX_LAYERED;
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        _openStartTick = Environment.TickCount64;
        Render();
    }

    private static double AnimationProgress(long startTick, double durationMs)
    {
        if (startTick < 0)
        {
            return 1.0;
        }

        double elapsed = Environment.TickCount64 - startTick;
        return Math.Clamp(elapsed / durationMs, 0.0, 1.0);
    }

    private static float EaseOutCubic(double t) => 1f - MathF.Pow(1f - (float)t, 3f);

    // Overshoots the target slightly before settling - gives the lift a springy "pop"
    // instead of a flat glide, matching the dial's mechanical, detent-like feel.
    private static float EaseOutBack(double t)
    {
        const float c1 = 1.70158f;
        const float c3 = c1 + 1f;
        float x = (float)t - 1f;
        return 1f + c3 * x * x * x + c1 * x * x;
    }

    private void ComputeNumberLayout()
    {
        for (int i = 0; i < DialOrder.Length; i++)
        {
            float angleDeg = -90f + i * 36f;
            _numberAngles[i] = angleDeg;
            double rad = angleDeg * Math.PI / 180.0;
            _numberCenters[i] = new PointF(
                _half + (float)(_ringRadius * Math.Cos(rad)),
                _half + (float)(_ringRadius * Math.Sin(rad)));
        }
    }

    private void ComputePillRects()
    {
        const float pillHeight = 26f;
        const float baseGap = 8f;
        float top = _cardSize * PillsTopFraction;
        _pillsRowBottom = top + pillHeight;

        _dlPillRect = RectangleF.Empty;
        _deskPillRect = RectangleF.Empty;
        _customPillRect = RectangleF.Empty;

        // Each visible pill is laid out by its own measured label width, so a re-pointed
        // DL/DESK slot expands just like the custom one instead of clipping a long name.
        var pills = new List<(string label, Action<RectangleF> assign)>();
        if (_dlPillLabel is not null) pills.Add((_dlPillLabel, r => _dlPillRect = r));
        if (_deskPillLabel is not null) pills.Add((_deskPillLabel, r => _deskPillRect = r));
        if (_customPillLabel is not null) pills.Add((_customPillLabel, r => _customPillRect = r));

        if (pills.Count == 0)
        {
            return;
        }

        float[] widths = pills.Select(p => MeasurePillWidth(p.label)).ToArray();
        float naturalWidth = widths.Sum() + baseGap * (pills.Count - 1);

        // Clamp the pill row to the same safe zone the thumbnail row respects (see
        // DrawCenterReadout) - otherwise long folder labels can push the row wide
        // enough to collide with the ring's number buttons.
        float safeWidth = Math.Max(0f, (_ringRadius - _buttonDiameter / 2f - ActiveButtonClearance) * 2f);
        float scale = naturalWidth > safeWidth ? safeWidth / naturalWidth : 1f;

        float gap = baseGap * scale;
        float cursor = _half - naturalWidth * scale / 2f;
        for (int i = 0; i < pills.Count; i++)
        {
            float width = widths[i] * scale;
            pills[i].assign(new RectangleF(cursor, top, width, pillHeight));
            cursor += width + gap;
        }
    }

    private static float MeasurePillWidth(string label)
    {
        using var font = new Font("Consolas", 9f, FontStyle.Bold, GraphicsUnit.Point);
        using var bitmap = new Bitmap(1, 1);
        using Graphics g = Graphics.FromImage(bitmap);
        return Math.Max(54f, g.MeasureString(label, font).Width + 20f);
    }

    // Keeps the pill short like "DL"/"DESK" instead of letting an arbitrary folder
    // name blow out the dial's width.
    private static string BuildPillLabel(string folderPath)
    {
        string name = Path.GetFileName(folderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrEmpty(name))
        {
            return "CUSTOM";
        }

        name = name.ToUpperInvariant();
        return name.Length > 10 ? name[..9] + "…" : name;
    }

    private void RefreshAvailableFiles()
    {
        string path = FilePicker.GetFolderPath(_folder, _downloadsFolderPath, _desktopFolderPath, _customFolderPath);
        _availableFiles = FilePicker.GetRecentFiles(path, _maxPickCount);
        RefreshThumbnails();
    }

    // Only the first 3 files are ever drawn (DrawThumbnailRow caps at 3 + an overflow
    // badge), so that's all that's worth pre-loading here.
    private void RefreshThumbnails()
    {
        for (int i = 0; i < _thumbnails.Length; i++)
        {
            _thumbnails[i]?.Dispose();
            _thumbnails[i] = i < _availableFiles.Count ? LoadThumbnail(_availableFiles[i]) : null;
        }
    }

    private static Image? LoadThumbnail(string path)
    {
        string ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        bool isImage = ext is "png" or "jpg" or "jpeg" or "bmp" or "gif";
        return isImage ? LoadImageThumbnail(path, ThumbPixelSize) : NativeMethods.GetFileIcon(path, ThumbPixelSize);
    }

    private static Bitmap? LoadImageThumbnail(string path, int size)
    {
        // Image.FromFile can throw various exceptions for corrupt/locked/unreadable
        // files; any failure here just falls back to the extension swatch.
        try
        {
            using var original = Image.FromFile(path);
            var thumb = new Bitmap(size, size);
            using Graphics g = Graphics.FromImage(thumb);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;

            float scale = Math.Max((float)size / original.Width, (float)size / original.Height);
            float drawW = original.Width * scale;
            float drawH = original.Height * scale;
            g.DrawImage(original, (size - drawW) / 2f, (size - drawH) / 2f, drawW, drawH);
            return thumb;
        }
        catch
        {
            return null;
        }
    }

    private int NumberToRequestedCount(int dialNumber) => (dialNumber == 0 ? 10 : dialNumber) * _multiplier;

    private int EffectiveCount(int index)
    {
        if (index < 0)
        {
            return 0;
        }

        return Math.Min(NumberToRequestedCount(DialOrder[index]), _availableFiles.Count);
    }

    private void OnMouseMove(object? sender, MouseEventArgs e)
    {
        // Freeze aiming while the confirm animation plays so the fired number stays put.
        if (IsFiring)
        {
            return;
        }

        float dx = e.X - _half;
        float dy = e.Y - _half;
        float distance = (float)Math.Sqrt(dx * dx + dy * dy);

        int newIndex = distance < _cardSize * DeadZoneFraction
            ? -1
            : ClosestNumberIndex(Math.Atan2(dy, dx) * 180.0 / Math.PI);

        if (newIndex != _activeIndex)
        {
            _previousActiveIndex = _activeIndex;
            _activeIndex = newIndex;
            _highlightStartTick = Environment.TickCount64;
            Render();

            if (newIndex >= 0)
            {
                PlayClickSound();
            }
        }
    }

    private int ClosestNumberIndex(double angle)
    {
        int best = 0;
        double bestDiff = double.MaxValue;
        for (int i = 0; i < _numberAngles.Length; i++)
        {
            double diff = Math.Abs(((angle - _numberAngles[i] + 540) % 360) - 180);
            if (diff < bestDiff)
            {
                bestDiff = diff;
                best = i;
            }
        }
        return best;
    }

    private void OnMouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left)
        {
            return;
        }

        // The window's bounding box is now a full square (no Region clip - see CreateParams),
        // so a click in one of the transparent corners outside the rounded card must be
        // treated like a click outside the card, not a confirm.
        if (!_cardPath.IsVisible(e.Location))
        {
            CancelAndClose();
            return;
        }

        if (_dlPillLabel is not null && _dlPillRect.Contains(e.Location))
        {
            SetFolder(SourceFolder.Downloads);
            return;
        }
        if (_deskPillLabel is not null && _deskPillRect.Contains(e.Location))
        {
            SetFolder(SourceFolder.Desktop);
            return;
        }
        if (_customPillLabel is not null && _customPillRect.Contains(e.Location))
        {
            SetFolder(SourceFolder.Custom);
            return;
        }

        ConfirmPick();
    }

    private void SetFolder(SourceFolder folder)
    {
        if (_folder == folder)
        {
            return;
        }

        _folder = folder;
        RefreshAvailableFiles();
        Render();
    }

    private void ConfirmPick()
    {
        if (IsFiring)
        {
            return;
        }

        int count = EffectiveCount(_activeIndex);
        PickedFolder = _folder;

        if (count > 0)
        {
            PickedCount = count;
            PlayClickSound();

            // Kick off the confirm "fire" animation; Render finalizes the close once it
            // finishes (see the fireProgress check there). DialogResult stays None until
            // then so the modal dialog doesn't close out from under the animation.
            _fireIndex = _activeIndex;
            _fireStartTick = Environment.TickCount64;
            _animationTimer.Start();
            Render();
        }
        else
        {
            DialogResult = DialogResult.Cancel;
            Close();
        }
    }

    private void PlayClickSound()
    {
        if (_soundEnabled)
        {
            AudioPlayer.PlayClick(_soundOutputDeviceIndex, _soundVolume);
        }
    }

    private void CancelAndClose()
    {
        // A committed pick is mid-animation; don't let a lost-focus/Escape cancel override it.
        if (IsFiring)
        {
            return;
        }

        if (DialogResult == DialogResult.None)
        {
            PickedFolder = _folder;
            DialogResult = DialogResult.Cancel;
            Close();
        }
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape)
        {
            CancelAndClose();
        }
    }

    private void Render()
    {
        if (!IsHandleCreated)
        {
            return;
        }

        double openProgress = AnimationProgress(_openStartTick, OpenAnimationDurationMs);
        double highlightProgress = AnimationProgress(_highlightStartTick, HighlightAnimationDurationMs);
        double liftProgress = AnimationProgress(_highlightStartTick, LiftAnimationDurationMs);
        double fireProgress = IsFiring ? AnimationProgress(_fireStartTick, FireAnimationDurationMs) : -1.0;
        float openEase = EaseOutCubic(openProgress);
        _highlightBlend = EaseOutCubic(highlightProgress);
        _liftProgress = (float)liftProgress;
        _fireProgress = (float)fireProgress;

        int size = (int)Math.Ceiling(_cardSize);
        using var bitmap = new Bitmap(size, size, PixelFormat.Format32bppPArgb);
        using (Graphics g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);

            if (openProgress < 1.0)
            {
                // Grows in from 85% scale, anchored on the card's center.
                float scale = 0.85f + 0.15f * openEase;
                g.TranslateTransform(_half, _half);
                g.ScaleTransform(scale, scale);
                g.TranslateTransform(-_half, -_half);
            }

            DrawContent(g);
        }

        if (openProgress < 1.0)
        {
            ApplyGlobalAlpha(bitmap, openEase);
        }

        PushLayeredBitmap(bitmap);

        // The ambient idle float drifts the numbers continuously while the dial is open, so
        // the render loop never goes quiet; the timer is only stopped when the form disposes.
        _animationTimer.Start();

        // The confirm "fire" animation plays to completion before the dial actually closes,
        // so the punch + shockwave reads first. Finalizing here (rather than in ConfirmPick)
        // keeps it on the render loop; assigning DialogResult closes the modal dialog.
        if (IsFiring && fireProgress >= 1.0)
        {
            _fireStartTick = -1;
            DialogResult = DialogResult.OK;
        }
    }

    private static void ApplyGlobalAlpha(Bitmap bitmap, float alphaFactor)
    {
        var rect = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        BitmapData data = bitmap.LockBits(rect, ImageLockMode.ReadWrite, PixelFormat.Format32bppPArgb);
        try
        {
            int byteCount = data.Stride * data.Height;
            byte[] buffer = new byte[byteCount];
            Marshal.Copy(data.Scan0, buffer, 0, byteCount);

            // Premultiplied ARGB: scaling every channel (not just alpha) by the same
            // factor keeps the premultiplication consistent as the bitmap fades in.
            int factor = (int)Math.Clamp(alphaFactor * 256f, 0f, 256f);
            for (int i = 0; i < buffer.Length; i++)
            {
                buffer[i] = (byte)(buffer[i] * factor >> 8);
            }

            Marshal.Copy(buffer, 0, data.Scan0, byteCount);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    private void PushLayeredBitmap(Bitmap bitmap)
    {
        IntPtr screenDc = NativeMethods.GetDC(IntPtr.Zero);
        IntPtr memDc = NativeMethods.CreateCompatibleDC(screenDc);
        IntPtr hBitmap = bitmap.GetHbitmap(Color.FromArgb(0));
        IntPtr oldBitmap = NativeMethods.SelectObject(memDc, hBitmap);

        try
        {
            var topLeft = new NativeMethods.POINT { X = Left, Y = Top };
            var sourcePoint = new NativeMethods.POINT { X = 0, Y = 0 };
            var size = new NativeMethods.SIZE(bitmap.Width, bitmap.Height);
            var blend = new NativeMethods.BLENDFUNCTION
            {
                BlendOp = NativeMethods.AC_SRC_OVER,
                BlendFlags = 0,
                SourceConstantAlpha = 255,
                AlphaFormat = NativeMethods.AC_SRC_ALPHA,
            };

            NativeMethods.UpdateLayeredWindow(Handle, screenDc, ref topLeft, ref size, memDc, ref sourcePoint, 0, ref blend, NativeMethods.ULW_ALPHA);
        }
        finally
        {
            NativeMethods.SelectObject(memDc, oldBitmap);
            NativeMethods.DeleteObject(hBitmap);
            NativeMethods.DeleteDC(memDc);
            NativeMethods.ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    private void DrawContent(Graphics g)
    {
        var cardRect = new RectangleF(0, 0, _cardSize, _cardSize);

        using (var gradient = new LinearGradientBrush(cardRect, CardColorTop, CardColorBottom, 55f))
        using (var borderPen = new Pen(BorderColor, 1f))
        {
            g.FillPath(gradient, _cardPath);
            g.DrawPath(borderPen, _cardPath);
        }

        DrawRingTrack(g);
        DrawTicks(g);
        DrawNumberButtons(g);
        DrawPills(g);
        DrawCenterReadout(g);
    }

    private void DrawRingTrack(Graphics g)
    {
        using var pen = new Pen(Color.FromArgb(26, 255, 255, 255), 1f) { DashStyle = DashStyle.Dash };
        float d = _ringRadius * 2;
        g.DrawEllipse(pen, _half - _ringRadius, _half - _ringRadius, d, d);
    }

    private void DrawTicks(Graphics g)
    {
        using var pen = new Pen(Color.FromArgb(46, 255, 255, 255), 2f);
        for (int i = 0; i < 10; i++)
        {
            double rad = (-90 + i * 36 + 18) * Math.PI / 180.0;
            float dirX = (float)Math.Cos(rad);
            float dirY = (float)Math.Sin(rad);
            var center = new PointF(_half + _tickRadius * dirX, _half + _tickRadius * dirY);
            g.DrawLine(pen, center.X - dirX * 4f, center.Y - dirY * 4f, center.X + dirX * 4f, center.Y + dirY * 4f);
        }
    }

    private void DrawAimLine(Graphics g, PointF origin, float hubRadius)
    {
        if (_activeIndex < 0)
        {
            return;
        }

        // Follow the active button to its lifted, scaled position so the line reaches the
        // number instead of stopping where it used to sit.
        float liftT = EaseOutBack(_liftProgress);
        float scale = ButtonScale(_activeIndex, liftT);
        PointF target = LiftedCenter(_activeIndex, liftT);
        float dx = target.X - origin.X;
        float dy = target.Y - origin.Y;
        float distance = (float)Math.Sqrt(dx * dx + dy * dy);

        // Starts at the hub circle's edge (not its center) and stops at the highlighted
        // button's edge (not its center), so the line doesn't draw on top of either.
        float trimEnd = _buttonDiameter * scale / 2f + 6f;
        if (distance <= hubRadius + trimEnd)
        {
            return;
        }

        float tStart = hubRadius / distance;
        float tEnd = (distance - trimEnd) / distance;
        var start = new PointF(origin.X + dx * tStart, origin.Y + dy * tStart);
        var end = new PointF(origin.X + dx * tEnd, origin.Y + dy * tEnd);

        using var pen = new Pen(AccentColor, 2.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawLine(pen, start, end);
    }

    private void DrawNumberButtons(Graphics g)
    {
        float baseFontSize = _buttonDiameter * 0.38f;
        using var baseFont = new Font("Consolas", baseFontSize, FontStyle.Bold, GraphicsUnit.Pixel);
        using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };

        for (int i = 0; i < DialOrder.Length; i++)
        {
            bool isActive = i == _activeIndex;
            bool isDimmed = !isActive && NumberToRequestedCount(DialOrder[i]) > _availableFiles.Count;

            // Eases the highlight in on the newly active button and out on the previously
            // active one, instead of snapping the color/glow instantly between them.
            float highlight = i == _activeIndex ? _highlightBlend
                : i == _previousActiveIndex ? 1f - _highlightBlend
                : 0f;

            // The active button lifts outward along its spoke and scales up with a springy
            // overshoot; the one losing focus eases smoothly back to its seat.
            float liftT = i == _activeIndex ? EaseOutBack(_liftProgress)
                : i == _previousActiveIndex ? 1f - EaseOutCubic(_liftProgress)
                : 0f;
            float scale = ButtonScale(i, liftT);
            PointF center = LiftedCenter(i, liftT);
            float diameter = _buttonDiameter * scale;
            var rect = new RectangleF(center.X - diameter / 2f, center.Y - diameter / 2f, diameter, diameter);

            // Confirm shockwave: an accent ring expanding out of the fired button as it fades.
            if (i == _fireIndex && _fireProgress >= 0f)
            {
                DrawFireRing(g, center, diameter);
            }

            if (highlight > 0f)
            {
                using var glowBrush = new SolidBrush(Color.FromArgb((int)(46 * highlight), 255, 90, 31));
                float pad = diameter * 0.18f;
                g.FillEllipse(glowBrush, rect.X - pad, rect.Y - pad, rect.Width + pad * 2, rect.Height + pad * 2);
            }

            Color baseFillColor = isDimmed ? ButtonDimColor : ButtonColor;
            Color fillColor = LerpColor(baseFillColor, AccentColor, highlight);
            using var brush = new SolidBrush(fillColor);
            g.FillEllipse(brush, rect);

            Color baseTextColor = isDimmed ? Color.FromArgb(90, 255, 255, 255) : Color.FromArgb(140, 255, 255, 255);
            Color textColor = LerpColor(baseTextColor, Color.FromArgb(0x1A, 0x0E, 0x06), highlight);
            using var textBrush = new SolidBrush(textColor);

            // The digit scales with its button. A fresh font is only allocated when scaled
            // (one or two buttons at most); everything else reuses the shared base font.
            if (scale > 1.001f)
            {
                using var scaledFont = new Font("Consolas", baseFontSize * scale, FontStyle.Bold, GraphicsUnit.Pixel);
                g.DrawString(DialOrder[i].ToString(), scaledFont, textBrush, rect, format);
            }
            else
            {
                g.DrawString(DialOrder[i].ToString(), baseFont, textBrush, rect, format);
            }
        }
    }

    // Resting seat plus the ambient idle drift, then the radial lift on top. liftT runs 0..1;
    // full lift pushes the button out by LiftDistanceFraction of a diameter along its spoke.
    // The idle drift fades out as the button lifts (idleScale) so the selected number locks
    // crisply into place instead of floating while it's being aimed at.
    private PointF LiftedCenter(int index, float liftT)
    {
        PointF seat = _numberCenters[index];
        PointF idle = IdleOffset(index);
        float idleScale = 1f - Math.Clamp(liftT, 0f, 1f);
        float x = seat.X + idle.X * idleScale;
        float y = seat.Y + idle.Y * idleScale;

        if (liftT > 0f)
        {
            double rad = _numberAngles[index] * Math.PI / 180.0;
            float dist = liftT * (_buttonDiameter * LiftDistanceFraction);
            x += (float)Math.Cos(rad) * dist;
            y += (float)Math.Sin(rad) * dist;
        }

        return new PointF(x, y);
    }

    // A gentle per-number float: a slow breathe in/out along the spoke plus a smaller sway
    // across it, phase-offset by index so the ring shimmers organically rather than pulsing
    // as one. Amplitudes are only a few px on a typical card.
    private PointF IdleOffset(int index)
    {
        double t = Environment.TickCount64 / 1000.0;
        double phase = index * (Math.PI * 2.0 / DialOrder.Length);
        double rad = _numberAngles[index] * Math.PI / 180.0;

        float breathe = (float)Math.Sin(t * 0.9 + phase) * (_buttonDiameter * IdleBreatheFraction);
        float sway = (float)Math.Sin(t * 0.7 + phase * 1.7) * (_buttonDiameter * IdleSwayFraction);
        float cos = (float)Math.Cos(rad);
        float sin = (float)Math.Sin(rad);
        return new PointF(cos * breathe - sin * sway, sin * breathe + cos * sway);
    }

    private float ButtonScale(int index, float liftT)
    {
        float scale = 1f + liftT * LiftScaleBoost;
        if (index == _fireIndex && _fireProgress >= 0f)
        {
            // Half-sine punch: scales up to the peak at mid-animation, back down by the end.
            scale *= 1f + 0.22f * MathF.Sin(MathF.PI * _fireProgress);
        }
        return scale;
    }

    private void DrawFireRing(Graphics g, PointF center, float buttonDiameter)
    {
        int alpha = (int)(140 * (1f - _fireProgress));
        if (alpha <= 0)
        {
            return;
        }

        float radius = buttonDiameter / 2f + EaseOutCubic(_fireProgress) * (_buttonDiameter * 0.8f);
        using var pen = new Pen(Color.FromArgb(alpha, 255, 90, 31), 3f);
        g.DrawEllipse(pen, center.X - radius, center.Y - radius, radius * 2f, radius * 2f);
    }

    private static Color LerpColor(Color a, Color b, float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return Color.FromArgb(
            a.A + (int)((b.A - a.A) * t),
            a.R + (int)((b.R - a.R) * t),
            a.G + (int)((b.G - a.G) * t),
            a.B + (int)((b.B - a.B) * t));
    }

    private void DrawPills(Graphics g)
    {
        if (_dlPillLabel is not null)
        {
            DrawPill(g, _dlPillRect, _dlPillLabel, _folder == SourceFolder.Downloads);
        }
        if (_deskPillLabel is not null)
        {
            DrawPill(g, _deskPillRect, _deskPillLabel, _folder == SourceFolder.Desktop);
        }
        if (_customPillLabel is not null)
        {
            DrawPill(g, _customPillRect, _customPillLabel, _folder == SourceFolder.Custom);
        }
    }

    private void DrawPill(Graphics g, RectangleF rect, string label, bool active)
    {
        using GraphicsPath path = RoundedRectPath(rect, rect.Height / 2.5f);
        using var brush = new SolidBrush(active ? AccentColor : ButtonColor);
        g.FillPath(brush, path);

        using var font = new Font("Consolas", 9f, FontStyle.Bold, GraphicsUnit.Point);
        using var textBrush = new SolidBrush(active ? Color.FromArgb(0x1A, 0x0E, 0x06) : TextMutedColor);
        using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString(label, font, textBrush, rect, format);
    }

    private void DrawCenterReadout(Graphics g)
    {
        int count = EffectiveCount(_activeIndex);
        string numberText = count > 0 ? count.ToString() : "-";
        string labelText = count > 0 ? $"หยิบ {count} ไฟล์" : "เลื่อนเพื่อเลือกจำนวนไฟล์";

        using var numberFont = new Font("Consolas", _cardSize * 0.09f, FontStyle.Bold, GraphicsUnit.Pixel);
        using var labelFont = new Font("Segoe UI", 9.5f, FontStyle.Regular, GraphicsUnit.Point);
        using var centerFormat = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Near };

        SizeF numberSize = g.MeasureString(numberText, numberFont);
        SizeF labelSize = g.MeasureString(labelText, labelFont);

        List<string> previewFiles = count > 0 ? _availableFiles.Take(count).ToList() : new List<string>();
        int thumbSlotCount = Math.Min(previewFiles.Count, 3) + (previewFiles.Count > 3 ? 1 : 0);

        // Clamp the thumbnail row to the horizontal space between the ring buttons - at
        // 4+ slots (3 thumbnails plus a "+N" overflow badge) the row can otherwise run wide
        // enough to overlap the dial numbers on either side, especially on a smaller card.
        float safeRowWidth = Math.Max(0f, (_ringRadius - _buttonDiameter / 2f - 10f) * 2f);
        float naturalRowWidth = thumbSlotCount * ThumbPixelSize + Math.Max(0, thumbSlotCount - 1) * 6f;
        float thumbScale = thumbSlotCount > 0 && naturalRowWidth > safeRowWidth ? safeRowWidth / naturalRowWidth : 1f;
        float thumbSize = ThumbPixelSize * thumbScale;
        float thumbGap = 6f * thumbScale;

        float gapAfterLabel = thumbSlotCount > 0 ? 8f : 0f;
        float totalHeight = numberSize.Height + 2f + labelSize.Height + gapAfterLabel + (thumbSlotCount > 0 ? thumbSize : 0f);

        // Center within the gap between the pill row and the bottom of the ring (not the
        // card's absolute midpoint) - otherwise tall content like the thumbnail row pushes
        // up into the pills. Anchored on the pill row's fixed position (not any individual
        // pill's rect), since DL/DESK/Custom can each be hidden independently.
        float zoneTop = _pillsRowBottom + 10f;
        // Extra margin beyond the resting button radius - the active button lifts, scales up
        // and grows a glow halo, so its visual footprint reaches further toward center than a
        // resting button does, and the thumbnail row needs clearance from that, not just the ring.
        float zoneBottom = _half + _ringRadius - _buttonDiameter / 2f - ActiveButtonClearance;
        float zoneHeight = Math.Max(0f, zoneBottom - zoneTop);
        float y = totalHeight <= zoneHeight ? zoneTop + (zoneHeight - totalHeight) / 2f : zoneTop;

        // MeasureString's default StringFormat pads its box with extra line-spacing above/below
        // the glyph's actual ink, so "y + numberSize.Height / 2" lands well above the digit's
        // visible center. GenericTypographic gives a tight box matching the rendered ink, so the
        // line anchors on the digit itself instead of floating in the padding above it.
        using var typographicFormat = (StringFormat)StringFormat.GenericTypographic.Clone();
        SizeF tightNumberSize = g.MeasureString(numberText, numberFont, new PointF(0, 0), typographicFormat);
        var numberCenter = new PointF(_half, y + tightNumberSize.Height / 2f);
        float hubRadius = Math.Max(tightNumberSize.Width, tightNumberSize.Height) / 2f + 8f;

        using (var numberBrush = new SolidBrush(AccentColor))
        {
            // Pulse the readout digit in sync with the confirm punch, scaled about its own
            // center so the surrounding layout (label, thumbnails) stays put.
            float numberPulse = _fireProgress >= 0f ? 1f + 0.22f * MathF.Sin(MathF.PI * _fireProgress) : 1f;
            GraphicsState? pulseState = null;
            if (numberPulse > 1.001f)
            {
                pulseState = g.Save();
                g.TranslateTransform(numberCenter.X, numberCenter.Y);
                g.ScaleTransform(numberPulse, numberPulse);
                g.TranslateTransform(-numberCenter.X, -numberCenter.Y);
            }

            g.DrawString(numberText, numberFont, numberBrush, new RectangleF(0, y, _cardSize, numberSize.Height), centerFormat);

            if (pulseState != null)
            {
                g.Restore(pulseState);
            }
        }

        // Drawn after the number text (not before) so it isn't hidden behind the digit -
        // it should read as reaching all the way to the number, not stopping at its edge.
        DrawAimLine(g, numberCenter, hubRadius);
        y += numberSize.Height + 2f;

        using (var labelBrush = new SolidBrush(TextMutedColor))
        {
            g.DrawString(labelText, labelFont, labelBrush, new RectangleF(0, y, _cardSize, labelSize.Height), centerFormat);
        }
        y += labelSize.Height + gapAfterLabel;

        if (thumbSlotCount > 0)
        {
            DrawThumbnailRow(g, previewFiles, y, thumbSize, thumbGap, thumbSlotCount);
        }
    }

    private void DrawThumbnailRow(Graphics g, List<string> files, float y, float thumbSize, float gap, int slotCount)
    {
        float x = _half - (slotCount * thumbSize + (slotCount - 1) * gap) / 2f;

        int shown = Math.Min(files.Count, 3);
        for (int i = 0; i < shown; i++)
        {
            DrawThumb(g, new RectangleF(x, y, thumbSize, thumbSize), files[i], _thumbnails[i]);
            x += thumbSize + gap;
        }

        if (files.Count > 3)
        {
            DrawMoreThumb(g, new RectangleF(x, y, thumbSize, thumbSize), files.Count - 3);
        }
    }

    private void DrawThumb(Graphics g, RectangleF rect, string filePath, Image? thumbnail)
    {
        using GraphicsPath path = RoundedRectPath(rect, 6f);

        if (thumbnail is not null)
        {
            Region previousClip = g.Clip;
            g.SetClip(path, CombineMode.Intersect);
            g.DrawImage(thumbnail, rect);
            g.Clip = previousClip;
            previousClip.Dispose();
            return;
        }

        string ext = Path.GetExtension(filePath).TrimStart('.').ToLowerInvariant();
        using var brush = new SolidBrush(ExtensionColor(ext));
        g.FillPath(brush, path);

        if (ext.Length == 0)
        {
            return;
        }

        using var font = new Font("Consolas", 6.5f, FontStyle.Bold, GraphicsUnit.Point);
        using var textBrush = new SolidBrush(Color.FromArgb(217, 255, 255, 255));
        using var format = new StringFormat { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Far };
        g.DrawString(ext, font, textBrush, new RectangleF(rect.X + 1, rect.Y + 1, rect.Width - 3, rect.Height - 3), format);
    }

    private void DrawMoreThumb(Graphics g, RectangleF rect, int more)
    {
        using GraphicsPath path = RoundedRectPath(rect, 6f);
        using var brush = new SolidBrush(ColorTranslator.FromHtml("#232427"));
        g.FillPath(brush, path);

        using var font = new Font("Consolas", 8f, FontStyle.Bold, GraphicsUnit.Point);
        using var textBrush = new SolidBrush(TextMutedColor);
        using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString("+" + more, font, textBrush, rect, format);
    }

    private static Color ExtensionColor(string ext) => ext switch
    {
        "png" or "jpg" or "jpeg" or "heic" or "gif" or "bmp" => ColorTranslator.FromHtml("#1E9BD6"),
        "pdf" => ColorTranslator.FromHtml("#C0392B"),
        "doc" or "docx" or "rtf" => ColorTranslator.FromHtml("#2F6FD0"),
        "xls" or "xlsx" or "csv" => ColorTranslator.FromHtml("#2F9E52"),
        "fig" or "psd" or "ai" => ColorTranslator.FromHtml("#7B5CF0"),
        "zip" or "rar" or "7z" => ColorTranslator.FromHtml("#8A8F99"),
        "mp4" or "mov" or "avi" or "mkv" => ColorTranslator.FromHtml("#2C2F36"),
        "mp3" or "wav" or "flac" => ColorTranslator.FromHtml("#E0B03C"),
        _ => ColorTranslator.FromHtml("#3A3B40"),
    };

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            foreach (Image? thumb in _thumbnails)
            {
                thumb?.Dispose();
            }

            _cardPath.Dispose();
            _animationTimer.Dispose();
        }

        base.Dispose(disposing);
    }

    private static GraphicsPath RoundedRectPath(RectangleF rect, float radius)
    {
        float d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
