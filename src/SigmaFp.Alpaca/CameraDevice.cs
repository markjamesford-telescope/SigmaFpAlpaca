using ASCOM.Common.Alpaca;
using AscomCameraState = ASCOM.Common.DeviceInterfaces.CameraState;
using SensorType = ASCOM.Common.DeviceInterfaces.SensorType;
using SigmaFp.Camera;
using SigmaFp.Imaging;

namespace SigmaFp.Alpaca;

/// <summary>An ASCOM error with its Alpaca error number.</summary>
public sealed class AlpacaException(AlpacaErrors code, string message) : Exception(message)
{
    public AlpacaErrors Code { get; } = code;
    public static AlpacaException NotImplemented(string member) => new(AlpacaErrors.NotImplemented, $"{member} is not implemented by the Sigma fp driver");
    public static AlpacaException InvalidValue(string message) => new(AlpacaErrors.InvalidValue, message);
    public static AlpacaException InvalidOperation(string message) => new(AlpacaErrors.InvalidOperationException, message);
    public static AlpacaException NotConnected() => new(AlpacaErrors.NotConnected, "The camera is not connected");
}

/// <summary>
/// ICameraV4 semantics over <see cref="CameraController"/>. Thread-safe. ImageReady becomes true only
/// after the camera DNG has been decoded into memory.
/// </summary>
public sealed class CameraDevice : IDisposable
{
    public const string DeviceName = "Sigma fp / fp L";
    public const string DriverVersion = "1.0.0";
    public const string ProjectUrl = "https://github.com/markjamesford-telescope/SigmaFpAlpaca";
    public const int InterfaceVersion = 4;
    /// <summary>fp L raw size (the default before a camera is connected).</summary>
    public const int SensorWidth = 9536, SensorHeight = 6344;
    public const int MaxAdu = 16383;

    /// <summary>ISO list proven with the old driver (Gain index = position; ISO 400 = Gain 10).</summary>
    public static readonly int[] IsoList =
        [6, 12, 25, 50, 100, 125, 160, 200, 250, 320, 400, 500, 640, 800, 1000, 1250, 1600, 2000, 2500, 3200, 4000, 5000, 6400, 8000, 10000, 12800, 16000, 20000, 25600];

    readonly CameraController _cam;
    readonly DriverSettings _settings;
    readonly Func<byte[], RawImage> _decode;
    readonly ILogger _log;
    readonly object _lock = new();

    RawImage? _image;            // decoded image of the last exposure
    bool _decoding;
    string? _decodeError;
    int _numX = SensorWidth, _numY = SensorHeight, _startX, _startY; // reset to the body's size on connect
    int _exposureNumX, _exposureNumY, _exposureStartX, _exposureStartY;
    Task? _connectTask;

    readonly string? _settingsPath;

    public CameraDevice(CameraController camera, DriverSettings settings, ILogger log, Func<byte[], RawImage>? decoder = null, string? settingsPath = null)
    {
        _cam = camera;
        _settings = settings;
        _settingsPath = settingsPath;
        _log = log;
        _decode = decoder ?? DngReader.Read;
        _cam.ImageDownloaded += OnImageDownloaded;
        if (Array.IndexOf(IsoList, settings.DefaultIso) >= 0) _cam.Iso = settings.DefaultIso;
    }

    // ---------------------------------------------------------------- common members

    /// <summary>The connected camera's model (e.g. "SIGMA fp L"), else the generic device name.</summary>
    public string Name => _cam.Info?.Model is { Length: > 0 } m ? m : _settings.LastModel ?? DeviceName;

    /// <summary>Model of the connected (or last connected) body.</summary>
    CameraModels.Model Model => CameraModels.Lookup(_cam.Info?.Model);

