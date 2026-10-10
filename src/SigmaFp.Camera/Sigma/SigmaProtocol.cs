using System.Buffers.Binary;
using System.Text;

namespace SigmaFp.Camera.Sigma;

/// <summary>Sigma vendor PTP operation codes (docs/sigma-ptp-protocol.md).</summary>
public static class SigmaOp
{
    public const ushort GetGroup1 = 0x9012, GetCaptStatus = 0x9015, SetGroup1 = 0x9016, SetGroup2 = 0x9017,
        SetGroup3 = 0x9018, SnapCommand = 0x901B, ClearImageDBSingle = 0x901C, GetBigPartialPictFile = 0x9022,
        SetGroup4 = 0x9024, GetPictFileInfo2 = 0x902D, CloseApplication = 0x902F, GetFocus = 0x9031,
        SetFocus = 0x9032, ConfigApi = 0x9035;
}

public static class CaptStatusCode
{
    public const ushort Cleared = 0x0000, ShootInProgress = 0x0001, ShootSuccess = 0x0002, ImageGenInProgress = 0x0004,
        ImageGenCompleted = 0x0005, ImageDataStorageCompleted = 0x8003, AfFailed = 0x6001, BufferFull = 0x6002,
        ImageGenFailed = 0x6004, Failed = 0x6005;

    public static bool IsDone(ushort s) => s is ImageGenCompleted or ImageDataStorageCompleted;
    public static bool IsFailure(ushort s) => (s & 0xF000) == 0x6000;

    public static string Describe(ushort s) => s switch
    {
        Cleared => "cleared",
        ShootInProgress => "exposing",
        ShootSuccess => "shot done",
        ImageGenInProgress => "processing",
        ImageGenCompleted => "image ready",
        ImageDataStorageCompleted => "image stored",
        AfFailed => "AF failure",
        BufferFull => "buffer full",
        ImageGenFailed => "image generation failed: the camera is locked up; switch it off and on, then take the next frame",
        Failed => "general failure",
        _ => $"0x{s:X4}",
    };
}

public readonly record struct CaptStatus(byte ImageId, byte Head, byte Tail, ushort Status);

public readonly record struct PictFileInfo(uint Address, uint Size, string Format, ushort Width, ushort Height, string Path, string Name);

/// <summary>
/// Encoders/decoders for Sigma vendor messages. SET messages are
/// <c>[0x00][FieldPresent u16 BE][present fields][0x00]</c>, byte-identical to the old driver's library.
/// </summary>
public static class SigmaMessages
{
    public const byte SnapNonAf = 0x02, SnapStart = 0x06, SnapStop = 0x07;
    public const byte ShutterBulb = 0x08;

    public static byte[] Group(ushort fieldPresent, params byte[] fields) =>
        [0x00, (byte)(fieldPresent >> 8), (byte)fieldPresent, .. fields, 0x00];

    /// <summary>Group1: ShutterSpeed (0x0100), ISOAuto=manual (0x0800), ISOSpeed (0x1000).</summary>
    public static byte[] ShutterAndIso(byte shutterCode, byte isoCode) => Group(0x0100 | 0x0800 | 0x1000, shutterCode, 0, isoCode);

    /// <summary>Group2: ExposureMode=M (0x0400), ImageQuality=DNG (0x0080).</summary>
    public static byte[] ManualDng() => Group(0x0400 | 0x0080, 4, 0x10);

    /// <summary>Group3: DestToSave=PC (0x0080).</summary>
    public static byte[] SaveToPc() => Group(0x0080, 2);

    /// <summary>Group3: DestToSave=InCamera (0x0080). Restored on disconnect so the camera stays usable on its own.</summary>
    public static byte[] SaveInCamera() => Group(0x0080, 1);

    /// <summary>Group4: DNG 14-bit. The old library always added the 6 lens-correction bytes (flag 0x0008); kept identical.</summary>
    public static byte[] Dng14Bit() => Group(0x0002 | 0x0008, 14, 0, 0, 0, 0, 0, 0);

    public static byte[] Snap(byte mode) => [0x00, mode, 0x01, 0x00];

    /// <summary>ClearImageDBSingle and CloseApplication carry 10 zero bytes.</summary>
    public static byte[] TenZeros() => new byte[10];

    public static CaptStatus ParseCaptStatus(ReadOnlySpan<byte> d)
    {
        if (d.Length < 7) throw new CameraCommException($"CaptStatus too short ({d.Length} bytes)");
        return new CaptStatus(d[1], d[2], d[3], BinaryPrimitives.ReadUInt16LittleEndian(d[4..]));
    }

    public static PictFileInfo ParsePictFileInfo(ReadOnlySpan<byte> d)
    {
        if (d.Length < 38) throw new CameraProtocolException($"No image file information ({d.Length} bytes)");
        uint address = BinaryPrimitives.ReadUInt32LittleEndian(d[12..]);
        uint size = BinaryPrimitives.ReadUInt32LittleEndian(d[16..]);
        string fmt = Encoding.ASCII.GetString(d.Slice(28, 4)).TrimEnd('\0');
        ushort w = BinaryPrimitives.ReadUInt16LittleEndian(d[32..]), h = BinaryPrimitives.ReadUInt16LittleEndian(d[34..]);
        var strings = Encoding.ASCII.GetString(d[36..]).Split('\0');
        return new PictFileInfo(address, size, fmt, w, h, strings[0], strings.Length > 1 ? strings[1] : "");
    }

