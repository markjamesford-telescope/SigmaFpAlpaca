using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using SigmaFp.Imaging;

// DngToTiff: batch-convert DNGs to 16-bit greyscale TIFFs WITHOUT demosaicing.
// Meant for mono-converted Sigma fp / fp L bodies, whose raw values are the real image.
//
//   DngToTiff <file-or-folder> [more...] [--out <folder>] [--ev <stops>] [--linear | --raw] [--full] [--recurse]
//     default  : display-ready: black subtracted, camera BaselineExposure (+ --ev) applied,
//                sRGB tone curve, cropped to the DNG DefaultCrop. Looks like a normal photo.
//     --linear : linear light (black subtracted, scaled to 0-65535), for astro/scientific work
//     --raw    : sensor values untouched (black level kept, 14-bit range)
//     --ev n   : extra exposure in stops (display and linear modes), e.g. --ev 1 or --ev -0.5
//     --full   : keep the full sensor area (no DefaultCrop)
//     --flat f : flat-field correction with a grey-card / flat DNG from the same camera (removes vignetting,
//                IR hot spot and dust spots): linear data are divided by the normalised, lightly smoothed flat
if (args.Length == 0 || args.Contains("-h") || args.Contains("--help"))
{
    Console.WriteLine("Usage: DngToTiff <file-or-folder> [...] [--out <folder>] [--ev <stops>] [--flat <flat.dng>] [--linear | --raw] [--full] [--recurse]");
    Console.WriteLine("  16-bit greyscale TIFF with no colour interpolation.");
    Console.WriteLine("  default: display-ready (camera exposure offset + sRGB curve). --linear: linear light. --raw: untouched.");
    Console.WriteLine("  --flat <grey-card.dng>: flat-field correction (vignetting, hot spot, dust) with a DNG from the same camera.");
    return 1;
}

