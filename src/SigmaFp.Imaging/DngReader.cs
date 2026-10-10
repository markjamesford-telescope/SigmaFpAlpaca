using System.Buffers.Binary;
using System.Text;

namespace SigmaFp.Imaging;

/// <summary>Raw sensor data from a DNG, untouched (no black subtraction, scaling or demosaicing).</summary>
public sealed class RawImage
{
    public required int Width { get; init; }
    public required int Height { get; init; }
    /// <summary>Row-major sensor values, <c>Pixels[y * Width + x]</c>.</summary>
    public required ushort[] Pixels { get; init; }
    public required int BitsPerSample { get; init; }
    public required int WhiteLevel { get; init; }
    public required double BlackLevel { get; init; }
    /// <summary>2x2 CFA as DNG colour indices (0=R,1=G,2=B) in row-major order, or null for LinearRaw.</summary>
    public byte[]? CfaPattern { get; init; }
    public string Make { get; init; } = "";
    public string Model { get; init; } = "";
    public double? ExposureSeconds { get; init; }
    /// <summary>DNG DefaultCrop (x, y, width, height): the area meant to be shown; full frame if absent.</summary>
    public (int X, int Y, int Width, int Height) DefaultCrop { get; init; }
    /// <summary>DNG BaselineExposure in EV (fp ~ +1.0, fp L ~ +0.94): the lift raw converters apply for normal brightness.</summary>
    public double BaselineExposure { get; init; }
    public int? Iso { get; init; }

    public ushort this[int x, int y] => Pixels[y * Width + x];

    /// <summary>"RGGB", "GRBG", "GBRG", "BGGR" (or null) from the CFA pattern.</summary>
    public string? CfaName => CfaPattern is { Length: 4 } p ? string.Concat(p.Select(c => "RGB"[c])) : null;
}

/// <summary>
/// Minimal DNG reader for camera raw files: TIFF IFD walk, raw SubIFD (NewSubFileType 0),
/// tiled or striped lossless-JPEG (compression 7) or uncompressed 16-bit (compression 1) data.
/// </summary>
public static class DngReader
{
    const ushort TagNewSubFileType = 254, TagWidth = 256, TagHeight = 257, TagBits = 258, TagCompression = 259,
        TagMake = 271, TagModel = 272, TagStripOffsets = 273, TagRowsPerStrip = 278, TagStripByteCounts = 279,
        TagTileWidth = 322, TagTileLength = 323, TagTileOffsets = 324, TagTileByteCounts = 325, TagSubIfds = 330,
        TagExif = 34665, TagCfaRepeat = 33421, TagCfaPattern = 33422, TagBlackLevel = 50714, TagWhiteLevel = 50717,
        TagDngVersion = 50706, TagExposureTime = 33434, TagIso = 34855, TagCropOrigin = 50719, TagCropSize = 50720, TagBaselineExposure = 50730;

    sealed class Ifd(Dictionary<ushort, (ushort Type, uint Count, int Offset)> entries)
    {
        public readonly Dictionary<ushort, (ushort Type, uint Count, int Offset)> Entries = entries;
        public bool Has(ushort tag) => Entries.ContainsKey(tag);
    }

