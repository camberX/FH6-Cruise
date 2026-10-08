using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.Runtime.InteropServices;

namespace Fh6Cruise;

internal sealed class CruiseOverlay : Form
{
    private const double AnimSeconds = 0.18;
    private readonly System.Windows.Forms.Timer _frames = new() { Interval = 16 };
    private string _speed = "";
    private string _from = "";
    private string _unit = "";
    private int _processId;
    private int _direction = 1;
    private long _animTicks;
    private Rectangle _place;
    private float _scale = 1;
    private float _right = 184f;
    private float _bottom = 116f;
    private float _userScale = 1f;
    private bool _editing;
    private bool _chrome;
    private bool _drag;
    private bool _sizing;
    private Point _origin;
    private Size _pixelSize;
    private Point _grabCursor;
    private Point _grabOrigin;
    private float _grabScale;

    public event Action<float, float, float>? PlacementChanged;

    public bool Editing
    {
        get => _editing;
        set
        {
            if (_editing == value)
                return;
            _editing = value;
            _drag = false;
            _sizing = false;
            Capture = false;
            Cursor = value ? Cursors.SizeAll : Cursors.Default;
            ApplyHitTest();
        }
    }

    public void SetPlacement(float right, float bottom, float userScale)
    {
        if (_drag || _sizing)
            return;
        _right = right;
        _bottom = bottom;
        _userScale = userScale;
    }

