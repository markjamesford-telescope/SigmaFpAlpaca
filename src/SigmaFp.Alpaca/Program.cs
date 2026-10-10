using System.Net;
using Serilog;
using SigmaFp.Alpaca;
using SigmaFp.Camera;
using SigmaFp.Camera.Bulb;
using SigmaFp.Camera.Session;

// Sigma fp L ASCOM Alpaca driver.
//   SigmaFp.Alpaca.exe              real camera (settings in %LOCALAPPDATA%\SigmaFpAlpaca\settings.json)
//   SigmaFp.Alpaca.exe --simulate   simulated camera (for trying NINA/ConformU without the fp L)
// As a Windows service (installed by tools/install), settings and logs live in C:\ProgramData\SigmaFpAlpaca.
bool asService = Microsoft.Extensions.Hosting.WindowsServices.WindowsServiceHelpers.IsWindowsService();
if (asService) DriverSettings.Folder = DriverSettings.ServiceFolder;
// Environment overrides (used by tests): SIGMAFP_SETTINGS=<path>, SIGMAFP_SIMULATE=1, SIGMAFP_NODISCOVERY=1
string settingsPath = Environment.GetEnvironmentVariable("SIGMAFP_SETTINGS") ?? DriverSettings.FilePath;
var settings = DriverSettings.Load(settingsPath);
if (!File.Exists(settingsPath)) settings.Save(settingsPath);
bool simulate = args.Contains("--simulate") || Environment.GetEnvironmentVariable("SIGMAFP_SIMULATE") == "1";
bool discoveryEnabled = Environment.GetEnvironmentVariable("SIGMAFP_NODISCOVERY") != "1";

string logDir = Path.Combine(DriverSettings.Folder, "logs");
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Debug()
    .MinimumLevel.Override("Microsoft", Serilog.Events.LogEventLevel.Warning)
    .MinimumLevel.Override("System", Serilog.Events.LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .WriteTo.Console(restrictedToMinimumLevel: Serilog.Events.LogEventLevel.Information,
        outputTemplate: "{Timestamp:HH:mm:ss.fff} {Level:u3} {Message:lj}{NewLine}{Exception}")
    .WriteTo.File(Path.Combine(logDir, "driver-.log"), rollingInterval: RollingInterval.Day, retainedFileCountLimit: 60,
        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} {Level:u3} [{ThreadId}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
    .Enrich.With<ThreadIdEnricher>()
    .CreateLogger();

try
{
    var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = AppContext.BaseDirectory });
    builder.Host.UseSerilog();
    builder.Host.UseWindowsService(o => o.ServiceName = "SigmaFpAlpaca");
    builder.WebHost.ConfigureKestrel(k =>
    {
        if (settings.AllowRemote) k.ListenAnyIP(settings.Port);
        else k.Listen(IPAddress.Loopback, settings.Port);
        k.Limits.MaxResponseBufferSize = null;
    });
    builder.Services.AddSingleton(settings);
    builder.Services.AddSingleton(sp => CreateController(sp, settings, simulate));
    builder.Services.AddSingleton(sp => new CameraDevice(sp.GetRequiredService<CameraController>(), settings,
        sp.GetRequiredService<ILoggerFactory>().CreateLogger("CameraDevice"), simulate ? SimulatedDecoder.Decode : null, settingsPath));

    var app = builder.Build();
    var log = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Alpaca");
    var cam = app.Services.GetRequiredService<CameraDevice>();
    AlpacaApi.Map(app, cam, settings, log);
    SetupPage.Map(app, settings, settingsPath, cam, asService ? () => Task.Run(async () =>
    {
        await Task.Delay(1000);
        log.LogWarning("Restarting for the new connection mode (the service manager starts the driver again)");
        Log.CloseAndFlush();
        Environment.Exit(3); // non-zero: the service recovery actions restart the service
    }) : null);

    using var discovery = discoveryEnabled ? DiscoveryHost.Start(settings, log) : null;
    log.LogInformation("Sigma fp L Alpaca driver {Ver} on port {Port} ({Scope}){Sim}{Svc}; settings {Settings}", CameraDevice.DriverVersion, settings.Port,
        settings.AllowRemote ? "network" : "this PC only", simulate ? " [SIMULATED CAMERA]" : "", asService ? " as Windows service" : "", settingsPath);
    app.Lifetime.ApplicationStopping.Register(() => cam.Dispose());
    app.Run();
    return 0;
}
catch (Exception ex)
{
    Log.Fatal(ex, "Driver failed to start");
    return 1;
}
finally
{
    Log.CloseAndFlush();
}

