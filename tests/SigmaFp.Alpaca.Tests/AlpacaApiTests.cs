using System.Net;
using System.Text.Json;
using ASCOM.Common.Alpaca;
using Microsoft.AspNetCore.Mvc.Testing;

namespace SigmaFp.Alpaca.Tests;

/// <summary>HTTP-level Alpaca tests against the driver running a simulated fp L.</summary>
public sealed class AlpacaApiTests : IClassFixture<AlpacaApiTests.Driver>
{
    public sealed class Driver : WebApplicationFactory<Program>
    {
        public Driver()
        {
            Environment.SetEnvironmentVariable("SIGMAFP_SIMULATE", "1");
            Environment.SetEnvironmentVariable("SIGMAFP_NODISCOVERY", "1");
            Environment.SetEnvironmentVariable("SIGMAFP_SETTINGS", Path.Combine(Path.GetTempPath(), $"sigmafp-test-{Guid.NewGuid():N}.json"));
        }
    }

    readonly HttpClient _c;
    public AlpacaApiTests(Driver d) => _c = d.CreateClient();

    async Task<JsonElement> Get(string path)
    {
        var r = await _c.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;
    }

    async Task<JsonElement> Put(string path, params (string, string)[] form)
    {
        var r = await _c.PutAsync(path, new FormUrlEncodedContent(form.Select(f => new KeyValuePair<string, string>(f.Item1, f.Item2))));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;
    }

    static int Err(JsonElement e) => e.GetProperty("ErrorNumber").GetInt32();

    async Task Connect()
    {
        Assert.Equal(0, Err(await Put("/api/v1/camera/0/connected", ("Connected", "True"))));
        Assert.True((await Get("/api/v1/camera/0/connected")).GetProperty("Value").GetBoolean());
    }

