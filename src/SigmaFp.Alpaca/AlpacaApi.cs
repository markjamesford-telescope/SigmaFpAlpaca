using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using ASCOM.Common.Alpaca;

namespace SigmaFp.Alpaca;

/// <summary>Alpaca REST API (camera device 0 + management), per the ASCOM Alpaca API v1 spec.</summary>
public static class AlpacaApi
{
    public static readonly Guid UniqueId = new("6a0c3f2e-6f1d-4f56-9a51-5f1a9b1e2c01");
    static int _serverTx;
    static uint NextServerTx() => (uint)Interlocked.Increment(ref _serverTx);

    static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = null, DefaultIgnoreCondition = JsonIgnoreCondition.Never };

    /// <summary>
    /// Request parameters. Alpaca spec: GET query parameter names are case-insensitive; PUT form
    /// parameter names are case-sensitive (wrong casing = missing = HTTP 400).
    /// </summary>
    sealed class Params(Dictionary<string, string> values)
    {
        public static async Task<Params> From(HttpRequest r)
        {
            bool put = HttpMethods.IsPut(r.Method);
            var d = new Dictionary<string, string>(put ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);
            if (put)
            {
                if (r.HasFormContentType)
                    foreach (var kv in await r.ReadFormAsync()) d[kv.Key] = kv.Value.ToString();
            }
            else
            {
                foreach (var kv in r.Query) d[kv.Key] = kv.Value.ToString();
            }
            return new Params(d);
        }

        public uint ClientTx => values.TryGetValue("ClientTransactionID", out var v) && uint.TryParse(v, out var n) ? n : 0;
        public uint ClientId => values.TryGetValue("ClientID", out var v) && uint.TryParse(v, out var n) ? n : 0;

        public string Required(string name) =>
            values.TryGetValue(name, out var v) ? v : throw new BadRequestException($"Missing parameter '{name}'");

        public bool Bool(string name) => Required(name).ToLowerInvariant() switch
        {
            "true" => true, "false" => false,
            var v => throw new BadRequestException($"'{name}' must be True or False, not '{v}'"),
        };

        public int Int(string name) =>
            int.TryParse(Required(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : throw new BadRequestException($"'{name}' must be an integer");

        public double Double(string name) =>
            double.TryParse(Required(name), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : throw new BadRequestException($"'{name}' must be a number");
    }

    sealed class BadRequestException(string message) : Exception(message);

    public static void Map(WebApplication app, CameraDevice cam, DriverSettings settings, ILogger log)
    {
        // ---------------- management
        app.MapGet("/management/apiversions", (HttpRequest r) => Envelope(r, new[] { 1 }));
        app.MapGet("/management/v1/description", (HttpRequest r) => Envelope(r, new
        {
            ServerName = "Sigma fp / fp L Alpaca driver",
            Manufacturer = "SigmaFpAlpaca (open source)",
            ManufacturerVersion = CameraDevice.DriverVersion,
            Location = Environment.MachineName,
        }));
        app.MapGet("/management/v1/configureddevices", (HttpRequest r) => Envelope(r, new[]
        {
            new { DeviceName = cam.Name, DeviceType = "Camera", DeviceNumber = 0, UniqueID = UniqueId.ToString() },
        }));

        // ---------------- camera
        void Get(string name, Func<Params, object?> getter) =>
            app.MapGet($"/api/v1/camera/{{n:int}}/{name}", (HttpContext c, int n) => Handle(c, n, log, p => getter(p), hasValue: true));
        void Put(string name, Action<Params> action) =>
            app.MapPut($"/api/v1/camera/{{n:int}}/{name}", (HttpContext c, int n) => Handle(c, n, log, p => { action(p); return null; }, hasValue: false));

        // common members
        Get("connected", p => cam.IsConnected(p.ClientId));
        Put("connected", p => cam.SetConnected(p.Bool("Connected"), p.ClientId));
        Get("connecting", _ => cam.Connecting);
        Put("connect", p => cam.Connect(p.ClientId));
        Put("disconnect", p => cam.Disconnect(p.ClientId));
        Get("description", _ => cam.Description);
        Get("driverinfo", _ => cam.DriverInfo);
        Get("driverversion", _ => CameraDevice.DriverVersion);
        Get("interfaceversion", _ => CameraDevice.InterfaceVersion);
        Get("name", _ => cam.Name);
        Get("supportedactions", _ => Array.Empty<string>());
        Get("devicestate", _ => cam.DeviceState().Select(s => new { Name = s.Name, Value = s.Value }).ToArray());
        Put("action", p => throw new AlpacaException(AlpacaErrors.ActionNotImplementedException, $"Action '{p.Required("Action")}' is not supported"));
        foreach (var c in new[] { "commandblind", "commandbool", "commandstring" })
            Put(c, _ => throw AlpacaException.NotImplemented(c));

        // camera members (properties that need a connection call RequireConnected inside CameraDevice where ASCOM demands it)
        Get("bayeroffsetx", _ => Connected(cam, () => cam.BayerOffsetX));
        Get("bayeroffsety", _ => Connected(cam, () => cam.BayerOffsetY));
        Get("binx", _ => Connected(cam, () => cam.BinX));
        Put("binx", p => Connected(cam, () => cam.BinX = (short)p.Int("BinX")));
        Get("biny", _ => Connected(cam, () => cam.BinY));
        Put("biny", p => Connected(cam, () => cam.BinY = (short)p.Int("BinY")));
        Get("camerastate", _ => (int)cam.CameraState);
        Get("cameraxsize", _ => Connected(cam, () => cam.CameraXSize));
        Get("cameraysize", _ => Connected(cam, () => cam.CameraYSize));
        Get("canabortexposure", _ => Connected(cam, () => cam.CanAbortExposure));
        Get("canasymmetricbin", _ => Connected(cam, () => cam.CanAsymmetricBin));
        Get("canfastreadout", _ => Connected(cam, () => cam.CanFastReadout));
        Get("cangetcoolerpower", _ => Connected(cam, () => cam.CanGetCoolerPower));
        Get("canpulseguide", _ => Connected(cam, () => cam.CanPulseGuide));
        Get("cansetccdtemperature", _ => Connected(cam, () => cam.CanSetCCDTemperature));
        Get("canstopexposure", _ => Connected(cam, () => cam.CanStopExposure));
        Get("ccdtemperature", _ => throw AlpacaException.NotImplemented("CCDTemperature"));
        Get("cooleron", _ => throw AlpacaException.NotImplemented("CoolerOn"));
        Put("cooleron", _ => throw AlpacaException.NotImplemented("CoolerOn"));
        Get("coolerpower", _ => throw AlpacaException.NotImplemented("CoolerPower"));
        Get("electronsperadu", _ => throw AlpacaException.NotImplemented("ElectronsPerADU"));
        Get("exposuremax", _ => Connected(cam, () => cam.ExposureMax));
        Get("exposuremin", _ => Connected(cam, () => cam.ExposureMin));
        Get("exposureresolution", _ => Connected(cam, () => cam.ExposureResolution));
        Get("fastreadout", _ => throw AlpacaException.NotImplemented("FastReadout"));
        Put("fastreadout", _ => throw AlpacaException.NotImplemented("FastReadout"));
        Get("fullwellcapacity", _ => throw AlpacaException.NotImplemented("FullWellCapacity"));
        Get("gain", _ => Connected(cam, () => cam.Gain));
        Put("gain", p => Connected(cam, () => cam.Gain = (short)p.Int("Gain")));
        Get("gainmax", _ => Connected(cam, () => cam.GainMax));
        Get("gainmin", _ => Connected(cam, () => cam.GainMin));
        Get("gains", _ => Connected(cam, () => cam.GainsForMode));
        Get("hasshutter", _ => Connected(cam, () => cam.HasShutter));
        Get("heatsinktemperature", _ => throw AlpacaException.NotImplemented("HeatSinkTemperature"));
        Get("imageready", _ => cam.ImageReady);
        Get("ispulseguiding", _ => throw AlpacaException.NotImplemented("IsPulseGuiding"));
        Get("lastexposureduration", _ => Connected(cam, () => cam.LastExposureDuration));
        Get("lastexposurestarttime", _ => Connected(cam, () => cam.LastExposureStartTime));
        Get("maxadu", _ => Connected(cam, () => cam.MaxADU));
        Get("maxbinx", _ => Connected(cam, () => cam.MaxBinX));
        Get("maxbiny", _ => Connected(cam, () => cam.MaxBinY));
        Get("numx", _ => Connected(cam, () => cam.NumX));
        Put("numx", p => Connected(cam, () => cam.NumX = p.Int("NumX")));
        Get("numy", _ => Connected(cam, () => cam.NumY));
        Put("numy", p => Connected(cam, () => cam.NumY = p.Int("NumY")));
        Get("offset", _ => throw AlpacaException.NotImplemented("Offset"));
        Put("offset", _ => throw AlpacaException.NotImplemented("Offset"));
        Get("offsetmax", _ => throw AlpacaException.NotImplemented("OffsetMax"));
        Get("offsetmin", _ => throw AlpacaException.NotImplemented("OffsetMin"));
        Get("offsets", _ => throw AlpacaException.NotImplemented("Offsets"));
        Get("percentcompleted", _ => cam.PercentCompleted);
        Get("pixelsizex", _ => Connected(cam, () => cam.PixelSizeX));
        Get("pixelsizey", _ => Connected(cam, () => cam.PixelSizeY));
        Get("readoutmode", _ => Connected(cam, () => cam.ReadoutMode));
        Put("readoutmode", p => Connected(cam, () => cam.ReadoutMode = (short)p.Int("ReadoutMode")));
        Get("readoutmodes", _ => Connected(cam, () => cam.ReadoutModes));
        Get("sensorname", _ => Connected(cam, () => cam.SensorName));
        Get("sensortype", _ => Connected(cam, () => (int)cam.SensorType));
        Get("setccdtemperature", _ => throw AlpacaException.NotImplemented("SetCCDTemperature"));
        Put("setccdtemperature", _ => throw AlpacaException.NotImplemented("SetCCDTemperature"));
        Get("startx", _ => Connected(cam, () => cam.StartX));
        Put("startx", p => Connected(cam, () => cam.StartX = p.Int("StartX")));
        Get("starty", _ => Connected(cam, () => cam.StartY));
        Put("starty", p => Connected(cam, () => cam.StartY = p.Int("StartY")));
        Get("subexposureduration", _ => throw AlpacaException.NotImplemented("SubExposureDuration"));
        Put("subexposureduration", _ => throw AlpacaException.NotImplemented("SubExposureDuration"));
        Put("startexposure", p => cam.StartExposure(p.Double("Duration"), p.Bool("Light")));
        Put("stopexposure", _ => cam.StopExposure());
        Put("abortexposure", _ => cam.AbortExposure());
        Put("pulseguide", _ => throw AlpacaException.NotImplemented("PulseGuide"));

        foreach (var name in new[] { "imagearray", "imagearrayvariant" })
            app.MapGet($"/api/v1/camera/{{n:int}}/{name}", (HttpContext c, int n) => ImageArray(c, n, cam, log));
    }

    static T Connected<T>(CameraDevice cam, Func<T> f) => cam.Connected ? f() : throw AlpacaException.NotConnected();
    static void Connected(CameraDevice cam, Action f) { if (!cam.Connected) throw AlpacaException.NotConnected(); f(); }

    static async Task Handle(HttpContext c, int device, ILogger log, Func<Params, object?> op, bool hasValue)
    {
        Params p;
        try { p = await Params.From(c.Request); }
        catch (Exception ex) { await BadRequest(c, ex.Message); return; }
        if (device != 0) { await BadRequest(c, $"Device number {device} does not exist (only 0)"); return; }
        if (!IsLowerCasePath(c.Request.Path)) { await BadRequest(c, "Alpaca device type and method names must be lower case"); return; }

        uint stx = NextServerTx();
        int err = 0; string msg = ""; object? value = null;
        try { value = op(p); }
        catch (BadRequestException ex) { await BadRequest(c, ex.Message); return; }
        catch (AlpacaException ex) { err = (int)ex.Code; msg = ex.Message; }
        catch (Exception ex)
        {
            err = (int)AlpacaErrors.UnspecifiedError; msg = ex.Message;
            log.LogError(ex, "{Method} {Path} failed", c.Request.Method, c.Request.Path);
        }
        if (err != 0) log.LogDebug("{Method} {Path} -> error {Err}: {Msg}", c.Request.Method, c.Request.Path, err, msg);

        var payload = new Dictionary<string, object?>();
        if (hasValue && err == 0) payload["Value"] = value;
        payload["ClientTransactionID"] = p.ClientTx;
        payload["ServerTransactionID"] = stx;
        payload["ErrorNumber"] = err;
        payload["ErrorMessage"] = msg;
        c.Response.ContentType = "application/json";
        await JsonSerializer.SerializeAsync(c.Response.Body, payload, Json);
    }

    static async Task ImageArray(HttpContext c, int device, CameraDevice cam, ILogger log)
    {
        var p = await Params.From(c.Request);
        if (device != 0) { await BadRequest(c, $"Device number {device} does not exist (only 0)"); return; }
        if (!IsLowerCasePath(c.Request.Path)) { await BadRequest(c, "Alpaca device type and method names must be lower case"); return; }
        uint stx = NextServerTx();
        bool wantsBytes = c.Request.Headers.Accept.Any(a => a?.Contains(ImageArrayWriter.ImageBytesMime, StringComparison.OrdinalIgnoreCase) == true);
        (ushort[] px, int w, int h) img;
        try { img = cam.GetImage(); }
        catch (Exception ex)
        {
            var code = ex is AlpacaException ae ? ae.Code : AlpacaErrors.UnspecifiedError;
            if (wantsBytes)
            {
                c.Response.ContentType = ImageArrayWriter.ImageBytesMime;
                await c.Response.Body.WriteAsync(AlpacaTools.ErrorMessageToByteArray(1, p.ClientTx, stx, code, ex.Message));
            }
            else
            {
                c.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(c.Response.Body, new { ClientTransactionID = p.ClientTx, ServerTransactionID = stx, ErrorNumber = (int)code, ErrorMessage = ex.Message }, Json);
            }
            return;
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        if (wantsBytes)
        {
            var bytes = ImageArrayWriter.ToImageBytes(img.px, img.w, img.h, p.ClientTx, stx);
            c.Response.ContentType = ImageArrayWriter.ImageBytesMime;
            c.Response.ContentLength = bytes.Length;
            await c.Response.Body.WriteAsync(bytes);
            log.LogInformation("ImageArray sent as ImageBytes: {W}x{H}, {MB:0.0} MB in {Ms} ms", img.w, img.h, bytes.Length / 1e6, sw.ElapsedMilliseconds);
        }
        else
        {
            c.Response.ContentType = "application/json";
            await ImageArrayWriter.WriteJsonAsync(c.Response.Body, img.px, img.w, img.h, p.ClientTx, stx);
            log.LogInformation("ImageArray sent as JSON: {W}x{H} in {Ms} ms", img.w, img.h, sw.ElapsedMilliseconds);
        }
    }

    /// <summary>/api/v1/{devicetype}/{n}/{method}: device type and method must be lower case (ASP.NET routing is not).</summary>
    static bool IsLowerCasePath(PathString path)
    {
        var parts = (path.Value ?? "").Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 5 && parts[2] == parts[2].ToLowerInvariant() && parts[4] == parts[4].ToLowerInvariant();
    }

    static IResult Envelope(HttpRequest r, object value)
    {
        uint ctx = uint.TryParse(r.Query["ClientTransactionID"], out var n) ? n : 0;
        return Results.Json(new Dictionary<string, object?>
        {
            ["Value"] = value, ["ClientTransactionID"] = ctx, ["ServerTransactionID"] = NextServerTx(), ["ErrorNumber"] = 0, ["ErrorMessage"] = "",
        }, Json);
    }

    static async Task BadRequest(HttpContext c, string message)
    {
        c.Response.StatusCode = 400;
        c.Response.ContentType = "text/plain";
        await c.Response.WriteAsync(message);
    }
}