    public CruiseOverlay()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
        _frames.Tick += (_, _) => Frame();
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.ExStyle |= 0x00080000 | 0x00000020 | 0x08000000 | 0x00000080;
            return parameters;
        }
    }

    public void Present(bool show, int processId, string speed, string unit)
    {
        _processId = processId;
        if (!show || processId == 0 || !TryGameClient(processId, out var client))
        {
            _frames.Stop();
            _speed = "";
            _from = "";
            if (Visible)
                Hide();
            return;
        }

        if (speed != _speed)
        {
            if (_speed.Length > 0 && speed.Length > 0 &&
                int.TryParse(speed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var next) &&
                int.TryParse(_speed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var previous) &&
                next != previous)
            {
                _from = _speed;
                _direction = next > previous ? 1 : -1;
                _animTicks = Stopwatch.GetTimestamp();
                _frames.Start();
            }
            else
            {
                _from = speed;
            }

            _speed = speed;
        }

        var scale = Math.Clamp(client.Height / 1080f, 0.65f, 2.4f);
        var moved = client != _place || Math.Abs(scale - _scale) > 0.01f || unit != _unit || AnimProgress() < 1f || _chrome != _editing;
        _place = client;
        _scale = scale;
        _unit = unit;
        _chrome = _editing;
        if (!IsHandleCreated)
        {
            CreateHandle();
            ApplyHitTest();
        }

        if (moved || !Visible)
            Blit(client, scale);
        if (!Visible)
            ShowWindow(Handle, 4);
        else
            SetWindowPos(Handle, new IntPtr(-1), 0, 0, 0, 0, 0x0010 | 0x0002 | 0x0001);
    }

    private void Frame()
    {
        if (!Visible || _processId == 0 || !TryGameClient(_processId, out var client))
        {
            _frames.Stop();
            return;
        }

        Blit(client, _scale);
        if (AnimProgress() >= 1f)
            _frames.Stop();
    }

    private float AnimProgress()
    {
        if (_from.Length == 0 || _from == _speed)
            return 1f;
        var seconds = (Stopwatch.GetTimestamp() - _animTicks) / (double)Stopwatch.Frequency;
        var t = seconds / AnimSeconds;
        if (t >= 1)
            return 1f;
        var remain = 1 - t;
        return (float)(1 - remain * remain * remain);
    }

    private void Blit(Rectangle client, float scale)
    {
        var progress = AnimProgress();
        using var badge = DrawBadge(_from.Length == 0 ? _speed : _from, _speed, _unit, scale * _userScale, progress, _direction, _editing);
        var right = (int)Math.Round(_right * scale);
        var bottom = (int)Math.Round(_bottom * scale);
        var x = client.Right - badge.Width - right;
        var y = client.Bottom - badge.Height - bottom;
        var screen = GetDC(IntPtr.Zero);
        var memory = CreateCompatibleDC(screen);
        var bits = badge.LockBits(new Rectangle(0, 0, badge.Width, badge.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
        var info = new BitmapInfo
        {
            Size = 40,
            Width = badge.Width,
            Height = -badge.Height,
            Planes = 1,
            BitCount = 32
        };
        var section = CreateDIBSection(screen, ref info, 0, out var destination, IntPtr.Zero, 0);
        if (section != IntPtr.Zero && destination != IntPtr.Zero)
        {
            var stride = Math.Abs(bits.Stride);
            for (var row = 0; row < badge.Height; row++)
                Copy(bits.Scan0 + row * bits.Stride, destination + row * stride, stride);
        }

        badge.UnlockBits(bits);
        var old = SelectObject(memory, section);
        var origin = new Point(x, y);
        var size = new Size(badge.Width, badge.Height);
        var source = new Point(0, 0);
        var blend = new Blend { Op = 0, Flags = 0, Alpha = 255, Format = 1 };
        UpdateLayeredWindow(Handle, screen, ref origin, ref size, memory, ref source, 0, ref blend, 2);
        _origin = origin;
        _pixelSize = size;
        SelectObject(memory, old);
        if (section != IntPtr.Zero)
            DeleteObject(section);
        DeleteDC(memory);
        ReleaseDC(IntPtr.Zero, screen);
    }

    internal static Bitmap DrawBadge(string from, string to, string unit, float scale, float progress, int direction, bool editing)
    {
        var speedFont = FontOf(23f * scale);
        var unitFont = FontOf(11.5f * scale);
        using var format = new StringFormat(StringFormat.GenericTypographic);
        format.FormatFlags |= StringFormatFlags.NoWrap;
        using var probe = new Bitmap(1, 1, PixelFormat.Format32bppPArgb);
        using var measure = Graphics.FromImage(probe);
        var fromWidth = TightWidth(measure, from, speedFont, format);
        var toWidth = TightWidth(measure, to, speedFont, format);
        var unitWidth = TightWidth(measure, unit, unitFont, format);
        var numberWidth = progress >= 1f ? toWidth : Math.Max(fromWidth, toWidth);
        var icon = Math.Max(16f, 19f * scale);
        var gap = 3f * scale;
        var pad = 4f * scale;
        var width = (int)Math.Ceiling(pad + icon + 8f * scale + numberWidth + gap + unitWidth + pad);
        var textBlock = Math.Max(LineHeight(speedFont), LineHeight(unitFont));
        var height = (int)Math.Ceiling(Math.Max(icon, textBlock) + pad * 2);
        var bitmap = new Bitmap(Math.Max(8, width), Math.Max(8, height), PixelFormat.Format32bppPArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.Clear(Color.Transparent);
        var layout = new BadgeLayout(icon, numberWidth, gap, pad, fromWidth, toWidth);
        Draw(graphics, speedFont, unitFont, format, from, to, unit, scale, layout, progress, direction, 1.3f * scale, 1.5f * scale, Color.FromArgb(170, 0, 0, 0), Color.FromArgb(170, 0, 0, 0));
        Draw(graphics, speedFont, unitFont, format, from, to, unit, scale, layout, progress, direction, 0, 0, Color.FromArgb(48, 209, 88), Color.White);
        if (editing)
        {
            using var border = new Pen(Color.FromArgb(210, 255, 255, 255), Math.Max(1f, scale));
            graphics.DrawRectangle(border, 1, 1, bitmap.Width - 3, bitmap.Height - 3);
            var grip = Math.Max(7f, 8f * scale);
            graphics.DrawLine(border, bitmap.Width - 2 - grip, bitmap.Height - 3, bitmap.Width - 3, bitmap.Height - 3);
            graphics.DrawLine(border, bitmap.Width - 3, bitmap.Height - 2 - grip, bitmap.Width - 3, bitmap.Height - 3);
        }

        speedFont.Dispose();
        unitFont.Dispose();
        return bitmap;
    }

    private readonly record struct BadgeLayout(float Icon, float NumberWidth, float Gap, float Pad, float FromWidth, float ToWidth);

    private static void Draw(Graphics graphics, Font speedFont, Font unitFont, StringFormat format, string from, string to, string unit, float scale, BadgeLayout layout, float progress, int direction, float dx, float dy, Color iconColor, Color textColor)
    {
        var penWidth = Math.Max(1.4f, 1.7f * scale);
        var bounds = new RectangleF(layout.Pad + dx, (graphics.VisibleClipBounds.Height - layout.Icon) / 2f + dy, layout.Icon, layout.Icon);
        using var pen = new Pen(iconColor, penWidth) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        graphics.DrawArc(pen, bounds.X + penWidth, bounds.Y + penWidth, bounds.Width - penWidth * 2, bounds.Height - penWidth * 2, 200, 240);
        var center = new PointF(bounds.X + bounds.Width / 2f, bounds.Y + bounds.Height / 2f);
        graphics.DrawLine(pen, center, new PointF(center.X + bounds.Width * 0.28f, center.Y - bounds.Height * 0.22f));
        using var dot = new SolidBrush(iconColor);
        var dotSize = Math.Max(2.2f, 2.6f * scale);
        graphics.FillEllipse(dot, center.X - dotSize / 2f, center.Y - dotSize / 2f, dotSize, dotSize);

        var baseline = graphics.VisibleClipBounds.Height - layout.Pad - Math.Max(Descent(speedFont), Descent(unitFont)) + dy;
        var textX = bounds.Right + 8f * scale + dx;
        var travel = speedFont.Size * 0.9f;
        var clip = new RectangleF(textX - 2f, 0, layout.NumberWidth + 4f, graphics.VisibleClipBounds.Height);
        var state = graphics.Save();
        graphics.SetClip(clip);
        var fromX = textX + layout.NumberWidth - layout.FromWidth;
        var toX = textX + layout.NumberWidth - layout.ToWidth;
        if (progress >= 1f || from == to)
        {
            DrawSpeed(graphics, to, speedFont, format, textColor, toX, baseline, 1f);
        }
        else if (direction > 0)
        {
            DrawSpeed(graphics, from, speedFont, format, textColor, fromX, baseline - progress * travel, 1f - progress);
            DrawSpeed(graphics, to, speedFont, format, textColor, toX, baseline + (1f - progress) * travel, progress);
        }
        else
        {
            DrawSpeed(graphics, from, speedFont, format, textColor, fromX, baseline + progress * travel, 1f - progress);
            DrawSpeed(graphics, to, speedFont, format, textColor, toX, baseline - (1f - progress) * travel, progress);
        }

        graphics.Restore(state);
        using var unitBrush = new SolidBrush(textColor);
        graphics.DrawString(unit, unitFont, unitBrush, textX + layout.NumberWidth + layout.Gap, baseline - Ascent(unitFont), format);
    }

    private static void DrawSpeed(Graphics graphics, string text, Font font, StringFormat format, Color color, float x, float baseline, float alpha)
    {
        var amount = Math.Clamp(alpha, 0f, 1f);
        if (amount <= 0.01f || text.Length == 0)
            return;
        using var brush = new SolidBrush(Color.FromArgb((int)(color.A * amount), color));
        graphics.DrawString(text, font, brush, x, baseline - Ascent(font), format);
    }

    private static float TightWidth(Graphics graphics, string text, Font font, StringFormat format)
    {
        if (text.Length == 0)
            return 0;
        return graphics.MeasureString(text, font, PointF.Empty, format).Width + 1f;
    }

    private static float Ascent(Font font)
    {
        var em = font.FontFamily.GetEmHeight(font.Style);
        return font.Size * font.FontFamily.GetCellAscent(font.Style) / em;
    }

    private static float Descent(Font font)
    {
        var em = font.FontFamily.GetEmHeight(font.Style);
        return font.Size * font.FontFamily.GetCellDescent(font.Style) / em;
    }

    private static float LineHeight(Font font) => Ascent(font) + Descent(font);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _frames.Dispose();
        base.Dispose(disposing);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (_editing && e.Button == MouseButtons.Left)
        {
            _sizing = e.X >= _pixelSize.Width - 18 && e.Y >= _pixelSize.Height - 18;
            _drag = !_sizing;
            _grabCursor = Cursor.Position;
            _grabOrigin = _origin;
            _grabScale = _userScale;
            Capture = true;
            Cursor = _sizing ? Cursors.SizeNWSE : Cursors.SizeAll;
        }

        base.OnMouseDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_editing && !_drag && !_sizing)
            Cursor = e.X >= _pixelSize.Width - 18 && e.Y >= _pixelSize.Height - 18 ? Cursors.SizeNWSE : Cursors.SizeAll;
        if ((_drag || _sizing) && _processId != 0 && TryGameClient(_processId, out var client))
        {
            var dx = Cursor.Position.X - _grabCursor.X;
            var dy = Cursor.Position.Y - _grabCursor.Y;
            if (_sizing)
                _userScale = Math.Clamp(_grabScale * (1f + Math.Max(dx, dy) / 140f), 0.55f, 2.4f);
            else if (_scale > 0.01f)
            {
                var x = _grabOrigin.X + dx;
                var y = _grabOrigin.Y + dy;
                _right = (client.Right - x - _pixelSize.Width) / _scale;
                _bottom = (client.Bottom - y - _pixelSize.Height) / _scale;
            }

            Blit(client, _scale);
        }

        base.OnMouseMove(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (_drag || _sizing)
        {
            _drag = false;
            _sizing = false;
            Capture = false;
            PlacementChanged?.Invoke(_right, _bottom, _userScale);
        }

        base.OnMouseUp(e);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        if (_editing && _processId != 0 && TryGameClient(_processId, out var client))
        {
            var step = e.Delta > 0 ? 1.08f : 1f / 1.08f;
            _userScale = Math.Clamp(_userScale * step, 0.55f, 2.4f);
            Blit(client, _scale);
            PlacementChanged?.Invoke(_right, _bottom, _userScale);
            return;
        }

        base.OnMouseWheel(e);
    }

    private void ApplyHitTest()
    {
        if (!IsHandleCreated)
            return;
        var style = GetWindowLongPtr(Handle, -20).ToInt64();
        style = _editing ? style & ~0x20 : style | 0x20;
        SetWindowLongPtr(Handle, -20, new IntPtr(style));
        SetWindowPos(Handle, IntPtr.Zero, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0004 | 0x0020);
    }

    private static Font FontOf(float pixels)
    {
        try
        {
            return new Font("Bahnschrift", pixels, FontStyle.Regular, GraphicsUnit.Pixel);
        }
        catch (ArgumentException)
        {
            return new Font("Segoe UI", pixels, FontStyle.Regular, GraphicsUnit.Pixel);
        }
    }

    private static void Copy(IntPtr source, IntPtr destination, int count)
    {
        var buffer = new byte[count];
        Marshal.Copy(source, buffer, 0, count);
        Marshal.Copy(buffer, 0, destination, count);
    }

    private static bool TryGameClient(int processId, out Rectangle client)
    {
        var bestArea = 0L;
        var best = Rectangle.Empty;
        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out var owner);
            if (owner != (uint)processId || !IsWindowVisible(hwnd) || IsIconic(hwnd))
                return true;
            if (!GetClientRect(hwnd, out var rect))
                return true;
            var width = rect.Right - rect.Left;
            var height = rect.Bottom - rect.Top;
            if (width < 320 || height < 240)
                return true;
            var area = (long)width * height;
            if (area <= bestArea)
                return true;
            var origin = new Point(0, 0);
            if (!ClientToScreen(hwnd, ref origin))
                return true;
            bestArea = area;
            best = new Rectangle(origin.X, origin.Y, width, height);
            return true;
        }, IntPtr.Zero);
        client = best;
        return bestArea > 0;
    }

    private delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct Blend
    {
        public byte Op;
        public byte Flags;
        public byte Alpha;
        public byte Format;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public int Size;
        public int Width;
        public int Height;
        public short Planes;
        public short BitCount;
        public int Compression;
        public int SizeImage;
        public int XPels;
        public int YPels;
        public int ClrUsed;
        public int ClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hwnd, out WinRect rect);

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr hwnd, ref Point point);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int command);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr screen, ref Point origin, ref Size size, IntPtr source, ref Point sourceOrigin, int key, ref Blend blend, int flags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr dc);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr dc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapInfo info, uint usage, out IntPtr bits, IntPtr section, uint offset);
}
