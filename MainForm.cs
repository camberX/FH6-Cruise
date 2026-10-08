using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace Fh6Cruise;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}

internal sealed class MainForm : Form
{
    private readonly CruiseController _cruise = new();
    private readonly SettingsList _page = new();
    private readonly TrafficBar _bar = new();
    private readonly SpeedHeader _header = new();
    private readonly ChevronRow _cruiseRow = new() { Title = "Cruise" };
    private readonly ChevronRow _brakeRow = new() { Title = "Brake" };
    private readonly ChevronRow _increaseRow = new() { Title = "Increase" };
    private readonly ChevronRow _decreaseRow = new() { Title = "Decrease" };
    private readonly SegmentRow _units = new("km/h", "mph") { Title = "Units" };
    private readonly SliderRow _ramp = new() { Title = "Catch-up" };
    private readonly ValueRow _port = new() { Title = "Port", Value = "20055" };
    private readonly InsetGroup _driving = new();
    private readonly InsetGroup _setup = new();
    private readonly Control[] _setupItems;
    private readonly Control[] _appItems;
    private bool _dataOutSeen;
    private readonly SwitchRow _pauseBrake = new() { Title = "Pause on brake" };
    private readonly SwitchRow _pauseImpact = new() { Title = "Pause after a hit" };
    private readonly SwitchRow _overlay = new() { Title = "Overlay" };
    private readonly ActionRow _adjust = new() { Title = "Move overlay" };
    private readonly CruiseOverlay _badge = new();
    private bool _movingOverlay;
    private readonly InsetGroup _adminGroup = new() { GapBefore = 16, Visible = false };
    private readonly LogRow _log = new();
    private readonly System.Windows.Forms.Timer _timer = new();
    private readonly bool _isAdmin = IsAdministrator();
    private bool _loading = true;
    private bool _adminShown;
    private BindSlot _captureSlot;
    private InputSnapshot? _capturePrevious;
    private string _captureRestore = "";

    private enum BindSlot
    {
        Cruise,
        Brake,
        Increase,
        Decrease
    }

