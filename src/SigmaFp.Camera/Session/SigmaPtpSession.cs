using System.Buffers.Binary;
using System.Text;
using Microsoft.Extensions.Logging;
using SigmaFp.Camera.Ptp;
using SigmaFp.Camera.Sigma;
using SigmaFp.Camera.Usb;

namespace SigmaFp.Camera.Session;

/// <summary>Real fp L: Sigma vendor PTP over libusb0. Sequence follows the old driver's proven helper.</summary>
public sealed class SigmaPtpSession : ICameraSession
{
    public const ushort SigmaVendorId = 0x1003;
    const int ChunkTimeoutMs = 30_000;

    readonly ILogger _log;
    readonly Func<IUsbPipe> _openPipe;
    PtpTransport? _ptp;

    public SigmaPtpSession(ILogger log, Func<IUsbPipe>? openPipe = null)
    {
        _log = log;
        _openPipe = openPipe ?? (() => LibUsb0Pipe.Open(SigmaVendorId, log));
    }

    PtpTransport Ptp => _ptp ?? throw new NotConnectedException("Camera session is not open");

    public CameraInfo Open()
    {
        _ptp = new PtpTransport(_openPipe(), _log);
        var info = Ptp.Transaction(PtpTransport.OpGetDeviceInfo, []);
        Require(info, "GetDeviceInfo");
        var (model, serial, version) = ParseDeviceInfo(info.Data);
        Ptp.OpenSession();
        var cfg = Require(Ptp.Transaction(SigmaOp.ConfigApi, [0]), "ConfigApi");
        var tags = SigmaMessages.ParseApiConfigStrings(cfg.Data);
        var ci = new CameraInfo(tags.GetValueOrDefault((ushort)1, model), tags.GetValueOrDefault((ushort)2, serial),
            tags.TryGetValue(3, out var fw) ? $"{fw} (PTP {version})" : version);
        _log.LogInformation("Camera opened: {Model}, serial {Serial}, firmware {Firmware}", ci.Model, ci.Serial, ci.Firmware);
        return ci;
    }

    public void Close()
    {
        if (_ptp is null) return;
        try
        {
            // Leave the camera saving to its own card (its normal setting) so it is usable on its own afterwards.
            try { Send(SigmaOp.SetGroup3, SigmaMessages.SaveInCamera(), "SetGroup3 (save in camera)"); }
            catch (CameraProtocolException ex) { _log.LogWarning("Could not set save-in-camera before closing: {Msg}", ex.Message); }
            var r = _ptp.Transaction(SigmaOp.CloseApplication, [], SigmaMessages.TenZeros());
            if (!r.Ok) _log.LogWarning("CloseApplication returned 0x{Code:X4}", r.Code);
            _ptp.CloseSession();
        }
        finally
        {
            Dispose();
        }
    }

    public void ApplySettings(byte shutterCode, byte isoCode)
    {
        Send(SigmaOp.SetGroup2, SigmaMessages.ManualDng(), "SetGroup2 (M, DNG)");
        Send(SigmaOp.SetGroup1, SigmaMessages.ShutterAndIso(shutterCode, isoCode), $"SetGroup1 (shutter 0x{shutterCode:X2}, ISO 0x{isoCode:X2})");
        Send(SigmaOp.SetGroup4, SigmaMessages.Dng14Bit(), "SetGroup4 (DNG 14-bit)");
        Send(SigmaOp.SetGroup3, SigmaMessages.SaveToPc(), "SetGroup3 (save to PC)");
    }

    public byte PrepareCapture()
    {
        var s = GetStatus(0);
        for (int id = s.Head; id != s.Tail; id = (id + 1) & 0xFF)
        {
            var r = Ptp.Transaction(SigmaOp.ClearImageDBSingle, [(uint)id], SigmaMessages.TenZeros());
            _log.LogDebug("Cleared stale capture entry {Id}: 0x{Code:X4}", id, r.Code);
        }
        return GetStatus(0).Tail;
    }

    public void Snap() => Send(SigmaOp.SnapCommand, SigmaMessages.Snap(SigmaMessages.SnapNonAf), "Snap NonAF");

    public CaptStatus GetStatus(byte imageId) =>
        SigmaMessages.ParseCaptStatus(Require(Ptp.Transaction(SigmaOp.GetCaptStatus, [imageId]), "GetCaptStatus").Data);

    public PictFileInfo GetImageInfo() =>
        SigmaMessages.ParsePictFileInfo(Require(Ptp.Transaction(SigmaOp.GetPictFileInfo2, []), "GetPictFileInfo2").Data);

    public ReadOnlyMemory<byte> ReadImageChunk(uint address, uint offset, uint length) =>
        SigmaMessages.ParseImageChunk(Require(Ptp.Transaction(SigmaOp.GetBigPartialPictFile, [address, offset, length], timeoutMs: ChunkTimeoutMs), "GetBigPartialPictFile").Data);

    public void ClearImage(byte imageId) =>
        Require(Ptp.Transaction(SigmaOp.ClearImageDBSingle, [imageId], SigmaMessages.TenZeros()), "ClearImageDBSingle");

    public void Heartbeat() => Require(Ptp.Transaction(SigmaOp.GetGroup1, []), "GetGroup1");

    public void Dispose()
    {
        try { _ptp?.Dispose(); } catch (Exception ex) { _log.LogDebug(ex, "Ignoring error while releasing USB"); }
        _ptp = null;
    }

    void Send(ushort op, byte[] data, string what)
    {
        _log.LogDebug("{What}: {Bytes}", what, Convert.ToHexString(data));
        Require(Ptp.Transaction(op, [], data), what);
    }

    const ushort RespSessionNotOpen = 0x2003;

    static PtpResponse Require(PtpResponse r, string what) => r switch
    {
        { Ok: true } => r,
        // The camera restarted (e.g. power loss, or its delayed power-off after an exposure) and dropped our
        // session: treat it like a lost link so the controller reconnects at once instead of failing on.
        { Code: RespSessionNotOpen } => throw new CameraCommException($"{what}: camera session is no longer open (camera restarted?)"),
        _ => throw new CameraProtocolException($"{what} refused by camera: response 0x{r.Code:X4}", r.Code),
    };

    static (string model, string serial, string version) ParseDeviceInfo(byte[] d)
    {
        int p = 0;
        ushort U16() { var v = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(p)); p += 2; return v; }
        uint U32() { var v = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(p)); p += 4; return v; }
        string Str() { int n = d[p++]; var s = Encoding.Unicode.GetString(d, p, Math.Max(0, n * 2 - 2)); p += n * 2; return s; }
        void Arr() { uint n = U32(); p += (int)n * 2; }
        U16(); U32(); U16(); Str(); U16();
        Arr(); Arr(); Arr(); Arr(); Arr();
        Str(); // manufacturer
        string model = Str(), version = Str(), serial = Str();
        return (model, serial, version);
    }
}
