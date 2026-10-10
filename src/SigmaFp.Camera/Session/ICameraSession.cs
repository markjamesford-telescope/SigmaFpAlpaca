using SigmaFp.Camera.Sigma;

namespace SigmaFp.Camera.Session;

public sealed record CameraInfo(string Model, string Serial, string Firmware);

/// <summary>How the camera should be triggered for one exposure.</summary>
public enum TriggerKind
{
    /// <summary>Camera-timed shutter (1/8000-30 s), Snap NonAF.</summary>
    Timed,
    /// <summary>Shutter=Bulb + Snap NonAF; the camera ends it at its own limit (300 s). Not abortable.</summary>
    CameraBulb,
    /// <summary>Shutter=Bulb; an external release (relay on the 3.5 mm jack) opens and closes it.</summary>
    ExternalBulb,
}

/// <summary>
/// Hardware-facing operations for one connection. Synchronous, NOT thread-safe: only the camera
/// worker thread calls it. Communication failures throw <see cref="CameraCommException"/>;
/// refusals by the camera throw <see cref="CameraProtocolException"/>.
/// </summary>
public interface ICameraSession : IDisposable
{
    CameraInfo Open();

    /// <summary>Ends API mode and the PTP session. Must not be called while the camera is exposing.</summary>
    void Close();

    /// <summary>Manual exposure, DNG 14-bit, save to PC, and the given shutter code (or Bulb) and ISO code.</summary>
    void ApplySettings(byte shutterCode, byte isoCode);

    /// <summary>Clears stale capture-database entries and returns the id the next shot will get.</summary>
    byte PrepareCapture();

    /// <summary>Releases the shutter (Snap NonAF).</summary>
    void Snap();

    CaptStatus GetStatus(byte imageId);

    PictFileInfo GetImageInfo();

    /// <summary>Reads image bytes [offset, offset+length) of the file at <paramref name="address"/>.</summary>
    ReadOnlyMemory<byte> ReadImageChunk(uint address, uint offset, uint length);

    void ClearImage(byte imageId);

    /// <summary>Cheap round trip to prove the camera is still there.</summary>
    void Heartbeat();
}
