using System.Diagnostics;
using Microsoft.Extensions.Logging;
using SigmaFp.Camera.Bulb;
using SigmaFp.Camera.Session;
using Xunit.Abstractions;

namespace SigmaFp.Camera.Tests;

/// <summary>
/// State machine, timeouts, reconnect and the Milestone 2 rules, against the simulated camera.
/// Simulated time runs 100x faster (1 camera second = 10 ms).
/// </summary>
public sealed class ControllerTests : IDisposable
{
    readonly ITestOutputHelper _out;
    readonly SimulatedWorld _world = new();
    readonly List<SimulatedSession> _sessions = new();
    readonly SimulatedSession.Options _sim = new() { TimeScale = 0.01, ProcessingSeconds = 3, BulbLimitSeconds = 300, ImageSize = 1_000_000 };
    readonly CameraOptions _opts = new()
    {
        PollInterval = TimeSpan.FromMilliseconds(10),
        HeartbeatInterval = TimeSpan.FromMilliseconds(50),
        ItemTimeout = TimeSpan.FromSeconds(2),
        ApiTimeout = TimeSpan.FromSeconds(5),
        CaptureGrace = TimeSpan.FromSeconds(1),
        StuckCaptureGiveUp = TimeSpan.FromSeconds(1),
        ReconnectBackoff = [TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(50)],
        ChunkSize = 300_000,
        MinExternalBulbSeconds = 0.1,
    };
    Action<string>? _fault;
    CameraController? _cam;

    public ControllerTests(ITestOutputHelper output) => _out = output;