    public MainForm()
    {
        Text = "Cruise";
        FormBorderStyle = FormBorderStyle.None;
        var mark = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        if (mark != null)
            Icon = mark;
        StartPosition = FormStartPosition.CenterScreen;
        Font = Ios.Body;
        BackColor = Ios.Canvas;
        ForeColor = Ios.Label;
        MinimumSize = new Size(460, 320);
        Size = new Size(560, 360);
        DoubleBuffered = true;
        KeyPreview = true;

        var controls = new InsetGroup();
        controls.Add(_cruiseRow);
        controls.Add(_brakeRow);
        controls.Add(_increaseRow);
        controls.Add(_decreaseRow);

        _driving.Add(_units);
        _driving.Add(_ramp);

        _setup.Add(new ValueRow { Title = "IP", Value = "127.0.0.1" });
        _setup.Add(_port);

        var options = new InsetGroup { GapBefore = 18 };
        options.Add(_pauseBrake);
        options.Add(_pauseImpact);
        options.Add(_overlay);
        options.Add(_adjust);

        var admin = new ActionRow { Title = "Run as administrator" };
        admin.Tapped += (_, _) => RestartElevated();
        _adminGroup.Add(admin);

        var activity = new InsetGroup();
        activity.Add(_log);

        var setupLead = new Footnote("Data Out") { TopPad = 8, BottomPad = 6 };
        var setupHelp = new Footnote("In Forza, open Settings, then HUD and Gameplay. Turn Data Out on and enter this address.") { TopPad = 6, BottomPad = 2 };
        var controlsLead = new Footnote("Controls") { TopPad = 8, BottomPad = 6, Visible = false };
        var controlsHelp = new Footnote("Increase and decrease move the lock by 5.") { TopPad = 6, BottomPad = 2, Visible = false };
        var drivingLead = new Footnote("Driving") { TopPad = 20, BottomPad = 6, Visible = false };
        var drivingHelp = new Footnote("After a brake, speed eases back at the catch-up rate.") { TopPad = 6, BottomPad = 2, Visible = false };
        var optionsHelp = new Footnote("Braking or a hard hit lets go of the hold for a moment.") { TopPad = 6, BottomPad = 2, Visible = false };
        var overlayHelp = new Footnote("The lock shows on the speedometer while you are driving. Drag it to move, and drag the corner to resize.") { TopPad = 2, BottomPad = 2, Visible = false };
        var activityLead = new Footnote("Activity") { TopPad = 20, BottomPad = 6, Visible = false };
        _header.Visible = false;
        controls.Visible = false;
        _driving.Visible = false;
        options.Visible = false;
        activity.Visible = false;
        _setupItems = new Control[] { setupLead, _setup, setupHelp };
        _appItems = new Control[] { _header, controlsLead, controls, controlsHelp, drivingLead, _driving, drivingHelp, options, optionsHelp, overlayHelp, activityLead, activity };

        _page.Add(setupLead);
        _page.Add(_setup);
        _page.Add(setupHelp);
        _page.Add(_header);
        _page.Add(controlsLead);
        _page.Add(controls);
        _page.Add(controlsHelp);
        _page.Add(drivingLead);
        _page.Add(_driving);
        _page.Add(drivingHelp);
        _page.Add(options);
        _page.Add(optionsHelp);
        _page.Add(overlayHelp);
        _page.Add(_adminGroup);
        _page.Add(activityLead);
        _page.Add(activity);
        Controls.Add(_page);
        Controls.Add(_bar);
        _bar.CloseClicked += (_, _) => Close();
        _bar.MinimizeClicked += (_, _) => WindowState = FormWindowState.Minimized;
        _bar.ZoomClicked += (_, _) => WindowState = WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized;

        _cruiseRow.Tapped += (_, _) => BeginCapture(BindSlot.Cruise);
        _brakeRow.Tapped += (_, _) => BeginCapture(BindSlot.Brake);
        _increaseRow.Tapped += (_, _) => BeginCapture(BindSlot.Increase);
        _decreaseRow.Tapped += (_, _) => BeginCapture(BindSlot.Decrease);
        _units.SelectedIndexChanged += (_, _) => PushTuning();
        _ramp.ValueChanged += (_, _) =>
        {
            _ramp.ValueText = _ramp.Value.ToString("0.0", CultureInfo.InvariantCulture) + " m/s²";
            _ramp.Invalidate();
            PushTuning();
        };
        _pauseBrake.CheckedChanged += (_, _) => PushTuning();
        _pauseImpact.CheckedChanged += (_, _) => PushTuning();
        _overlay.CheckedChanged += (_, _) =>
        {
            if (!_overlay.Checked)
                StopMoving();
            PushTuning();
        };
        _adjust.Tapped += (_, _) =>
        {
            if (_movingOverlay)
            {
                StopMoving();
                return;
            }

            _movingOverlay = true;
            if (!_overlay.Checked)
            {
                _overlay.Checked = true;
                PushTuning();
            }

            _adjust.Title = "Done";
            _adjust.Invalidate();
            _badge.Editing = true;
        };
        _badge.PlacementChanged += SavePlacement;

        _timer.Interval = 50;
        _timer.Tick += OnTick;
        Load += OnLoad;
        FormClosed += (_, _) =>
        {
            _badge.Dispose();
            _cruise.Dispose();
        };
    }

    protected override CreateParams CreateParams
    {
        get
        {
            const int dropShadow = 0x00020000;
            var parameters = base.CreateParams;
            parameters.ClassStyle |= dropShadow;
            return parameters;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyWindowChrome();
        FitWorkArea();
        ApplyShape();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        ApplyWindowChrome();
    }

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        _bar.Active = true;
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        _bar.Active = false;
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        _bar.Zoomed = WindowState == FormWindowState.Maximized;
        FitWorkArea();
        ApplyShape();
    }

    private void FitWorkArea()
    {
        if (!IsHandleCreated)
            return;
        var area = Screen.FromHandle(Handle).WorkingArea;
        if (MaximizedBounds != area)
            MaximizedBounds = area;
    }

    private void ApplyShape()
    {
        if (!IsHandleCreated)
            return;
        if (WindowState == FormWindowState.Maximized)
        {
            SetWindowRgn(Handle, IntPtr.Zero, true);
            return;
        }

        var radius = (int)Math.Round(16f * DeviceDpi / 96f);
        var region = CreateRoundRectRgn(0, 0, Width + 1, Height + 1, radius * 2, radius * 2);
        SetWindowRgn(Handle, region, true);
    }