    public static RawImage Read(byte[] file)
    {
        var d = file.AsSpan();
        if (d.Length < 8 || d[0] != 0x49 || d[1] != 0x49 || BinaryPrimitives.ReadUInt16LittleEndian(d[2..]) != 42)
            throw new ImageFormatException("Not a little-endian TIFF/DNG file");

        var ifd0 = ReadIfd(file, (int)BinaryPrimitives.ReadUInt32LittleEndian(d[4..]));
        if (!ifd0.Has(TagDngVersion)) throw new ImageFormatException("TIFF without DNGVersion tag");

        var candidates = new List<Ifd> { ifd0 };
        if (ifd0.Entries.ContainsKey(TagSubIfds))
            candidates.AddRange(UInts(file, ifd0, TagSubIfds).Select(o => ReadIfd(file, (int)o)));
        var raw = candidates.FirstOrDefault(i => (i.Has(TagNewSubFileType) ? UInts(file, i, TagNewSubFileType)[0] : 0) == 0)
                  ?? throw new ImageFormatException("No full-resolution raw image (NewSubFileType 0)");

        int width = (int)UInts(file, raw, TagWidth)[0], height = (int)UInts(file, raw, TagHeight)[0];
        int bits = (int)UInts(file, raw, TagBits)[0];
        int compression = (int)UInts(file, raw, TagCompression)[0];
        var pixels = new ushort[(long)width * height];

        if (raw.Has(TagTileOffsets))
            DecodeTiles(file, raw, width, height, compression, pixels);
        else
            DecodeStrips(file, raw, width, height, compression, pixels);

        byte[]? cfa = null;
        if (raw.Has(TagCfaPattern))
        {
            var dim = raw.Has(TagCfaRepeat) ? UInts(file, raw, TagCfaRepeat) : [2, 2];
            if (dim is [2, 2]) cfa = Bytes(file, raw, TagCfaPattern);
        }

        double black = raw.Has(TagBlackLevel) ? Numbers(file, raw, TagBlackLevel).Average() : 0;
        int white = raw.Has(TagWhiteLevel) ? (int)UInts(file, raw, TagWhiteLevel)[0] : (1 << bits) - 1;

        var crop = (0, 0, width, height);
        if (raw.Has(TagCropOrigin) && raw.Has(TagCropSize))
        {
            var o = Numbers(file, raw, TagCropOrigin); var z = Numbers(file, raw, TagCropSize);
            crop = ((int)o[0], (int)o[1], (int)z[0], (int)z[1]);
        }

        double baseline = ifd0.Has(TagBaselineExposure) ? Numbers(file, ifd0, TagBaselineExposure)[0]
                        : raw.Has(TagBaselineExposure) ? Numbers(file, raw, TagBaselineExposure)[0] : 0;

        double? exposure = null; int? iso = null;
        if (ifd0.Has(TagExif))
        {
            var exif = ReadIfd(file, (int)UInts(file, ifd0, TagExif)[0]);
            if (exif.Has(TagExposureTime)) exposure = Numbers(file, exif, TagExposureTime)[0];
            if (exif.Has(TagIso)) iso = (int)UInts(file, exif, TagIso)[0];
        }

        return new RawImage
        {
            Width = width, Height = height, Pixels = pixels, BitsPerSample = bits, WhiteLevel = white, BlackLevel = black,
            CfaPattern = cfa, Make = Ascii(file, ifd0, TagMake), Model = Ascii(file, ifd0, TagModel),
            ExposureSeconds = exposure, Iso = iso, DefaultCrop = crop, BaselineExposure = baseline,
        };
    }

    static void DecodeTiles(byte[] file, Ifd raw, int width, int height, int compression, ushort[] pixels)
    {
        int tw = (int)UInts(file, raw, TagTileWidth)[0], th = (int)UInts(file, raw, TagTileLength)[0];
        var offsets = UInts(file, raw, TagTileOffsets);
        var counts = UInts(file, raw, TagTileByteCounts);
        int across = (width + tw - 1) / tw, down = (height + th - 1) / th;
        if (offsets.Length != across * down) throw new ImageFormatException($"Expected {across * down} tiles, found {offsets.Length}");
        if (compression != 7) throw new ImageFormatException($"Unsupported tile compression {compression}");

        Parallel.For(0, offsets.Length, t =>
        {
            var src = file.AsSpan((int)offsets[t], (int)counts[t]);
            var f = LosslessJpegDecoder.ReadFrame(src);
            var tile = new ushort[f.Width * f.Height * f.Components];
            LosslessJpegDecoder.Decode(src, tile);
            if (tile.Length < tw * th) throw new ImageFormatException($"Tile {t} decodes to {tile.Length} samples, need {tw * th}");
            int x0 = t % across * tw, y0 = t / across * th;
            int copyW = Math.Min(tw, width - x0), copyH = Math.Min(th, height - y0);
            for (int ty = 0; ty < copyH; ty++)
                tile.AsSpan(ty * tw, copyW).CopyTo(pixels.AsSpan((y0 + ty) * width + x0, copyW));
        });
    }