    /// <summary>Raw size: learned for this serial, else the model's (published or verified) size.</summary>
    (int W, int H) Size =>
        Serial is { } s && _settings.BodySizes.TryGetValue(s, out var v) && CameraModels.ParseSize(v) is { } learned
            ? learned : (Model.Width, Model.Height);
    // ASCOM: at most 68 characters (FITS header compatible).
    public string Description
    {
        get
        {
            var d = $"{Name}, {(Monochrome ? "mono" : "colour")} body, {(_settings.MassStorageMode ? "relay bulb, card via USB" : "Sigma PTP over USB")}";
            return d.Length <= 68 ? d : d[..68];
        }
    }
    public string DriverInfo => $"SigmaFpAlpaca {DriverVersion}: open-source Alpaca driver for the Sigma fp and fp L";
    public bool Connecting => _connectTask is { IsCompleted: false };

    // Per-client connections (Alpaca ClientID): the camera is only released when the last client
    // disconnects, so e.g. a second tool disconnecting cannot pull the camera away from NINA.
    readonly HashSet<uint> _clients = new();

    /// <summary>True when the camera is connected (any client).</summary>
    public bool Connected => _cam.Connected;

    /// <summary>This client's view: connected only if it connected and the camera is connected.</summary>
    public bool IsConnected(uint clientId)
    {
        lock (_lock) return _clients.Contains(clientId) && _cam.Connected;
    }

    public int ClientCount { get { lock (_lock) return _clients.Count; } }

    public void SetConnected(bool value, uint clientId = 0)
    {
        if (value)
        {
            bool fresh = !_cam.Connected;
            if (fresh) ResetForNewConnection();
            _cam.Connect();
            lock (_lock) _clients.Add(clientId);
            RegisterBody();
            if (fresh) { ResetSubframe(); ApplyDefaultIso(); }
            if (fresh && _settings.MassStorageMode)
                _log.LogInformation("Mass Storage mode: exposure mode, Bulb, ISO and DNG are set on the camera; NINA's gain has no effect");
            _log.LogInformation("Client {Client} connected ({N} client(s))", clientId, ClientCount);
        }
        else
        {
            bool last;
            lock (_lock)
            {
                if (!_clients.Remove(clientId) && _clients.Count > 0) return; // unknown client: nothing to do
                last = _clients.Count == 0;
            }
            _log.LogInformation("Client {Client} disconnected ({N} client(s) left)", clientId, ClientCount);
            if (last) _cam.Disconnect();
        }
    }

    /// <summary>The connected body's serial (from the camera), if known.</summary>
    public string? Serial => _cam.Info?.Serial;

    bool Monochrome => _settings.IsMonochrome(Serial);

    /// <summary>Remember each body; new ones default to Colour (stock).</summary>
    void RegisterBody()
    {
        var serial = Serial;
        if (_cam.Info?.Model is { Length: > 0 } model && model != _settings.LastModel)
        {
            _settings.LastModel = model;
            try { _settings.Save(_settingsPath); } catch (Exception ex) { _log.LogWarning(ex, "Could not save settings"); }
        }
        if (serial is null || _settings.Bodies.ContainsKey(serial))
        {
            _log.LogInformation("Body {Serial}: {Type}", serial, Monochrome ? "Monochrome" : "Colour");
            return;
        }
        _settings.Bodies[serial] = "Colour";
        try { _settings.Save(_settingsPath); } catch (Exception ex) { _log.LogWarning(ex, "Could not save settings"); }
        _log.LogInformation("New body {Serial} registered as Colour (change on the setup page if it is mono-converted)", serial);
    }

    /// <summary>New connection: the model's default ISO (fp L 400, fp 1250 unless changed on the setup page).</summary>
    void ApplyDefaultIso()
    {
        int iso = _settings.DefaultIsoFor(Model);
        if (Array.IndexOf(IsoList, iso) < 0) return;
        _cam.Iso = iso;
        _log.LogInformation("Default ISO for {Model}: {Iso}", Model.Name, iso);
    }

    /// <summary>New connection: full-sensor subframe for the connected body.</summary>
    void ResetSubframe()
    {
        var (w, h) = Size;
        (_numX, _numY, _startX, _startY) = (w, h, 0, 0);
        _log.LogInformation("Sensor {Model}: {W}x{H}, {Pix} um{Note}", Model.Name, w, h, Model.PixelMicrons,
            Serial is { } s && _settings.BodySizes.ContainsKey(s) ? " (learned from images)" : Model.RawSizeVerified ? "" : " (published size; exact raw size is learned from the first image)");
    }

