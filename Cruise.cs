using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Fh6Cruise;

internal sealed class CruiseMemory
{
    public long LastTickUtc;
    public bool HasPrevious;
    public float PreviousSpeed;
    public float PreviousHeadingX;
    public float PreviousHeadingZ;
    public bool HeadingFlipped;
    public long SuppressUntilTicks;
}

internal static class CruiseMath
{
    public const float DefaultRampPerSecond = 45f;
    public const float DefaultCatchUpPerSecond = 4f;
    public const float DefaultMaxSpeed = 250f;

    public static bool Step(
        float vx,
        float vy,
        float vz,
        float targetMps,
        float rampPerSecond,
        float catchUpPerSecond,
        float maxSpeed,
        bool brakeHeld,
        bool pauseOnImpact,
        long nowTicks,
        CruiseMemory memory,
        out float newVx,
        out float newVz)
    {
        newVx = vx;
        newVz = vz;
        if (!Plausible(vx, vy, vz))
            return false;

        var dt = AdvanceClock(memory, nowTicks);
        var speed = HorizontalSpeed(vx, vz);
        if (speed < 1f)
        {
            if (pauseOnImpact && memory.HasPrevious && memory.PreviousSpeed > 3f)
                memory.SuppressUntilTicks = nowTicks + TimeSpan.TicksPerMillisecond * 450;
            memory.HasPrevious = false;
            return false;
        }

        var headingX = vx / speed;
        var headingZ = vz / speed;
        Remember(memory, nowTicks, speed, headingX, headingZ, pauseOnImpact);
        if (brakeHeld || (pauseOnImpact && nowTicks < memory.SuppressUntilTicks))
            return false;

        var target = MathF.Min(targetMps, maxSpeed);
        if (!float.IsFinite(target) || target < 0f)
            return false;

        var next = Blend(speed, target, rampPerSecond, catchUpPerSecond, dt);
        if (MathF.Abs(next - speed) < 0.0005f)
            return false;

        newVx = headingX * next;
        newVz = headingZ * next;
        return float.IsFinite(newVx) && float.IsFinite(newVz);
    }

    public static float HorizontalSpeed(float vx, float vz)
    {
        if (!float.IsFinite(vx) || !float.IsFinite(vz))
            return 0f;
        return MathF.Sqrt((vx * vx) + (vz * vz));
    }

    private static bool Plausible(float vx, float vy, float vz)
    {
        if (!float.IsFinite(vx) || !float.IsFinite(vy) || !float.IsFinite(vz))
            return false;
        return (vx * vx) + (vz * vz) <= 2000f * 2000f;
    }

    private static double AdvanceClock(CruiseMemory memory, long nowTicks)
    {
        var previous = memory.LastTickUtc;
        memory.LastTickUtc = nowTicks;
        var dt = previous == 0 ? 0.016 : (nowTicks - previous) / (double)TimeSpan.TicksPerSecond;
        if (dt < 0.001)
            dt = 0.001;
        if (dt > 0.1)
            dt = 0.1;
        return dt;
    }

    private static void Remember(CruiseMemory memory, long nowTicks, float speed, float headingX, float headingZ, bool pauseOnImpact)
    {
        if (memory.HasPrevious)
        {
            var dot = (headingX * memory.PreviousHeadingX) + (headingZ * memory.PreviousHeadingZ);
            var impact = (memory.PreviousSpeed - speed) > 4f && speed < memory.PreviousSpeed * 0.6f;
            var flipped = dot < 0.2f;
            if (pauseOnImpact && (impact || (flipped && memory.HeadingFlipped)))
                memory.SuppressUntilTicks = nowTicks + TimeSpan.TicksPerMillisecond * 450;
            memory.HeadingFlipped = flipped;
        }
        else
        {
            memory.HeadingFlipped = false;
        }

        memory.HasPrevious = true;
        memory.PreviousSpeed = speed;
        memory.PreviousHeadingX = headingX;
        memory.PreviousHeadingZ = headingZ;
    }

