using SigmaFp.Camera.Bulb;
using SigmaFp.Camera.Sigma;
using SigmaFp.Camera.Usb;

namespace SigmaFp.Camera.Tests;

/// <summary>Encodings must match docs/sigma-ptp-protocol.md and bytes captured from the real fp L.</summary>
public class ProtocolTests
{
    static byte[] Hex(string s) => Convert.FromHexString(s.Replace(" ", ""));

    [Fact] public void Group1_bulb_iso400_matches_library() =>
        Assert.Equal(Hex("00 19 00 08 00 30 00"), SigmaMessages.ShutterAndIso(0x08, 0x30));

    [Fact] public void Group2_manual_dng_matches_library() =>
        Assert.Equal(Hex("00 04 80 04 10 00"), SigmaMessages.ManualDng());

    [Fact] public void Group3_save_in_camera() =>
        Assert.Equal(Hex("00 00 80 01 00"), SigmaMessages.SaveInCamera());

    [Fact] public void Group3_save_to_pc_matches_library() =>
        Assert.Equal(Hex("00 00 80 02 00"), SigmaMessages.SaveToPc());

    [Fact] public void Group4_dng14_matches_library() =>
        Assert.Equal(Hex("00 00 0A 0E 00 00 00 00 00 00 00"), SigmaMessages.Dng14Bit());

    [Theory]
    [InlineData(SigmaMessages.SnapNonAf, "00020100")]
    [InlineData(SigmaMessages.SnapStart, "00060100")]
    [InlineData(SigmaMessages.SnapStop, "00070100")]
    public void Snap_encoding(byte mode, string hex) => Assert.Equal(Hex(hex), SigmaMessages.Snap(mode));

    [Fact]
    public void CaptStatus_from_camera()
    {
        var s = SigmaMessages.ParseCaptStatus(Hex("060000010500020E")); // logged 2026-09-30 22:29:11
        Assert.Equal(new CaptStatus(0, 0, 1, CaptStatusCode.ImageGenCompleted), s);
        Assert.True(CaptStatusCode.IsDone(s.Status));
        Assert.True(CaptStatusCode.IsFailure(SigmaMessages.ParseCaptStatus(Hex("0600000101600068")).Status)); // 0x6001
    }

    [Fact]
    public void PictFileInfo_from_camera()
    {
        var f = SigmaMessages.ParsePictFileInfo(Hex(
            "38000000010000000C000000007C4F732BD31802240000002D000000444E47004025C8183130305349474D41005344494D303030312E444E47000000"));
        Assert.Equal(0x734F7C00u, f.Address);
        Assert.Equal(35181355u, f.Size);
        Assert.Equal("DNG", f.Format);
        Assert.Equal((ushort)9536, f.Width);
        Assert.Equal((ushort)6344, f.Height);
        Assert.Equal("100SIGMA", f.Path);
        Assert.Equal("SDIM0001.DNG", f.Name);
    }

    [Fact]
    public void No_image_info_is_a_protocol_error() =>
        Assert.Throws<CameraProtocolException>(() => SigmaMessages.ParsePictFileInfo(Hex("0400000000000000")));

    [Fact]
    public void ApiConfig_from_camera()
    {
        var tags = SigmaMessages.ParseApiConfigStrings(Hex(
            "4C00000004000000010002000B0000003C00000002000200090000004700000003000200040000005639300005000B000100000052B89E3F000000005349474D41206670204C003132333435363738001B"));
        Assert.Equal("SIGMA fp L", tags[1]);
        Assert.Equal("12345678", tags[2]); // serial anonymised in the captured reply
        Assert.Equal("V90", tags[3]);
    }

    [Fact]
    public void Image_chunk_strips_size_prefix_and_trailer()
    {
        // [AcquiredSize=4][II*\0][28-byte trailer starting 01 00 18 00]
        var payload = Hex("04000000 49492A00 0100180000000000000000000000000000000000000000000000004A");
        var img = SigmaMessages.ParseImageChunk(payload);
        Assert.Equal(Hex("49492A00"), img.ToArray());
    }