    CameraController Create(bool withTrigger = false)
    {
        IBulbTrigger? trigger = withTrigger ? new SimulatedBulbTrigger(() => _sessions.LastOrDefault()) : null;
        _cam = new CameraController(() =>
        {
            var s = new SimulatedSession(_world, _sim) { BeforeOp = op => _fault?.Invoke(op) };
            lock (_sessions) _sessions.Add(s);
            return s;
        }, _opts, new TestLogger(_out), trigger);
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
    public void Connect_goes_idle_and_reports_camera()
    {
        var cam = Create();
        Assert.Equal(CameraState.Disconnected, cam.State);
        cam.Connect();
        Assert.Equal(CameraState.Idle, cam.State);
        Assert.True(cam.Connected);
        Assert.Contains("fp L", cam.Info!.Model);
    }

    [Fact]
    public void Timed_exposure_full_cycle_downloads_exact_image()
    {
        var cam = Create();
        cam.Connect();
        _fault = op => { if (op == nameof(ICameraSession.ReadImageChunk)) Thread.Sleep(25); }; // make Downloading observable
        var seen = new HashSet<CameraState>();
        bool readyEarly = false;
        cam.StartExposure(10);
        Assert.Equal(CameraState.Exposing, cam.State);
        WaitUntil(() =>
        {
            var st = cam.State;
            seen.Add(st);
            if (st != CameraState.Idle && cam.ImageReady) readyEarly = true;
            return st == CameraState.Idle;
        }, what: "idle after exposure");

        Assert.Contains(CameraState.Downloading, seen);
        Assert.False(readyEarly, "ImageReady must flip only after the whole file is in memory");
        Assert.True(cam.ImageReady);
        var img = cam.GetImage();
        Assert.Equal(_sim.ImageSize, img.Length);
        for (int i = 0; i < img.Length; i += 9973) Assert.Equal(SimulatedSession.ExpectedByte(i), img[i]);
        Assert.Equal(100, cam.PercentCompleted);
    }

    [Fact]
    public void Each_shot_clears_stale_capture_entries_first()
    {
        var cam = Create();
        cam.Connect();
        cam.StartExposure(1);
        WaitUntil(() => cam.ImageReady);
        var calls = _sessions[0].CallsCopy();
        Assert.True(calls.IndexOf(nameof(ICameraSession.PrepareCapture)) < calls.IndexOf(nameof(ICameraSession.Snap)));
    }

    [Fact]
    public void Starting_while_busy_queues_one_exposure_and_refuses_a_second()
    {
        var cam = Create();
        cam.Connect();
        int downloads = 0;
        cam.ImageDownloaded += (_, _) => Interlocked.Increment(ref downloads);
        cam.StartExposure(300);   // un-abortable camera bulb (3 s real)
        Thread.Sleep(100);
        cam.StartExposure(10);    // e.g. NINA restarted its sequence: queued, not refused
        Assert.True(cam.ExposureQueued);
        Assert.Throws<InvalidOperationException>(() => cam.StartExposure(10));
        WaitUntil(() => cam.ImageReady, 15, "queued exposure image");
        Assert.Equal(1, downloads);                     // the abandoned bulb image was discarded
        Assert.Equal(10, cam.LastExposureSeconds);      // the delivered image is the queued exposure
        Assert.False(cam.ExposureQueued);
    }

    [Fact]
    public void Starting_while_busy_is_refused_when_queueing_disabled()
    {
        _opts.QueueStartWhileBusy = false;
        var cam = Create();
        cam.Connect();
        cam.StartExposure(10);
        Assert.Throws<InvalidOperationException>(() => cam.StartExposure(10));
    }

    [Fact]
    public void Starting_when_disconnected_is_refused()
    {
        var cam = Create();
        Assert.Throws<NotConnectedException>(() => cam.StartExposure(10));
    }

    [Theory]
    [InlineData(120)]
    [InlineData(31)]
    [InlineData(400)]
    public void Unsupported_lengths_without_cable_are_rejected(double s)
    {
        var cam = Create();
        cam.Connect();
        Assert.Throws<ArgumentOutOfRangeException>(() => cam.StartExposure(s));
        Assert.Equal(CameraState.Idle, cam.State);
    }

    [Fact]
    public void Camera_bulb_runs_to_limit_and_downloads()
    {
        var cam = Create();
        cam.Connect();
        cam.StartExposure(300); // 3 s real
        Assert.Equal(TriggerKind.CameraBulb, cam.LastPlan!.Kind);
        WaitUntil(() => cam.ImageReady, 15, "bulb image");
        Assert.Equal(CameraState.Idle, cam.State);
    }

    [Fact]
    public void Usb_loss_mid_exposure_goes_error_then_reconnects_and_works_again()
    {
        var cam = Create();
        cam.Connect();
        cam.StartExposure(10);
        Thread.Sleep(30);
        _world.Present = false;
        WaitUntil(() => cam.State == CameraState.Error, what: "error state");
        Assert.Contains("Exposure lost", cam.LastError);
        Assert.False(cam.ImageReady);

        _world.Present = true;
        WaitUntil(() => cam.State == CameraState.Idle, what: "reconnected");
        Assert.True(_world.Sessions >= 2);
        Assert.True(cam.LastExposureFailed, "the lost frame must stay visible after the reconnect");
        cam.StartExposure(1);
        Assert.False(cam.LastExposureFailed);
        WaitUntil(() => cam.ImageReady, what: "image after reconnect");
    }

    [Fact]
    public void Start_during_reconnect_is_held_and_runs_when_camera_returns()
    {
        _opts.ReconnectBackoff = [TimeSpan.FromMilliseconds(200)];
        var cam = Create();
        cam.Connect();
        cam.StartExposure(10);
        Thread.Sleep(30);
        _world.Present = false;
        WaitUntil(() => cam.LastExposureFailed, what: "lost frame");
        cam.StartExposure(1);                       // NINA moves on at once: must not be refused
        cam.StartExposure(1);                       // and a repeat replaces the held one, still no error
        Assert.True(cam.ExposureQueued);
        Assert.True(cam.CaptureInFlight);
        Assert.False(cam.LastExposureFailed);
        _world.Present = true;
        WaitUntil(() => cam.ImageReady, 10, "held exposure image after reconnect");
        Assert.Equal(1, cam.LastExposureSeconds);
    }

    [Fact]
    public void Lost_shot_is_drained_after_reconnect_before_the_session_can_close()
    {
        // Field failure 2026-10-04: USB dropped mid-shot, the camera kept its session and the shot, the driver
        // went idle, the client disconnected, and closing the session over the uncollected shot locked the camera.
        var cam = Create();
        cam.Connect();
        cam.StartExposure(300);                     // 3 s real, so the shot is still in the camera after the reconnect
        Thread.Sleep(100);
        _world.Present = false;
        WaitUntil(() => cam.LastExposureFailed, what: "lost frame");
        _world.Present = true;
        WaitUntil(() => _sessions.Count >= 2 && _sessions[^1].HasCalled(nameof(ICameraSession.GetStatus)), what: "drain polling");

        Assert.True(cam.LastExposureFailed, "the lost frame stays an error while it is drained");
        Assert.False(cam.CaptureInFlight, "draining must not delay the lost-frame error for clients");
        cam.Disconnect();
        Assert.False(_sessions[^1].HasCalled(nameof(ICameraSession.Close)));

        WaitUntil(() => _sessions[^1].HasCalled(nameof(ICameraSession.Close)), 15, "deferred close after the drain");
        var calls = _sessions[^1].CallsCopy();
        Assert.True(calls.LastIndexOf(nameof(ICameraSession.ClearImage)) < calls.LastIndexOf(nameof(ICameraSession.Close)));
        Assert.False(_world.LockedUp, "closing over an uncollected shot locks the camera; must not happen");
    }

    [Fact]
    public void Start_during_drain_is_queued_and_runs_after_the_lost_shot_clears()
    {
        var cam = Create();
        cam.Connect();
        cam.StartExposure(300);
        Thread.Sleep(100);
        _world.Present = false;
        WaitUntil(() => cam.LastExposureFailed, what: "lost frame");
        _world.Present = true;
        WaitUntil(() => _sessions.Count >= 2 && _sessions[^1].HasCalled(nameof(ICameraSession.GetStatus)), what: "drain polling");
        cam.StartExposure(1);
        Assert.True(cam.ExposureQueued);
        Assert.False(cam.LastExposureFailed);
        WaitUntil(() => cam.ImageReady, 15, "queued image after the drain");
        Assert.Equal(1, cam.LastExposureSeconds);
        Assert.False(_world.LockedUp);
    }

    [Fact]
    public void Lost_shot_already_gone_after_reconnect_goes_idle_at_once()
    {
        var cam = Create();
        cam.Connect();
        cam.StartExposure(300);
        Thread.Sleep(100);
        _world.Present = false;
        WaitUntil(() => cam.LastExposureFailed, what: "lost frame");
        _world.PowerCycle();                        // e.g. the owner power-cycled the camera: its capture list is empty
        _world.Present = true;
        WaitUntil(() => cam.State == CameraState.Idle, 2, "idle without waiting for the 300 s shot");
        Assert.True(cam.LastExposureFailed);
    }

    [Fact]
    public void Usb_loss_while_idle_is_detected_by_heartbeat()
    {
        var cam = Create();
        cam.Connect();
        _world.Present = false;
        WaitUntil(() => cam.State == CameraState.Error, what: "heartbeat failure");
        _world.Present = true;
        WaitUntil(() => cam.State == CameraState.Idle, what: "reconnect");
    }

    [Fact]
    public void Hung_camera_call_trips_watchdog_and_recovers()
    {
        _opts.ItemTimeout = TimeSpan.FromMilliseconds(300);
        var cam = Create();
        cam.Connect();
        using var block = new ManualResetEventSlim(false);
        int hangOnce = 1;
        _fault = op => { if (op == nameof(ICameraSession.GetStatus) && Interlocked.Exchange(ref hangOnce, 0) == 1) block.Wait(); };
        cam.StartExposure(10);
        WaitUntil(() => cam.State == CameraState.Error, what: "watchdog");
        Assert.Contains("did not respond", cam.LastError);
        WaitUntil(() => cam.State == CameraState.Idle, what: "recovered");
        cam.StartExposure(1);
        WaitUntil(() => cam.ImageReady, what: "image after watchdog recovery");
        block.Set();
    }

    [Fact]
    public void Disconnect_during_capture_defers_close_until_camera_finishes()
    {
        var cam = Create();
        cam.Connect();
        cam.StartExposure(300); // 3 s real camera bulb
        Thread.Sleep(100);
        cam.Disconnect();
        Assert.False(cam.Connected);
        Assert.Equal(CameraState.Disconnected, cam.State);
        Assert.False(_sessions[0].HasCalled(nameof(ICameraSession.Close)));

        WaitUntil(() => _sessions[0].HasCalled(nameof(ICameraSession.Close)), 15, "deferred close");
        Assert.False(_world.LockedUp, "closing mid-exposure locks the camera; must not happen");
        Assert.False(cam.ImageReady);
    }

    [Fact]
    public void Reconnect_during_deferred_disconnect_keeps_session()
    {
        var cam = Create();
        cam.Connect();
        cam.StartExposure(10);
        cam.Disconnect();
        cam.Connect();
        Assert.True(cam.Connected);
        WaitUntil(() => cam.State == CameraState.Idle && !cam.CaptureInFlight, what: "finish");
        Assert.Equal(1, _world.Sessions);
    }

    [Fact]
    public void Locked_camera_reports_error_but_stays_connected()
    {
        _world.LockedUp = true;
        var cam = Create();
        cam.Connect();
        cam.StartExposure(1);
        WaitUntil(() => cam.State == CameraState.Error, what: "capture failure");
        Assert.Contains("switch it off and on", cam.LastError);
        Assert.True(cam.Connected);
        _world.PowerCycle();
        cam.StartExposure(1); // allowed from Error
        WaitUntil(() => cam.ImageReady, what: "image after power cycle");
    }

    [Fact]
    public void Connect_failure_throws_and_stays_disconnected()
    {
        _world.Present = false;
        var cam = Create();
        Assert.Throws<NotConnectedException>(() => cam.Connect());
        Assert.Equal(CameraState.Disconnected, cam.State);
        Assert.False(cam.Connected);
    }

    [Fact]
    public void External_bulb_exposes_for_requested_time()
    {
        var cam = Create(withTrigger: true);
        cam.Connect();
        var sw = Stopwatch.StartNew();
        cam.StartExposure(120); // 1.2 s real via relay
        Assert.Equal(TriggerKind.ExternalBulb, cam.LastPlan!.Kind);
        WaitUntil(() => cam.ImageReady, 15, "relay bulb image");
        Assert.InRange(sw.Elapsed.TotalSeconds, 1.2, 10);
    }

    [Fact]
    public void External_bulb_abort_releases_early_and_discards()
    {
        var cam = Create(withTrigger: true);
        cam.Connect();
        cam.StartExposure(200); // 2 s real
        Thread.Sleep(200);
        cam.AbortExposure();
        WaitUntil(() => !cam.CaptureInFlight, 3, "abort to finish well before 2 s");
        Assert.False(cam.ImageReady);
        Assert.Equal(CameraState.Idle, cam.State);
    }

    [Fact]
    public void External_bulb_stop_keeps_image()
    {
        var cam = Create(withTrigger: true);
        cam.Connect();
        cam.StartExposure(200);
        Thread.Sleep(200);
        cam.StopExposure();
        WaitUntil(() => cam.ImageReady, 3, "stopped image");
    }

    [Fact]
    public void Stop_is_refused_for_camera_timed_exposures()
    {
        var cam = Create();
        cam.Connect();
        cam.StartExposure(10);
        Assert.Throws<InvalidOperationException>(() => cam.StopExposure());
    }

    [Fact]
    public void Iso_validation_and_default()
    {
        var cam = Create();
        Assert.Equal(400, cam.Iso);
        cam.Iso = 800;
        Assert.Equal(800, cam.Iso);
        Assert.Throws<ArgumentOutOfRangeException>(() => cam.Iso = 123);
    }

    public void Dispose() => _cam?.Dispose();
}

sealed class TestLogger(ITestOutputHelper output) : ILogger
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel level) => level >= LogLevel.Information;
    public void Log<TState>(LogLevel level, EventId id, TState state, Exception? ex, Func<TState, Exception?, string> fmt)
    {
        try { output.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {level,-11} {fmt(state, ex)}{(ex is null ? "" : " | " + ex.GetType().Name + ": " + ex.Message)}"); }
        catch (InvalidOperationException) { /* test finished */ }
    }
}
