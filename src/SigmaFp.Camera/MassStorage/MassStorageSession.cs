using System.Diagnostics;
using Microsoft.Extensions.Logging;
using SigmaFp.Camera.Bulb;
using SigmaFp.Camera.Session;
using SigmaFp.Camera.Sigma;

namespace SigmaFp.Camera.MassStorage;

/// <summary>Switches the camera's USB port on and off (e.g. a Yepkit YKUSH XS): off = cable unplugged.</summary>
public interface IPortSwitch
{
    void SetPower(bool on);
}

/// <summary>The camera's card as a drive (USB mode Mass Storage).</summary>
public sealed record CardInfo(string Root, string Model, string Serial);

public interface ICardLocator
{
    /// <summary>The camera's drive if it is currently mounted (with a DCIM folder), else null.</summary>
    CardInfo? Find();
}

/// <summary>Press/release times of the relay, shared by the trigger and every session (sessions are recreated on reconnect).</summary>
public sealed class BulbState
{
    readonly object _lock = new();
    long _pressed, _released; // Stopwatch timestamps, 0 = none

    public void Press() { lock (_lock) { _pressed = Stopwatch.GetTimestamp(); _released = 0; } }
    public void Release() { lock (_lock) if (_pressed != 0 && _released == 0) _released = Stopwatch.GetTimestamp(); }
    public void Reset() { lock (_lock) _pressed = _released = 0; }
    public bool Pressed { get { lock (_lock) return _pressed != 0; } }
    /// <summary>Time since the release, or null while the shutter is still held (or no shot).</summary>
    public TimeSpan? SinceRelease { get { lock (_lock) return _released == 0 ? null : Stopwatch.GetElapsedTime(_released); } }
}

/// <summary>Relay trigger that also records press/release for <see cref="MassStorageSession"/>.</summary>
public sealed class MassStorageTrigger(IBulbTrigger relay, BulbState state) : IBulbTrigger
{
    public string Description => relay.Description;
    public void Open() => relay.Open();
    public void Press() { relay.Press(); state.Press(); }
    public void Release() { relay.Release(); state.Release(); }
    public void Dispose() => relay.Dispose();
}

public sealed class MassStorageOptions
{
    /// <summary>Wait after switching the port off before the relay may fire (the camera leaves "Now connecting").</summary>
    // Measured 2026-10-09 (fp L): 1.5 s was enough; 2 s gives a margin.
    public TimeSpan SettleAfterPortOff { get; set; } = TimeSpan.FromSeconds(2);
    /// <summary>Wait after the relay release before the port is switched on (the camera writes the file).</summary>
    // Tested 2026-10-09 with 4 s: the file was complete when the card appeared (written ~6 s after the release).
    public TimeSpan SettleAfterRelease { get; set; } = TimeSpan.FromSeconds(4);
    public TimeSpan DriveTimeout { get; set; } = TimeSpan.FromSeconds(30);
    public TimeSpan FileTimeout { get; set; } = TimeSpan.FromSeconds(45);
    public TimeSpan DriveGoneTimeout { get; set; } = TimeSpan.FromSeconds(10);
    public TimeSpan PollStep { get; set; } = TimeSpan.FromMilliseconds(250);
    /// <summary>
    /// Before an exposure, if the card has not been seen for this long, switch the port on and check that the camera
    /// answers. A sleeping camera (auto power off) ignores both the relay and the USB port, so the frame would be lost.
    /// </summary>
    public TimeSpan AwakeCheckAfterIdle { get; set; } = TimeSpan.FromSeconds(60);
    public TimeSpan AwakeCheckTimeout { get; set; } = TimeSpan.FromSeconds(10);
    /// <summary>Delete each DNG from the card after it has been read (keeps the card from filling up).</summary>
    public bool DeleteAfterRead { get; set; }
}

/// <summary>
/// Camera in USB mode Mass Storage behind a switchable USB port (open question 19). The camera is used on its own:
/// exposure mode, Bulb and ISO are set on the camera; the relay on the remote jack times every exposure while the port
/// is off; afterwards the port is switched on, the new DNG is read from the card, and the port is switched off again.
/// Each <see cref="GetStatus"/> call advances one short step, so no call blocks the worker for long.
/// </summary>
public sealed class MassStorageSession(IPortSwitch port, ICardLocator cards, BulbState bulb, MassStorageOptions options, ILogger log) : ICameraSession
{
    enum Phase { Idle, Settling, WaitDrive, WaitFile, Ready }

