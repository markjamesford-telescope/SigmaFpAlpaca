namespace SigmaFp.Imaging.Tests;

/// <summary>Round-trips through a small reference encoder covering predictors, components, restarts, stuffing.</summary>
public class LosslessJpegTests
{
    /// <summary>Minimal LJ92 encoder: one shared Huffman table where every category 0..16 has a 5-bit code.</summary>
    static byte[] Encode(ushort[] samples, int w, int h, int nf, int precision, int predictor, int restart = 0)
    {
        var o = new List<byte> { 0xFF, 0xD8 };
        // DHT: table 0, 17 symbols of length 5
        var counts = new byte[16]; counts[4] = 17;
        o.AddRange([0xFF, 0xC4, 0, (byte)(2 + 1 + 16 + 17), 0x00]);
        o.AddRange(counts);
        for (byte s = 0; s <= 16; s++) o.Add(s);
        // SOF3
        int sofLen = 8 + 3 * nf;
        o.AddRange([0xFF, 0xC3, 0, (byte)sofLen, (byte)precision, (byte)(h >> 8), (byte)h, (byte)(w >> 8), (byte)w, (byte)nf]);
        for (int c = 0; c < nf; c++) o.AddRange([(byte)(c + 1), 0x11, 0]);
        if (restart > 0) o.AddRange([0xFF, 0xDD, 0, 4, (byte)(restart >> 8), (byte)restart]);
        // SOS
        o.AddRange([0xFF, 0xDA, 0, (byte)(6 + 2 * nf), (byte)nf]);
        for (int c = 0; c < nf; c++) o.AddRange([(byte)(c + 1), 0x00]);
        o.AddRange([(byte)predictor, 0, 0]);

        ulong acc = 0; int nbits = 0; int rst = 0;
        void Put(int value, int n)
        {
            for (int i = n - 1; i >= 0; i--)
            {
                acc = (acc << 1) | (uint)((value >> i) & 1); nbits++;
                if (nbits == 8) { byte b = (byte)acc; o.Add(b); if (b == 0xFF) o.Add(0); acc = 0; nbits = 0; }
            }
        }
        void Flush() { while (nbits != 0) Put(1, 1); }

        int line = w * nf, initial = 1 << (precision - 1), mask = (1 << precision) - 1, mcu = 0;
        bool reset = true, firstLine = false;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                if (restart > 0 && mcu > 0 && mcu % restart == 0) { Flush(); o.AddRange([0xFF, (byte)(0xD0 + (rst++ & 7))]); reset = true; }
                bool useInit = reset;
                if (reset) { reset = false; firstLine = true; }
                for (int c = 0; c < nf; c++)
                {
                    int i = y * line + x * nf + c;
                    int pred;
                    if (useInit) pred = initial;
                    else if (firstLine) pred = samples[i - nf];
                    else if (x == 0) pred = samples[i - line];
                    else
                    {
                        int ra = samples[i - nf], rb = samples[i - line], rc = samples[i - line - nf];
                        pred = predictor switch { 1 => ra, 2 => rb, 3 => rc, 4 => ra + rb - rc, 5 => ra + ((rb - rc) >> 1), 6 => rb + ((ra - rc) >> 1), _ => (ra + rb) >> 1 };
                    }
                    int diff = (samples[i] - pred) & mask;
                    if (diff >= 1 << (precision - 1)) diff -= 1 << precision; // signed modulo
                    int ssss = diff == 0 ? 0 : 32 - System.Numerics.BitOperations.LeadingZeroCount((uint)Math.Abs(diff));
                    Put(ssss, 5); // canonical code for symbol s with all lengths 5 is s itself
                    if (ssss is > 0 and < 16) Put(diff > 0 ? diff : diff + (1 << ssss) - 1, ssss);
                }
                mcu++;
            }
            firstLine = false;
        }
        Flush();
        o.AddRange([0xFF, 0xD9]);
        return o.ToArray();
    }

    static ushort[] Random(int n, int precision, int seed)
    {
        var r = new Random(seed);
        var a = new ushort[n];
        int v = 1 << (precision - 1);
        for (int i = 0; i < n; i++) { v = Math.Clamp(v + r.Next(-300, 301), 0, (1 << precision) - 1); a[i] = (ushort)(r.Next(20) == 0 ? r.Next(1 << precision) : v); }
        return a;
    }

    [Theory]
    [InlineData(1, 1)] [InlineData(2, 1)] [InlineData(3, 1)] [InlineData(4, 1)]
    [InlineData(5, 1)] [InlineData(6, 1)] [InlineData(7, 1)]
    [InlineData(1, 2)] [InlineData(6, 2)] [InlineData(1, 4)]
    public void Round_trip_predictors_and_components(int predictor, int nf)
    {
        int w = 37, h = 23;
        var src = Random(w * h * nf, 14, predictor * 10 + nf);
        var enc = Encode(src, w, h, nf, 14, predictor);
        var dst = new ushort[src.Length];
        var f = LosslessJpegDecoder.Decode(enc, dst);
        Assert.Equal((w, h, nf, 14), (f.Width, f.Height, f.Components, f.Precision));
        Assert.Equal(src, dst);
    }

    [Theory]
    [InlineData(37)]  // one line per interval
    [InlineData(10)]  // restarts mid-line
    public void Round_trip_with_restart_intervals(int restart)
    {
        int w = 37, h = 9;
        var src = Random(w * h * 2, 14, restart);
        var dst = new ushort[src.Length];
        LosslessJpegDecoder.Decode(Encode(src, w, h, 2, 14, 1, restart), dst);
        Assert.Equal(src, dst);
    }

    [Fact]
    public void Extreme_values_and_byte_stuffing()
    {
        int w = 64, h = 4;
        var src = new ushort[w * h];
        for (int i = 0; i < src.Length; i++) src[i] = (ushort)(i % 2 == 0 ? 0 : 16383); // max diffs, many 0xFF bytes
        var dst = new ushort[src.Length];
        LosslessJpegDecoder.Decode(Encode(src, w, h, 1, 14, 1), dst);
        Assert.Equal(src, dst);
    }

    [Fact]
    public void Lossy_jpeg_is_rejected() =>
        Assert.Throws<ImageFormatException>(() => LosslessJpegDecoder.ReadFrame(new byte[] { 0xFF, 0xD8, 0xFF, 0xC0, 0, 4, 0, 0 }));
}