    async Task WaitImageReady(double seconds = 30)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!(await Get("/api/v1/camera/0/imageready")).GetProperty("Value").GetBoolean())
        {
            if (sw.Elapsed.TotalSeconds > seconds) throw new TimeoutException("image not ready");
            await Task.Delay(100);
        }
    }

    [Fact]
    public async Task Management_endpoints()
    {
        Assert.Equal(1, (await Get("/management/apiversions")).GetProperty("Value")[0].GetInt32());
        var dev = (await Get("/management/v1/configureddevices")).GetProperty("Value")[0];
        Assert.Equal("Camera", dev.GetProperty("DeviceType").GetString());
        Assert.Equal(0, dev.GetProperty("DeviceNumber").GetInt32());
        Assert.False(string.IsNullOrEmpty(dev.GetProperty("UniqueID").GetString()));
        Assert.Contains("Sigma", (await Get("/management/v1/description")).GetProperty("Value").GetProperty("ServerName").GetString());
    }

    [Fact]
    public async Task Transaction_ids_echoed_and_increasing()
    {
        var a = await Get("/api/v1/camera/0/name?ClientTransactionID=77");
        var b = await Get("/api/v1/camera/0/name?ClientTransactionID=78");
        Assert.Equal(77u, a.GetProperty("ClientTransactionID").GetUInt32());
        Assert.True(b.GetProperty("ServerTransactionID").GetUInt32() > a.GetProperty("ServerTransactionID").GetUInt32());
        Assert.StartsWith("Sigma", a.GetProperty("Value").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Bad_requests_return_http_400()
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await _c.GetAsync("/api/v1/camera/1/name")).StatusCode);
        var r = await _c.PutAsync("/api/v1/camera/0/connected", new FormUrlEncodedContent([new("Connected", "maybe")]));
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        r = await _c.PutAsync("/api/v1/camera/0/startexposure", new FormUrlEncodedContent([new("Duration", "1")])); // Light missing
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [Fact]
    public async Task Put_parameter_names_are_case_sensitive_get_query_is_not()
    {
        var r = await _c.PutAsync("/api/v1/camera/0/connected", new FormUrlEncodedContent([new("connected", "true")]));
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Equal(0, Err(await Put("/api/v1/camera/0/connected", ("Connected", "true"))));
        Assert.Equal(55u, (await Get("/api/v1/camera/0/name?clienttransactionid=55")).GetProperty("ClientTransactionID").GetUInt32());
    }

    [Fact]
    public async Task One_client_disconnecting_does_not_disconnect_another()
    {
        Assert.Equal(0, Err(await Put("/api/v1/camera/0/connected", ("Connected", "True"), ("ClientID", "101"))));
        Assert.Equal(0, Err(await Put("/api/v1/camera/0/connected", ("Connected", "True"), ("ClientID", "202"))));
        Assert.Equal(0, Err(await Put("/api/v1/camera/0/connected", ("Connected", "False"), ("ClientID", "202"))));
        Assert.False((await Get("/api/v1/camera/0/connected?ClientID=202")).GetProperty("Value").GetBoolean());
        Assert.True((await Get("/api/v1/camera/0/connected?ClientID=101")).GetProperty("Value").GetBoolean());
        Assert.Equal(0, Err(await Get("/api/v1/camera/0/name?ClientID=101")));
        await Put("/api/v1/camera/0/connected", ("Connected", "False"), ("ClientID", "101"));
    }

    [Fact]
    public async Task Upper_case_device_type_or_method_is_rejected()
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await _c.GetAsync("/api/v1/CAMERA/0/name")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _c.GetAsync("/api/v1/camera/0/Name")).StatusCode);
    }

    [Fact]
    public async Task Properties_after_connect()
    {
        await Connect();
        Assert.Equal(4, (await Get("/api/v1/camera/0/interfaceversion")).GetProperty("Value").GetInt32());
        Assert.Equal(2, (await Get("/api/v1/camera/0/sensortype")).GetProperty("Value").GetInt32()); // RGGB
        Assert.Equal(9536, (await Get("/api/v1/camera/0/cameraxsize")).GetProperty("Value").GetInt32());
        Assert.Equal(16383, (await Get("/api/v1/camera/0/maxadu")).GetProperty("Value").GetInt32());
        var gains = (await Get("/api/v1/camera/0/gains")).GetProperty("Value").EnumerateArray().Select(g => g.GetString()).ToList();
        Assert.Equal("400", gains[10]);
        Assert.Equal(10, (await Get("/api/v1/camera/0/gain")).GetProperty("Value").GetInt32());
        Assert.Equal((int)AlpacaErrors.NotImplemented, Err(await Get("/api/v1/camera/0/ccdtemperature")));
        Assert.Equal((int)AlpacaErrors.InvalidValue, Err(await Put("/api/v1/camera/0/binx", ("BinX", "2"))));
        Assert.Equal((int)AlpacaErrors.InvalidValue, Err(await Put("/api/v1/camera/0/gain", ("Gain", "99"))));
    }

    [Fact]
    public async Task Unsupported_exposure_length_is_invalid_value()
    {
        await Connect();
        var e = await Put("/api/v1/camera/0/startexposure", ("Duration", "120"), ("Light", "True"));
        Assert.Equal((int)AlpacaErrors.InvalidValue, Err(e));
        Assert.Contains("300", e.GetProperty("ErrorMessage").GetString());
    }

    [Fact]
    public async Task Exposure_and_imagebytes_round_trip_with_ascom_decoder()
    {
        await Connect();
        await Put("/api/v1/camera/0/numx", ("NumX", "9536"));
        await Put("/api/v1/camera/0/numy", ("NumY", "6344"));
        await Put("/api/v1/camera/0/startx", ("StartX", "0"));
        await Put("/api/v1/camera/0/starty", ("StartY", "0"));
        Assert.Equal(0, Err(await Put("/api/v1/camera/0/startexposure", ("Duration", "1"), ("Light", "True"))));
        await WaitImageReady();
        Assert.Equal(0, (await Get("/api/v1/camera/0/camerastate")).GetProperty("Value").GetInt32());

        var req = new HttpRequestMessage(HttpMethod.Get, "/api/v1/camera/0/imagearray?ClientTransactionID=5");
        req.Headers.Accept.ParseAdd(ImageArrayWriter.ImageBytesMime);
        var resp = await _c.SendAsync(req);
        Assert.Equal(ImageArrayWriter.ImageBytesMime, resp.Content.Headers.ContentType!.MediaType);
        var bytes = await resp.Content.ReadAsByteArrayAsync();

        var meta = AlpacaTools.GetMetadataV1(bytes);
        Assert.Equal(0, (int)meta.ErrorNumber);
        Assert.Equal(5u, meta.ClientTransactionID);
        Assert.Equal(2, meta.Rank);
        Assert.Equal(9536, meta.Dimension1);
        Assert.Equal(6344, meta.Dimension2);

        var arr = (int[,])AlpacaTools.ToImageArray(bytes); // official ASCOM decoder
        var expected = SimulatedDecoder.Decode([]);
        foreach (var (x, y) in new[] { (0, 0), (1, 0), (0, 1), (4767, 3171), (9535, 6343), (123, 4567) })
            Assert.Equal(expected[x, y], arr[x, y]);
    }

    [Fact]
    public async Task Json_imagearray_for_subframe()
    {
        await Connect();
        await Put("/api/v1/camera/0/numx", ("NumX", "40"));
        await Put("/api/v1/camera/0/numy", ("NumY", "30"));
        await Put("/api/v1/camera/0/startx", ("StartX", "100"));
        await Put("/api/v1/camera/0/starty", ("StartY", "200"));
        Assert.Equal(0, Err(await Put("/api/v1/camera/0/startexposure", ("Duration", "1"), ("Light", "True"))));
        await WaitImageReady();
        var j = await Get("/api/v1/camera/0/imagearray");
        Assert.Equal(2, j.GetProperty("Type").GetInt32());
        Assert.Equal(2, j.GetProperty("Rank").GetInt32());
        var v = j.GetProperty("Value");
        Assert.Equal(40, v.GetArrayLength());
        Assert.Equal(30, v[0].GetArrayLength());
        var expected = SimulatedDecoder.Decode([]);
        Assert.Equal(expected[100 + 7, 200 + 11], v[7][11].GetInt32());
        // restore full frame for other tests
        await Put("/api/v1/camera/0/numx", ("NumX", "9536"));
        await Put("/api/v1/camera/0/numy", ("NumY", "6344"));
        await Put("/api/v1/camera/0/startx", ("StartX", "0"));
        await Put("/api/v1/camera/0/starty", ("StartY", "0"));
    }

    [Fact]
    public void Lost_frame_is_reported_as_ready_with_download_error()
    {
        var world = new SigmaFp.Camera.Session.SimulatedWorld();
        var opts = new SigmaFp.Camera.CameraOptions
        {
            PollInterval = TimeSpan.FromMilliseconds(10), HeartbeatInterval = TimeSpan.FromMilliseconds(50),
            ReconnectBackoff = [TimeSpan.FromMilliseconds(20)], ItemTimeout = TimeSpan.FromSeconds(2), ApiTimeout = TimeSpan.FromSeconds(5),
        };
        var log = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        using var ctl = new SigmaFp.Camera.CameraController(
            () => new SigmaFp.Camera.Session.SimulatedSession(world, new() { TimeScale = 0.01, ImageSize = 4096 }), opts, log);
        var dev = new CameraDevice(ctl, new DriverSettings(), log, SimulatedDecoder.Decode);
        dev.SetConnected(true, 1);
        dev.StartExposure(10, true);
        Thread.Sleep(30);
        world.Present = false;                       // USB pulled mid-exposure
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!dev.ImageReady && sw.Elapsed.TotalSeconds < 5) Thread.Sleep(10);
        Assert.True(dev.ImageReady, "client must see the lost frame as 'ready' at once");
        var ex = Assert.Throws<AlpacaException>(() => dev.GetImage());
        Assert.Contains("Exposure lost", ex.Message);
        world.Present = true;
        sw.Restart();
        while (dev.CameraState != ASCOM.Common.DeviceInterfaces.CameraState.Error && sw.Elapsed.TotalSeconds < 5) Thread.Sleep(10);
        while (!ctl.Connected || ctl.State != SigmaFp.Camera.CameraState.Idle) { if (sw.Elapsed.TotalSeconds > 5) break; Thread.Sleep(10); }
        dev.StartExposure(1, true);                  // next exposure clears the lost-frame state
        Assert.False(dev.ImageReady);
    }

    [Fact]
    public void Mass_storage_mode_reports_the_camera_iso_as_gain_and_ignores_gain_changes()
    {
        var world = new SigmaFp.Camera.Session.SimulatedWorld();
        var opts = new SigmaFp.Camera.CameraOptions
        {
            PollInterval = TimeSpan.FromMilliseconds(10), HeartbeatInterval = TimeSpan.FromMilliseconds(50),
            ReconnectBackoff = [TimeSpan.FromMilliseconds(20)], ItemTimeout = TimeSpan.FromSeconds(2), ApiTimeout = TimeSpan.FromSeconds(5),
        };
        var log = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        using var ctl = new SigmaFp.Camera.CameraController(
            () => new SigmaFp.Camera.Session.SimulatedSession(world, new() { TimeScale = 0.01, ImageSize = 4096 }), opts, log);
        var settings = new DriverSettings { ConnectionMode = "MassStorage", GainMode = "Iso" };
        SigmaFp.Imaging.RawImage Decode(byte[] b)
        {
            var i = SimulatedDecoder.Decode(b); // the camera was set to ISO 800 by hand
            return new SigmaFp.Imaging.RawImage { Width = i.Width, Height = i.Height, Pixels = i.Pixels, BitsPerSample = i.BitsPerSample,
                WhiteLevel = i.WhiteLevel, BlackLevel = i.BlackLevel, CfaPattern = i.CfaPattern, Iso = 800 };
        }
        var dev = new CameraDevice(ctl, settings, log, Decode, Path.Combine(Path.GetTempPath(), $"sfp-{Guid.NewGuid():N}.json"));
        dev.SetConnected(true, 1);
        dev.Gain = 1600;                             // NINA's sequence gain: ignored in this mode
        Assert.Equal(400, dev.Gain);                 // default until the first image
        Assert.Throws<AlpacaException>(() => dev.Gain = 1);
        dev.StartExposure(1, true);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!dev.ImageReady && sw.Elapsed.TotalSeconds < 5) Thread.Sleep(10);
        Assert.True(dev.ImageReady);
        Assert.Equal(800, dev.Gain);                 // the ISO the camera really used
    }

    [Fact]
    public void Camera_model_lookup()
    {
        Assert.Same(CameraModels.FpL, CameraModels.Lookup("SIGMA fp L"));
        Assert.Same(CameraModels.Fp, CameraModels.Lookup("SIGMA fp"));
        Assert.Same(CameraModels.FpL, CameraModels.Lookup("SIGMA fp L (simulated)"));
        Assert.Equal((6072, 4056), CameraModels.ParseSize("6072x4056"));
        Assert.Null(CameraModels.ParseSize("junk"));
        var s = new DriverSettings();
        Assert.Equal(400, s.DefaultIsoFor(CameraModels.FpL));
        Assert.Equal(1250, s.DefaultIsoFor(CameraModels.Fp));
        s.DefaultIsoByModel["SIGMA fp"] = 800;
        Assert.Equal(800, s.DefaultIsoFor(CameraModels.Fp));
    }

    [Fact]
    public async Task Setup_page_is_served()
    {
        var html = await _c.GetStringAsync("/setup/v1/camera/0/setup");
        Assert.Contains("Camera bodies", html);
    }
}