    /// <summary>ASCOM: LastExposure*, ImageReady and ImageArray must not carry over from a previous connection.</summary>
    void ResetForNewConnection()
    {
        _cam.ResetExposureHistory();
        lock (_lock) { if (!_cam.CaptureInFlight) { _image = null; _decodeError = null; } }
    }

    /// <summary>ICameraV4 asynchronous Connect().</summary>
    public void Connect(uint clientId = 0)
    {
        lock (_lock)
        {
            if (Connecting) return;
            if (_cam.Connected) { _clients.Add(clientId); return; }
            ResetForNewConnection();
            _connectTask = Task.Run(() =>
            {
                try { SetConnected(true, clientId); }
                catch (Exception ex) { _log.LogError(ex, "Asynchronous connect failed"); }
            });
        }
    }

    public void Disconnect(uint clientId = 0) => SetConnected(false, clientId);

    void RequireConnected()
    {
        if (!_cam.Connected) throw AlpacaException.NotConnected();
    }

    // ---------------------------------------------------------------- camera properties

    public int BayerOffsetX { get { RequireColour(); return Sensor.BayerOffsetX; } }
    public int BayerOffsetY { get { RequireColour(); return Sensor.BayerOffsetY; } }

    void RequireColour()
    {
        if (Monochrome) throw AlpacaException.NotImplemented("BayerOffset (monochrome body)");
    }

    /// <summary>fp L DNGs are RGGB at (0,0); a decoded image overrides that if it says otherwise.</summary>
    SensorDescription Sensor
    {
        get
        {
            RawImage? img;
            lock (_lock) img = _image;
            return img is not null ? AscomImage.Describe(img, Monochrome)
                : Monochrome ? new(AscomSensorType.Monochrome, 0, 0) : new(AscomSensorType.RGGB, 0, 0);
        }
    }

    public SensorType SensorType => (SensorType)(int)Sensor.Type;
    public string SensorName => Model.SensorName;