static CameraController CreateController(IServiceProvider sp, DriverSettings s, bool simulate)
{
    var lf = sp.GetRequiredService<ILoggerFactory>();
    var camLog = lf.CreateLogger("Camera");
    SimulatedSession? lastSim = null;
    var world = new SimulatedWorld();
    Func<ICameraSession> factory = simulate
        ? () => lastSim = new SimulatedSession(world, new SimulatedSession.Options { TimeScale = 0.2, ImageSize = 4096 })
        : () => new SigmaPtpSession(camLog);
    if (!simulate && s.MassStorageMode && OperatingSystem.IsWindows())
    {
        // Mass Storage mode (open question 19): relay-timed bulb with the USB port switched off, DNG read from the card.
        var bulb = new SigmaFp.Camera.MassStorage.BulbState();
        var msOptions = new SigmaFp.Camera.MassStorage.MassStorageOptions { DeleteAfterRead = s.DeleteFromCard };
        var portSwitch = new SigmaFp.Camera.MassStorage.YkushXsSwitch(s.YkushPath ?? "", camLog);
        var cards = new SigmaFp.Camera.MassStorage.WindowsCardLocator();
        IBulbTrigger? relay = s.RelayPort is null ? null
            : new SigmaFp.Camera.MassStorage.MassStorageTrigger(new LcusRelayTrigger(s.RelayPort, camLog, s.RelayChannel), bulb);
        if (relay is null) camLog.LogError("Mass Storage mode needs the relay COM port (setup page); exposures will be refused");
        camLog.LogInformation("Connection mode: Mass Storage + relay {Port} + YKUSH XS ({Tool})", s.RelayPort, string.IsNullOrWhiteSpace(s.YkushPath) ? "ykushcmd" : s.YkushPath);
        return new CameraController(() => new SigmaFp.Camera.MassStorage.MassStorageSession(portSwitch, cards, bulb, msOptions, camLog),
            new CameraOptions
            {
                DefaultIso = s.DefaultIso, QueueStartWhileBusy = s.QueueStartWhileBusy, ExternalBulbOnly = true,
                CameraBulbSeconds = s.MaxBulbSeconds, MinExternalBulbSeconds = 1,
                // No heartbeat traffic is possible with the port off; keep the timers relaxed.
                HeartbeatInterval = TimeSpan.FromSeconds(30),
                ApiTimeout = TimeSpan.FromSeconds(90), ItemTimeout = TimeSpan.FromSeconds(120),
            }, camLog, relay);
    }
    // USB mode: the fp / fp L ignore their remote jack while under USB control (open question 17), so the relay is
    // only used by the simulator here.
    if (!simulate && s.RelayPort is not null)
        camLog.LogWarning("RelayPort {Port} is set but ignored in USB mode: the camera does not accept its remote jack under USB control", s.RelayPort);
    IBulbTrigger? trigger = simulate && s.RelayPort is not null ? new SimulatedBulbTrigger(() => lastSim) : null;
    return new CameraController(factory, new CameraOptions { DefaultIso = s.DefaultIso, QueueStartWhileBusy = s.QueueStartWhileBusy }, camLog, trigger);
}

sealed class ThreadIdEnricher : Serilog.Core.ILogEventEnricher
{
    public void Enrich(Serilog.Events.LogEvent e, Serilog.Core.ILogEventPropertyFactory f) =>
        e.AddPropertyIfAbsent(f.CreateProperty("ThreadId", Environment.CurrentManagedThreadId));
}

public partial class Program;
