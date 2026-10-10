using System.IO.Ports;
using Microsoft.Extensions.Logging;
using SigmaFp.Camera.Session;

namespace SigmaFp.Camera.Bulb;

/// <summary>External shutter release on the fp L's 3.5 mm jack (via the JJC TRRS->TRS cable).</summary>
public interface IBulbTrigger : IDisposable
{
    string Description { get; }
    void Open();
    /// <summary>Close the shutter contact (exposure starts).</summary>
    void Press();
    /// <summary>Open the shutter contact (exposure ends). Must be safe to call repeatedly.</summary>
    void Release();
}

/// <summary>
/// LCUS-1/LCUS-2 USB relay (CH340 serial, 9600 8N1). Command: <c>A0 ch state sum</c> where
/// sum = (0xA0 + ch + state) &amp; 0xFF. Relay 1 NO/COM wired to tip/sleeve (shutter); the fp L
/// does not need half-press (the Pluto Trigger never used it).
/// </summary>
public sealed class LcusRelayTrigger(string portName, ILogger log, byte channel = 1) : IBulbTrigger
{
    readonly string portName = NormalizePortName(portName);
    SerialPort? _port;

    /// <summary>"9", "com9" or " COM9 " -> "COM9" (users often type just the number).</summary>
    public static string NormalizePortName(string name)
    {
        name = name.Trim().ToUpperInvariant();
        return int.TryParse(name, out var n) && n > 0 ? $"COM{n}" : name;
    }

    public string Description => $"LCUS relay ch{channel} on {portName}";

    public static byte[] Command(byte channel, bool on) =>
        [0xA0, channel, (byte)(on ? 1 : 0), (byte)(0xA0 + channel + (on ? 1 : 0))];

    public void Open()
    {
        _port = new SerialPort(portName, 9600, Parity.None, 8, StopBits.One) { WriteTimeout = 2000, ReadTimeout = 500 };
        _port.Open();
        Release(); // known state
        log.LogInformation("Bulb trigger ready: {Desc}", Description);
    }

    public void Press() => Send(true);
    public void Release() => Send(false);

    void Send(bool on)
    {
        if (_port is not { IsOpen: true }) throw new CameraCommException($"{Description} is not open");
        try { _port.Write(Command(channel, on), 0, 4); }
        catch (Exception ex) when (ex is TimeoutException or IOException or InvalidOperationException)
        {
            throw new CameraCommException($"{Description}: write failed: {ex.Message}", ex);
        }
        log.LogDebug("{Desc}: {State}", Description, on ? "PRESS" : "release");
    }

    public void Dispose()
    {
        try { if (_port is { IsOpen: true }) Release(); } catch { /* best effort */ }
        _port?.Dispose();
        _port = null;
    }
}

/// <summary>Drives <see cref="SimulatedSession.ExternalRelease"/> for tests.</summary>
public sealed class SimulatedBulbTrigger(Func<SimulatedSession?> session) : IBulbTrigger
{
    public string Description => "simulated relay";
    public bool Pressed { get; private set; }
    public void Open() { }
    public void Press() { Pressed = true; session()?.ExternalRelease(true); }
    public void Release() { Pressed = false; session()?.ExternalRelease(false); }
    public void Dispose() { }
}
