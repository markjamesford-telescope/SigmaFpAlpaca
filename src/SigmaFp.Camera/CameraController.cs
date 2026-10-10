using System.Diagnostics;
using Microsoft.Extensions.Logging;
using SigmaFp.Camera.Bulb;
using SigmaFp.Camera.Session;
using SigmaFp.Camera.Sigma;

namespace SigmaFp.Camera;

public enum CameraState { Disconnected, Idle, Exposing, Downloading, Error }

public sealed class CameraOptions
{
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(10);
    /// <summary>Watchdog limit for one work item (one or a few PTP transactions).</summary>
    public TimeSpan ItemTimeout { get; set; } = TimeSpan.FromSeconds(60);
    /// <summary>How long an API call waits for the worker.</summary>
    public TimeSpan ApiTimeout { get; set; } = TimeSpan.FromSeconds(45);
    /// <summary>Time beyond the exposure length before a capture counts as stuck.</summary>
    public TimeSpan CaptureGrace { get; set; } = TimeSpan.FromSeconds(90);
    /// <summary>After a stuck capture, keep polling this long before giving up on the camera.</summary>
    public TimeSpan StuckCaptureGiveUp { get; set; } = TimeSpan.FromMinutes(10);
    public TimeSpan[] ReconnectBackoff { get; set; } =
        [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10)];
    public uint ChunkSize { get; set; } = 8 * 1024 * 1024;
    public double CameraBulbSeconds { get; set; } = 300;
    public double MinExternalBulbSeconds { get; set; } = 1;
    /// <summary>Mass Storage mode: every exposure is a relay-timed bulb (no USB shutter release).</summary>
    public bool ExternalBulbOnly { get; set; }
    public int DefaultIso { get; set; } = 400;
    /// <summary>
    /// StartExposure while the camera is still finishing a capture that cannot be aborted: queue the
    /// new exposure (the old image is discarded) instead of refusing. Prevents a client that cancelled
    /// a sequence from burning through all remaining exposures with "camera busy" errors.
    /// </summary>
    public bool QueueStartWhileBusy { get; set; } = true;
}

/// <summary>
/// Camera layer: explicit state machine (Disconnected -> Idle -> Exposing -> Downloading -> Idle,
/// plus Error), all camera I/O on one <see cref="CameraWorker"/>, watchdog, reconnect with backoff.
/// Public members are thread-safe and may be called from any thread (e.g. Alpaca requests).
/// </summary>
public sealed class CameraController : IDisposable
{
    readonly Func<ICameraSession> _sessionFactory;
    readonly IBulbTrigger? _trigger;
    readonly CameraOptions _o;
    readonly ILogger _log;
    readonly object _lock = new();

    CameraWorker _worker;
    ICameraSession? _session;          // worker thread only
    int _generation;                   // invalidates scheduled callbacks after failures/reconnects
    int _reconnectAttempt;
    bool _wantConnected, _pendingDisconnect, _disposed;
    bool _reconnecting;                // USB lost; reconnect attempts in progress

    CameraState _state = CameraState.Disconnected;
    Shot? _shot;                       // non-null while the camera has a capture in flight
    Shot? _orphan;                     // capture lost with the USB link; drained after the reconnect
    ExposurePlan? _queued;             // exposure to start as soon as the in-flight capture finishes
    byte[]? _image;
    int _iso;

    sealed class Shot
    {
        public required ExposurePlan Plan;
        public required byte Id;
        public required Stopwatch Clock;
        public bool Released, Discard, Stuck;
        /// <summary>Lost with the USB link and re-adopted after the reconnect only to collect and clear it.</summary>
        public bool Orphan;
        public PictFileInfo? File;
        public byte[]? Buffer;
        public uint Offset;
    }

    public CameraController(Func<ICameraSession> sessionFactory, CameraOptions options, ILogger log, IBulbTrigger? trigger = null)
    {
        _sessionFactory = sessionFactory;
        _o = options;
        _log = log;
        _trigger = trigger;
        _iso = options.DefaultIso;
        Planner = new ExposurePlanner(trigger is not null, options.CameraBulbSeconds, options.MinExternalBulbSeconds, options.ExternalBulbOnly);
        _worker = NewWorker();
    }

    // ---------------------------------------------------------------- public state

    public ExposurePlanner Planner { get; }