    private void ApplyWindowChrome()
    {
        var dark = 1;
        DwmSetWindowAttribute(Handle, 20, ref dark, sizeof(int));
        DwmSetWindowAttribute(Handle, 19, ref dark, sizeof(int));
        var square = 1;
        DwmSetWindowAttribute(Handle, 33, ref square, sizeof(int));
        var border = 0x001E1C1C;
        DwmSetWindowAttribute(Handle, 34, ref border, sizeof(int));
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x0084)
        {
            var packed = m.LParam.ToInt64();
            var screen = new Point((short)(packed & 0xFFFF), (short)((packed >> 16) & 0xFFFF));
            m.Result = (IntPtr)HitTest(PointToClient(screen));
            return;
        }

        if (m.Msg == 0x020A && ActiveControl is not TextBox)
        {
            var delta = (short)((m.WParam.ToInt64() >> 16) & 0xFFFF);
            var ticks = delta / 120;
            if (ticks == 0)
                ticks = Math.Sign(delta);
            _page.ScrollBy(-ticks * 72);
            return;
        }

        base.WndProc(ref m);
    }

    private int HitTest(Point client)
    {
        if (WindowState != FormWindowState.Maximized)
        {
            const int grip = 6;
            var left = client.X <= grip;
            var right = client.X >= ClientSize.Width - grip - 1;
            var top = client.Y <= grip;
            var bottom = client.Y >= ClientSize.Height - grip - 1;
            if (top && left)
                return 13;
            if (top && right)
                return 14;
            if (bottom && left)
                return 16;
            if (bottom && right)
                return 17;
            if (left)
                return 10;
            if (right)
                return 11;
            if (top)
                return 12;
            if (bottom)
                return 15;
        }

        if (client.Y >= 0 && client.Y < _bar.Height && !_bar.OverButton(client))
            return 2;
        return 1;
    }

    private void OnLoad(object? sender, EventArgs e)
    {
        var settings = _cruise.Settings;
        _units.SelectedIndex = settings.UseMph ? 1 : 0;
        _ramp.Value = Math.Clamp(settings.CatchUpPerSecond, 0.5f, 30f);
        _ramp.ValueText = _ramp.Value.ToString("0.0", CultureInfo.InvariantCulture) + " m/s²";
        _port.Value = Math.Clamp(settings.TelemetryPort, 1, 65535).ToString(CultureInfo.InvariantCulture);
        _pauseBrake.Checked = settings.PauseOnBrake;
        _pauseImpact.Checked = settings.PauseOnImpact;
        _overlay.Checked = settings.Overlay;
        _badge.SetPlacement(settings.OverlayRight, settings.OverlayBottom, settings.OverlayScale);
        _cruiseRow.Value = settings.ReadCruiseBind().Label;
        _brakeRow.Value = settings.ReadBrakeBind().Label;
        _increaseRow.Value = settings.ReadIncreaseBind().Label;
        _decreaseRow.Value = settings.ReadDecreaseBind().Label;
        _loading = false;
        _page.Reflow();
        _cruise.Start();
        _timer.Start();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (_cruise.Capturing)
            PollCapture();

        var dash = _cruise.ReadDashboard();
        var unit = dash.UseMph ? "mph" : "km/h";
        _header.SpeedText = dash.LiveSpeedMps.HasValue
            ? Math.Round(dash.UseMph ? dash.LiveSpeedMps.Value * 2.23693629f : dash.LiveSpeedMps.Value * 3.6f).ToString(CultureInfo.InvariantCulture)
            : "—";
        _header.UnitText = unit;
        _header.LockedText = dash.LockedSpeedMps.HasValue
            ? CruiseController.FormatSpeed(dash.LockedSpeedMps.Value, dash.UseMph)
            : "—";
        (_header.StatusText, _header.StatusColor) = dash.Phase switch
        {
            "Holding" => ("Holding", Ios.Green),
            "Brake" => ("Braking", Ios.Orange),
            "Ready" => ("Ready", Ios.Label),
            _ => ("Waiting", Ios.Secondary)
        };
        _header.Publish();

        if (dash.DataOutSeen && !_dataOutSeen)
            RevealApp();

        var showAdmin = _dataOutSeen && dash.NeedsAdmin && !_isAdmin;
        if (showAdmin != _adminShown)
        {
            _adminShown = showAdmin;
            _adminGroup.Visible = showAdmin;
            _page.Reflow();
        }

        _log.ShowText(dash.Log);
        var locked = dash.LockedSpeedMps.HasValue
            ? Math.Round(dash.UseMph ? dash.LockedSpeedMps.Value * 2.23693629f : dash.LockedSpeedMps.Value * 3.6f).ToString(CultureInfo.InvariantCulture)
            : "";
        var showBadge = _overlay.Checked && (_movingOverlay || (locked.Length > 0 && _cruise.SpeedometerVisible()));
        _badge.Present(showBadge, _cruise.GameProcessId, locked.Length > 0 ? locked : "100", unit);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape && _movingOverlay && !_cruise.Capturing)
        {
            StopMoving();
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }

    private void StopMoving()
    {
        if (!_movingOverlay && !_badge.Editing)
            return;
        _movingOverlay = false;
        _adjust.Title = "Move overlay";
        _adjust.Invalidate();
        _badge.Editing = false;
    }

    private void SavePlacement(float right, float bottom, float scale)
    {
        var settings = _cruise.Settings;
        settings.OverlayRight = right;
        settings.OverlayBottom = bottom;
        settings.OverlayScale = scale;
        SettingsStore.Save(settings);
    }

    private void RevealApp()
    {
        _dataOutSeen = true;
        foreach (var item in _setupItems)
            item.Visible = false;
        foreach (var item in _appItems)
            item.Visible = true;
        _adminGroup.Visible = false;
        _adminShown = false;
        MinimumSize = new Size(460, 640);
        if (Height < 700)
            Size = new Size(Math.Max(Width, 560), 860);
        _page.Reflow();
    }

    private void PollCapture()
    {
        var current = InputReader.Poll();
        if (_capturePrevious == null)
        {
            _capturePrevious = current;
            return;
        }

        var result = InputReader.TryCapture(_capturePrevious, current, out var bind);
        _capturePrevious = current;
        if (result == CaptureResult.None)
            return;

        var slot = _captureSlot;
        EndCapture(restore: true);
        if (result == CaptureResult.Cancel || bind == null)
            return;

        var row = RowFor(slot);
        row.Value = bind.Label;
        row.ValueColor = Ios.Secondary;
        row.Publish();
        switch (slot)
        {
            case BindSlot.Cruise:
                _cruise.SetCruiseBind(bind);
                break;
            case BindSlot.Brake:
                _cruise.SetBrakeBind(bind);
                break;
            case BindSlot.Increase:
                _cruise.SetIncreaseBind(bind);
                break;
            case BindSlot.Decrease:
                _cruise.SetDecreaseBind(bind);
                break;
        }
    }

    private void BeginCapture(BindSlot slot)
    {
        if (_cruise.Capturing)
            EndCapture(restore: true);
        _captureSlot = slot;
        _capturePrevious = null;
        _cruise.Capturing = true;
        var row = RowFor(slot);
        _captureRestore = row.Value;
        row.Value = "Press a key";
        row.ValueColor = Ios.Blue;
        row.Publish();
    }

    private void EndCapture(bool restore)
    {
        if (_cruise.Capturing && restore)
        {
            var row = RowFor(_captureSlot);
            row.Value = _captureRestore;
            row.ValueColor = Ios.Secondary;
            row.Publish();
        }

        _cruise.Capturing = false;
        _capturePrevious = null;
    }

    private ChevronRow RowFor(BindSlot slot)
    {
        return slot switch
        {
            BindSlot.Cruise => _cruiseRow,
            BindSlot.Brake => _brakeRow,
            BindSlot.Increase => _increaseRow,
            _ => _decreaseRow
        };
    }

    private void PushTuning()
    {
        if (_loading)
            return;
        _cruise.UpdateTuning(
            _units.SelectedIndex == 1,
            _ramp.Value,
            CruiseMath.DefaultMaxSpeed,
            _pauseBrake.Checked,
            _pauseImpact.Checked,
            _overlay.Checked);
    }

    private static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static void RestartElevated()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = Application.ExecutablePath,
                UseShellExecute = true,
                Verb = "runas"
            });
            Application.Exit();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Cruise", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(IntPtr window, IntPtr region, bool redraw);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int widthEllipse, int heightEllipse);
}