    public short BinX { get => 1; set { if (value != 1) throw AlpacaException.InvalidValue("Only 1x1 binning is supported"); } }
    public short BinY { get => 1; set { if (value != 1) throw AlpacaException.InvalidValue("Only 1x1 binning is supported"); } }
    public short MaxBinX => 1;
    public short MaxBinY => 1;
    public bool CanAsymmetricBin => false;
    public int CameraXSize => Size.W;
    public int CameraYSize => Size.H;
    public double PixelSizeX => Model.PixelMicrons;
    public double PixelSizeY => Model.PixelMicrons;
    public int MaxADU => MaxAdu;
    public bool HasShutter => false; // electronic shutter only
    public bool CanAbortExposure => _cam.HasBulbTrigger;
    public bool CanStopExposure => _cam.HasBulbTrigger;
    public bool CanPulseGuide => false;
    public bool CanFastReadout => false;
    public bool CanGetCoolerPower => false;
    public bool CanSetCCDTemperature => false;
    public double ExposureMin => _cam.Planner.MinExposure;
    public double ExposureMax => _cam.Planner.MaxExposure;
    public double ExposureResolution => 0;
    public IList<string> ReadoutModes => ["Normal"];
    public short ReadoutMode { get => 0; set { if (value != 0) throw AlpacaException.InvalidValue("Only readout mode 0 exists"); } }
    // Plain numbers, like the old driver: NINA parses gain names as numbers ("Gain 10 Mapped to 400").
    public IList<string> Gains => IsoList.Select(i => i.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToList();

    public bool IsoGainMode => _settings.IsoGainMode;

    public IList<string> GainsForMode =>
        IsoGainMode ? throw AlpacaException.NotImplemented("Gains (gain is the ISO value; use GainMin/GainMax)") : Gains;

    public short GainMin => IsoGainMode ? (short)IsoList[0] : throw AlpacaException.NotImplemented("GainMin (gains are a list)");
    public short GainMax => IsoGainMode ? (short)IsoList[^1] : throw AlpacaException.NotImplemented("GainMax (gains are a list)");

    public short Gain
    {
        get => IsoGainMode ? (short)_cam.Iso : (short)Math.Max(0, Array.IndexOf(IsoList, _cam.Iso));
        set
        {
            if (_settings.MassStorageMode)
            {
                // The camera's own ISO is used; Gain reports it from the last image. Validate, then ignore.
                if (IsoGainMode ? value < IsoList[0] || value > IsoList[^1] : value < 0 || value >= IsoList.Length)
                    throw AlpacaException.InvalidValue(IsoGainMode ? $"Gain (ISO) must be {IsoList[0]}-{IsoList[^1]}" : $"Gain must be 0-{IsoList.Length - 1}");
                _log.LogDebug("Gain {Value} ignored in Mass Storage mode (ISO is set on the camera)", value);
                return;
            }
            if (IsoGainMode)
            {
                if (value < IsoList[0] || value > IsoList[^1]) throw AlpacaException.InvalidValue($"Gain (ISO) must be {IsoList[0]}-{IsoList[^1]}");
                int iso = IsoList.MinBy(i => Math.Abs(Math.Log(i) - Math.Log(value)));
                if (iso != value) _log.LogInformation("Gain {Requested} -> nearest camera ISO {Iso}", value, iso);
                _cam.Iso = iso;
                return;
            }
            if (value < 0 || value >= IsoList.Length) throw AlpacaException.InvalidValue($"Gain must be 0-{IsoList.Length - 1}");
            _cam.Iso = IsoList[value];
        }
    }

    // ASCOM: subframe setters accept any value; StartExposure rejects invalid combinations with InvalidValue.
    public int NumX { get => _numX; set => _numX = value; }
    public int NumY { get => _numY; set => _numY = value; }
    public int StartX { get => _startX; set => _startX = value; }
    public int StartY { get => _startY; set => _startY = value; }

    public AscomCameraState CameraState
    {
        get
        {
            RequireConnected();
            bool decoding;
            lock (_lock) decoding = _decoding;
            return _cam.State switch
            {
                Camera.CameraState.Exposing => AscomCameraState.Exposing,
                Camera.CameraState.Downloading => AscomCameraState.Download,
                Camera.CameraState.Error => _cam.CaptureInFlight ? AscomCameraState.Exposing : AscomCameraState.Error,
                _ when _cam.ExposureQueued => AscomCameraState.Exposing,
                _ when decoding => AscomCameraState.Download,
                // A lost frame stays visible as Error (even after a reconnect) until the next StartExposure,
                // so NINA abandons it at once instead of waiting exposure + 400 s for ImageReady.
                _ when _cam.LastExposureFailed => AscomCameraState.Error,
                _ => AscomCameraState.Idle,
            };
        }
    }

    public bool ImageReady
    {
        get
        {
            RequireConnected();
            lock (_lock)
            {
                if (_image is not null && !_decoding) return true;
            }
            // Lost frame: "ready" so the client's download fails immediately (see FailLostFramesFast).
            return _settings.FailLostFramesFast && _cam.LastExposureFailed && !_cam.CaptureInFlight;
        }
    }

    public double PercentCompleted
    {
        get
        {
            RequireConnected();
            return Math.Round(_cam.PercentCompleted); // 0 before any exposure
        }
    }

    public double LastExposureDuration =>
        _cam.LastExposureSeconds ?? throw AlpacaException.InvalidOperation("No exposure has been taken yet");

    public string LastExposureStartTime =>
        _cam.LastExposureStartUtc?.ToString("yyyy-MM-ddTHH:mm:ss.fff") ?? throw AlpacaException.InvalidOperation("No exposure has been taken yet");

    public string? LastError => _cam.LastError;

    /// <summary>The current camera error for the setup page, or null when the camera is fine.</summary>
    public string? Problem => _cam.Connected && (_cam.State == Camera.CameraState.Error || _cam.LastExposureFailed) ? _cam.LastError
        : _cam.Connected ? _exposureWarning : null;

    // ---------------------------------------------------------------- operations

    public void StartExposure(double duration, bool light)
    {
        RequireConnected();
        if (duration < 0) throw AlpacaException.InvalidValue("Duration must not be negative");
        var (sw, sh) = Size;
        if (_numX < 1 || _numY < 1 || _startX < 0 || _startY < 0 || _numX + _startX > sw || _numY + _startY > sh)
            throw AlpacaException.InvalidValue($"Subframe {_startX},{_startY} {_numX}x{_numY} exceeds the {sw}x{sh} sensor");
        if (duration == 0) duration = ExposureMin; // ASCOM allows 0 for a bias; use the shortest exposure
        try
        {
            lock (_lock) { _image = null; _decodeError = null; }
            _cam.StartExposure(duration);
            (_exposureNumX, _exposureNumY, _exposureStartX, _exposureStartY) = (_numX, _numY, _startX, _startY);
            if (!light) _log.LogInformation("Dark frame requested: the fp L has no shutter; cover the telescope");
        }
        catch (ArgumentOutOfRangeException ex) { throw AlpacaException.InvalidValue(ex.Message.Split(Environment.NewLine)[0]); }
        catch (NotConnectedException) { throw AlpacaException.NotConnected(); }
        catch (InvalidOperationException ex) { throw AlpacaException.InvalidOperation(ex.Message); }
    }

    public void AbortExposure()
    {
        if (!_cam.HasBulbTrigger) throw AlpacaException.NotImplemented("AbortExposure (the camera cannot stop a running exposure; CanAbortExposure is false)");
        RequireConnected();
        _cam.AbortExposure();
    }

    public void StopExposure()
    {
        RequireConnected();
        if (!_cam.HasBulbTrigger) throw AlpacaException.InvalidOperation("StopExposure is not possible: the camera cannot stop a running exposure (CanStopExposure is false)");
        try { _cam.StopExposure(); }
        catch (InvalidOperationException ex) { throw AlpacaException.InvalidOperation(ex.Message); }
    }

    /// <summary>The image (or requested subframe) as row-major pixels with its size.</summary>
    public (ushort[] Pixels, int Width, int Height) GetImage()
    {
        RequireConnected();
        RawImage img;
        if (_cam.LastExposureFailed && !_cam.CaptureInFlight)
            throw AlpacaException.InvalidOperation($"Exposure lost, no image: {_cam.LastError}");
        lock (_lock)
        {
            if (_decodeError is not null) throw AlpacaException.InvalidOperation($"The last image could not be decoded: {_decodeError}");
            img = _image is not null && !_decoding ? _image : throw AlpacaException.InvalidOperation("No image is ready");
        }
        if (_exposureNumX == img.Width && _exposureNumY == img.Height && _exposureStartX == 0 && _exposureStartY == 0)
            return (img.Pixels, img.Width, img.Height);

        int w = Math.Min(_exposureNumX, img.Width - _exposureStartX), h = Math.Min(_exposureNumY, img.Height - _exposureStartY);
        var sub = new ushort[w * h];
        for (int y = 0; y < h; y++)
            img.Pixels.AsSpan((_exposureStartY + y) * img.Width + _exposureStartX, w).CopyTo(sub.AsSpan(y * w, w));
        return (sub, w, h);
    }

    void OnImageDownloaded(byte[] dng, ExposurePlan plan)
    {
        lock (_lock) { _decoding = true; _image = null; _decodeError = null; }
        // ImageDownloaded runs on the camera worker; decode elsewhere so the worker stays free.
        _ = Task.Run(() =>
        {
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var img = _decode(dng);
                LearnSize(img);
                AdoptCameraIso(img);
                lock (_lock) { _image = img; _decoding = false; }
                _log.LogInformation("Image decoded: {W}x{H}, black {Black}, CFA {Cfa}, {Ms} ms", img.Width, img.Height, img.BlackLevel, img.CfaName ?? "none", sw.ElapsedMilliseconds);
                CheckExposureTime(img, plan);
                if (_settings.SaveDng) SaveDng(dng, plan);
            }
            catch (Exception ex)
            {
                lock (_lock) { _decodeError = ex.Message; _decoding = false; }
                _log.LogError(ex, "DNG decode failed");
                SaveDng(dng, plan, failed: true);
            }
        });
    }

