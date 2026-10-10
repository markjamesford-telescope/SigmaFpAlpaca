using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Serilog;
using SigmaFp.Camera;
using SigmaFp.Camera.Bulb;
using SigmaFp.Camera.Session;

// Hardware test host for the Milestone 3 camera layer.
//   CameraCli shot  <seconds> [--iso 400] [--count N] [--relay COMx] [--out dir]
//   CameraCli watch [--minutes M]          connect and report state changes (USB pull / power-cycle tests)
//   CameraCli sim   <seconds> [--count N]  same as shot, against the simulated camera
string logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SigmaFpAlpaca", "logs");
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Debug()
    .WriteTo.Console(restrictedToMinimumLevel: Serilog.Events.LogEventLevel.Information, outputTemplate: "{Timestamp:HH:mm:ss.fff} {Level:u3} {Message:lj}{NewLine}{Exception}")
    .WriteTo.File(Path.Combine(logDir, "cameracli-.log"), rollingInterval: RollingInterval.Day, retainedFileCountLimit: 30,
        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} {Level:u3} [{ThreadId}] {Message:lj}{NewLine}{Exception}")
    .Enrich.With(new ThreadIdEnricher())
    .CreateLogger();
using var factory = LoggerFactory.Create(b => b.AddSerilog(dispose: true));
var log = factory.CreateLogger("Camera");

string? Arg(string name) { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
string cmd = args.Length > 0 ? args[0] : "";
double seconds = args.Length > 1 && double.TryParse(args[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var s) ? s : 10;
int count = int.Parse(Arg("--count") ?? "1");
string outDir = Arg("--out") ?? Path.Combine(logDir, "frames");
Directory.CreateDirectory(outDir);

var world = new SimulatedWorld();
Func<ICameraSession> factoryFn = cmd == "sim"
    ? () => new SimulatedSession(world, new SimulatedSession.Options { TimeScale = 0.1, ImageSize = 35_000_000 })
    : () => new SigmaPtpSession(log);
IBulbTrigger? trigger = Arg("--relay") is { } port ? new LcusRelayTrigger(port, log) : null;

using var cam = new CameraController(factoryFn, new CameraOptions(), log, trigger);
try
{
    cam.Connect();
    if (Arg("--iso") is { } iso) cam.Iso = int.Parse(iso);
    log.LogInformation("Connected: {Model} serial {Serial} fw {Fw}; ISO {Iso}; bulb trigger: {Trig}",
        cam.Info!.Model, cam.Info.Serial, cam.Info.Firmware, cam.Iso, trigger?.Description ?? "none");

    if (cmd == "watch")
    {
        var until = DateTime.Now.AddMinutes(double.Parse(Arg("--minutes") ?? "5"));
        var last = (CameraState)(-1);
        while (DateTime.Now < until)
        {
            var st = cam.State;
            if (st != last) { log.LogInformation("WATCH state={State} lastError={Err}", st, cam.LastError ?? "-"); last = st; }
            Thread.Sleep(200);
        }
        return 0;
    }

    int ok = 0, failed = 0;
    for (int n = 1; n <= count; n++)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            while (cam.State is not (CameraState.Idle or CameraState.Error) || cam.CaptureInFlight) Thread.Sleep(200);
            // Wait out a reconnect instead of burning frames (NINA would also just wait or retry).
            while (true)
            {
                try { cam.StartExposure(seconds); break; }
                catch (InvalidOperationException ex) when (ex.Message.Contains("re-established")) { Thread.Sleep(1000); }
            }
            var last = CameraState.Exposing;
            while (true)
            {
                Thread.Sleep(100);
                var st = cam.State;
                if (st != last) { log.LogInformation("  [{N}] {State} at {T:0.0} s ({Pct:0}%)", n, st, sw.Elapsed.TotalSeconds, cam.PercentCompleted); last = st; }
                if (cam.ImageReady || (st is CameraState.Error && !cam.CaptureInFlight)) break;
            }
            if (cam.ImageReady)
            {
                var img = cam.GetImage();
                string path = Path.Combine(outDir, $"cli_{seconds}s_iso{cam.Iso}_{DateTime.Now:yyyyMMdd-HHmmss}_{n:D3}.dng");
                File.WriteAllBytes(path, img);
                bool tiff = img is [0x49, 0x49, 0x2A, 0x00, ..];
                log.LogInformation("FRAME {N}/{Count} OK: {Size} bytes, {T:0.0} s total, TIFF={Tiff} -> {Path}", n, count, img.Length, sw.Elapsed.TotalSeconds, tiff, path);
                ok++;
            }
            else
            {
                log.LogError("FRAME {N}/{Count} FAILED after {T:0.0} s: {Err}", n, count, sw.Elapsed.TotalSeconds, cam.LastError);
                failed++;
            }
        }
        catch (Exception ex)
        {
            log.LogError("FRAME {N}/{Count} FAILED to start: {Err}", n, count, ex.Message);
            failed++;
            Thread.Sleep(2000);
        }
    }
    log.LogInformation("RESULT: {Ok} ok, {Failed} failed", ok, failed);
    return failed == 0 ? 0 : 1;
}
catch (Exception ex)
{
    log.LogError(ex, "RESULT: FAILED");
    return 1;
}

sealed class ThreadIdEnricher : Serilog.Core.ILogEventEnricher
{
    public void Enrich(Serilog.Events.LogEvent e, Serilog.Core.ILogEventPropertyFactory f) =>
        e.AddPropertyIfAbsent(f.CreateProperty("ThreadId", Environment.CurrentManagedThreadId));
}
