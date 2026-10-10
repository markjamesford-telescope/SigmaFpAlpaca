using System.Text.Json;

namespace SigmaFp.Alpaca;

/// <summary>Persistent driver settings: %LOCALAPPDATA%\SigmaFpAlpaca\settings.json.</summary>
public sealed class DriverSettings
{
    /// <summary>Per-user folder when run interactively; C:\ProgramData\SigmaFpAlpaca when run as a Windows service.</summary>
    public static string Folder { get; set; } = UserFolder;
    public static string UserFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SigmaFpAlpaca");
    public static string ServiceFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "SigmaFpAlpaca");
    public static string FilePath => Path.Combine(Folder, "settings.json");

    /// <summary>HTTP port of the Alpaca server.</summary>
    public int Port { get; set; } = 11111;
    /// <summary>Answer Alpaca discovery and accept connections from other machines (default: this PC only).</summary>
    public bool AllowRemote { get; set; }
    /// <summary>
    /// Body type per camera serial: "Monochrome" (Bayer mask removed) or "Colour". The camera
    /// itself cannot tell (a converted body still writes CFA tags). Unknown serials are added
    /// as Colour (stock) on first connect and can be changed on the setup page.
    /// </summary>
    public Dictionary<string, string> Bodies { get; set; } = new();

    /// <summary>Raw image size learned from the first image of each body, e.g. "9536x6344".</summary>
    public Dictionary<string, string> BodySizes { get; set; } = new();

    /// <summary>Model of the last connected camera (shown in the Alpaca device list before connecting).</summary>
    public string? LastModel { get; set; }

    public bool IsMonochrome(string? serial) =>
        serial is not null && Bodies.TryGetValue(serial, out var t) && t.Equals("Monochrome", StringComparison.OrdinalIgnoreCase);
    /// <summary>Default ISO of the fp L (kept for older settings files); see <see cref="DefaultIsoByModel"/>.</summary>
    public int DefaultIso { get; set; } = 400;
    /// <summary>Default ISO per camera model, applied on connect (fp L 400, fp 1250 unless set here).</summary>
    public Dictionary<string, int> DefaultIsoByModel { get; set; } = new();

    public int DefaultIsoFor(CameraModels.Model m) =>
        DefaultIsoByModel.TryGetValue(m.Name, out var iso) ? iso : m == CameraModels.FpL ? DefaultIso : m.DefaultIso;
    /// <summary>
    /// "List": ASCOM gain list, Gain = index (NINA shows 0-28, Gain 10 = ISO 400; old-driver compatible).
    /// "Iso": Gain = the ISO value itself (NINA shows/records 400); snapped to the nearest supported ISO.
    /// </summary>
    public string GainMode { get; set; } = "List";
    /// <summary>Queue StartExposure while an un-abortable capture is still finishing (see CameraOptions).</summary>
    public bool QueueStartWhileBusy { get; set; } = true;
    /// <summary>
    /// A lost frame (USB loss, watchdog, camera failure) is reported as ImageReady = true with an
    /// ImageArray error, so clients that only poll ImageReady (NINA) fail the frame at once instead
    /// of waiting for their timeout (NINA: exposure + 400 s). No image data is ever faked.
    /// </summary>
    public bool FailLostFramesFast { get; set; } = true;
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsoGainMode => GainMode.Equals("Iso", StringComparison.OrdinalIgnoreCase);
    /// <summary>
    /// "Usb" (default): the driver controls the camera over USB (USB mode Camera Control; up to 30 s and 300 s).
    /// "MassStorage": camera in USB mode Mass Storage behind a switchable port (YKUSH XS); every exposure is a bulb
    /// timed by the relay on the remote jack, and the DNG is read from the card (open question 19).
    /// </summary>
    public string ConnectionMode { get; set; } = "Usb";
    [System.Text.Json.Serialization.JsonIgnore]
    public bool MassStorageMode => ConnectionMode.Equals("MassStorage", StringComparison.OrdinalIgnoreCase);
    /// <summary>
    /// COM port of an LCUS USB relay on the 3.5 mm release jack. Used in Mass Storage mode (and by the simulator);
    /// ignored in USB mode, because the fp / fp L ignore the jack while under USB control (open question 17).
    /// </summary>
    public string? RelayPort { get; set; }
    /// <summary>Path to Yepkit's ykushcmd.exe (empty = "ykushcmd" on the PATH).</summary>
    public string? YkushPath { get; set; }
    /// <summary>Mass Storage mode: delete each DNG from the card after it has been read.</summary>
    public bool DeleteFromCard { get; set; }
    /// <summary>Mass Storage mode: longest relay bulb (the camera's bulb limit).</summary>
    public double MaxBulbSeconds { get; set; } = 300;
    public byte RelayChannel { get; set; } = 1;
    /// <summary>Also save each camera DNG (date/time, exposure, ISO in the name). Off by default.</summary>
    public bool SaveDng { get; set; }
    // Public Pictures: reachable from both the service account and the user.
    public string DngFolder { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPictures), "SigmaFpAlpaca");

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static DriverSettings Load(string? path = null)
    {
        path ??= FilePath;
        try { return File.Exists(path) ? JsonSerializer.Deserialize<DriverSettings>(File.ReadAllText(path), Json) ?? new() : new(); }
        catch (JsonException) { return new(); }
    }

    public void Save(string? path = null)
    {
        path ??= FilePath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, Json));
    }
}