    string? _exposureWarning;

    /// <summary>Mass Storage mode: report the ISO the camera actually used (from the DNG) as Gain from now on.</summary>
    void AdoptCameraIso(RawImage img)
    {
        if (!_settings.MassStorageMode || img.Iso is not int iso || iso <= 0) return;
        int nearest = IsoList.MinBy(i => Math.Abs(Math.Log(i) - Math.Log(iso)));
        if (nearest == _cam.Iso) return;
        _log.LogInformation("Camera ISO from the image: {Iso}; reported as Gain from now on", nearest);
        _cam.Iso = nearest;
    }

    /// <summary>
    /// Mass Storage mode: the camera's own settings decide the exposure, so compare the DNG's exposure time with the
    /// request. A mismatch almost always means the camera is not set to Bulb (seen 2026-10-09: 20 s instead of 5 s).
    /// </summary>
    void CheckExposureTime(RawImage img, ExposurePlan plan)
    {
        if (!_settings.MassStorageMode || img.ExposureSeconds is not double actual) return;
        double requested = plan.ActualSeconds;
        if (Math.Abs(actual - requested) > Math.Max(1.0, 0.15 * requested))
        {
            _exposureWarning = $"The camera exposed {actual:0.#} s instead of {requested:0.#} s: set the shutter to Bulb on the camera";
            _log.LogWarning("{Warning}", _exposureWarning);
        }
        else _exposureWarning = null;
    }

