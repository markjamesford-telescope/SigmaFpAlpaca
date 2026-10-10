namespace SigmaFp.Imaging;

public sealed class ImageFormatException(string message) : Exception(message);

/// <summary>
/// Lossless JPEG (ITU T.81 process 14, SOF3) decoder as used for DNG raw tiles. Supports 1-4
/// interleaved components, predictors 1-7, point transform, restart intervals and byte stuffing.
/// Output: samples in scan order, i.e. each frame line holds X * Nf values.
/// </summary>
public static class LosslessJpegDecoder
{
    public readonly record struct Frame(int Width, int Height, int Components, int Precision);

    sealed class Huffman
    {
        // Fast lookup on the next 9 bits: (length << 8) | symbol, 0 = not in table.
        public readonly ushort[] Fast = new ushort[1 << FastBits];
        public readonly int[] MaxCode = new int[18];
        public readonly int[] ValPtr = new int[17];
        public readonly int[] MinCode = new int[17];
        public byte[] Symbols = [];
        public const int FastBits = 9;

        public static Huffman Build(ReadOnlySpan<byte> counts, ReadOnlySpan<byte> symbols)
        {
            var h = new Huffman { Symbols = symbols.ToArray() };
            int code = 0, k = 0;
            for (int len = 1; len <= 16; len++)
            {
                h.ValPtr[len] = k;
                h.MinCode[len] = code;
                int n = counts[len - 1];
                for (int i = 0; i < n; i++, k++, code++)
                {
                    if (len <= FastBits)
                    {
                        int shift = FastBits - len;
                        int start = code << shift;
                        for (int j = 0; j < 1 << shift; j++) h.Fast[start + j] = (ushort)((len << 8) | h.Symbols[k]);
                    }
                }
                h.MaxCode[len] = n > 0 ? code - 1 : -1;
                code <<= 1;
            }
            h.MaxCode[17] = int.MaxValue;
            return h;
        }
    }

    ref struct BitReader
    {
        readonly ReadOnlySpan<byte> _d;
        int _pos;
        ulong _buf;
        int _bits;
        bool _marker; // hit a marker: feed zeros

        public BitReader(ReadOnlySpan<byte> data, int pos) { _d = data; _pos = pos; _buf = 0; _bits = 0; _marker = false; }

        void Fill()
        {
            while (_bits <= 56)
            {
                byte b = 0;
                if (!_marker && _pos < _d.Length)
                {
                    b = _d[_pos];
                    if (b == 0xFF)
                    {
                        byte next = _pos + 1 < _d.Length ? _d[_pos + 1] : (byte)0;
                        if (next == 0x00) _pos += 2;          // stuffed 0xFF
                        else { _marker = true; b = 0; }        // marker: stop consuming
                    }
                    else _pos++;
                }
                _buf |= (ulong)b << (56 - _bits);
                _bits += 8;
            }
        }

        public int Peek(int n) { if (_bits < n) Fill(); return (int)(_buf >> (64 - n)); }
        public void Skip(int n) { _buf <<= n; _bits -= n; }
        public int Get(int n) { if (n == 0) return 0; int v = Peek(n); Skip(n); return v; }

        public int Decode(Huffman h)
        {
            int look = Peek(Huffman.FastBits);
            ushort f = h.Fast[look];
            if (f != 0) { Skip(f >> 8); return f & 0xFF; }
            int code = Peek(16);
            for (int len = Huffman.FastBits + 1; len <= 16; len++)
            {
                int c = code >> (16 - len);
                if (c <= h.MaxCode[len] && h.MaxCode[len] >= 0)
                {
                    Skip(len);
                    return h.Symbols[h.ValPtr[len] + c - h.MinCode[len]];
                }
            }
            throw new ImageFormatException("Invalid Huffman code in lossless JPEG");
        }

        /// <summary>Skips to and over the next RSTn marker; resets the bit buffer.</summary>
        public void Restart()
        {
            _buf = 0; _bits = 0; _marker = false;
            while (_pos + 1 < _d.Length && !(_d[_pos] == 0xFF && _d[_pos + 1] is >= 0xD0 and <= 0xD7)) _pos++;
            if (_pos + 1 >= _d.Length) throw new ImageFormatException("Restart marker missing");
            _pos += 2;
        }
    }

    public static Frame ReadFrame(ReadOnlySpan<byte> data) => Parse(data, out _, out _, out _, out _, out _);

