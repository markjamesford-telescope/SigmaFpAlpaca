namespace SigmaFp.Imaging;

/// <summary>ASCOM SensorType values (ICameraV4).</summary>
public enum AscomSensorType { Monochrome = 0, Color = 1, RGGB = 2, CMYG = 3, CMYG2 = 4, LRGB = 5 }

public readonly record struct SensorDescription(AscomSensorType Type, int BayerOffsetX, int BayerOffsetY);

/// <summary>Maps raw DNG data to what ASCOM clients expect. No processing of pixel values.</summary>
public static class AscomImage
{
    /// <summary>
    /// The body type is a driver setting (a mono-converted fp L still writes CFA tags). For a colour
    /// body the Bayer offsets come from the DNG CFA pattern, expressed relative to RGGB.
    /// </summary>
    public static SensorDescription Describe(RawImage raw, bool monochromeBody)
    {
        if (monochromeBody) return new(AscomSensorType.Monochrome, 0, 0);
        return raw.CfaName switch
        {
            "RGGB" => new(AscomSensorType.RGGB, 0, 0),
            "GRBG" => new(AscomSensorType.RGGB, 1, 0),
            "GBRG" => new(AscomSensorType.RGGB, 0, 1),
            "BGGR" => new(AscomSensorType.RGGB, 1, 1),
            null => new(AscomSensorType.Monochrome, 0, 0),
            var other => throw new ImageFormatException($"Unsupported CFA pattern {other}"),
        };
    }

    /// <summary>ASCOM ImageArray layout: <c>[x, y]</c>, values as-is.</summary>
    public static int[,] ToImageArray(RawImage raw)
    {
        var a = new int[raw.Width, raw.Height];
        Parallel.For(0, raw.Height, y =>
        {
            int row = y * raw.Width;
            for (int x = 0; x < raw.Width; x++) a[x, y] = raw.Pixels[row + x];
        });
        return a;
    }
}
