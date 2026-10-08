using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace Fh6Cruise;

internal static class Ios
{
    public static readonly Color Canvas = Color.FromArgb(0, 0, 0);
    public static readonly Color Group = Color.FromArgb(28, 28, 30);
    public static readonly Color Line = Color.FromArgb(56, 56, 58);
    public static readonly Color Label = Color.FromArgb(255, 255, 255);
    public static readonly Color Secondary = Color.FromArgb(142, 142, 147);
    public static readonly Color Blue = Color.FromArgb(10, 132, 255);
    public static readonly Color Green = Color.FromArgb(48, 209, 88);
    public static readonly Color Orange = Color.FromArgb(255, 159, 10);
    public static readonly Color Fill = Color.FromArgb(58, 58, 60);

    public const float Radius = 26f;
    public const int Pad = 24;

    public static Font Title { get; }
    public static Font Speed { get; }
    public static Font Unit { get; }
    public static Font Body { get; }
    public static Font Foot { get; }
    public static Font Segment { get; }

    static Ios()
    {
        Title = Face("Segoe UI Variable Display", 18f, FontStyle.Bold);
        Speed = Face("Segoe UI Variable Display", 30f, FontStyle.Regular);
        Unit = Face("Segoe UI Variable Text", 12f, FontStyle.Regular);
        Body = Face("Segoe UI Variable Text", 12f, FontStyle.Regular);
        Foot = Face("Segoe UI Variable Text", 10f, FontStyle.Regular);
        Segment = Face("Segoe UI Variable Text", 10f, FontStyle.Bold);
    }

    public static GraphicsPath Rounded(RectangleF bounds, float radius, bool tl = true, bool tr = true, bool br = true, bool bl = true)
    {
        var path = new GraphicsPath();
        var limit = Math.Min(bounds.Width, bounds.Height) / 2f;
        var topLeft = tl ? Math.Min(radius, limit) : 0f;
        var topRight = tr ? Math.Min(radius, limit) : 0f;
        var bottomRight = br ? Math.Min(radius, limit) : 0f;
        var bottomLeft = bl ? Math.Min(radius, limit) : 0f;

        if (topLeft > 0)
            path.AddArc(bounds.X, bounds.Y, topLeft * 2, topLeft * 2, 180, 90);
        path.AddLine(bounds.X + topLeft, bounds.Y, bounds.Right - topRight, bounds.Y);
        if (topRight > 0)
            path.AddArc(bounds.Right - topRight * 2, bounds.Y, topRight * 2, topRight * 2, 270, 90);
        path.AddLine(bounds.Right, bounds.Y + topRight, bounds.Right, bounds.Bottom - bottomRight);
        if (bottomRight > 0)
            path.AddArc(bounds.Right - bottomRight * 2, bounds.Bottom - bottomRight * 2, bottomRight * 2, bottomRight * 2, 0, 90);
        path.AddLine(bounds.Right - bottomRight, bounds.Bottom, bounds.X + bottomLeft, bounds.Bottom);
        if (bottomLeft > 0)
            path.AddArc(bounds.X, bounds.Bottom - bottomLeft * 2, bottomLeft * 2, bottomLeft * 2, 90, 90);
        path.AddLine(bounds.X, bounds.Bottom - bottomLeft, bounds.X, bounds.Y + topLeft);
        path.CloseFigure();
        return path;
    }

    private static Font Face(string family, float size, FontStyle style)
    {
        try
        {
            return new Font(family, size, style, GraphicsUnit.Point);
        }
        catch (ArgumentException)
        {
            return new Font("Segoe UI", size, style, GraphicsUnit.Point);
        }
    }
}

internal sealed class TrafficBar : Control
{
    private static readonly Color[] Colors =
    {
        Color.FromArgb(255, 95, 87),
        Color.FromArgb(254, 188, 46),
        Color.FromArgb(40, 200, 64)
    };

    private static readonly Color Idle = Color.FromArgb(78, 78, 80);
    private const int Diameter = 14;
    private const int Gap = 8;
    private const int Inset = 20;

    private bool _hot;
    private bool _dragging;
    private bool _active = true;
    private bool _zoomed;

    public event EventHandler? CloseClicked;
    public event EventHandler? MinimizeClicked;
    public event EventHandler? ZoomClicked;