    /// <summary>Decodes one lossless JPEG stream into <paramref name="output"/> (length >= W*H*Nf).</summary>
    public static Frame Decode(ReadOnlySpan<byte> data, Span<ushort> output)
    {
        var frame = Parse(data, out var tables, out int predictor, out int pt, out int restart, out int scanStart);
        var (w, h, nf, p) = (frame.Width, frame.Height, frame.Components, frame.Precision);
        int lineLen = w * nf;
        if (output.Length < lineLen * h) throw new ArgumentException("Output buffer too small");

        var br = new BitReader(data, scanStart);
        int initial = 1 << (p - pt - 1);
        int mask = (1 << p) - 1;
        int mcuCount = 0;
        bool resetPending = true;   // next MCU uses the default prediction (scan start / after RSTn)
        bool inFirstLine = false;   // rest of the line after a reset predicts from the left (Ra)

        for (int y = 0; y < h; y++)
        {
            var line = output.Slice(y * lineLen, lineLen);
            var prev = y > 0 ? output.Slice((y - 1) * lineLen, lineLen) : default;
            for (int x = 0; x < w; x++)
            {
                if (restart > 0 && mcuCount > 0 && mcuCount % restart == 0)
                {
                    br.Restart();
                    resetPending = true;
                }
                bool useInitial = resetPending;
                if (resetPending) { resetPending = false; inFirstLine = true; }

                for (int c = 0; c < nf; c++)
                {
                    int ssss = br.Decode(tables[c]);
                    int diff;
                    if (ssss == 0) diff = 0;
                    else if (ssss == 16) diff = 32768;
                    else
                    {
                        diff = br.Get(ssss);
                        if (diff < 1 << (ssss - 1)) diff -= (1 << ssss) - 1;
                    }

                    int idx = x * nf + c;
                    int pred;
                    if (useInitial) pred = initial;
                    else if (inFirstLine) pred = line[idx - nf];   // x > 0 here
                    else if (x == 0) pred = prev[idx];
                    else
                    {
                        int ra = line[idx - nf], rb = prev[idx], rc = prev[idx - nf];
                        pred = predictor switch
                        {
                            1 => ra,
                            2 => rb,
                            3 => rc,
                            4 => ra + rb - rc,
                            5 => ra + ((rb - rc) >> 1),
                            6 => rb + ((ra - rc) >> 1),
                            7 => (ra + rb) >> 1,
                            _ => throw new ImageFormatException($"Invalid predictor {predictor}"),
                        };
                    }
                    line[idx] = (ushort)((pred + diff) & mask);
                }
                mcuCount++;
            }
            inFirstLine = false;
        }
        if (pt > 0) for (int i = 0; i < lineLen * h; i++) output[i] = (ushort)(output[i] << pt);
        return frame;
    }

    static Frame Parse(ReadOnlySpan<byte> d, out Huffman[] perComponent, out int predictor, out int pt, out int restart, out int scanStart)
    {
        if (d.Length < 4 || d[0] != 0xFF || d[1] != 0xD8) throw new ImageFormatException("Not a JPEG stream (no SOI)");
        var tables = new Huffman?[4];
        var compIds = new int[4];
        Frame? frame = null;
        restart = 0;
        int pos = 2;
        while (pos + 4 <= d.Length)
        {
            if (d[pos] != 0xFF) { pos++; continue; }
            byte marker = d[pos + 1];
            if (marker == 0xFF) { pos++; continue; }
            int len = (d[pos + 2] << 8) | d[pos + 3];
            var seg = d.Slice(pos + 4, Math.Max(0, len - 2));
            switch (marker)
            {
                case 0xC4: // DHT
                    for (int i = 0; i < seg.Length;)
                    {
                        int id = seg[i] & 0x0F;
                        var counts = seg.Slice(i + 1, 16);
                        int total = 0;
                        foreach (var c in counts) total += c;
                        tables[id] = Huffman.Build(counts, seg.Slice(i + 17, total));
                        i += 17 + total;
                    }
                    break;
                case 0xC3: // SOF3
                    {
                        int p = seg[0], hgt = (seg[1] << 8) | seg[2], wid = (seg[3] << 8) | seg[4], nf = seg[5];
                        if (nf is < 1 or > 4) throw new ImageFormatException($"Unsupported component count {nf}");
                        for (int c = 0; c < nf; c++) compIds[c] = seg[6 + 3 * c];
                        frame = new Frame(wid, hgt, nf, p);
                    }
                    break;
                case 0xC0: case 0xC1: case 0xC2:
                    throw new ImageFormatException("Lossy JPEG is not a raw tile");
                case 0xDD: // DRI
                    restart = (seg[0] << 8) | seg[1];
                    break;
                case 0xDA: // SOS
                    {
                        if (frame is null) throw new ImageFormatException("SOS before SOF3");
                        int ns = seg[0];
                        perComponent = new Huffman[frame.Value.Components];
                        for (int i = 0; i < ns; i++)
                        {
                            int cid = seg[1 + 2 * i], td = seg[2 + 2 * i] >> 4;
                            int ci = Array.IndexOf(compIds, cid, 0, frame.Value.Components);
                            perComponent[ci < 0 ? i : ci] = tables[td] ?? throw new ImageFormatException($"Missing Huffman table {td}");
                        }
                        predictor = seg[1 + 2 * ns];
                        pt = seg[3 + 2 * ns] & 0x0F;
                        scanStart = pos + 2 + len;
                        return frame.Value;
                    }
            }
            pos += 2 + len;
        }
        throw new ImageFormatException("No SOS marker in lossless JPEG");
    }
}