bool raw = args.Contains("--raw"), linear = args.Contains("--linear"), full = args.Contains("--full"), recurse = args.Contains("--recurse");
string? Opt(string name) { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
string? outDir = Opt("--out");
double ev = double.TryParse(Opt("--ev"), NumberStyles.Float, CultureInfo.InvariantCulture, out var e) ? e : 0;
string? flatPath = Opt("--flat");
var valueArgs = new HashSet<int>(new[] { "--out", "--ev", "--flat" }.Select(n => Array.IndexOf(args, n)).Where(i => i >= 0).Select(i => i + 1));
var inputs = args.Where((a, i) => !a.StartsWith("--") && !valueArgs.Contains(i)).ToList();
string mode = raw ? "raw" : linear ? "linear" : "display";

var files = inputs.SelectMany(p => Directory.Exists(p)
        ? Directory.EnumerateFiles(p, "*.*", recurse ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
            .Where(f => f.EndsWith(".dng", StringComparison.OrdinalIgnoreCase))
        : File.Exists(p) ? [p] : Enumerable.Empty<string>())
    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
    .ToList();
if (files.Count == 0) { Console.Error.WriteLine("No DNG files found."); return 1; }
Console.WriteLine($"{files.Count} DNG file(s), mode: {mode}{(ev != 0 ? $", {ev:+0.##;-0.##} EV" : "")}{(flatPath is null ? "" : $", flat: {Path.GetFileName(flatPath)}")}");

FlatField? flat = null;
if (flatPath is not null)
{
    if (!File.Exists(flatPath)) { Console.Error.WriteLine($"Flat file not found: {flatPath}"); return 1; }
    var fsw = Stopwatch.StartNew();
    flat = FlatField.Load(flatPath);
    Console.WriteLine($"Flat: {flat.Width}x{flat.Height}, centre {flat.CentreLevel:0} ADU above black, corners at {flat.CornerRatio:P0} of centre, gain {flat.MinGain:0.00}-{flat.MaxGain:0.00} ({fsw.ElapsedMilliseconds} ms)");
}

int ok = 0, failed = 0;
foreach (var f in files)
{
    var sw = Stopwatch.StartNew();
    try
    {
        var img = DngReader.Read(File.ReadAllBytes(f));
        var (cx, cy, cw, ch) = full ? (0, 0, img.Width, img.Height) : img.DefaultCrop;
        if (flat is not null && (flat.Width != img.Width || flat.Height != img.Height))
            throw new InvalidOperationException($"flat is {flat.Width}x{flat.Height} but this image is {img.Width}x{img.Height} (different camera?)");
        var px = Convert(img, cx, cy, cw, ch, mode, ev, flat?.Gain);
        string target = Path.Combine(outDir ?? Path.GetDirectoryName(Path.GetFullPath(f))!, Path.GetFileNameWithoutExtension(f) + ".tif");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        Tiff16.Write(target, px, cw, ch,
            $"DngToTiff (SigmaFpAlpaca): {mode}{(mode == "display" ? $" (baseline {img.BaselineExposure:+0.##;-0.##} EV, sRGB curve)" : "")}{(ev != 0 ? $", {ev:+0.##;-0.##} EV" : "")}{(flat is null ? "" : $", flat {Path.GetFileName(flatPath)}")}, no demosaic; {img.Model}");
        Console.WriteLine($"OK   {Path.GetFileName(f)} -> {Path.GetFileName(target)}  {cw}x{ch}  ({sw.ElapsedMilliseconds} ms)");
        ok++;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"FAIL {Path.GetFileName(f)}: {ex.Message}");
        failed++;
    }
}
Console.WriteLine($"{ok} converted, {failed} failed");
if (args.Length > 0 && inputs.Count == args.Length && !Console.IsInputRedirected)
{
    // Started by drag-and-drop (no options): keep the window open so the result can be read.
    Console.WriteLine("Press Enter to close.");
    Console.ReadLine();
}
return failed == 0 ? 0 : 1;

static ushort[] Convert(RawImage img, int cx, int cy, int cw, int ch, string mode, double ev, float[]? gain)
{
    if (gain is not null) return ConvertFlat(img, cx, cy, cw, ch, mode, ev, gain);
    var px = new ushort[cw * ch];
    double black = img.BlackLevel, range = Math.Max(1, img.WhiteLevel - img.BlackLevel);
    double gainDisplay = Math.Pow(2, img.BaselineExposure + ev) / range;
    double gainLinear = Math.Pow(2, ev) * 65535.0 / range;

    // Display mode via a lookup table over all possible raw values (fast and exact).
    ushort[]? lut = null;
    if (mode == "display")
    {
        lut = new ushort[65536];
        for (int v = 0; v < lut.Length; v++)
        {
            double l = Math.Clamp((v - black) * gainDisplay, 0, 1);
            double s = l <= 0.0031308 ? 12.92 * l : 1.055 * Math.Pow(l, 1 / 2.4) - 0.055; // sRGB curve
            lut[v] = (ushort)Math.Round(s * 65535);
        }
    }

    Parallel.For(0, ch, y =>
    {
        int src = (cy + y) * img.Width + cx, dst = y * cw;
        for (int x = 0; x < cw; x++)
        {
            ushort v = img.Pixels[src + x];
            px[dst + x] = mode switch
            {
                "raw" => v,
                "linear" => (ushort)Math.Clamp(Math.Round((v - black) * gainLinear), 0, 65535),
                _ => lut![v],
            };
        }
    });
    return px;
}

/// <summary>Flat-fielded conversion: linear values (black subtracted) multiplied by the flat gain.</summary>
static ushort[] ConvertFlat(RawImage img, int cx, int cy, int cw, int ch, string mode, double ev, float[] gain)
{
    var px = new ushort[cw * ch];
    double black = img.BlackLevel, range = Math.Max(1, img.WhiteLevel - img.BlackLevel);
    double gainDisplay = Math.Pow(2, img.BaselineExposure + ev) / range;
    double gainLinear = Math.Pow(2, ev) * 65535.0 / range;
    Parallel.For(0, ch, y =>
    {
        int src = (cy + y) * img.Width + cx, dst = y * cw;
        for (int x = 0; x < cw; x++)
        {
            double lin = (img.Pixels[src + x] - black) * gain[src + x]; // ADU above black, flat-corrected
            px[dst + x] = mode switch
            {
                "raw" => (ushort)Math.Clamp(Math.Round(black + lin), 0, 65535),
                "linear" => (ushort)Math.Clamp(Math.Round(lin * gainLinear), 0, 65535),
                _ => Srgb16(Math.Clamp(lin * gainDisplay, 0, 1)),
            };
        }
    });
    return px;
}

static ushort Srgb16(double l) =>
    (ushort)Math.Round((l <= 0.0031308 ? 12.92 * l : 1.055 * Math.Pow(l, 1 / 2.4) - 0.055) * 65535);

/// <summary>
/// Flat field from a grey-card/flat DNG: black subtracted, smoothed 5x5 over same-CFA-phase pixels
/// (step 2: keeps dust spots and the IR hot spot, drops the flat's own noise), normalised per 2x2
/// phase to the mean of the central 20% of the frame. Gain = centre / flat, limited to 0.2-5.
/// </summary>
sealed class FlatField
{
    public required int Width, Height;
    public required float[] Gain;
    public double CentreLevel, CornerRatio, MinGain, MaxGain;

    public static FlatField Load(string path)
    {
        var f = DngReader.Read(File.ReadAllBytes(path));
        int w = f.Width, h = f.Height;
        double black = f.BlackLevel;
        var lin = new float[w * h];
        Parallel.For(0, h, y => { for (int x = 0; x < w; x++) lin[y * w + x] = (float)Math.Max(0, f.Pixels[y * w + x] - black); });

        var tmp = new float[w * h];
        var smooth = new float[w * h];
        Parallel.For(0, h, y =>
        {
            for (int x = 0; x < w; x++)
            {
                double sum = 0; int n = 0;
                for (int d = -4; d <= 4; d += 2) { int xx = x + d; if (xx >= 0 && xx < w) { sum += lin[y * w + xx]; n++; } }
                tmp[y * w + x] = (float)(sum / n);
            }
        });
        Parallel.For(0, h, y =>
        {
            for (int x = 0; x < w; x++)
            {
                double sum = 0; int n = 0;
                for (int d = -4; d <= 4; d += 2) { int yy = y + d; if (yy >= 0 && yy < h) { sum += tmp[yy * w + x]; n++; } }
                smooth[y * w + x] = (float)(sum / n);
            }
        });

        var centre = new double[4];
        var count = new long[4];
        for (int y = (int)(h * 0.4); y < (int)(h * 0.6); y++)
            for (int x = (int)(w * 0.4); x < (int)(w * 0.6); x++)
            {
                int ph = (y & 1) * 2 + (x & 1);
                centre[ph] += smooth[y * w + x];
                count[ph]++;
            }
        for (int i = 0; i < 4; i++) centre[i] /= Math.Max(1, count[i]);
        double centreMean = centre.Average();
        if (centreMean < 50) throw new InvalidOperationException($"flat is too dark ({centreMean:0} ADU above black at the centre)");

        var gain = new float[w * h];
        Parallel.For(0, h, y =>
        {
            for (int x = 0; x < w; x++)
            {
                int ph = (y & 1) * 2 + (x & 1);
                double v = smooth[y * w + x];
                gain[y * w + x] = (float)Math.Clamp(v > 1 ? centre[ph] / v : 5, 0.2, 5);
            }
        });

        double corner = (smooth[8 * w + 8] + smooth[8 * w + w - 9] + smooth[(h - 9) * w + 8] + smooth[(h - 9) * w + w - 9]) / 4;
        return new FlatField
        {
            Width = w, Height = h, Gain = gain, CentreLevel = centreMean, CornerRatio = corner / centreMean,
            MinGain = gain.Min(), MaxGain = gain.Max(),
        };
    }
}

/// <summary>Baseline TIFF: little-endian, 1 x 16-bit sample, uncompressed, one strip, BlackIsZero, 300 dpi.</summary>
static class Tiff16
{
    public static void Write(string path, ushort[] px, int w, int h, string software)
    {
        var sw = System.Text.Encoding.ASCII.GetBytes(software + "\0");
        var entries = new List<(ushort Tag, ushort Type, uint Count, uint Value)>();
        const int ifdOffset = 8, nEntries = 16;
        int ifdSize = 2 + 12 * nEntries + 4;
        uint resOffset = (uint)(ifdOffset + ifdSize);              // two RATIONALs (X/Y resolution)
        uint softwareOffset = resOffset + 16;
        uint dataOffset = (uint)(softwareOffset + sw.Length + (sw.Length & 1));
        uint dataBytes = (uint)(px.Length * 2);
        entries.Add((254, 4, 1, 0));                  // NewSubfileType
        entries.Add((256, 4, 1, (uint)w));            // ImageWidth
        entries.Add((257, 4, 1, (uint)h));            // ImageLength
        entries.Add((258, 3, 1, 16));                 // BitsPerSample
        entries.Add((259, 3, 1, 1));                  // Compression: none
        entries.Add((262, 3, 1, 1));                  // Photometric: BlackIsZero
        entries.Add((273, 4, 1, dataOffset));         // StripOffsets
        entries.Add((274, 3, 1, 1));                  // Orientation
        entries.Add((277, 3, 1, 1));                  // SamplesPerPixel
        entries.Add((278, 4, 1, (uint)h));            // RowsPerStrip
        entries.Add((279, 4, 1, dataBytes));          // StripByteCounts
        entries.Add((282, 5, 1, resOffset));          // XResolution
        entries.Add((283, 5, 1, resOffset + 8));      // YResolution
        entries.Add((284, 3, 1, 1));                  // PlanarConfiguration: chunky
        entries.Add((296, 3, 1, 2));                  // ResolutionUnit: inch
        entries.Add((305, 2, (uint)sw.Length, softwareOffset)); // Software
        entries.Sort((a, b) => a.Tag.CompareTo(b.Tag));

        using var fs = File.Create(path);
        var head = new byte[dataOffset];
        head[0] = 0x49; head[1] = 0x49; head[2] = 42; head[3] = 0;
        BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(4), ifdOffset);
        BinaryPrimitives.WriteUInt16LittleEndian(head.AsSpan(ifdOffset), (ushort)entries.Count);
        for (int i = 0; i < entries.Count; i++)
        {
            var e = head.AsSpan(ifdOffset + 2 + 12 * i, 12);
            BinaryPrimitives.WriteUInt16LittleEndian(e, entries[i].Tag);
            BinaryPrimitives.WriteUInt16LittleEndian(e[2..], entries[i].Type);
            BinaryPrimitives.WriteUInt32LittleEndian(e[4..], entries[i].Count);
            if (entries[i].Type == 3 && entries[i].Count == 1) BinaryPrimitives.WriteUInt16LittleEndian(e[8..], (ushort)entries[i].Value);
            else BinaryPrimitives.WriteUInt32LittleEndian(e[8..], entries[i].Value);
        }
        foreach (int off in new[] { (int)resOffset, (int)resOffset + 8 })
        {
            BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(off), 300);
            BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(off + 4), 1);
        }
        sw.CopyTo(head, (int)softwareOffset);
        fs.Write(head);
        var bytes = new byte[dataBytes];
        Buffer.BlockCopy(px, 0, bytes, 0, bytes.Length); // x64 little-endian
        fs.Write(bytes);
    }
}