    public TrafficBar()
    {
        Height = 40;
        Dock = DockStyle.Top;
        TabStop = false;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable, false);
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Ios.Canvas;
    }

    public bool Active
    {
        get => _active;
        set
        {
            if (_active == value)
                return;
            _active = value;
            Invalidate();
        }
    }

    public bool Zoomed
    {
        get => _zoomed;
        set
        {
            if (_zoomed == value)
                return;
            _zoomed = value;
            Invalidate();
        }
    }

    public bool OverButton(Point formClient)
    {
        if (Parent == null)
            return false;
        return IndexAt(PointToClient(Parent.PointToScreen(formClient))) >= 0;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var hot = IndexAt(e.Location) >= 0;
        if (hot != _hot)
        {
            _hot = hot;
            Invalidate();
        }

        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        if (_hot)
        {
            _hot = false;
            Invalidate();
        }

        base.OnMouseLeave(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left && IndexAt(e.Location) < 0)
            DragWindow();
        base.OnMouseDown(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (_dragging)
        {
            _dragging = false;
            base.OnMouseUp(e);
            return;
        }

        if (e.Button == MouseButtons.Left)
        {
            switch (IndexAt(e.Location))
            {
                case 0:
                    CloseClicked?.Invoke(this, EventArgs.Empty);
                    break;
                case 1:
                    MinimizeClicked?.Invoke(this, EventArgs.Empty);
                    break;
                case 2:
                    ZoomClicked?.Invoke(this, EventArgs.Empty);
                    break;
            }
        }

        base.OnMouseUp(e);
    }

    private void DragWindow()
    {
        var form = FindForm();
        if (form == null)
            return;
        _dragging = true;
        ReleaseCapture();
        SendMessage(form.Handle, 0xA1, (IntPtr)2, IntPtr.Zero);
    }

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(Ios.Canvas);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        for (var i = 0; i < 3; i++)
        {
            var circle = Circle(i);
            using var fill = new SolidBrush(_active ? Colors[i] : Idle);
            e.Graphics.FillEllipse(fill, circle);
            if (_active && _hot)
                DrawGlyph(e.Graphics, circle, i, _zoomed);
        }
    }

    private int IndexAt(Point local)
    {
        for (var i = 0; i < 3; i++)
        {
            var circle = Circle(i);
            var dx = local.X - (circle.X + circle.Width / 2f);
            var dy = local.Y - (circle.Y + circle.Height / 2f);
            if ((dx * dx) + (dy * dy) <= (Diameter / 2f) * (Diameter / 2f))
                return i;
        }

        return -1;
    }

    private RectangleF Circle(int index)
    {
        var y = (Height - Diameter) / 2f;
        var group = (3 * Diameter) + (2 * Gap);
        var x = Width - Inset - group + (index * (Diameter + Gap));
        return new RectangleF(x, y, Diameter, Diameter);
    }

    private static void DrawGlyph(Graphics graphics, RectangleF circle, int index, bool zoomed)
    {
        using var pen = new Pen(Color.FromArgb(90, 30, 20), 1.35f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };
        var centerX = circle.X + circle.Width / 2f;
        var centerY = circle.Y + circle.Height / 2f;
        var arm = circle.Width * 0.22f;
        if (index == 0)
        {
            graphics.DrawLine(pen, centerX - arm, centerY - arm, centerX + arm, centerY + arm);
            graphics.DrawLine(pen, centerX + arm, centerY - arm, centerX - arm, centerY + arm);
            return;
        }

        if (index == 1)
        {
            graphics.DrawLine(pen, centerX - arm, centerY, centerX + arm, centerY);
            return;
        }

        if (zoomed)
        {
            var side = arm * 1.7f;
            graphics.DrawRectangle(pen, centerX - side / 2f, centerY - side / 2f, side, side);
            return;
        }

        graphics.DrawLine(pen, centerX - arm, centerY, centerX + arm, centerY);
        graphics.DrawLine(pen, centerX, centerY - arm, centerX, centerY + arm);
    }
}

internal sealed class IosIndicator : Control
{
    private readonly System.Windows.Forms.Timer _fade = new() { Interval = 16 };
    private int _pos;
    private int _view = 1;
    private int _extent = 1;
    private float _alpha;
    private int _quiet;
    private bool _drag;
    private int _grabY;
    private int _grabPos;

    public event Action<int>? ScrollTo;