    /// <summary>
    /// Raised on the worker thread after a complete image file is in memory (before ImageReady
    /// is visible to callers that poll). Handlers must be quick; start heavy work on another thread.
    /// </summary>
    public event Action<byte[], ExposurePlan>? ImageDownloaded;
    public bool HasBulbTrigger => _trigger is not null;
    public IReadOnlyList<int> IsoValues => Apex.IsoValues;

    public CameraState State { get { lock (_lock) return _wantConnected ? _state : CameraState.Disconnected; } }
    public bool Connected { get { lock (_lock) return _wantConnected; } }
    public CameraInfo? Info { get; private set; }
    public string? LastError { get; private set; }
    public bool ImageReady { get { lock (_lock) return _image is not null; } }
    public ExposurePlan? LastPlan { get; private set; }
    public DateTime? LastExposureStartUtc { get; private set; }
    public double? LastExposureSeconds { get; private set; }

    /// <summary>True while the camera itself is busy with a capture (or one is queued).</summary>
    public bool CaptureInFlight { get { lock (_lock) return (_shot is not null && !_shot.Orphan) || _queued is not null; } }

    /// <summary>
    /// The last requested exposure produced no image (USB loss, watchdog, camera failure). Stays set,
    /// even after a reconnect, until the next StartExposure, so clients waiting for ImageReady can see
    /// the failure (ASCOM CameraState = Error) instead of timing out.
    /// </summary>
    public bool LastExposureFailed { get; private set; }

    /// <summary>An exposure is queued behind a capture that is still finishing.</summary>
    public bool ExposureQueued { get { lock (_lock) return _queued is not null; } }

    public int Iso
    {
        get { lock (_lock) return _iso; }
        set
        {
            Apex.IsoCode(value); // validates
            lock (_lock) _iso = value;
        }
    }

    public double PercentCompleted
    {
        get
        {
            lock (_lock)
            {
                if (_shot is null || _shot.Orphan) return _image is null ? 0 : 100;
                if (_state == CameraState.Downloading && _shot.File is { } f) return 100.0 * _shot.Offset / Math.Max(1, f.Size);
                return Math.Min(100, 100.0 * _shot.Clock.Elapsed.TotalSeconds / Math.Max(0.001, _shot.Plan.ActualSeconds));
            }
        }
    }

    // ---------------------------------------------------------------- public operations