    /// <summary>
    /// GetBigPartialPictFile payload: <c>[AcquiredSize u32][image bytes][~28-33 byte trailer]</c>.
    /// Returns only the image bytes (the trailer is not image data).
    /// </summary>
    public static ReadOnlyMemory<byte> ParseImageChunk(byte[] payload)
    {
        if (payload.Length < 4) throw new CameraCommException("Image chunk too short");
        uint acquired = BinaryPrimitives.ReadUInt32LittleEndian(payload);
        if (acquired == 0 || acquired > payload.Length - 4)
            throw new CameraCommException($"Bad AcquiredSize {acquired} (payload {payload.Length - 4})");
        return payload.AsMemory(4, (int)acquired);
    }

    /// <summary>Parses the ApiConfig directory (tag 1 model, 2 serial, 3 firmware).</summary>
    public static Dictionary<ushort, string> ParseApiConfigStrings(ReadOnlySpan<byte> d)
    {
        var result = new Dictionary<ushort, string>();
        if (d.Length < 8) return result;
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(d[4..]);
        for (int i = 0; i < count && 8 + 12 * i + 12 <= d.Length; i++)
        {
            var e = d.Slice(8 + 12 * i, 12);
            ushort tag = BinaryPrimitives.ReadUInt16LittleEndian(e);
            ushort type = BinaryPrimitives.ReadUInt16LittleEndian(e[2..]);
            uint n = BinaryPrimitives.ReadUInt32LittleEndian(e[4..]);
            uint value = BinaryPrimitives.ReadUInt32LittleEndian(e[8..]);
            if (type != 2) continue; // ASCII only
            ReadOnlySpan<byte> s = n <= 4 ? e.Slice(8, (int)n) : (value + n <= d.Length ? d.Slice((int)value, (int)n) : default);
            result[tag] = Encoding.ASCII.GetString(s).TrimEnd('\0');
        }
        return result;
    }
}

/// <summary>APEX code tables from sigma_ptpy (ISOSpeedConverter, ShutterSpeed3Converter).</summary>
public static class Apex
{
    static readonly (byte Code, double Iso)[] IsoTable =
    {
        (0,6),(3,8),(5,10),(8,12),(11,16),(13,20),(16,25),(19,32),(21,40),(24,50),(27,64),(29,80),(32,100),
        (35,125),(37,160),(40,200),(43,250),(45,320),(48,400),(51,500),(53,640),(56,800),(59,1000),(61,1250),
        (64,1600),(67,2000),(69,2500),(72,3200),(75,4000),(77,5000),(80,6400),(83,8000),(85,10000),(88,12800),
        (91,16000),(93,20000),(96,25600),
    };

    static readonly (byte Code, double Seconds)[] ShutterTable =
    {
        (16,30),(19,25),(21,20),(24,15),(27,13),(29,10),(32,8),(35,6),(37,5),(40,4),(43,3.2),(45,2.5),(48,2),
        (51,1.6),(53,1.3),(56,1),(59,0.8),(61,0.6),(64,0.5),(67,0.4),(69,0.3),(72,0.25),(75,0.2),(77,1/6.0),
        (80,0.125),(83,0.1),(85,1/13.0),(88,1/15.0),(91,0.05),(93,0.04),(96,1/30.0),(99,0.025),(101,0.02),
        (104,1/60.0),(107,0.0125),(109,0.01),(112,0.008),(115,0.00625),(117,0.005),(120,0.004),(123,0.003125),
        (125,0.0025),(128,0.002),(131,0.0015625),(133,0.00125),(136,0.001),(139,0.0008),(141,0.000625),
        (144,0.0005),(147,0.0004),(149,0.0003125),(152,0.00025),(155,0.0002),(157,1/6000.0),(160,0.000125),
    };

    /// <summary>ISO values the camera accepts (ascending); index = ASCOM Gain.</summary>
    public static IReadOnlyList<int> IsoValues { get; } = IsoTable.Select(e => (int)e.Iso).ToArray();

    public const double MinTimedSeconds = 0.000125, MaxTimedSeconds = 30;

    public static byte IsoCode(int iso)
    {
        foreach (var e in IsoTable) if ((int)e.Iso == iso) return e.Code;
        throw new ArgumentOutOfRangeException(nameof(iso), iso, "ISO not supported by the camera");
    }

    /// <summary>Nearest shutter step to <paramref name="seconds"/> (0.000125-30 s).</summary>
    public static (byte Code, double Seconds) Shutter(double seconds)
    {
        if (seconds < MinTimedSeconds * 0.5 || seconds > MaxTimedSeconds)
            throw new ArgumentOutOfRangeException(nameof(seconds), seconds, "Timed exposures are 1/8000-30 s");
        // Compare on a log scale so short exposures pick the right neighbour.
        return ShutterTable.MinBy(e => Math.Abs(Math.Log(e.Seconds) - Math.Log(seconds)));
    }
}