    private static float Blend(float speed, float target, float rampPerSecond, float catchUpPerSecond, double dt)
    {
        if (speed < target)
            return MathF.Min(speed + catchUpPerSecond * (float)dt, target);
        if (speed > target)
            return MathF.Max(speed - rampPerSecond * (float)dt, target);
        return speed;
    }
}

internal sealed class AppSettings
{
    public string CruiseBind { get; set; } = "key:117";
    public string BrakeBind { get; set; } = "pad:lt";
    public string IncreaseBind { get; set; } = "key:118";
    public string DecreaseBind { get; set; } = "key:119";
    public bool UseMph { get; set; }
    public float RampPerSecond { get; set; } = CruiseMath.DefaultRampPerSecond;
    public float CatchUpPerSecond { get; set; } = CruiseMath.DefaultCatchUpPerSecond;
    public float MaxSpeedMps { get; set; } = CruiseMath.DefaultMaxSpeed;
    public bool PauseOnBrake { get; set; } = true;
    public bool PauseOnImpact { get; set; } = true;
    public bool Overlay { get; set; } = true;
    public float OverlayRight { get; set; } = 184f;
    public float OverlayBottom { get; set; } = 116f;
    public float OverlayScale { get; set; } = 1f;
    public int TelemetryPort { get; set; } = 20055;

    public InputBind ReadCruiseBind() => InputBind.Parse(CruiseBind);

    public InputBind ReadBrakeBind() => InputBind.Parse(BrakeBind);

    public InputBind ReadIncreaseBind() => InputBind.Parse(IncreaseBind);

    public InputBind ReadDecreaseBind() => InputBind.Parse(DecreaseBind);
}

internal static class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static string Path { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Fh6Cruise",
        "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(Path))
                return new AppSettings();
            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Path), Options);
            return Sanitize(settings ?? new AppSettings());
        }
        catch
        {
            return new AppSettings();
        }
    }

    public static void Save(AppSettings settings)
    {
        var directory = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        File.WriteAllText(Path, JsonSerializer.Serialize(Sanitize(settings), Options));
    }

    private static AppSettings Sanitize(AppSettings settings)
    {
        if (float.IsNaN(settings.RampPerSecond) || float.IsInfinity(settings.RampPerSecond))
            settings.RampPerSecond = CruiseMath.DefaultRampPerSecond;
        settings.RampPerSecond = Math.Clamp(settings.RampPerSecond, 1f, 200f);
        if (settings.CatchUpPerSecond < 0.5f || float.IsNaN(settings.CatchUpPerSecond) || float.IsInfinity(settings.CatchUpPerSecond))
            settings.CatchUpPerSecond = CruiseMath.DefaultCatchUpPerSecond;
        settings.CatchUpPerSecond = Math.Clamp(settings.CatchUpPerSecond, 0.5f, 30f);
        settings.MaxSpeedMps = CruiseMath.DefaultMaxSpeed;
        if (settings.TelemetryPort is < 1 or > 65535)
            settings.TelemetryPort = 20055;
        if (float.IsNaN(settings.OverlayRight) || float.IsInfinity(settings.OverlayRight))
            settings.OverlayRight = 184f;
        if (float.IsNaN(settings.OverlayBottom) || float.IsInfinity(settings.OverlayBottom))
            settings.OverlayBottom = 116f;
        if (float.IsNaN(settings.OverlayScale) || float.IsInfinity(settings.OverlayScale) || settings.OverlayScale <= 0f)
            settings.OverlayScale = 1f;
        settings.OverlayRight = Math.Clamp(settings.OverlayRight, -400f, 1600f);
        settings.OverlayBottom = Math.Clamp(settings.OverlayBottom, -400f, 1600f);
        settings.OverlayScale = Math.Clamp(settings.OverlayScale, 0.55f, 2.4f);
        if (string.IsNullOrWhiteSpace(settings.IncreaseBind))
            settings.IncreaseBind = "key:118";
        if (string.IsNullOrWhiteSpace(settings.DecreaseBind))
            settings.DecreaseBind = "key:119";
        settings.CruiseBind = InputBind.Parse(settings.CruiseBind).Encode();
        settings.BrakeBind = InputBind.Parse(settings.BrakeBind).Encode();
        settings.IncreaseBind = InputBind.Parse(settings.IncreaseBind).Encode();
        settings.DecreaseBind = InputBind.Parse(settings.DecreaseBind).Encode();
        return settings;
    }
}