    public IosIndicator(Color paper)
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        SetStyle(ControlStyles.Selectable, false);
        TabStop = false;
        BackColor = paper;
        Width = 12;
        _fade.Tick += (_, _) => Fade();
    }

    public void ShowRange(int position, int view, int extent, bool poke)
    {
        _pos = Math.Max(0, position);
        _view = Math.Max(1, view);
        _extent = Math.Max(1, extent);
        if (poke && CanScroll)
            Wake();
        else
            Invalidate();
    }

    public void Wake()
    {
        if (!CanScroll)
            return;
        _quiet = 0;
        _alpha = 1f;
        _fade.Start();
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (_alpha < 0.02f || !Thumb(out var thumb))
            return;
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var brush = new SolidBrush(Color.FromArgb((int)(150 * _alpha), 255, 255, 255));
        using var path = Ios.Rounded(thumb, thumb.Width / 2f);
        g.FillPath(brush, path);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left && Thumb(out var thumb))
        {
            var hit = Rectangle.Round(thumb);
            hit.Inflate(8, 10);
            if (!hit.Contains(e.Location))
            {
                base.OnMouseDown(e);
                return;
            }

            _drag = true;
            _grabY = e.Y;
            _grabPos = _pos;
            Capture = true;
            Wake();
        }

        base.OnMouseDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_drag && Thumb(out var thumb))
        {
            var travel = Math.Max(1f, Height - 8f - thumb.Height);
            var span = Math.Max(1, _extent - _view);
            var next = _grabPos + (int)Math.Round((e.Y - _grabY) * span / travel);
            ScrollTo?.Invoke(Math.Clamp(next, 0, span));
            Wake();
        }

        base.OnMouseMove(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        _drag = false;
        Capture = false;
        base.OnMouseUp(e);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x0084 && _alpha < 0.2f && !_drag)
        {
            m.Result = (IntPtr)(-1);
            return;
        }

        base.WndProc(ref m);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _fade.Dispose();
        base.Dispose(disposing);
    }

    private bool CanScroll => _extent > _view;

    private void Fade()
    {
        if (_drag)
        {
            _quiet = 0;
            return;
        }

        _quiet += 16;
        if (_quiet < 650)
            return;
        _alpha = Math.Max(0f, _alpha - 0.07f);
        Invalidate();
        if (_alpha <= 0f)
            _fade.Stop();
    }

    private bool Thumb(out RectangleF thumb)
    {
        thumb = RectangleF.Empty;
        if (!CanScroll || Height < 16)
            return false;
        var track = Height - 8f;
        var height = Math.Max(28f, track * _view / _extent);
        height = Math.Min(height, track);
        var span = Math.Max(1, _extent - _view);
        var y = 4f + (track - height) * Math.Clamp(_pos, 0, span) / span;
        thumb = new RectangleF(4f, y, 4f, height);
        return true;
    }
}

internal sealed class SettingsList : Panel
{
    private readonly List<Control> _items = new();
    private readonly IosIndicator _bar;
    private bool _layout;
    private int _offset;
    private int _content;

    public SettingsList()
    {
        Dock = DockStyle.Fill;
        AutoScroll = false;
        BackColor = Ios.Canvas;
        DoubleBuffered = true;
        _bar = new IosIndicator(Ios.Canvas);
        _bar.ScrollTo += ScrollTo;
        Controls.Add(_bar);
        Resize += (_, _) => Reflow();
    }

    public void ScrollBy(int delta)
    {
        ScrollTo(_offset + delta);
        _bar.Wake();
    }

    public void Add(Control item)
    {
        _items.Add(item);
        Controls.Add(item);
    }

    public void Reflow()
    {
        if (_layout || ClientSize.Width < 40)
            return;
        _layout = true;
        try
        {
            ReflowCore();
        }
        finally
        {
            _layout = false;
        }
    }

    private void ReflowCore()
    {
        if (ClientSize.Width < 40)
            return;

        var width = Math.Min(520, Math.Max(280, ClientSize.Width - 56));
        var x = Math.Max(28, (ClientSize.Width - width) / 2);
        var y = 16;
        SuspendLayout();
        foreach (var item in _items)
        {
            if (!item.Visible)
                continue;
            var gap = item switch
            {
                Footnote note => note.GapBefore,
                InsetGroup group => group.GapBefore,
                _ => 0
            };
            y += gap;
            switch (item)
            {
                case SpeedHeader header:
                    header.Arrange(width);
                    break;
                case Footnote note:
                    note.Arrange(width);
                    break;
                case InsetGroup group:
                    group.Arrange(width);
                    break;
            }

            item.SetBounds(x, y, width, item.Height);
            y += item.Height;
        }

        _content = y + 28;
        _offset = Math.Clamp(_offset, 0, Math.Max(0, _content - ClientSize.Height));
        if (_offset > 0)
        {
            foreach (var item in _items)
            {
                if (item.Visible)
                    item.Top -= _offset;
            }
        }

        ResumeLayout();
        PlaceBar();
    }

    private void ScrollTo(int offset)
    {
        var max = Math.Max(0, _content - ClientSize.Height);
        var next = Math.Clamp(offset, 0, max);
        if (next == _offset)
            return;
        var diff = next - _offset;
        _offset = next;
        foreach (var item in _items)
        {
            if (item.Visible)
                item.Top -= diff;
        }

        _bar.ShowRange(_offset, ClientSize.Height, _content, false);
    }

    private void PlaceBar()
    {
        _bar.SetBounds(Width - 14, 4, 12, Math.Max(12, Height - 8));
        _bar.BringToFront();
        _bar.ShowRange(_offset, ClientSize.Height, _content, false);
    }
}

internal sealed class SpeedHeader : Control
{
    private static readonly StringFormat Tight = new(StringFormat.GenericTypographic);

    public string StatusText { get; set; } = "Waiting";
    public Color StatusColor { get; set; } = Ios.Secondary;
    public string SpeedText { get; set; } = "—";
    public string UnitText { get; set; } = "km/h";
    public string LockedText { get; set; } = "—";