    static void DecodeStrips(byte[] file, Ifd raw, int width, int height, int compression, ushort[] pixels)
    {
        var offsets = UInts(file, raw, TagStripOffsets);
        var counts = UInts(file, raw, TagStripByteCounts);
        int rows = raw.Has(TagRowsPerStrip) ? (int)UInts(file, raw, TagRowsPerStrip)[0] : height;
        for (int s = 0; s < offsets.Length; s++)
        {
            var src = file.AsSpan((int)offsets[s], (int)counts[s]);
            int y0 = s * rows, h = Math.Min(rows, height - y0);
            var dst = pixels.AsSpan(y0 * width, h * width);
            if (compression == 1)
            {
                for (int i = 0; i < dst.Length; i++) dst[i] = BinaryPrimitives.ReadUInt16LittleEndian(src[(2 * i)..]);
            }
            else if (compression == 7)
            {
                var f = LosslessJpegDecoder.ReadFrame(src);
                var tmp = new ushort[f.Width * f.Height * f.Components];
                LosslessJpegDecoder.Decode(src, tmp);
                tmp.AsSpan(0, dst.Length).CopyTo(dst);
            }
            else throw new ImageFormatException($"Unsupported strip compression {compression}");
        }
    }

    static Ifd ReadIfd(byte[] f, int offset)
    {
        int n = BinaryPrimitives.ReadUInt16LittleEndian(f.AsSpan(offset));
        var e = new Dictionary<ushort, (ushort, uint, int)>();
        for (int i = 0; i < n; i++)
        {
            var p = f.AsSpan(offset + 2 + 12 * i, 12);
            ushort tag = BinaryPrimitives.ReadUInt16LittleEndian(p), type = BinaryPrimitives.ReadUInt16LittleEndian(p[2..]);
            uint count = BinaryPrimitives.ReadUInt32LittleEndian(p[4..]);
            long size = TypeSize(type) * (long)count;
            int dataOff = size <= 4 ? offset + 2 + 12 * i + 8 : (int)BinaryPrimitives.ReadUInt32LittleEndian(p[8..]);
            e[tag] = (type, count, dataOff);
        }
        return new Ifd(e);
    }

    static int TypeSize(ushort t) => t switch { 1 or 2 or 6 or 7 => 1, 3 or 8 => 2, 4 or 9 or 11 or 13 => 4, 5 or 10 or 12 => 8, _ => 1 };

    static uint[] UInts(byte[] f, Ifd ifd, ushort tag)
    {
        var (type, count, off) = ifd.Entries[tag];
        var r = new uint[count];
        for (int i = 0; i < count; i++)
            r[i] = type switch
            {
                1 or 7 => f[off + i],
                3 => BinaryPrimitives.ReadUInt16LittleEndian(f.AsSpan(off + 2 * i)),
                4 or 13 => BinaryPrimitives.ReadUInt32LittleEndian(f.AsSpan(off + 4 * i)),
                _ => throw new ImageFormatException($"Tag {tag}: unexpected type {type}"),
            };
        return r;
    }

    static double[] Numbers(byte[] f, Ifd ifd, ushort tag)
    {
        var (type, count, off) = ifd.Entries[tag];
        if (type is 5 or 10)
        {
            var r = new double[count];
            for (int i = 0; i < count; i++)
            {
                double num = type == 5 ? BinaryPrimitives.ReadUInt32LittleEndian(f.AsSpan(off + 8 * i)) : BinaryPrimitives.ReadInt32LittleEndian(f.AsSpan(off + 8 * i));
                double den = type == 5 ? BinaryPrimitives.ReadUInt32LittleEndian(f.AsSpan(off + 8 * i + 4)) : BinaryPrimitives.ReadInt32LittleEndian(f.AsSpan(off + 8 * i + 4));
                r[i] = den == 0 ? 0 : num / den;
            }
            return r;
        }
        return UInts(f, ifd, tag).Select(u => (double)u).ToArray();
    }

    static byte[] Bytes(byte[] f, Ifd ifd, ushort tag)
    {
        var (_, count, off) = ifd.Entries[tag];
        return f.AsSpan(off, (int)count).ToArray();
    }

    static string Ascii(byte[] f, Ifd ifd, ushort tag) =>
        ifd.Entries.TryGetValue(tag, out var e) ? Encoding.ASCII.GetString(f, e.Offset, (int)e.Count).TrimEnd('\0', ' ') : "";
}