internal readonly struct TelemetrySample
{
    public float SpeedMps { get; init; }
    public bool HasVector { get; init; }
}

internal sealed class TelemetryListener : IDisposable
{
    private readonly object _gate = new();
    private UdpClient? _client;
    private CancellationTokenSource? _cancel;
    private int _port;
    private TelemetrySample _sample;
    private DateTime _lastPacketUtc;
    private bool _seen;

    public void Start(int port)
    {
        lock (_gate)
        {
            if (_client != null && _port == port)
                return;
            Stop();
            _port = port;
            _cancel = new CancellationTokenSource();
            var token = _cancel.Token;
            _client = new UdpClient(new IPEndPoint(IPAddress.Any, port));
            _ = Task.Run(() => Listen(token));
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            try { _cancel?.Cancel(); } catch { }
            try { _client?.Close(); } catch { }
            _client = null;
            _cancel = null;
        }
    }

    public void Read(out TelemetrySample sample, out bool fresh, out bool seen)
    {
        lock (_gate)
        {
            sample = _sample;
            seen = _seen;
            fresh = _sample.HasVector && (DateTime.UtcNow - _lastPacketUtc).TotalSeconds < 1.0;
        }
    }

    private void Listen(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            UdpClient? client;
            lock (_gate)
                client = _client;
            if (client == null)
                return;
            try
            {
                var result = client.ReceiveAsync(token).AsTask().GetAwaiter().GetResult();
                if (TryReadPacket(result.Buffer, out var sample))
                {
                    lock (_gate)
                    {
                        _sample = sample;
                        _lastPacketUtc = DateTime.UtcNow;
                        _seen = true;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (Exception)
            {
                Thread.Sleep(200);
            }
        }
    }

    public static bool TryReadPacket(byte[] packet, out TelemetrySample sample)
    {
        sample = default;
        if (packet.Length < 44 ||
            !TryFloat(packet, 32, out var vx) ||
            !TryFloat(packet, 36, out _) ||
            !TryFloat(packet, 40, out var vz))
            return false;

        var speed = MathF.Sqrt((vx * vx) + (vz * vz));
        if (!IsPlausible(speed))
            return false;

        sample = new TelemetrySample
        {
            SpeedMps = speed,
            HasVector = true
        };
        return true;
    }

    private static bool TryFloat(byte[] packet, int offset, out float value)
    {
        value = 0f;
        if (offset < 0 || offset + 4 > packet.Length)
            return false;
        value = BitConverter.ToSingle(packet, offset);
        return !float.IsNaN(value) && !float.IsInfinity(value);
    }

    private static bool IsPlausible(float speed) => speed >= -5f && speed <= 1500f;

    public void Dispose() => Stop();
}

internal enum BindSource
{
    Key,
    PadButton,
    LeftTrigger,
    RightTrigger
}

internal sealed class InputBind
{
    public BindSource Source { get; init; }
    public int Code { get; init; }

    public static InputBind Key(int virtualKey) => new() { Source = BindSource.Key, Code = virtualKey };

    public static InputBind PadButton(int mask) => new() { Source = BindSource.PadButton, Code = mask };

    public static InputBind LeftTrigger() => new() { Source = BindSource.LeftTrigger };

    public static InputBind RightTrigger() => new() { Source = BindSource.RightTrigger };

    public string Encode()
    {
        return Source switch
        {
            BindSource.Key => "key:" + Code.ToString(System.Globalization.CultureInfo.InvariantCulture),
            BindSource.PadButton => "pad:" + Code.ToString(System.Globalization.CultureInfo.InvariantCulture),
            BindSource.LeftTrigger => "pad:lt",
            BindSource.RightTrigger => "pad:rt",
            _ => "key:117"
        };
    }

    public static InputBind Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return Key(0x75);
        if (string.Equals(text, "pad:lt", StringComparison.OrdinalIgnoreCase))
            return LeftTrigger();
        if (string.Equals(text, "pad:rt", StringComparison.OrdinalIgnoreCase))
            return RightTrigger();
        if (text.StartsWith("key:", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(text.AsSpan(4), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var key))
            return Key(key);
        if (text.StartsWith("pad:", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(text.AsSpan(4), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var button))
            return PadButton(button);
        return Key(0x75);
    }

    public bool IsDown(InputSnapshot snapshot)
    {
        return Source switch
        {
            BindSource.Key => snapshot.KeyDown(Code),
            BindSource.PadButton => snapshot.ButtonDown(Code),
            BindSource.LeftTrigger => snapshot.LeftTrigger,
            BindSource.RightTrigger => snapshot.RightTrigger,
            _ => false
        };
    }

    public string Label => Source switch
    {
        BindSource.Key => KeyName(Code),
        BindSource.PadButton => ButtonName(Code),
        BindSource.LeftTrigger => "LT",
        BindSource.RightTrigger => "RT",
        _ => "Unbound"
    };

    public static string KeyName(int virtualKey)
    {
        if (virtualKey >= 0x70 && virtualKey <= 0x87)
            return "F" + (virtualKey - 0x6F).ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (virtualKey is >= 0x30 and <= 0x39 || virtualKey is >= 0x41 and <= 0x5A)
            return ((char)virtualKey).ToString();
        return virtualKey switch
        {
            0x08 => "Backspace",
            0x09 => "Tab",
            0x0D => "Enter",
            0x13 => "Pause",
            0x14 => "Caps Lock",
            0x1B => "Esc",
            0x20 => "Space",
            0x21 => "Page Up",
            0x22 => "Page Down",
            0x23 => "End",
            0x24 => "Home",
            0x25 => "Left",
            0x26 => "Up",
            0x27 => "Right",
            0x28 => "Down",
            0x2D => "Insert",
            0x2E => "Delete",
            0x5B => "Left Win",
            0x5C => "Right Win",
            0x60 => "Num 0",
            0x61 => "Num 1",
            0x62 => "Num 2",
            0x63 => "Num 3",
            0x64 => "Num 4",
            0x65 => "Num 5",
            0x66 => "Num 6",
            0x67 => "Num 7",
            0x68 => "Num 8",
            0x69 => "Num 9",
            0x6A => "Num *",
            0x6B => "Num +",
            0x6D => "Num -",
            0x6E => "Num .",
            0x6F => "Num /",
            0x90 => "Num Lock",
            0x91 => "Scroll Lock",
            0xA0 => "Left Shift",
            0xA1 => "Right Shift",
            0xA2 => "Left Ctrl",
            0xA3 => "Right Ctrl",
            0xA4 => "Left Alt",
            0xA5 => "Right Alt",
            0xBA => ";",
            0xBB => "=",
            0xBC => ",",
            0xBD => "-",
            0xBE => ".",
            0xBF => "/",
            0xC0 => "`",
            0xDB => "[",
            0xDC => "\\",
            0xDD => "]",
            0xDE => "'",
            _ => "Key " + virtualKey.ToString(System.Globalization.CultureInfo.InvariantCulture)
        };
    }

    public static string ButtonName(int mask)
    {
        return mask switch
        {
            0x0001 => "D-pad Up",
            0x0002 => "D-pad Down",
            0x0004 => "D-pad Left",
            0x0008 => "D-pad Right",
            0x0010 => "Start",
            0x0020 => "Back",
            0x0040 => "L3",
            0x0080 => "R3",
            0x0100 => "LB",
            0x0200 => "RB",
            0x1000 => "A",
            0x2000 => "B",
            0x4000 => "X",
            0x8000 => "Y",
            _ => "Button " + mask.ToString("X", System.Globalization.CultureInfo.InvariantCulture)
        };
    }
}

internal enum CaptureResult
{
    None,
    Cancel,
    Bound
}

internal readonly struct PadState
{
    public bool Connected { get; init; }
    public ushort Buttons { get; init; }
    public byte LeftTrigger { get; init; }
    public byte RightTrigger { get; init; }
}

internal sealed class InputSnapshot
{
    public const byte TriggerThreshold = 80;
    private readonly bool[] _keys = new bool[256];
    private readonly PadState[] _pads;

    public InputSnapshot(bool[] keys, PadState[] pads)
    {
        Array.Copy(keys, _keys, Math.Min(keys.Length, _keys.Length));
        _pads = pads;
    }

    public bool KeyDown(int virtualKey)
    {
        return virtualKey is > 0 and < 256 && _keys[virtualKey];
    }

    public bool ButtonDown(int mask)
    {
        foreach (var pad in _pads)
        {
            if (pad.Connected && (pad.Buttons & mask) != 0)
                return true;
        }
        return false;
    }

    public bool LeftTrigger => _pads.Any(pad => pad.Connected && pad.LeftTrigger >= TriggerThreshold);

    public bool RightTrigger => _pads.Any(pad => pad.Connected && pad.RightTrigger >= TriggerThreshold);

    public IReadOnlyList<PadState> Pads => _pads;
}

internal static class InputReader
{
    private static XInputGetStateDelegate? _getState;

    private delegate uint XInputGetStateDelegate(uint userIndex, out XInputState state);

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputGamepad
    {
        public ushort Buttons;
        public byte LeftTrigger;
        public byte RightTrigger;
        public short ThumbLX;
        public short ThumbLY;
        public short ThumbRX;
        public short ThumbRY;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputState
    {
        public uint PacketNumber;
        public XInputGamepad Gamepad;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadLibrary(string fileName);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi)]
    private static extern IntPtr GetProcAddress(IntPtr module, string name);

    public static InputSnapshot Poll()
    {
        var keys = new bool[256];
        for (var vk = 1; vk < 256; vk++)
            keys[vk] = (Native.GetAsyncKeyState(vk) & unchecked((short)0x8000)) != 0;

        var pads = new PadState[4];
        var getState = GetXInput();
        if (getState != null)
        {
            for (uint i = 0; i < 4; i++)
            {
                if (getState(i, out var state) != 0)
                    continue;
                pads[i] = new PadState
                {
                    Connected = true,
                    Buttons = state.Gamepad.Buttons,
                    LeftTrigger = state.Gamepad.LeftTrigger,
                    RightTrigger = state.Gamepad.RightTrigger
                };
            }
        }

        return new InputSnapshot(keys, pads);
    }

    public static CaptureResult TryCapture(InputSnapshot previous, InputSnapshot current, out InputBind? bind)
    {
        bind = null;
        for (var vk = 8; vk <= 0xFE; vk++)
        {
            if (IsIgnoredKey(vk))
                continue;
            if (!previous.KeyDown(vk) && current.KeyDown(vk))
            {
                if (vk == 0x1B)
                    return CaptureResult.Cancel;
                bind = InputBind.Key(vk);
                return CaptureResult.Bound;
            }
        }

        var previousPads = previous.Pads;
        var currentPads = current.Pads;
        var count = Math.Min(previousPads.Count, currentPads.Count);
        for (var i = 0; i < count; i++)
        {
            var before = previousPads[i];
            var after = currentPads[i];
            if (!after.Connected)
                continue;
            var pressed = after.Buttons & ~before.Buttons;
            if (pressed != 0)
            {
                var bit = pressed & -pressed;
                bind = InputBind.PadButton(bit);
                return CaptureResult.Bound;
            }

            var beforeLeft = before.Connected && before.LeftTrigger >= InputSnapshot.TriggerThreshold;
            var afterLeft = after.LeftTrigger >= InputSnapshot.TriggerThreshold;
            if (!beforeLeft && afterLeft)
            {
                bind = InputBind.LeftTrigger();
                return CaptureResult.Bound;
            }

            var beforeRight = before.Connected && before.RightTrigger >= InputSnapshot.TriggerThreshold;
            var afterRight = after.RightTrigger >= InputSnapshot.TriggerThreshold;
            if (!beforeRight && afterRight)
            {
                bind = InputBind.RightTrigger();
                return CaptureResult.Bound;
            }
        }

        return CaptureResult.None;
    }

    private static bool IsIgnoredKey(int virtualKey)
    {
        return virtualKey is >= 0x01 and <= 0x06
            or 0x10 or 0x11 or 0x12
            or 0x5B or 0x5C;
    }

    private static XInputGetStateDelegate? GetXInput()
    {
        if (_getState != null)
            return _getState;
        foreach (var library in new[] { "xinput1_4.dll", "xinput1_3.dll", "xinput9_1_0.dll" })
        {
            var module = LoadLibrary(library);
            if (module == IntPtr.Zero)
                continue;
            var address = GetProcAddress(module, "XInputGetState");
            if (address == IntPtr.Zero)
                continue;
            _getState = Marshal.GetDelegateForFunctionPointer<XInputGetStateDelegate>(address);
            return _getState;
        }
        return null;
    }
}

internal sealed class CruiseController : IDisposable
{
    private readonly object _gate = new();
    private readonly TelemetryListener _telemetry = new();
    private readonly GameLane _lane = new();
    private readonly CruiseMemory _memory = new();
    private readonly List<string> _log = new();
    private Thread? _thread;
    private volatile bool _run;
    private volatile bool _capturing;
    private bool _holding;
    private bool _cruiseLatch;
    private bool _cruiseWasDown;
    private float? _lockedMps;
    private float? _liveMps;
    private string _phase = "Idle";
    private bool _needsAdmin;
    private bool _upWasDown;
    private bool _downWasDown;
    private bool _upLatch;
    private bool _downLatch;
    private long _upNextTicks;
    private long _downNextTicks;

    public AppSettings Settings { get; } = SettingsStore.Load();

    public bool Capturing
    {
        get => _capturing;
        set => _capturing = value;
    }

    public void Start()
    {
        try
        {
            _telemetry.Start(Settings.TelemetryPort);
        }
        catch (Exception ex)
        {
            Log("Data Out did not start. " + ex.Message);
        }

        _run = true;
        _thread = new Thread(Loop) { IsBackground = true, Name = "Cruise" };
        _thread.Start();
        Log("Ready. Press the cruise bind while driving to lock that speed.");
    }

    public void SetCruiseBind(InputBind bind)
    {
        lock (_gate)
        {
            Settings.CruiseBind = bind.Encode();
            _cruiseLatch = true;
            _cruiseWasDown = true;
        }
        SettingsStore.Save(Settings);
        Log("Cruise bind set to " + bind.Label + ".");
    }

    public void SetBrakeBind(InputBind bind)
    {
        lock (_gate)
            Settings.BrakeBind = bind.Encode();
        SettingsStore.Save(Settings);
        Log("Brake bind set to " + bind.Label + ".");
    }

    public void SetIncreaseBind(InputBind bind)
    {
        lock (_gate)
        {
            Settings.IncreaseBind = bind.Encode();
            _upLatch = true;
            _upWasDown = true;
        }
        SettingsStore.Save(Settings);
        Log("Speed up bind set to " + bind.Label + ".");
    }

    public void SetDecreaseBind(InputBind bind)
    {
        lock (_gate)
        {
            Settings.DecreaseBind = bind.Encode();
            _downLatch = true;
            _downWasDown = true;
        }
        SettingsStore.Save(Settings);
        Log("Speed down bind set to " + bind.Label + ".");
    }

    public int GameProcessId => _lane.ProcessId;

    public bool SpeedometerVisible() => _lane.SpeedometerVisible();

    public void UpdateTuning(bool useMph, float catchUp, float maxMps, bool pauseOnBrake, bool pauseOnImpact, bool overlay)
    {
        lock (_gate)
        {
            Settings.UseMph = useMph;
            Settings.CatchUpPerSecond = Math.Clamp(catchUp, 0.5f, 30f);
            Settings.MaxSpeedMps = Math.Clamp(maxMps, 5f, CruiseMath.DefaultMaxSpeed);
            Settings.PauseOnBrake = pauseOnBrake;
            Settings.PauseOnImpact = pauseOnImpact;
            Settings.Overlay = overlay;
        }
        SettingsStore.Save(Settings);
    }

    public Dashboard ReadDashboard()
    {
        lock (_gate)
        {
            _telemetry.Read(out var sample, out var telemetryFresh, out var dataOutSeen);
            return new Dashboard
            {
                Phase = _phase,
                LiveSpeedMps = _liveMps ?? (telemetryFresh ? sample.SpeedMps : null),
                LockedSpeedMps = _holding ? _lockedMps : null,
                NeedsAdmin = _needsAdmin || _lane.NeedsAdmin,
                UseMph = Settings.UseMph,
                DataOutSeen = dataOutSeen,
                Log = string.Join(Environment.NewLine, _log)
            };
        }
    }

    private void Loop()
    {
        var previous = new InputSnapshot(new bool[256], Array.Empty<PadState>());
        while (_run)
        {
            try
            {
                Tick(previous);
                previous = InputReader.Poll();
            }
            catch (Exception ex)
            {
                Log(ex.Message);
            }
            Thread.Sleep(16);
        }
    }

    private void Tick(InputSnapshot previous)
    {
        var current = previous;
        AppSettings settings;
        lock (_gate)
            settings = CloneSettings();

        _lane.Poll();
        current = InputReader.Poll();
        var nowTicks = DateTime.UtcNow.Ticks;
        if (!_capturing)
        {
            var down = settings.ReadCruiseBind().IsDown(current);
            if (_cruiseLatch)
            {
                if (!down)
                    _cruiseLatch = false;
            }
            else if (down && !_cruiseWasDown)
            {
                Toggle(settings, nowTicks);
            }
            _cruiseWasDown = down;
            PollAdjust(current, settings, nowTicks, settings.ReadIncreaseBind(), 1, ref _upWasDown, ref _upLatch, ref _upNextTicks);
            PollAdjust(current, settings, nowTicks, settings.ReadDecreaseBind(), -1, ref _downWasDown, ref _downLatch, ref _downNextTicks);
        }

        _telemetry.Read(out var sample, out var fresh, out _);
        var userBraking = settings.PauseOnBrake && settings.ReadBrakeBind().IsDown(current);
        ApplyHold(settings, sample, fresh, userBraking, nowTicks);
    }

    private void ApplyHold(AppSettings settings, TelemetrySample sample, bool fresh, bool userBraking, long nowTicks)
    {
        if (_lane.TryReadSpeed(out var carSpeed))
        {
            lock (_gate)
                _liveMps = carSpeed;
        }
        else if (fresh)
        {
            lock (_gate)
                _liveMps = sample.SpeedMps;
        }

        if (_holding && !_lane.IsHooked)
        {
            lock (_gate)
            {
                _holding = false;
                _lockedMps = null;
                _phase = "Idle";
            }
            Log("Cruise off. The game connection dropped.");
            return;
        }

        if (!_holding || !_lockedMps.HasValue)
        {
            lock (_gate)
                _phase = _lane.IsHooked || fresh ? "Ready" : "Idle";
            return;
        }

        lock (_gate)
            _needsAdmin = _lane.NeedsAdmin;

        float? locked;
        lock (_gate)
            locked = _lockedMps;

        if (locked.HasValue &&
            _lane.TryHold(locked.Value, settings.RampPerSecond, settings.CatchUpPerSecond, settings.MaxSpeedMps, userBraking, settings.PauseOnImpact, nowTicks, _memory, out var speed))
        {
            lock (_gate)
            {
                _liveMps = speed;
                _phase = userBraking ? "Brake" : "Holding";
            }
            return;
        }

        lock (_gate)
            _phase = userBraking ? "Brake" : "Holding";
    }

    private void Toggle(AppSettings settings, long nowTicks)
    {
        if (_holding)
        {
            lock (_gate)
            {
                _holding = false;
                _lockedMps = null;
                _phase = "Ready";
            }
            Log("Cruise off.");
            return;
        }

        try
        {
            if (!_lane.IsHooked)
                Log("Connecting to the car.");
            _lane.EnsureHook();
        }
        catch (Exception ex)
        {
            Log(ex.Message);
            return;
        }

        float speed = 0;
        var until = DateTime.UtcNow.Ticks + TimeSpan.TicksPerMillisecond * 1500;
        var got = false;
        while (DateTime.UtcNow.Ticks < until)
        {
            if (_lane.TryReadSpeed(out speed) && speed >= 1f)
            {
                got = true;
                break;
            }
            Thread.Sleep(16);
        }

        if (!got)
        {
            Log("Get in a car and get it moving, then press the bind again.");
            return;
        }

        lock (_gate)
        {
            _memory.LastTickUtc = 0;
            _memory.HasPrevious = false;
            _memory.SuppressUntilTicks = 0;
            _lockedMps = speed;
            _holding = true;
            _phase = "Holding";
        }

        Log("Locked " + FormatSpeed(speed, settings.UseMph) + ".");
    }

    private void PollAdjust(InputSnapshot current, AppSettings settings, long nowTicks, InputBind bind, int direction, ref bool wasDown, ref bool latch, ref long nextTicks)
    {
        var down = bind.IsDown(current);
        if (latch)
        {
            if (!down)
                latch = false;
            wasDown = down;
            return;
        }

        if (!down)
        {
            wasDown = false;
            return;
        }

        var first = !wasDown;
        wasDown = true;
        if (!first && nowTicks < nextTicks)
            return;
        nextTicks = nowTicks + TimeSpan.TicksPerMillisecond * (first ? 420 : 170);
        AdjustLocked(settings, direction, first);
    }

    private void AdjustLocked(AppSettings settings, int direction, bool firstPress)
    {
        float updated;
        lock (_gate)
        {
            if (!_holding || !_lockedMps.HasValue)
            {
                if (firstPress)
                    Log("Lock a speed first, then use the +5 and -5 binds.");
                return;
            }

            var scale = settings.UseMph ? 2.23693629f : 3.6f;
            var display = _lockedMps.Value * scale;
            var limit = Math.Max(5f, settings.MaxSpeedMps * scale);
            var next = Math.Clamp(display + (direction * 5f), 5f, limit);
            _lockedMps = next / scale;
            updated = _lockedMps.Value;
        }

        Log("Locked speed " + FormatSpeed(updated, settings.UseMph) + ".");
    }

    private AppSettings CloneSettings()
    {
        return new AppSettings
        {
            CruiseBind = Settings.CruiseBind,
            BrakeBind = Settings.BrakeBind,
            IncreaseBind = Settings.IncreaseBind,
            DecreaseBind = Settings.DecreaseBind,
            UseMph = Settings.UseMph,
            RampPerSecond = Settings.RampPerSecond,
            CatchUpPerSecond = Settings.CatchUpPerSecond,
            MaxSpeedMps = Settings.MaxSpeedMps,
            PauseOnBrake = Settings.PauseOnBrake,
            PauseOnImpact = Settings.PauseOnImpact,
            Overlay = Settings.Overlay,
            OverlayRight = Settings.OverlayRight,
            OverlayBottom = Settings.OverlayBottom,
            OverlayScale = Settings.OverlayScale,
            TelemetryPort = Settings.TelemetryPort
        };
    }

    private void Log(string message)
    {
        var line = DateTime.Now.ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture) + "  " + message;
        lock (_gate)
        {
            _log.Add(line);
            if (_log.Count > 8)
                _log.RemoveAt(0);
        }
    }

    public static string FormatSpeed(float mps, bool useMph)
    {
        var value = useMph ? mps * 2.23693629f : mps * 3.6f;
        return Math.Round(value).ToString(System.Globalization.CultureInfo.InvariantCulture) + (useMph ? " mph" : " km/h");
    }

    public void Dispose()
    {
        _run = false;
        _thread?.Join(2000);
        _lane.Dispose();
        _telemetry.Dispose();
    }
}

internal sealed class Dashboard
{
    public string Phase { get; init; } = "Idle";
    public float? LiveSpeedMps { get; init; }
    public float? LockedSpeedMps { get; init; }
    public bool NeedsAdmin { get; init; }
    public bool UseMph { get; init; }
    public bool DataOutSeen { get; init; }
    public string Log { get; init; } = "";
}