    public SpeedHeader()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Ios.Canvas;
        Height = 108;
    }

    public void Arrange(int width)
    {
        Height = 108;
        Width = width;
    }

    public void Publish() => Invalidate();

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        TextRenderer.DrawText(g, "Cruise", Ios.Title, new Rectangle(8, 0, Width - 130, 26), Ios.Label, TextFormatFlags.Left | TextFormatFlags.Bottom | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
        TextRenderer.DrawText(g, StatusText, Ios.Body, new Rectangle(8, 2, Width - 16, 24), StatusColor, TextFormatFlags.Right | TextFormatFlags.Bottom | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);

        var speedTop = 30f;
        var ascent = Ascent(Ios.Speed, g);
        var baseline = speedTop + ascent;
        float mark;
        using (var ink = new SolidBrush(Ios.Label))
        using (var secondary = new SolidBrush(Ios.Secondary))
        {
            if (SpeedText == "—")
            {
                mark = 30f;
                var mid = baseline - ascent * 0.34f;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using var dash = Ios.Rounded(new RectangleF(8f, mid - 2f, 22f, 3f), 1.5f);
                g.FillPath(ink, dash);
            }
            else
            {
                var speedSize = g.MeasureString(SpeedText, Ios.Speed, 1000, Tight);
                mark = speedSize.Width;
                g.DrawString(SpeedText, Ios.Speed, ink, new PointF(8, speedTop), Tight);
            }

            var unitTop = baseline - Ascent(Ios.Unit, g) - 1f;
            g.DrawString(UnitText, Ios.Unit, secondary, new PointF(mark + 14f, unitTop), Tight);
        }

        var lockedTop = (int)baseline + 8;
        var lockedLabel = TextRenderer.MeasureText(g, "Locked", Ios.Body, new Size(160, 26), TextFormatFlags.NoPadding);
        TextRenderer.DrawText(g, "Locked", Ios.Body, new Rectangle(8, lockedTop, lockedLabel.Width + 2, 22), Ios.Secondary, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        var lockedX = lockedLabel.Width + 16;
        TextRenderer.DrawText(g, LockedText, Ios.Body, new Rectangle(lockedX, lockedTop, Math.Max(20, Width - lockedX - 8), 22), Ios.Label, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
    }

    private static float Ascent(Font font, Graphics g)
    {
        var family = font.FontFamily;
        var style = font.Style;
        if (!family.IsStyleAvailable(style))
            style = FontStyle.Regular;
        var em = family.GetEmHeight(style);
        if (em <= 0)
            return font.GetHeight(g) * 0.8f;
        return font.Size * family.GetCellAscent(style) / em * g.DpiY / 72f;
    }
}

internal sealed class Footnote : Control
{
    public int GapBefore { get; set; }
    public int TopPad { get; set; } = 18;
    public int BottomPad { get; set; } = 6;
    public int LeftInset { get; set; } = Ios.Pad;

    public Footnote(string text)
    {
        Text = text;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Ios.Canvas;
        Font = Ios.Foot;
        ForeColor = Ios.Secondary;
    }

    public void Arrange(int width)
    {
        var size = TextRenderer.MeasureText(Text, Font, new Size(Math.Max(20, width - LeftInset * 2), 800), TextFormatFlags.WordBreak | TextFormatFlags.Left);
        Height = string.IsNullOrEmpty(Text) ? TopPad + BottomPad : TopPad + size.Height + BottomPad;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (string.IsNullOrEmpty(Text))
            return;
        TextRenderer.DrawText(e.Graphics, Text, Font, new Rectangle(LeftInset, TopPad, Math.Max(10, Width - LeftInset * 2), Math.Max(10, Height - TopPad)), ForeColor, TextFormatFlags.WordBreak | TextFormatFlags.Left | TextFormatFlags.NoPadding);
    }
}

internal sealed class InsetGroup : Control
{
    private readonly List<Control> _rows = new();

    public int GapBefore { get; set; }

    public InsetGroup()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Ios.Canvas;
    }

    public void Add(Control row)
    {
        _rows.Add(row);
        Controls.Add(row);
    }

    public void Arrange(int width)
    {
        var visible = new List<Control>();
        foreach (var row in _rows)
        {
            if (row.Visible)
                visible.Add(row);
        }

        var y = 0;
        for (var i = 0; i < visible.Count; i++)
        {
            if (visible[i] is IosRow row)
            {
                row.IsFirst = i == 0;
                row.IsLast = i == visible.Count - 1;
                row.DrawSeparator = i != visible.Count - 1;
            }

            visible[i].SetBounds(0, y, width, visible[i].Height);
            y += visible[i].Height;
        }

        Height = y;
    }

    public void PaintBehind(Graphics childGraphics, Control child)
    {
        var state = childGraphics.Save();
        var offset = Point.Empty;
        for (var node = child; node != null && node != this; node = node.Parent)
            offset.Offset(node.Left, node.Top);
        childGraphics.TranslateTransform(-offset.X, -offset.Y);
        PaintCard(childGraphics);
        childGraphics.Restore(state);
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        PaintCard(e.Graphics);
    }

    private void PaintCard(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        using (var canvas = new SolidBrush(Ios.Canvas))
            g.FillRectangle(canvas, ClientRectangle);
        if (Width < 4 || Height < 4)
            return;

        var bounds = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        using var path = Ios.Rounded(bounds, Ios.Radius);
        using (var fill = new SolidBrush(Ios.Group))
            g.FillPath(fill, path);
        using var rim = new Pen(Color.FromArgb(18, 255, 255, 255), 1f);
        g.DrawPath(rim, path);
    }
}