    readonly HashSet<string> _known = new(StringComparer.OrdinalIgnoreCase);
    Phase _phase;
    bool _portOn;
    long _phaseStart;
    string? _file, _lastCandidate;
    long _lastContact; // Stopwatch timestamp when the card was last seen
    long _lastSize = -1;
    byte _nextId;

    public CameraInfo Open()
    {
        SetPort(true);
        var card = WaitForCard(options.DriveTimeout)
            ?? throw new CameraCommException("The camera's card did not appear as a drive: is the camera on, in USB mode Mass Storage, and connected through the switchable port?");
        foreach (var f in Dngs(card.Root)) _known.Add(f);
        _lastContact = Stopwatch.GetTimestamp();
        log.LogInformation("Mass Storage: {Model} {Serial} on {Root}, {Count} DNG files on the card", card.Model, card.Serial, card.Root, _known.Count);
        SwitchOffAndWait();
        return new CameraInfo(card.Model, card.Serial, "Mass Storage + relay");
    }

    public void Close() => SetPort(false); // leave the camera usable on its own

    public void ApplySettings(byte shutterCode, byte isoCode) { /* set on the camera itself in this mode */ }

    public byte PrepareCapture()
    {
        if (Stopwatch.GetElapsedTime(_lastContact) > options.AwakeCheckAfterIdle) CheckAwake();
        if (_portOn || cards.Find() is not null) SwitchOffAndWait();
        Thread.Sleep(options.SettleAfterPortOff);
        bulb.Reset();
        (_phase, _file, _lastCandidate, _lastSize) = (Phase.Idle, null, null, -1);
        return _nextId++;
    }

    public void Snap() =>
        throw new CameraProtocolException("Mass Storage mode: every exposure is timed by the relay (no USB shutter release)");

    public CaptStatus GetStatus(byte imageId)
    {
        ushort st = Advance();
        return new CaptStatus(imageId, imageId, (byte)(imageId + 1), st);
    }

    ushort Advance()
    {
        if (!bulb.Pressed) return CaptStatusCode.Cleared;
        if (bulb.SinceRelease is not { } since) return CaptStatusCode.ShootInProgress;
        switch (_phase)
        {
            case Phase.Idle:
                _phase = Phase.Settling;
                goto case Phase.Settling;
            case Phase.Settling:
                if (since < options.SettleAfterRelease) return CaptStatusCode.ImageGenInProgress;
                SetPort(true);
                (_phase, _phaseStart) = (Phase.WaitDrive, Stopwatch.GetTimestamp());
                return CaptStatusCode.ImageGenInProgress;
            case Phase.WaitDrive:
                if (cards.Find() is { } card)
                {
                    _lastContact = Stopwatch.GetTimestamp();
                    log.LogInformation("Card {Root} mounted {Sec:0.0} s after the port was switched on", card.Root, Stopwatch.GetElapsedTime(_phaseStart).TotalSeconds);
                    (_phase, _phaseStart) = (Phase.WaitFile, Stopwatch.GetTimestamp());
                    return CheckFile(card.Root);
                }
                if (Stopwatch.GetElapsedTime(_phaseStart) > options.DriveTimeout)
                    throw new CameraProtocolException($"The camera's card did not appear within {options.DriveTimeout.TotalSeconds:0} s after the exposure (camera off, or not in USB mode Mass Storage?)");
                return CaptStatusCode.ImageGenInProgress;
            case Phase.WaitFile:
                return cards.Find() is { } c ? CheckFile(c.Root) : CaptStatusCode.ImageGenInProgress;
            default:
                return CaptStatusCode.ImageGenCompleted;
        }
    }