    /// <summary>Remember this body's exact raw size from its first image (fp: published size until then).</summary>
    void LearnSize(RawImage img)
    {
        if (Serial is not { } serial) return;
        var (w, h) = Size;
        if (_settings.BodySizes.ContainsKey(serial) && w == img.Width && h == img.Height) return;
        bool changed = w != img.Width || h != img.Height;
        _settings.BodySizes[serial] = $"{img.Width}x{img.Height}";
        try { _settings.Save(_settingsPath); } catch (Exception ex) { _log.LogWarning(ex, "Could not save settings"); }
        if (!changed) return;
        _log.LogWarning("Body {Serial}: raw size is {W}x{H}, not {OldW}x{OldH}; saved. Reconnect the camera in the client so it picks up the new size.",
            serial, img.Width, img.Height, w, h);
        // Keep this image consistent: if the full frame was requested, deliver the full raw frame.
        if (_exposureNumX == w && _exposureNumY == h && _exposureStartX == 0 && _exposureStartY == 0)
            (_exposureNumX, _exposureNumY) = (img.Width, img.Height);
        if (_numX == w && _numY == h) (_numX, _numY) = (img.Width, img.Height);
    }

    void SaveDng(byte[] dng, ExposurePlan plan, bool failed = false)
    {
        try
        {
            string folder = failed ? Path.Combine(DriverSettings.Folder, "failed-dng") : _settings.DngFolder;
            Directory.CreateDirectory(folder);
            string name = $"{DateTime.Now:yyyy-MM-dd_HHmmss}_{plan.ActualSeconds:0.###}s_ISO{_cam.Iso}.dng".Replace(',', '.');
            File.WriteAllBytes(Path.Combine(folder, name), dng);
        }
        catch (Exception ex) { _log.LogWarning(ex, "Could not save the DNG copy"); }
    }

    /// <summary>ICameraV4 DeviceState: operational properties in one call.</summary>
    public IEnumerable<(string Name, object Value)> DeviceState()
    {
        if (!_cam.Connected) yield break;
        yield return ("CameraState", (int)CameraState);
        yield return ("ImageReady", ImageReady);
        if (_cam.CaptureInFlight || _cam.LastPlan is not null) yield return ("PercentCompleted", Math.Round(_cam.PercentCompleted));
        yield return ("TimeStamp", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fff"));
    }

    public void Dispose() => _cam.Dispose();
}
