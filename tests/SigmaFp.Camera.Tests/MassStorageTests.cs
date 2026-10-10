using System.Diagnostics;
using SigmaFp.Camera.Bulb;
using SigmaFp.Camera.MassStorage;
using SigmaFp.Camera.Session;
using Xunit.Abstractions;

namespace SigmaFp.Camera.Tests;

/// <summary>
/// Mass Storage mode (open question 19) against a simulated port switch, card (temp folder) and camera that writes a
/// DNG to the card when the relay is released while the port is off.
/// </summary>
public sealed class MassStorageTests : IDisposable
{
    readonly ITestOutputHelper _out;
    readonly string _root = Path.Combine(Path.GetTempPath(), "SigmaFpMsTest_" + Guid.NewGuid().ToString("N"));
    readonly FakePort _port = new();
    readonly FakeCard _card;
    readonly FakeCamera _camera;
    readonly BulbState _bulb = new();
    readonly MassStorageOptions _ms = new()
    {
        SettleAfterPortOff = TimeSpan.FromMilliseconds(20), SettleAfterRelease = TimeSpan.FromMilliseconds(50),
        DriveTimeout = TimeSpan.FromSeconds(2), FileTimeout = TimeSpan.FromSeconds(1),
        DriveGoneTimeout = TimeSpan.FromSeconds(2), PollStep = TimeSpan.FromMilliseconds(10),
    };
    CameraController? _cam;

    public MassStorageTests(ITestOutputHelper output)
    {
        _out = output;
        Directory.CreateDirectory(Path.Combine(_root, "DCIM", "100SIGMA"));
        File.WriteAllBytes(Path.Combine(_root, "DCIM", "100SIGMA", "SDIM0001.DNG"), [1, 2, 3]); // an old picture
        _card = new FakeCard(_port, _root);
        _camera = new FakeCamera(_port, _root);
    }

    CameraController Create()
    {
        var trigger = new MassStorageTrigger(_camera, _bulb);
        _cam = new CameraController(() => new MassStorageSession(_port, _card, _bulb, _ms, new TestLogger(_out)), new CameraOptions
        {
            PollInterval = TimeSpan.FromMilliseconds(20), HeartbeatInterval = TimeSpan.FromMilliseconds(200),
            ItemTimeout = TimeSpan.FromSeconds(10), ApiTimeout = TimeSpan.FromSeconds(10), CaptureGrace = TimeSpan.FromSeconds(5),
            StuckCaptureGiveUp = TimeSpan.FromSeconds(5), ChunkSize = 64 * 1024, ExternalBulbOnly = true, MinExternalBulbSeconds = 0.1,
            ReconnectBackoff = [TimeSpan.FromMilliseconds(50)],
        }, new TestLogger(_out), trigger);
        return _cam;
    }

    static void WaitUntil(Func<bool> cond, double seconds = 10, string? what = null)
    {
        var sw = Stopwatch.StartNew();
        while (!cond())
        {
            if (sw.Elapsed.TotalSeconds > seconds) throw new TimeoutException($"Timed out waiting for {what ?? "condition"}");
            Thread.Sleep(5);
        }
    }

    [Fact]
    public void Connect_reads_the_card_then_switches_the_port_off()
    {
        var cam = Create();
        cam.Connect();
        Assert.Equal("SIGMA fp L", cam.Info!.Model);
        Assert.Equal("12345678", cam.Info.Serial);
        Assert.False(_port.On, "the camera must be left on its own between exposures");
    }

    [Fact]
    public void Relay_bulb_with_port_off_delivers_the_new_dng_from_the_card()
    {
        var cam = Create();
        cam.Connect();
        cam.StartExposure(0.3);
        Assert.Equal(TriggerKind.ExternalBulb, cam.LastPlan!.Kind);
        WaitUntil(() => cam.ImageReady, what: "image from the card");
        Assert.Equal(_camera.LastWritten, cam.GetImage());
        Assert.True(_camera.FiredWithPortOff, "the relay must fire while the USB port is off");
        Assert.InRange(_camera.HeldSeconds, 0.25, 2);
        WaitUntil(() => !_port.On && cam.State == CameraState.Idle, what: "port off again");
        Assert.True(File.Exists(_camera.LastPath), "kept on the card by default");
    }

    [Fact]
    public void Several_frames_in_a_row_each_get_their_own_file()
    {
        var cam = Create();
        cam.Connect();
        for (int i = 0; i < 3; i++)
        {
            cam.StartExposure(0.15);
            WaitUntil(() => cam.ImageReady && cam.State == CameraState.Idle, what: $"frame {i}");
            Assert.Equal(_camera.LastWritten, cam.GetImage());
        }
        Assert.Equal(3, _camera.Shots);
    }

    [Fact]
    public void Delete_after_read_removes_the_file_from_the_card()
    {
        _ms.DeleteAfterRead = true;
        var cam = Create();
        cam.Connect();
        cam.StartExposure(0.15);
        WaitUntil(() => cam.ImageReady && cam.State == CameraState.Idle, what: "image");
        Assert.False(File.Exists(_camera.LastPath));
        Assert.True(File.Exists(Path.Combine(_root, "DCIM", "100SIGMA", "SDIM0001.DNG")), "old pictures are never touched");
    }

    [Fact]
    public void Abort_releases_the_relay_early_and_discards_the_image()
    {
        var cam = Create();
        cam.Connect();
        cam.StartExposure(20);
        Thread.Sleep(200);
        cam.AbortExposure();
        WaitUntil(() => !cam.CaptureInFlight, 5, "abort to finish long before 20 s");
        Assert.False(cam.ImageReady);
        Assert.InRange(_camera.HeldSeconds, 0.1, 2);
    }

