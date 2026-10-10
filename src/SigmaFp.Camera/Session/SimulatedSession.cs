using System.Diagnostics;
using SigmaFp.Camera.Sigma;

namespace SigmaFp.Camera.Session;

/// <summary>
/// Simulated fp L for tests and development: models exposure time, processing, the 300 s bulb
/// limit, the capture database, chunked download and the "abandoned bulb" lock-up. Supports
/// fault injection. Thread-safe enough for tests (the controller only calls it from its worker).
/// </summary>
public sealed class SimulatedSession : ICameraSession
{
    public sealed class Options
    {
        /// <summary>Real time per simulated second (e.g. 0.01 = 100x faster).</summary>
        public double TimeScale { get; set; } = 1.0;
        public double ProcessingSeconds { get; set; } = 3.0;
        public double BulbLimitSeconds { get; set; } = 300.0;
        public int ImageSize { get; set; } = 1_000_000;
    }

    readonly Options _o;
    readonly SimulatedWorld _world;
    object _lock => _world.Db;
    Dictionary<byte, SimulatedWorld.Shot> _shots => _world.Shots;
    ref byte _head => ref _world.Head;
    ref byte _tail => ref _world.Tail;

    bool _open;
    byte _shutter, _iso;

    /// <summary>Called before every operation with its name; throw to inject a fault.</summary>
    public Action<string>? BeforeOp { get; set; }

    /// <summary>Operation names in call order. Written by the camera worker: read it via <see cref="HasCalled"/> or <see cref="CallsCopy"/> while it runs.</summary>
    public List<string> Calls { get; } = new();

    public bool HasCalled(string op) { lock (_lock) return Calls.Contains(op); }
    public List<string> CallsCopy() { lock (_lock) return [.. Calls]; }

    public SimulatedSession(SimulatedWorld world, Options? options = null)
    {
        _world = world;
        _o = options ?? new Options();
    }

    double Now => _world.Clock.Elapsed.TotalSeconds / _o.TimeScale;

    void Op(string name)
    {
        lock (_lock) Calls.Add(name);
        BeforeOp?.Invoke(name);
        if (!_world.Present) throw new CameraCommException($"{name}: device not present (simulated USB loss)");
        if (name != nameof(Open) && !_open) throw new NotConnectedException("simulated session not open");
    }

    public CameraInfo Open()
    {
        Op(nameof(Open));
        _open = true;
        _world.Sessions++;
        return new CameraInfo("SIGMA fp L (simulated)", "SIM0001", "sim");
    }

    public void Close()
    {
        Op(nameof(Close));
        lock (_lock)
        {
            // Closing while the camera still holds a capture (exposing, processing or an uncollected image) leaves it
            // refusing captures until power-cycled (seen on hardware, 2026-10-04). Closing resets the capture database.
            if (_shots.Values.Any(s => !s.Failed)) _world.LockedUp = true;
            _shots.Clear();
            _head = _tail = 0;
            _open = false;
        }
    }

    public void ApplySettings(byte shutterCode, byte isoCode)
    {
        Op(nameof(ApplySettings));
        (_shutter, _iso) = (shutterCode, isoCode);
    }

    public byte PrepareCapture()
    {
        Op(nameof(PrepareCapture));
        lock (_lock)
        {
            _shots.Clear();
            _head = _tail;
            return _tail;
        }
    }

    public void Snap()
    {
        Op(nameof(Snap));
        lock (_lock)
        {
            double exposure = _shutter == SigmaMessages.ShutterBulb ? _o.BulbLimitSeconds : ShutterSeconds(_shutter);
            _shots[_tail++] = new SimulatedWorld.Shot { Start = Now, End = Now + exposure, Failed = _world.LockedUp || BusyExposing() };
        }
    }

    /// <summary>External release (relay) closes/opens the shutter contact.</summary>
    public void ExternalRelease(bool closed)
    {
        lock (_lock)
        {
            if (closed)
            {
                if (_shutter != SigmaMessages.ShutterBulb || !_open) return; // camera ignores it
                _shots[_tail++] = new SimulatedWorld.Shot { Start = Now, External = true, Failed = _world.LockedUp };
            }
            else
            {
                foreach (var s in _shots.Values.Where(s => s.External && s.End is null))
                    s.End = Math.Min(Now, s.Start + _o.BulbLimitSeconds);
            }
        }
    }