    [Fact]
    public void Image_chunk_with_impossible_size_is_comm_error() =>
        Assert.Throws<CameraCommException>(() => SigmaMessages.ParseImageChunk(Hex("FF000000 0102")));

    [Theory]
    [InlineData(1, true, "A00101A2")]
    [InlineData(1, false, "A00100A1")]
    [InlineData(2, true, "A00201A3")]
    public void Lcus_relay_commands(byte ch, bool on, string hex) => Assert.Equal(Hex(hex), LcusRelayTrigger.Command(ch, on));

    [Theory]
    [InlineData("9", "COM9")]
    [InlineData(" com12 ", "COM12")]
    [InlineData("COM3", "COM3")]
    public void Relay_port_name_is_normalized(string typed, string expected) => Assert.Equal(expected, LcusRelayTrigger.NormalizePortName(typed));

    [Fact]
    public void Descriptor_parser_finds_fp_l_endpoints()
    {
        // config(9) + interface class 6 (9) + bulk-in 0x82/512 + bulk-out 0x01/512 + int-in 0x83
        var d = Hex("09022700010100C0FA" + "090400000306010100" + "07058202000200" + "07050102000200" + "07058303080009");
        var (iface, epIn, epOut, epInt, mps) = DescriptorParser.FindPtpEndpoints(d);
        Assert.Equal((0, 0x82, 0x01, 0x83, 512), (iface, epIn, epOut, epInt, mps));
    }
}

public class ApexTests
{
    [Theory]
    [InlineData(400, 0x30)]
    [InlineData(100, 0x20)]
    [InlineData(6400, 0x50)]
    public void Iso_codes(int iso, byte code) => Assert.Equal(code, Apex.IsoCode(iso));

    [Fact] public void Unsupported_iso_throws() => Assert.Throws<ArgumentOutOfRangeException>(() => Apex.IsoCode(123));

    [Theory]
    [InlineData(10, 0x1D, 10)]
    [InlineData(30, 0x10, 30)]
    [InlineData(1, 0x38, 1)]
    [InlineData(0.000125, 160, 0.000125)]
    [InlineData(9, 0x1D, 10)]     // nearest step on a log scale
    public void Shutter_codes(double s, byte code, double actual)
    {
        var (c, a) = Apex.Shutter(s);
        Assert.Equal(code, c);
        Assert.Equal(actual, a, 6);
    }

    [Fact] public void Shutter_over_30s_throws() => Assert.Throws<ArgumentOutOfRangeException>(() => Apex.Shutter(31));

    [Fact] public void Iso_list_is_ascending_and_contains_default() => Assert.Contains(400, Apex.IsoValues);
}

public class PlannerTests
{
    [Fact]
    public void Short_exposures_are_timed()
    {
        var p = new ExposurePlanner(false).Plan(10);
        Assert.Equal(Session.TriggerKind.Timed, p.Kind);
        Assert.False(p.Abortable);
    }

    [Fact]
    public void Exactly_300_without_cable_is_camera_bulb() =>
        Assert.Equal(Session.TriggerKind.CameraBulb, new ExposurePlanner(false).Plan(300).Kind);

    [Theory]
    [InlineData(120)]
    [InlineData(180)]
    [InlineData(31)]
    public void Other_long_exposures_without_cable_are_rejected(double s) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new ExposurePlanner(false).Plan(s));

    [Theory]
    [InlineData(120)]
    [InlineData(180)]
    [InlineData(300)]
    public void Long_exposures_with_cable_are_external_bulb(double s)
    {
        var p = new ExposurePlanner(true).Plan(s);
        Assert.Equal(Session.TriggerKind.ExternalBulb, p.Kind);
        Assert.Equal(s, p.ActualSeconds);
        Assert.True(p.Abortable);
    }

    [Fact] public void Beyond_bulb_limit_is_rejected() => Assert.Throws<ArgumentOutOfRangeException>(() => new ExposurePlanner(true).Plan(301));
    [Fact] public void Zero_is_rejected() => Assert.Throws<ArgumentOutOfRangeException>(() => new ExposurePlanner(true).Plan(0));
}