    [Fact]
    public void Camera_that_does_not_fire_fails_the_frame_with_a_clear_message()
    {
        _camera.Fires = false;
        var cam = Create();
        cam.Connect();
        cam.StartExposure(0.1);
        WaitUntil(() => cam.LastExposureFailed, what: "failure");
        Assert.Contains("No new DNG", cam.LastError);
        Assert.False(cam.ImageReady);
    }

    [Fact]
    public void Card_that_does_not_come_back_fails_the_frame_with_a_clear_message()
    {
        var cam = Create();
        cam.Connect();
        _card.Mountable = false;
        cam.StartExposure(0.1);
        WaitUntil(() => cam.LastExposureFailed, what: "failure");
        Assert.Contains("did not appear", cam.LastError);
    }

    [Fact]
    public void Sleeping_camera_is_detected_before_the_exposure_instead_of_losing_the_frame()
    {
        _ms.AwakeCheckAfterIdle = TimeSpan.FromMilliseconds(100);
        _ms.AwakeCheckTimeout = TimeSpan.FromMilliseconds(300);
        var cam = Create();
        cam.Connect();
        Thread.Sleep(200);                           // idle long enough for the check
        _card.Mountable = false;                     // camera fell asleep: no card on USB
        var ex = Assert.ThrowsAny<Exception>(() => cam.StartExposure(5));
        Assert.Contains("asleep", ex.Message);
        Assert.Equal(0, _camera.Shots);
        Assert.False(_camera.FiredWithPortOff, "the relay must not fire for a sleeping camera");
        Assert.False(_port.On);
        _card.Mountable = true;                      // woken up
        cam.StartExposure(0.15);
        WaitUntil(() => cam.ImageReady, what: "image after waking");
    }

    [Fact]
    public void Connect_fails_clearly_when_the_card_never_appears()
    {
        _card.Mountable = false;
        var cam = Create();
        var ex = Assert.Throws<NotConnectedException>(() => cam.Connect());
        Assert.Contains("Mass Storage", ex.Message);
    }

    [Theory]
    [InlineData(0.05)]
    [InlineData(301)]
    public void Planner_in_mass_storage_mode_only_accepts_relay_lengths(double s)
    {
        var p = new ExposurePlanner(true, 300, 0.1, externalOnly: true);
        Assert.Throws<ArgumentOutOfRangeException>(() => p.Plan(s));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(30)]
    [InlineData(120)]
    [InlineData(300)]
    public void Planner_in_mass_storage_mode_uses_the_relay_for_every_length(double s)
    {
        var plan = new ExposurePlanner(true, 300, 1, externalOnly: true).Plan(s);
        Assert.Equal(TriggerKind.ExternalBulb, plan.Kind);
        Assert.Equal(s, plan.ActualSeconds);
    }

    [Theory]
    [InlineData("SIGMA SIGMA fp L USB Device", "SIGMA fp L")]
    [InlineData("SIGMA SIGMA fp USB Device", "SIGMA fp")]
    public void Wmi_model_names_are_cleaned(string wmi, string expected) =>
        Assert.Equal(expected, WindowsCardLocator.ModelName(wmi));

    [Fact]
    public void Serial_comes_from_the_usb_instance_id() =>
        Assert.Equal("12345678", WindowsCardLocator.SerialFrom(@"USBSTOR\DISK&VEN_SIGMA&PROD_SIGMA_FP_L&REV_1.00\0000000012345678&0"));

    public void Dispose()
    {
        _cam?.Dispose();
        try { Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    // ---------------------------------------------------------------- fakes

    sealed class FakePort : IPortSwitch
    {
        public volatile bool On;
        public long OnSince;
        public void SetPower(bool on) { if (on && !On) Interlocked.Exchange(ref OnSince, Stopwatch.GetTimestamp()); On = on; }
    }

    /// <summary>The card is visible only while the port is on (after a short "mount" delay).</summary>
    sealed class FakeCard(FakePort port, string root) : ICardLocator
    {
        public volatile bool Mountable = true;
        public CardInfo? Find() =>
            Mountable && port.On && Stopwatch.GetElapsedTime(Interlocked.Read(ref port.OnSince)) > TimeSpan.FromMilliseconds(30)
                ? new CardInfo(root, "SIGMA fp L", "12345678") : null;
    }

    /// <summary>The camera on its own: a relay press while the port is off starts a bulb, the release writes a DNG.</summary>
    sealed class FakeCamera(FakePort port, string root) : IBulbTrigger
    {
        public volatile bool Fires = true;
        public bool FiredWithPortOff;
        public int Shots;
        public double HeldSeconds;
        public byte[]? LastWritten;
        public string? LastPath;
        int _number = 1;
        long _pressedAt;

        public string Description => "fake camera relay";
        public void Open() { }
        public void Press() { _pressedAt = Stopwatch.GetTimestamp(); FiredWithPortOff = !port.On; }
        public void Release()
        {
            if (_pressedAt == 0) return;
            HeldSeconds = Stopwatch.GetElapsedTime(_pressedAt).TotalSeconds;
            _pressedAt = 0;
            if (!Fires || port.On) return; // in "Now connecting" the camera does not shoot
            Shots++;
            var data = new byte[200_000 + Shots];
            new Random(Shots).NextBytes(data);
            string path = Path.Combine(root, "DCIM", "100SIGMA", $"SDIM{++_number:0000}.DNG");
            File.WriteAllBytes(path, data);
            (LastWritten, LastPath) = (data, path);
        }
        public void Dispose() { }
    }
}
