using System.Diagnostics;
using System.Security.Cryptography;
using Xunit.Abstractions;

namespace SigmaFp.Imaging.Tests;

/// <summary>
/// Checks against a real fp L DNG decoded independently with LibRaw (rawpy). The 31 MB file is not
/// in the repo; the test skips when it is absent. Point SIGMAFP_TEST_DNG at a file to override.
/// </summary>
public class DngReaderTests(ITestOutputHelper output)
{
    // cli_10s_iso400_20261003-075916_015.dng, lens capped, 10 s, ISO 400 (reference values from rawpy 0.2x / LibRaw).
    const string ReferenceName = "cli_10s_iso400_20261003-075916_015.dng";
    const string ReferenceSha256 = "5b55506eae8b6c1e8b2db4ec1d465d7ed609b519a840fbbf99f0d90491b61531";

    static string? FindReference()
    {
        var env = Environment.GetEnvironmentVariable("SIGMAFP_TEST_DNG");
        if (env is not null && File.Exists(env)) return env;
        var p = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SigmaFpAlpaca", "logs", "frames", ReferenceName);
        return File.Exists(p) ? p : null;
    }

    [SkippableFact]
    public void Real_fp_l_frame_matches_libraw_bit_for_bit()
    {
        var path = FindReference();
        Skip.If(path is null, "reference DNG not on this machine");
        var bytes = File.ReadAllBytes(path!);

        var sw = Stopwatch.StartNew();
        var raw = DngReader.Read(bytes);
        output.WriteLine($"decoded {raw.Width}x{raw.Height} in {sw.ElapsedMilliseconds} ms");

        Assert.Equal(9536, raw.Width);
        Assert.Equal(6344, raw.Height);
        Assert.Equal(14, raw.BitsPerSample);
        Assert.Equal(16383, raw.WhiteLevel);
        Assert.Equal(1024, raw.BlackLevel);
        Assert.Equal("RGGB", raw.CfaName);
        Assert.Equal("SIGMA fp L", raw.Model);
        Assert.Equal(10, raw.ExposureSeconds);
        Assert.Equal(400, raw.Iso);

        // Spot values from LibRaw: raw_image[y, x]
        Assert.Equal(1022, raw[0, 0]);
        Assert.Equal(1023, raw[1, 0]);
        Assert.Equal(1021, raw[0, 1]);
        Assert.Equal(1024, raw[256, 256]);
        Assert.Equal(1017, raw[4767, 3171]);
        Assert.Equal(1021, raw[9535, 6343]);
        Assert.Equal(1025, raw[9535, 0]);

        var le = new byte[raw.Pixels.Length * 2];
        Buffer.BlockCopy(raw.Pixels, 0, le, 0, le.Length); // x64 is little-endian
        Assert.Equal(ReferenceSha256, Convert.ToHexString(SHA256.HashData(le)).ToLowerInvariant());
    }

    [SkippableFact]
    public void Ascom_mapping_for_colour_and_mono_bodies()
    {
        var path = FindReference();
        Skip.If(path is null, "reference DNG not on this machine");
        var raw = DngReader.Read(File.ReadAllBytes(path!));
        Assert.Equal(new SensorDescription(AscomSensorType.RGGB, 0, 0), AscomImage.Describe(raw, monochromeBody: false));
        Assert.Equal(new SensorDescription(AscomSensorType.Monochrome, 0, 0), AscomImage.Describe(raw, monochromeBody: true));
        var arr = AscomImage.ToImageArray(raw);
        Assert.Equal(raw.Width, arr.GetLength(0));
        Assert.Equal(raw[4767, 3171], arr[4767, 3171]);
    }

    [Fact]
    public void Non_dng_is_rejected() => Assert.Throws<ImageFormatException>(() => DngReader.Read(new byte[16]));

    [Theory]
    [InlineData(new byte[] { 0, 1, 1, 2 }, 0, 0)]
    [InlineData(new byte[] { 1, 0, 2, 1 }, 1, 0)]
    [InlineData(new byte[] { 1, 2, 0, 1 }, 0, 1)]
    [InlineData(new byte[] { 2, 1, 1, 0 }, 1, 1)]
    public void Bayer_offsets_relative_to_rggb(byte[] cfa, int ox, int oy)
    {
        var raw = new RawImage { Width = 2, Height = 2, Pixels = new ushort[4], BitsPerSample = 14, WhiteLevel = 16383, BlackLevel = 0, CfaPattern = cfa };
        Assert.Equal(new SensorDescription(AscomSensorType.RGGB, ox, oy), AscomImage.Describe(raw, false));
    }
}