    public CaptStatus GetStatus(byte imageId)
    {
        Op(nameof(GetStatus));
        lock (_lock)
        {
            ushort st = _shots.TryGetValue(imageId, out var s) ? Status(s) : CaptStatusCode.Cleared;
            return new CaptStatus(imageId, _head, _tail, st);
        }
    }

    public PictFileInfo GetImageInfo()
    {
        Op(nameof(GetImageInfo));
        lock (_lock)
        {
            bool any = _shots.Values.Any(s => Status(s) == CaptStatusCode.ImageGenCompleted);
            return any
                ? new PictFileInfo(0x734F7C00, (uint)_o.ImageSize, "DNG", 9536, 6344, "100SIGMA", "SDIM0001.DNG")
                : throw new CameraProtocolException("No image file information (8 bytes)");
        }
    }

    public ReadOnlyMemory<byte> ReadImageChunk(uint address, uint offset, uint length)
    {
        Op(nameof(ReadImageChunk));
        int n = (int)Math.Min(length, (uint)_o.ImageSize - offset);
        var b = new byte[n];
        for (int i = 0; i < n; i++) b[i] = (byte)((offset + i) * 31 + 7);
        if (offset == 0 && n >= 4) { b[0] = 0x49; b[1] = 0x49; b[2] = 0x2A; b[3] = 0x00; }
        return b;
    }

    public void ClearImage(byte imageId)
    {
        Op(nameof(ClearImage));
        lock (_lock) { _shots.Remove(imageId); _head = (byte)(imageId + 1); }
    }

    public void Heartbeat() => Op(nameof(Heartbeat));

    public void Dispose() { }

    /// <summary>Expected image bytes for verification in tests.</summary>
    public static byte ExpectedByte(int index) => index < 4 ? (byte)(new byte[] { 0x49, 0x49, 0x2A, 0x00 }[index]) : (byte)(index * 31 + 7);

    bool BusyExposing() => _shots.Values.Any(s => !s.Failed && Status(s) == CaptStatusCode.ShootInProgress);

    ushort Status(SimulatedWorld.Shot s)
    {
        if (s.Failed) return CaptStatusCode.ImageGenFailed;
        double now = Now;
        if (s.End is null)
        {
            if (now - s.Start < _o.BulbLimitSeconds) return CaptStatusCode.ShootInProgress;
            s.End = s.Start + _o.BulbLimitSeconds;
        }
        if (now < s.End) return CaptStatusCode.ShootInProgress;
        return now < s.End + _o.ProcessingSeconds ? CaptStatusCode.ImageGenInProgress : CaptStatusCode.ImageGenCompleted;
    }

    static double ShutterSeconds(byte code) => code switch
    {
        16 => 30, 29 => 10, 48 => 2, 56 => 1,
        _ => Math.Pow(2, (56 - code) / 8.0), // APEX: 8 steps per stop, 56 = 1 s
    };
}

/// <summary>State shared by all simulated sessions: the "physical" camera.</summary>
public sealed class SimulatedWorld
{
    /// <summary>False = camera unplugged / USB lost.</summary>
    public volatile bool Present = true;
    /// <summary>Set when a session was closed while the camera held a capture; cleared by <see cref="PowerCycle"/>.</summary>
    public volatile bool LockedUp;
    public int Sessions;

    // The camera's capture database survives a USB drop (no session close) and is shared by all sessions.
    internal readonly object Db = new();
    internal readonly Stopwatch Clock = Stopwatch.StartNew();
    internal readonly Dictionary<byte, Shot> Shots = new();
    internal byte Head, Tail;

    internal sealed class Shot
    {
        public double Start;
        public double? End;      // simulated seconds when the shutter closes (null = open, external)
        public bool External;
        public bool Failed;
    }
    public void PowerCycle() { lock (Db) { LockedUp = false; Shots.Clear(); Head = Tail = 0; } }
}