internal static class CardPaint
{
    public static void Behind(Control control, Graphics graphics)
    {
        for (var parent = control.Parent; parent != null; parent = parent.Parent)
        {
            if (parent is InsetGroup group)
            {
                group.PaintBehind(graphics, control);
                return;
            }
        }

        graphics.Clear(Ios.Group);
    }
}

internal class IosRow : Control
{
    private bool _hot;
    private bool _down;

    public bool IsFirst { get; set; }
    public bool IsLast { get; set; }
    public bool DrawSeparator { get; set; } = true;

    public IosRow()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        Height = 40;
        Font = Ios.Body;
    }

    protected virtual bool Hoverable => false;

    protected override void OnMouseEnter(EventArgs e)
    {
        _hot = true;
        Invalidate();
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hot = false;
        _down = false;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (Hoverable && e.Button == MouseButtons.Left)
        {
            _down = true;
            Invalidate();
        }

        base.OnMouseDown(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        _down = false;
        Invalidate();
        base.OnMouseUp(e);
    }

    protected void PaintChrome(Graphics g)
    {
        if (Hoverable && (_hot || _down))
        {
            var bounds = new RectangleF(0, 0, Width, Height);
            using var path = Ios.Rounded(bounds, Ios.Radius, IsFirst, IsFirst, IsLast, IsLast);
            using var brush = new SolidBrush(Color.FromArgb(_down ? 28 : 16, 255, 255, 255));
            var previous = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.FillPath(brush, path);
            g.SmoothingMode = previous;
        }

        if (!DrawSeparator)
            return;
        using var pen = new Pen(Ios.Line, 1f);
        g.DrawLine(pen, Ios.Pad, Height - 1, Width - Ios.Pad, Height - 1);
    }

    protected static void DrawChevron(Graphics g, Rectangle bounds)
    {
        var x = bounds.Right - 30f;
        var y = bounds.Top + bounds.Height / 2f;
        using var pen = new Pen(Color.FromArgb(160, 142, 142, 147), 1.7f);
        pen.StartCap = LineCap.Round;
        pen.EndCap = LineCap.Round;
        var previous = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.DrawLines(pen, new[]
        {
            new PointF(x, y - 5f),
            new PointF(x + 4.5f, y),
            new PointF(x, y + 5f)
        });
        g.SmoothingMode = previous;
    }
}

internal sealed class ChevronRow : IosRow
{
    public string Title { get; set; } = "";
    public string Value { get; set; } = "";
    public Color ValueColor { get; set; } = Ios.Secondary;
    public event EventHandler? Tapped;

    public ChevronRow()
    {
        Cursor = Cursors.Hand;
    }

    protected override bool Hoverable => true;

    public void Publish() => Invalidate();