    ushort CheckFile(string root)
    {
        var fresh = Dngs(root).Where(f => !_known.Contains(f)).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
        if (fresh.Count == 0)
        {
            if (Stopwatch.GetElapsedTime(_phaseStart) > options.FileTimeout)
                throw new CameraProtocolException("No new DNG on the card after the exposure: did the camera fire? (Bulb set on the camera, DNG quality, remote jack plugged in?)");
            return CaptStatusCode.ImageGenInProgress;
        }
        foreach (var extra in fresh.Take(fresh.Count - 1))
        {
            _known.Add(extra);
            log.LogWarning("Ignoring an extra new file on the card: {File}", extra);
        }
        string newest = fresh[^1];
        long size = new FileInfo(newest).Length;
        if (newest == _lastCandidate && size == _lastSize && size > 0)
        {
            (_phase, _file) = (Phase.Ready, newest);
            return CaptStatusCode.ImageGenCompleted;
        }
        (_lastCandidate, _lastSize) = (newest, size); // complete once the size is stable over two polls
        return CaptStatusCode.ImageGenInProgress;
    }

    public PictFileInfo GetImageInfo()
    {
        var path = _file ?? throw new CameraProtocolException("No image file information (no new DNG yet)");
        var fi = new FileInfo(path);
        return new PictFileInfo(0, (uint)fi.Length, "DNG", 0, 0, fi.DirectoryName ?? "", fi.Name);
    }

    public ReadOnlyMemory<byte> ReadImageChunk(uint address, uint offset, uint length)
    {
        var path = _file ?? throw new CameraProtocolException("No image to read");
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            fs.Position = offset;
            var buf = new byte[length];
            int n = 0, r;
            while (n < length && (r = fs.Read(buf, n, (int)length - n)) > 0) n += r;
            return buf.AsMemory(0, n);
        }
        catch (IOException ex) { throw new CameraCommException($"Reading {path} from the card failed: {ex.Message}", ex); }
    }

    public void ClearImage(byte imageId)
    {
        if (_file is { } f)
        {
            _known.Add(f);
            if (options.DeleteAfterRead)
            {
                try { File.Delete(f); log.LogDebug("Deleted {File} from the card", f); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { log.LogWarning("Could not delete {File}: {Msg}", f, ex.Message); }
            }
        }
        (_phase, _file) = (Phase.Idle, null);
        bulb.Reset();
        if (_portOn) SwitchOffAndWait();
    }

    /// <summary>The camera cannot be reached while the port is off, so there is nothing to check between exposures.</summary>
    public void Heartbeat() { }

    public void Dispose() { }

    void SetPort(bool on)
    {
        port.SetPower(on);
        _portOn = on;
        log.LogDebug("USB port {State}", on ? "ON" : "OFF");
    }

    void CheckAwake()
    {
        SetPort(true);
        var card = WaitForCard(options.AwakeCheckTimeout);
        if (card is null)
        {
            SetPort(false);
            throw new CameraProtocolException("The camera did not answer: it is probably asleep (auto power off) or switched off. " +
                "Wake it (press a button) and set Auto Power Off to Off in the camera menu, then start the exposure again.");
        }
        foreach (var f in Dngs(card.Root)) _known.Add(f); // anything taken by hand in the meantime is not ours
        _lastContact = Stopwatch.GetTimestamp();
        log.LogInformation("Camera is awake (card {Root} answered); exposing", card.Root);
        SwitchOffAndWait();
    }

    void SwitchOffAndWait()
    {
        SetPort(false);
        var sw = Stopwatch.StartNew();
        while (cards.Find() is not null)
        {
            if (sw.Elapsed > options.DriveGoneTimeout)
                throw new CameraCommException("The camera's drive is still present after switching the port off: is the camera connected through the switchable port?");
            Thread.Sleep(options.PollStep);
        }
    }

    CardInfo? WaitForCard(TimeSpan limit)
    {
        var sw = Stopwatch.StartNew();
        while (true)
        {
            if (cards.Find() is { } c) return c;
            if (sw.Elapsed > limit) return null;
            Thread.Sleep(options.PollStep);
        }
    }

    static IEnumerable<string> Dngs(string root)
    {
        string dcim = Path.Combine(root, "DCIM");
        return Directory.Exists(dcim) ? Directory.EnumerateFiles(dcim, "*.DNG", SearchOption.AllDirectories) : [];
    }
}
