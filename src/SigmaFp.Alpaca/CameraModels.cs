namespace SigmaFp.Alpaca;

/// <summary>Known Sigma fp-series bodies. Exact raw sizes are learned from the first image per serial.</summary>
public static class CameraModels
{
    public sealed record Model(string Name, int Width, int Height, double PixelMicrons, string SensorName, bool RawSizeVerified, int DefaultIso);

    public static IReadOnlyList<Model> All => [FpL, Fp];

    /// <summary>fp L: raw size verified from real DNGs (9536 x 6344; DefaultCrop 9520 x 6328).</summary>
    public static readonly Model FpL = new("SIGMA fp L", 9536, 6344, 3.76, "SIGMA fp L 61 MP BSI CMOS", true, DefaultIso: 400);

    /// <summary>fp: published 6000 x 4000, 35.9 x 23.9 mm, ~5.98 um. Raw DNG size is confirmed by the first image.</summary>
    public static readonly Model Fp = new("SIGMA fp", 6000, 4000, 5.98, "SIGMA fp 24.6 MP BSI CMOS", false, DefaultIso: 1250);

    /// <summary>Matches the model string the camera reports (ApiConfig / PTP DeviceInfo).</summary>
    public static Model Lookup(string? cameraModel)
    {
        var m = (cameraModel ?? "").Trim();
        if (m.EndsWith("fp L", StringComparison.OrdinalIgnoreCase) || m.Contains("fp L ", StringComparison.OrdinalIgnoreCase)) return FpL;
        if (m.EndsWith("fp", StringComparison.OrdinalIgnoreCase) || m.Contains("fp ", StringComparison.OrdinalIgnoreCase)) return Fp;
        return FpL; // unknown: assume the fp L until the first image says otherwise
    }

    /// <summary>"9536x6344" -> (9536, 6344).</summary>
    public static (int W, int H)? ParseSize(string? s)
    {
        var p = (s ?? "").Split('x');
        return p.Length == 2 && int.TryParse(p[0], out var w) && int.TryParse(p[1], out var h) && w > 0 && h > 0 ? (w, h) : null;
    }
}