    public void Connect()
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_wantConnected) return;
            _wantConnected = true;
            if (_pendingDisconnect) { _pendingDisconnect = false; _log.LogInformation("Connect: cancelled pending disconnect; keeping session"); return; }
        }
        try
        {
            _worker.Invoke("connect", () => { if (_session is null) OpenSession(); }, _o.ItemTimeout, _o.ApiTimeout);
        }
        catch (Exception ex)
        {
            lock (_lock) { _wantConnected = false; _state = CameraState.Disconnected; }
            _log.LogError(ex, "Connect failed");
            throw new NotConnectedException($"Could not connect to the camera: {ex.Message}");
        }
    }

    public void Disconnect()
    {
        lock (_lock)
        {
            if (!_wantConnected) return;
            _wantConnected = false;
            _queued = null;
            _reconnecting = false;
            if (_shot is not null)
            {
                // Rule: never close the session while the camera is exposing; close when it finishes.
                _pendingDisconnect = true;
                _shot.Discard = true;
                _log.LogWarning("Disconnect requested during a capture; the session will close when the camera finishes");
                if (_shot.Plan.Kind == TriggerKind.ExternalBulb) _worker.Post("release on disconnect", ReleaseBulb, _o.ItemTimeout);
                return;
            }
        }
        try { _worker.Invoke("disconnect", CloseSession, _o.ItemTimeout, _o.ApiTimeout); }
        catch (Exception ex) { _log.LogWarning(ex, "Disconnect: error while closing (ignored)"); }
    }

    public void StartExposure(double seconds)
    {
        ExposurePlan plan;
        lock (_lock)
        {
            if (!_wantConnected) throw new NotConnectedException("Camera is not connected");
            if (_shot is not null)
            {
                if (!_o.QueueStartWhileBusy || _queued is not null)
                    throw new InvalidOperationException($"Camera is busy ({_state}); wait for the current exposure to finish");
                var next = Planner.Plan(seconds);
                _queued = next;
                _shot.Discard = true; // nobody can read the old image once a new exposure is requested
                LastExposureFailed = false;
                _image = null;
                _log.LogWarning("StartExposure({Seconds} s) while the camera is still finishing a {Kind} capture (it cannot be aborted): " +
                                "queued; it starts as soon as the camera is free and the old image is discarded", seconds, _shot.Plan.Kind);
                if (_shot.Plan.Kind == TriggerKind.ExternalBulb) _worker.Post("release for queued exposure", ReleaseBulb, _o.ItemTimeout);
                return;
            }
            if (_reconnecting && _o.QueueStartWhileBusy)
            {
                // The camera is being reconnected after a USB loss: hold the exposure and start it as soon as the
                // camera is back, instead of refusing (a client with "continue on error" would burn through its
                // whole sequence in a second). If the camera never returns, the client's own timeout applies.
                var held = Planner.Plan(seconds);
                bool replaced = _queued is not null;
                _queued = held;
                LastExposureFailed = false;
                _image = null;
                _log.LogWarning("StartExposure({Seconds} s) while reconnecting: held{Replaced}; it starts when the camera is back",
                    seconds, replaced ? " (replacing an earlier held exposure)" : "");
                return;
            }
            if (_state is not (CameraState.Idle or CameraState.Error))
                throw new InvalidOperationException($"Cannot start an exposure while the camera is {_state}");
            plan = Planner.Plan(seconds); // throws ArgumentOutOfRangeException
            LastExposureFailed = false;
        }
        _worker.Invoke("start exposure", () => BeginExposure(plan), _o.ItemTimeout, _o.ApiTimeout);
    }

    /// <summary>Ends an external-bulb exposure now and discards it. Other kinds finish in the camera and are discarded.</summary>
    public void AbortExposure()
    {
        lock (_lock)
        {
            if (_shot is null || _shot.Orphan) return;
            _shot.Discard = true;
            _log.LogInformation("Abort requested ({Kind})", _shot.Plan.Kind);
            if (_shot.Plan.Kind == TriggerKind.ExternalBulb) _worker.Post("abort (release)", ReleaseBulb, _o.ItemTimeout);
        }
    }

    /// <summary>Ends an external-bulb exposure now and keeps the image.</summary>
    public void StopExposure()
    {
        lock (_lock)
        {
            if (_shot is null || _shot.Orphan) return;
            if (_shot.Plan.Kind != TriggerKind.ExternalBulb)
                throw new InvalidOperationException("Only bulb-cable exposures can be stopped early");
            _worker.Post("stop (release)", ReleaseBulb, _o.ItemTimeout);
        }
    }

    /// <summary>Forget the previous connection's exposure (ASCOM: a new connection starts clean).</summary>
    public void ResetExposureHistory()
    {
        lock (_lock)
        {
            if (_shot is not null && !_shot.Orphan) return;
            _image = null;
            LastExposureFailed = false;
            LastPlan = null;
            LastExposureStartUtc = null;
            LastExposureSeconds = null;
        }
    }

    /// <summary>The last downloaded image file (DNG bytes).</summary>
    public byte[] GetImage()
    {
        lock (_lock) return _image ?? throw new InvalidOperationException("No image is ready");
    }

    // ---------------------------------------------------------------- worker-side logic

    CameraWorker NewWorker()
    {
        var w = new CameraWorker(_log, "SigmaFpCamera");
        w.Hung += OnWorkerHung;
        return w;
    }

    /// <summary>Runs a scheduled step on the worker if no failure/reconnect happened since scheduling.</summary>
    void Schedule(TimeSpan delay, string name, Action step)
    {
        int gen;
        lock (_lock) gen = _generation;
        _worker.PostDelayed(delay, name, () => Guarded(name, gen, step), _o.ItemTimeout);
    }

    void Guarded(string name, int gen, Action step)
    {
        lock (_lock) if (gen != _generation || _disposed) return;
        try { step(); }
        catch (CameraCommException ex) { HandleCommFailure(name, ex); }
        catch (Exception ex)
        {
            _log.LogError(ex, "{Step} failed", name);
            FinishShot($"{name}: {ex.Message}");
        }
    }

    void OpenSession()
    {
        var session = _sessionFactory();
        try
        {
            var info = session.Open();
            _trigger?.Open();
            _session = session;
            Info = info;
            ExposurePlan? held = null;
            Shot? orphan;
            lock (_lock)
            {
                _reconnectAttempt = 0; _generation++; _reconnecting = false;
                orphan = _shot is null ? _orphan : null;
                _orphan = null;
                if (orphan is not null)
                {
                    // Rule: never close the session while the camera may still hold a capture. Collect and clear the
                    // shot that was in flight when USB dropped before going idle; a held exposure starts after it.
                    _shot = orphan;
                    _state = CameraState.Error; // the lost frame stays an error for clients
                }
                else
                {
                    _state = CameraState.Idle;
                    held = _shot is null ? _queued : null;
                    if (held is not null) _queued = null;
                }
            }
            if (orphan is not null)
            {
                _log.LogWarning("Connected to {Model} {Serial}; clearing shot {Id} (lost with the USB link) before going idle", info.Model, info.Serial, orphan.Id);
                Schedule(TimeSpan.Zero, "poll", Poll);
                return;
            }
            _log.LogInformation("State -> Idle (connected to {Model} {Serial})", info.Model, info.Serial);
            if (held is not null)
            {
                _log.LogInformation("Starting the exposure held during the reconnect ({Seconds} s)", held.ActualSeconds);
                var plan = held;
                _worker.Post("start held exposure", () =>
                {
                    try { BeginExposure(plan); }
                    catch (CameraCommException) { /* handled inside BeginExposure */ }
                    catch (Exception ex)
                    {
                        lock (_lock) { LastExposureFailed = true; _state = CameraState.Error; LastError = $"Held exposure could not start: {ex.Message}"; }
                        _log.LogError(ex, "Held exposure could not start");
                    }
                }, _o.ItemTimeout);
                return;
            }
            Schedule(_o.HeartbeatInterval, "heartbeat", Heartbeat);
        }
        catch
        {
            try { session.Dispose(); } catch { /* ignore */ }
            try { _trigger?.Dispose(); } catch { /* ignore */ }
            throw;
        }
    }

    void CloseSession()
    {
        lock (_lock) { _generation++; _pendingDisconnect = false; }
        var s = _session;
        _session = null;
        try { s?.Close(); }
        catch (Exception ex) { _log.LogWarning(ex, "Error while closing the camera session (ignored)"); }
        finally { s?.Dispose(); }
        try { _trigger?.Dispose(); } catch { /* ignore */ }
        lock (_lock) _state = CameraState.Disconnected;
        _log.LogInformation("State -> Disconnected");
    }

    void BeginExposure(ExposurePlan plan)
    {
        var s = _session ?? throw new InvalidOperationException("Camera connection was lost and is being re-established; try again shortly");
        int iso;
        lock (_lock) iso = _iso;
        try
        {
            s.ApplySettings(plan.ShutterCode, Apex.IsoCode(iso));
            byte id = s.PrepareCapture();
            if (plan.Kind == TriggerKind.ExternalBulb) (_trigger ?? throw new InvalidOperationException("No bulb trigger")).Press();
            else s.Snap();

            var shot = new Shot { Plan = plan, Id = id, Clock = Stopwatch.StartNew() };
            lock (_lock)
            {
                _shot = shot;
                _image = null;
                _state = CameraState.Exposing;
                LastPlan = plan;
                LastExposureStartUtc = DateTime.UtcNow;
                LastExposureSeconds = plan.ActualSeconds;
                _generation++; // stop heartbeats; polling takes over
            }
            _log.LogInformation("State -> Exposing: {Kind} {Seconds} s at ISO {Iso} (shot id {Id})", plan.Kind, plan.ActualSeconds, iso, id);
            if (plan.Kind == TriggerKind.ExternalBulb)
                Schedule(TimeSpan.FromSeconds(plan.ActualSeconds), "bulb release", ReleaseBulb);
            Schedule(_o.PollInterval, "poll", Poll);
        }
        catch (CameraCommException ex) { HandleCommFailure("start exposure", ex); throw; }
        catch (CameraProtocolException ex)
        {
            try { _trigger?.Release(); } catch { /* ignore */ }
            lock (_lock) { _state = CameraState.Error; LastError = ex.Message; LastExposureFailed = true; }
            _log.LogError("Start exposure refused by camera: {Msg}", ex.Message);
            throw;
        }
    }

    void ReleaseBulb()
    {
        Shot? shot;
        lock (_lock) shot = _shot;
        if (shot is null || shot.Released || shot.Plan.Kind != TriggerKind.ExternalBulb) return;
        try { _trigger!.Release(); }
        catch (CameraCommException ex) { _log.LogError(ex, "Bulb release failed; the camera will stop at its {Limit} s limit", _o.CameraBulbSeconds); }
        shot.Released = true;
        _log.LogInformation("Bulb released after {Seconds:0.00} s", shot.Clock.Elapsed.TotalSeconds);
    }

    void Poll()
    {
        Shot? shot;
        lock (_lock) shot = _shot;
        if (shot is null || _session is null) return;

        var st = _session.GetStatus(shot.Id);
        if (shot.Orphan && st.Status == CaptStatusCode.Cleared)
        {
            _log.LogInformation("Lost shot {Id} is no longer in the camera", shot.Id);
            FinishShot(null, discarded: true);
            return;
        }
        if (CaptStatusCode.IsDone(st.Status))
        {
            if (shot.Discard) { _session.ClearImage(shot.Id); FinishShot(null, discarded: true); return; }
            var file = _session.GetImageInfo();
            lock (_lock) { shot.File = file; shot.Buffer = new byte[file.Size]; _state = CameraState.Downloading; }
            _log.LogInformation("State -> Downloading: {Name} {W}x{H}, {Size} bytes (ready after {Sec:0.0} s)", file.Name, file.Width, file.Height, file.Size, shot.Clock.Elapsed.TotalSeconds);
            Schedule(TimeSpan.Zero, "download", DownloadChunk);
            return;
        }
        if (CaptStatusCode.IsFailure(st.Status))
        {
            try { _session.ClearImage(shot.Id); } catch (CameraProtocolException) { /* best effort */ }
            FinishShot($"Camera reported: {CaptStatusCode.Describe(st.Status)} (0x{st.Status:X4})");
            return;
        }

        var deadline = TimeSpan.FromSeconds(shot.Plan.ActualSeconds) + _o.CaptureGrace;
        if (shot.Clock.Elapsed > deadline && !shot.Stuck)
        {
            shot.Stuck = true;
            lock (_lock) { _state = CameraState.Error; LastError = $"Camera has not finished {shot.Clock.Elapsed.TotalSeconds:0} s after a {shot.Plan.ActualSeconds} s exposure (status {CaptStatusCode.Describe(st.Status)})"; }
            _log.LogError("{Msg}; still waiting (never closing mid-capture)", LastError);
        }
        if (shot.Clock.Elapsed > deadline + _o.StuckCaptureGiveUp)
        {
            FinishShot("Camera never finished the capture; power-cycle the camera");
            return;
        }
        Schedule(_o.PollInterval, "poll", Poll);
    }

    void DownloadChunk()
    {
        Shot? shot;
        lock (_lock) shot = _shot;
        if (shot?.File is not { } f || shot.Buffer is null || _session is null) return;

        uint want = Math.Min(_o.ChunkSize, f.Size - shot.Offset);
        var data = _session.ReadImageChunk(f.Address, shot.Offset, want);
        if (data.Length == 0 || shot.Offset + data.Length > f.Size)
            throw new CameraCommException($"Bad image chunk: {data.Length} bytes at offset {shot.Offset} of {f.Size}");
        data.Span.CopyTo(shot.Buffer.AsSpan((int)shot.Offset));
        lock (_lock) shot.Offset += (uint)data.Length;

        if (shot.Offset < f.Size) { Schedule(TimeSpan.Zero, "download", DownloadChunk); return; }

        _session.ClearImage(shot.Id);
        if (!shot.Discard)
        {
            try { ImageDownloaded?.Invoke(shot.Buffer, shot.Plan); }
            catch (Exception ex) { _log.LogError(ex, "ImageDownloaded handler failed"); }
            lock (_lock) _image = shot.Buffer; // ImageReady only after the whole file is in memory
            _log.LogInformation("Image downloaded: {Size} bytes, {Sec:0.0} s after start", f.Size, shot.Clock.Elapsed.TotalSeconds);
        }
        FinishShot(null, discarded: shot.Discard);
    }

    void FinishShot(string? error, bool discarded = false)
    {
        bool closeNow;
        ExposurePlan? next;
        lock (_lock)
        {
            if (_shot is null) return;
            _shot = null;
            closeNow = _pendingDisconnect;
            next = closeNow ? null : _queued;
            _queued = null;
            if (error is not null) { LastError = error; _state = CameraState.Error; if (next is null) LastExposureFailed = true; }
            else _state = CameraState.Idle;
        }
        if (error is not null) _log.LogError("Exposure failed: {Error}. State -> Error", error);
        else _log.LogInformation("State -> Idle{Discarded}", discarded ? " (image discarded)" : "");

        if (closeNow) { CloseSession(); return; }
        if (next is not null)
        {
            _log.LogInformation("Starting the queued exposure ({Seconds} s)", next.ActualSeconds);
            try { BeginExposure(next); }
            catch (CameraCommException) { /* handled inside BeginExposure */ }
            catch (Exception ex)
            {
                lock (_lock) { LastExposureFailed = true; _state = CameraState.Error; LastError = $"Queued exposure could not start: {ex.Message}"; }
                _log.LogError(ex, "Queued exposure could not start");
            }
            return;
        }
        lock (_lock) _generation++;
        Schedule(_o.HeartbeatInterval, "heartbeat", Heartbeat);
    }

    void Heartbeat()
    {
        lock (_lock) if (_shot is not null || _session is null) return;
        try { _session!.Heartbeat(); }
        catch (CameraProtocolException ex) { _log.LogWarning("Heartbeat refused by camera: {Msg}", ex.Message); }
        Schedule(_o.HeartbeatInterval, "heartbeat", Heartbeat);
    }

    void HandleCommFailure(string where, Exception ex)
    {
        bool reconnect;
        lock (_lock)
        {
            _generation++;
            if (_shot is not null || _queued is not null)
            {
                LastError = $"Exposure lost: camera communication failed during {where}: {ex.Message}";
                LastExposureFailed = true;
            }
            else LastError = $"Camera communication failed during {where}: {ex.Message}";
            Adopt(_shot);
            _shot = null;
            _queued = null;
            _image = null;
            reconnect = _wantConnected;
            _reconnecting = reconnect;
            _state = reconnect ? CameraState.Error : CameraState.Disconnected;
            _pendingDisconnect = false;
        }
        _log.LogError(ex, "Camera communication failed during {Where}. State -> {State}", where, State);
        try { _trigger?.Release(); } catch { /* ignore */ }
        try { _session?.Dispose(); } catch { /* ignore: the device is gone */ }
        _session = null;
        if (reconnect) ScheduleReconnect();
    }

    /// <summary>Remembers a capture lost with the USB link so the reconnect can drain it (caller holds the lock).</summary>
    void Adopt(Shot? shot)
    {
        if (shot is null) return;
        shot.Orphan = true;
        shot.Discard = true;
        shot.Released = true; // the relay is released on every comm failure
        shot.File = null; shot.Buffer = null; shot.Offset = 0;
        _orphan = shot;
    }

    void ScheduleReconnect()
    {
        int attempt;
        lock (_lock) attempt = _reconnectAttempt++;
        var delay = _o.ReconnectBackoff[Math.Min(attempt, _o.ReconnectBackoff.Length - 1)];
        _log.LogInformation("Reconnect attempt {N} in {Delay:0.#} s", attempt + 1, delay.TotalSeconds);
        int gen;
        lock (_lock) gen = _generation;
        _worker.PostDelayed(delay, "reconnect", () =>
        {
            lock (_lock) if (gen != _generation || !_wantConnected || _disposed) return;
            try
            {
                OpenSession();
                _log.LogInformation("Reconnected after {N} attempt(s)", attempt + 1);
            }
            catch (Exception ex)
            {
                _log.LogWarning("Reconnect attempt {N} failed: {Msg}", attempt + 1, ex.Message);
                ScheduleReconnect();
            }
        }, _o.ItemTimeout);
    }

    void OnWorkerHung(string item, TimeSpan elapsed)
    {
        bool reconnect;
        lock (_lock)
        {
            if (_disposed) return;
            _generation++;
            LastError = $"Camera did not respond ({item} hung for {elapsed.TotalSeconds:0} s)";
            if (_shot is not null || _queued is not null) LastExposureFailed = true;
            Adopt(_shot);
            _shot = null;
            _queued = null;
            reconnect = _wantConnected;
            _reconnecting = reconnect;
            _state = reconnect ? CameraState.Error : CameraState.Disconnected;
            var old = _worker;
            _worker = NewWorker(); // the stuck thread is abandoned, never reused
            old.Dispose();
            _session = null;       // owned by the stuck thread; do not touch it from here
        }
        if (reconnect) ScheduleReconnect();
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            if (_shot is not null)
                _log.LogWarning("Disposing while a capture is in flight; closing anyway (the camera may need a power cycle)");
        }
        try { _worker.Invoke("dispose", CloseSession, _o.ItemTimeout, TimeSpan.FromSeconds(10)); }
        catch (Exception ex) { _log.LogWarning(ex, "Dispose: close failed"); }
        lock (_lock) _disposed = true;
        _worker.Dispose();
    }
}