    protected override void OnMouseClick(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
            Tapped?.Invoke(this, EventArgs.Empty);
        base.OnMouseClick(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        PaintChrome(e.Graphics);
        var titleWidth = Math.Max(40, Width / 2);
        TextRenderer.DrawText(e.Graphics, Title, Font, new Rectangle(Ios.Pad, 0, titleWidth, Height), Ios.Label, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        var valueWidth = Math.Max(40, Width - titleWidth - 48);
        TextRenderer.DrawText(e.Graphics, Value, Font, new Rectangle(Width - valueWidth - 40, 0, valueWidth, Height), ValueColor, TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        DrawChevron(e.Graphics, ClientRectangle);
    }
}

internal sealed class SwitchRow : IosRow
{
    private readonly IosSwitch _switch = new();

    public string Title { get; set; } = "";
    public bool Checked
    {
        get => _switch.Checked;
        set => _switch.Checked = value;
    }

    public event EventHandler? CheckedChanged
    {
        add => _switch.CheckedChanged += value;
        remove => _switch.CheckedChanged -= value;
    }

    public SwitchRow()
    {
        Controls.Add(_switch);
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        if (_switch != null)
            _switch.SetBounds(Width - Ios.Pad - _switch.Width, (Height - _switch.Height) / 2, _switch.Width, _switch.Height);
        base.OnLayout(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        PaintChrome(e.Graphics);
        TextRenderer.DrawText(e.Graphics, Title, Font, new Rectangle(Ios.Pad, 0, Width - 100, Height), Ios.Label, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
    }
}

internal sealed class IosSwitch : Control
{
    private readonly System.Windows.Forms.Timer _anim = new() { Interval = 15 };
    private bool _checked;
    private float _pos;
    private float _target;
    private bool _ready;

    public event EventHandler? CheckedChanged;

    public bool Checked
    {
        get => _checked;
        set
        {
            if (_checked == value && _ready)
                return;
            _checked = value;
            _target = value ? 1f : 0f;
            if (!_ready || !IsHandleCreated)
            {
                _pos = _target;
                _ready = true;
                Invalidate();
                return;
            }

            _anim.Start();
            CheckedChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public IosSwitch()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Opaque, true);
        BackColor = Ios.Group;
        Size = new Size(51, 31);
        Cursor = Cursors.Hand;
        _anim.Tick += (_, _) =>
        {
            if (IsDisposed)
                return;
            var next = _pos + (_target - _pos) * 0.38f;
            if (Math.Abs(_target - next) < 0.03f)
            {
                _pos = _target;
                _anim.Stop();
            }
            else
            {
                _pos = next;
            }

            Invalidate();
        };
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
            Checked = !Checked;
        base.OnMouseClick(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        CardPaint.Behind(this, g);
        var track = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        var off = Color.FromArgb(255, 57, 57, 61);
        var on = Ios.Green;
        var t = Math.Clamp(_pos, 0f, 1f);
        using (var brush = new SolidBrush(Lerp(off, on, t)))
        using (var path = Ios.Rounded(track, track.Height / 2f))
            g.FillPath(brush, path);

        var knob = track.Height - 4f;
        var x = 2f + (track.Width - knob - 4f) * t;
        var y = 2f;
        using var shadow = new SolidBrush(Color.FromArgb(40, 0, 0, 0));
        g.FillEllipse(shadow, x, y + 1f, knob, knob);
        using var white = new SolidBrush(Color.White);
        g.FillEllipse(white, x, y, knob, knob);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _anim.Dispose();
        base.Dispose(disposing);
    }

    private static Color Lerp(Color a, Color b, float amount)
    {
        return Color.FromArgb(
            (int)(a.R + (b.R - a.R) * amount),
            (int)(a.G + (b.G - a.G) * amount),
            (int)(a.B + (b.B - a.B) * amount));
    }
}

internal sealed class SegmentRow : IosRow
{
    private readonly IosSegment _segment = new();

    public string Title { get; set; } = "";

    public int SelectedIndex
    {
        get => _segment.SelectedIndex;
        set => _segment.SelectedIndex = value;
    }

    public event EventHandler? SelectedIndexChanged
    {
        add => _segment.SelectedIndexChanged += value;
        remove => _segment.SelectedIndexChanged -= value;
    }

    public SegmentRow(params string[] items)
    {
        _segment.Items.AddRange(items);
        _segment.Width = 156;
        Controls.Add(_segment);
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        if (_segment != null)
            _segment.SetBounds(Width - Ios.Pad - _segment.Width, (Height - _segment.Height) / 2, _segment.Width, _segment.Height);
        base.OnLayout(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        PaintChrome(e.Graphics);
        TextRenderer.DrawText(e.Graphics, Title, Font, new Rectangle(Ios.Pad, 0, Width - _segment.Width - 56, Height), Ios.Label, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
    }
}

internal sealed class IosSegment : Control
{
    private int _index = -1;
    public List<string> Items { get; } = new();
    public event EventHandler? SelectedIndexChanged;

    public int SelectedIndex
    {
        get => _index;
        set
        {
            if (_index == value)
                return;
            _index = value;
            Invalidate();
            SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public IosSegment()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Opaque, true);
        BackColor = Ios.Group;
        Height = 28;
        Cursor = Cursors.Hand;
        Font = Ios.Segment;
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        if (Items.Count > 0 && e.Button == MouseButtons.Left)
            SelectedIndex = Math.Clamp(e.X * Items.Count / Math.Max(1, Width), 0, Items.Count - 1);
        base.OnMouseClick(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        CardPaint.Behind(this, g);
        var bounds = new RectangleF(0, 0, Width - 1f, Height - 1f);
        using (var track = Ios.Rounded(bounds, bounds.Height / 2f))
        using (var brush = new SolidBrush(Color.FromArgb(46, 118, 118, 128)))
            g.FillPath(brush, track);

        if (Items.Count > 0 && _index >= 0 && _index < Items.Count)
        {
            var inner = 2f;
            var slot = (bounds.Width - inner * 2) / Items.Count;
            var pill = new RectangleF(bounds.X + inner + slot * _index, bounds.Y + inner, slot, bounds.Height - inner * 2);
            using var pillPath = Ios.Rounded(pill, pill.Height / 2f);
            using var pillBrush = new SolidBrush(Color.FromArgb(152, 152, 157));
            g.FillPath(pillBrush, pillPath);
            using var pillPen = new Pen(Color.FromArgb(50, 255, 255, 255), 1f);
            g.DrawPath(pillPen, pillPath);
        }

        if (Items.Count == 0)
            return;
        var slotWidth = Width / Items.Count;
        for (var i = 0; i < Items.Count; i++)
        {
            using var brush = new SolidBrush(i == _index ? Ios.Label : Ios.Secondary);
            using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString(Items[i], Font, brush, new RectangleF(slotWidth * i, 0, slotWidth, Height), format);
        }
    }
}

internal sealed class SliderRow : IosRow
{
    private readonly IosSlider _slider = new();

    public string Title { get; set; } = "";
    public string ValueText { get; set; } = "";

    public float Value
    {
        get => _slider.Value;
        set => _slider.Value = value;
    }

    public event EventHandler? ValueChanged
    {
        add => _slider.ValueChanged += value;
        remove => _slider.ValueChanged -= value;
    }

    public SliderRow()
    {
        Height = 58;
        _slider.Minimum = 0.5f;
        _slider.Maximum = 30f;
        _slider.Step = 0.5f;
        Controls.Add(_slider);
        _slider.ValueChanged += (_, _) => Invalidate();
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        if (_slider != null)
            _slider.SetBounds(Ios.Pad, 28, Math.Max(20, Width - Ios.Pad * 2), 24);
        base.OnLayout(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        PaintChrome(e.Graphics);
        TextRenderer.DrawText(e.Graphics, Title, Font, new Rectangle(Ios.Pad, 2, Width / 2, 24), Ios.Label, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        var valueColor = _slider != null && _slider.Dragging ? Ios.Label : Ios.Secondary;
        TextRenderer.DrawText(e.Graphics, ValueText, Font, new Rectangle(Width / 2, 2, Width / 2 - Ios.Pad, 24), valueColor, TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
    }
}

internal sealed class IosSlider : Control
{
    private float _value;
    private bool _drag;

    public float Minimum { get; set; }
    public float Maximum { get; set; } = 1f;
    public float Step { get; set; } = 0.5f;
    public bool Dragging => _drag;
    public event EventHandler? ValueChanged;

    public float Value
    {
        get => _value;
        set
        {
            var next = Snap(value);
            if (Math.Abs(next - _value) < 0.001f)
                return;
            _value = next;
            Invalidate();
            ValueChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public IosSlider()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Opaque, true);
        BackColor = Ios.Group;
        Cursor = Cursors.Hand;
        Height = 28;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            _drag = true;
            Capture = true;
            MoveTo(e.X);
        }

        base.OnMouseDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_drag)
            MoveTo(e.X);
        base.OnMouseMove(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        _drag = false;
        Capture = false;
        Parent?.Invalidate();
        base.OnMouseUp(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        CardPaint.Behind(this, g);
        var y = Height / 2f;
        var x0 = 13f;
        var x1 = Width - 13f;
        using var track = new Pen(Ios.Fill, 4f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawLine(track, x0, y, x1, y);
        var t = Maximum <= Minimum ? 0f : (_value - Minimum) / (Maximum - Minimum);
        var x = x0 + (x1 - x0) * Math.Clamp(t, 0f, 1f);
        using var filled = new Pen(Ios.Blue, 4f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawLine(filled, x0, y, x, y);
        using var shadow = new SolidBrush(Color.FromArgb(36, 0, 0, 0));
        g.FillEllipse(shadow, x - 13f, y - 12f, 26f, 26f);
        using var thumb = new SolidBrush(Color.White);
        g.FillEllipse(thumb, x - 13f, y - 13f, 26f, 26f);
    }

    private void MoveTo(int x)
    {
        var x0 = 13f;
        var span = Math.Max(1f, Width - 26f);
        var t = Math.Clamp((x - x0) / span, 0f, 1f);
        Value = Minimum + (Maximum - Minimum) * t;
    }

    private float Snap(float value)
    {
        var step = Step <= 0 ? 0.5f : Step;
        var snapped = (float)(Math.Round(value / step) * step);
        return Math.Clamp(snapped, Minimum, Maximum);
    }
}


internal sealed class ValueRow : IosRow
{
    public string Title { get; set; } = "";
    public string Value { get; set; } = "";

    protected override void OnPaint(PaintEventArgs e)
    {
        PaintChrome(e.Graphics);
        var titleWidth = Math.Max(40, Width / 2);
        TextRenderer.DrawText(e.Graphics, Title, Font, new Rectangle(Ios.Pad, 0, titleWidth, Height), Ios.Label, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        TextRenderer.DrawText(e.Graphics, Value, Font, new Rectangle(titleWidth, 0, Math.Max(20, Width - titleWidth - Ios.Pad), Height), Ios.Secondary, TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
    }
}

internal sealed class ActionRow : IosRow
{
    public string Title { get; set; } = "";
    public event EventHandler? Tapped;

    public ActionRow()
    {
        Cursor = Cursors.Hand;
    }

    protected override bool Hoverable => true;

    protected override void OnMouseClick(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
            Tapped?.Invoke(this, EventArgs.Empty);
        base.OnMouseClick(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        PaintChrome(e.Graphics);
        TextRenderer.DrawText(e.Graphics, Title, Font, ClientRectangle, Ios.Blue, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }
}

internal sealed class LogRow : IosRow
{
    private const int LogPaper = 0x001E1C1C;
    private const int LogInk = 0x00938E8E;
    private readonly LogBox _box;
    private readonly IosIndicator _bar;
    private IntPtr _brush;

    public TextBox Box => _box;

    public void ShowText(string text)
    {
        if (_box.Text == text)
            return;
        _box.Text = text;
        _box.SelectionStart = _box.TextLength;
        _box.ScrollToCaret();
        SyncLog(true);
    }

    public LogRow()
    {
        Height = 160;
        _brush = CreateSolidBrush(LogPaper);
        _box = new LogBox();
        _box.HandleCreated += (_, _) => SendMessage(_box.Handle, 0x0443, IntPtr.Zero, (IntPtr)LogPaper);
        _box.Scrolled += (_, _) => SyncLog(true);
        _box.TextChanged += (_, _) => SyncLog(false);
        _bar = new IosIndicator(Ios.Group);
        _bar.ScrollTo += ScrollLog;
        Controls.Add(_box);
        Controls.Add(_bar);
    }

    private void SyncLog(bool poke)
    {
        if (!_box.IsHandleCreated)
            return;
        _box.ReadMetrics(out var pos, out var view, out var extent);
        _bar.ShowRange(pos, view, extent, poke);
    }

    private void ScrollLog(int line)
    {
        _box.ReadMetrics(out var pos, out _, out _);
        _box.ScrollLines(line - pos);
    }

    protected override void WndProc(ref Message m)
    {
        if ((m.Msg is 0x0138 or 0x0133) && _brush != IntPtr.Zero)
        {
            SetBkMode(m.WParam, 2);
            SetBkColor(m.WParam, LogPaper);
            SetTextColor(m.WParam, LogInk);
            m.Result = _brush;
            return;
        }

        base.WndProc(ref m);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && _brush != IntPtr.Zero)
        {
            DeleteObject(_brush);
            _brush = IntPtr.Zero;
        }

        base.Dispose(disposing);
    }

    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    private static extern IntPtr CreateSolidBrush(int color);

    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr handle);

    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    private static extern int SetBkColor(IntPtr hdc, int color);

    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    private static extern int SetTextColor(IntPtr hdc, int color);

    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    private static extern int SetBkMode(IntPtr hdc, int mode);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);

    protected override void OnLayout(LayoutEventArgs e)
    {
        if (_box != null)
            _box.SetBounds(Ios.Pad, 12, Math.Max(20, Width - Ios.Pad * 2), Math.Max(20, Height - 24));
        if (_bar != null && _box != null)
        {
            _bar.SetBounds(Width - 18, _box.Top + 2, 12, Math.Max(12, _box.Height - 4));
            _bar.BringToFront();
        }

        base.OnLayout(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        PaintChrome(e.Graphics);
    }
}

internal sealed class LogBox : TextBox
{
    public event EventHandler? Scrolled;

    public LogBox()
    {
        BorderStyle = BorderStyle.None;
        Multiline = true;
        ReadOnly = true;
        ScrollBars = ScrollBars.None;
        BackColor = Ios.Group;
        ForeColor = Ios.Secondary;
        Font = Ios.Foot;
        WordWrap = true;
        ShortcutsEnabled = true;
    }

    public void ReadMetrics(out int position, out int view, out int extent)
    {
        extent = Math.Max(1, (int)SendMessage(Handle, 0x00BA, IntPtr.Zero, IntPtr.Zero));
        position = Math.Max(0, (int)SendMessage(Handle, 0x00CE, IntPtr.Zero, IntPtr.Zero));
        view = Math.Max(1, ClientSize.Height / Math.Max(1, Font.Height));
    }

    public void ScrollLines(int lines)
    {
        if (lines == 0 || !IsHandleCreated)
            return;
        SendMessage(Handle, 0x00B6, IntPtr.Zero, (IntPtr)lines);
        Scrolled?.Invoke(this, EventArgs.Empty);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x020A)
        {
            var delta = (short)((m.WParam.ToInt64() >> 16) & 0xFFFF);
            ScrollLines(delta > 0 ? -2 : 2);
            return;
        }

        base.WndProc(ref m);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
}
