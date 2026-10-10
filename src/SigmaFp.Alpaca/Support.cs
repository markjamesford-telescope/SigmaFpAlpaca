using System.Net;
using ASCOM.Alpaca.Discovery;
using SigmaFp.Imaging;

namespace SigmaFp.Alpaca;

/// <summary>Alpaca UDP discovery (port 32227) via the official ASCOM responder.</summary>
public static class DiscoveryHost
{
    sealed class AscomLog(ILogger log) : ASCOM.Common.Interfaces.ILogger
    {
        public ASCOM.Common.Interfaces.LogLevel LoggingLevel { get; private set; } = ASCOM.Common.Interfaces.LogLevel.Information;
        public void SetMinimumLoggingLevel(ASCOM.Common.Interfaces.LogLevel level) => LoggingLevel = level;
        public void Log(ASCOM.Common.Interfaces.LogLevel level, string message)
        {
            if (level < LoggingLevel) return;
            log.Log(level switch
            {
                ASCOM.Common.Interfaces.LogLevel.Error or ASCOM.Common.Interfaces.LogLevel.Fatal => LogLevel.Error,
                ASCOM.Common.Interfaces.LogLevel.Warning => LogLevel.Warning,
                ASCOM.Common.Interfaces.LogLevel.Information => LogLevel.Information,
                _ => LogLevel.Debug,
            }, "Discovery: {Msg}", message);
        }
    }

    public static IDisposable? Start(DriverSettings s, ILogger log)
    {
        try
        {
            // Local-only setups still answer discovery from this PC (NINA's Alpaca discovery).
            return new Responder(s.Port, true, false, new AscomLog(log))
            {
                AllowRemoteAccess = s.AllowRemote,
                LocalRespondOnlyToLocalHost = !s.AllowRemote,
            };
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Alpaca discovery not available (port 32227 in use?); connect NINA manually to port {Port}", s.Port);
            return null;
        }
    }
}

/// <summary>Minimal browser setup page: Alpaca /setup and /setup/v1/camera/0/setup.</summary>
public static class SetupPage
{
    /// <param name="restart">Restarts the driver (as a service: exit; the service manager starts it again within 5 s). Null = not possible.</param>
    public static void Map(WebApplication app, DriverSettings s, string settingsPath, CameraDevice cam, Action? restart = null)
    {
        foreach (var path in new[] { "/setup", "/setup/v1/camera/0/setup" })
        {
            app.MapGet(path, () => Results.Content(Html(s, null, cam.Serial, cam.Problem), "text/html"));
            app.MapPost(path, async (HttpRequest r) =>
            {
                var f = await r.ReadFormAsync();
                foreach (var serial in s.Bodies.Keys.ToList())
                    s.Bodies[serial] = f[$"body_{serial}"] == "Monochrome" ? "Monochrome" : "Colour";
                s.SaveDng = f["savedng"] == "on";
                s.AllowRemote = f["remote"] == "on";
                foreach (var m in CameraModels.All)
                    if (int.TryParse(f[$"iso_{m.Name}"], out var iso) && CameraDevice.IsoList.Contains(iso))
                    {
                        s.DefaultIsoByModel[m.Name] = iso;
                        if (m == CameraModels.FpL) s.DefaultIso = iso;
                    }
                s.GainMode = f["gainmode"] == "Iso" ? "Iso" : "List";
                string before = $"{s.ConnectionMode}|{s.RelayPort}|{s.YkushPath}|{s.DeleteFromCard}";
                s.ConnectionMode = f["mode"] == "MassStorage" ? "MassStorage" : "Usb";
                s.RelayPort = string.IsNullOrWhiteSpace(f["relay"]) ? null : SigmaFp.Camera.Bulb.LcusRelayTrigger.NormalizePortName(f["relay"].ToString());
                s.YkushPath = string.IsNullOrWhiteSpace(f["ykush"]) ? null : f["ykush"].ToString().Trim().Trim('"');
                s.DeleteFromCard = f["delcard"] == "on";
                bool modeChanged = before != $"{s.ConnectionMode}|{s.RelayPort}|{s.YkushPath}|{s.DeleteFromCard}";
                s.FailLostFramesFast = f["failfast"] == "on";
                s.QueueStartWhileBusy = f["queue"] == "on";
                if (int.TryParse(f["port"], out var port) && port is > 1024 and < 65536) s.Port = port;
                if (!string.IsNullOrWhiteSpace(f["dngfolder"])) s.DngFolder = f["dngfolder"].ToString().Trim();
                s.Save(settingsPath);
                if (modeChanged && restart is not null && !cam.Connected)
                {
                    restart();
                    return Results.Content(Html(s, "Saved. The driver restarts now for the new connection mode; reload this page in about 10 s, then connect in NINA.", cam.Serial, null), "text/html");
                }
                string note = modeChanged
                    ? (cam.Connected ? "Saved. Disconnect the camera in NINA and save again to restart the driver in the new mode."
                                     : "Saved. Restart the driver to use the new connection mode.")
                    : "Saved. Body type applies from the next image; restart the driver for port and network changes.";
                return Results.Content(Html(s, note, cam.Serial, cam.Problem), "text/html");
            });
        }
    }

    static string Html(DriverSettings s, string? note, string? connectedSerial, string? problem)
    {
        string E(string? v) => WebUtility.HtmlEncode(v ?? "");
        string Chk(bool b) => b ? "checked" : "";
        string IsoSelect(CameraModels.Model m) =>
            $"<label>Default ISO, {E(m.Name)} <select name='iso_{E(m.Name)}'>" +
            string.Concat(CameraDevice.IsoList.Select(i => $"<option {(i == s.DefaultIsoFor(m) ? "selected" : "")}>{i}</option>")) + "</select></label>";
        string isoSelects = string.Concat(CameraModels.All.Select(IsoSelect));
        string BodyRow(KeyValuePair<string, string> b)
        {
            bool mono = b.Value == "Monochrome";
            string connected = (b.Key == connectedSerial ? " (connected)" : "") + (s.BodySizes.TryGetValue(b.Key, out var sz) ? $", raw {sz}" : "");
            return $"<label>Body serial <b>{E(b.Key)}</b>{connected}: <select name='body_{E(b.Key)}'>"
                 + $"<option {(mono ? "" : "selected")}>Colour</option><option {(mono ? "selected" : "")}>Monochrome</option></select></label>";
        }
        string bodies = s.Bodies.Count == 0
            ? "<p>No camera body seen yet: connect once, then set its type here.</p>"
            : string.Concat(s.Bodies.Select(BodyRow));
        return $$"""
        <!doctype html><html><head><meta charset="utf-8"><title>Sigma fp L driver setup</title>
        <style>body{font-family:Segoe UI,sans-serif;max-width:640px;margin:2em auto}label{display:block;margin:.6em 0}input[type=text]{width:22em}.n{color:#060}.p{color:#b00;font-weight:600}</style></head><body>
        <h1>Sigma fp / fp L Alpaca driver</h1>
        {{(problem is null ? "" : $"<p class=p>Camera problem: {E(problem)}</p>")}}
        {{(note is null ? "" : $"<p class=n>{E(note)}</p>")}}
        <form method="post">
          <fieldset><legend>Camera bodies (Monochrome = Bayer mask removed: no debayering)</legend>{{bodies}}</fieldset>
          {{isoSelects}}
          <label>Gain in NINA <select name="gainmode">
            <option value="List" {{(s.IsoGainMode ? "" : "selected")}}>List: 0-28, Gain 10 = ISO 400 (old-driver compatible)</option>
            <option value="Iso" {{(s.IsoGainMode ? "selected" : "")}}>ISO: Gain = ISO value (e.g. 400); update NINA sequences</option>
          </select> (reconnect NINA after changing)</label>
          <fieldset><legend>Connection mode</legend>
            <label><input type="radio" name="mode" value="Usb" {{(s.MassStorageMode ? "" : "checked")}}> <b>USB control</b> (camera USB mode Camera Control): 1/8000-30 s and 300 s, ISO from NINA</label>
            <label><input type="radio" name="mode" value="MassStorage" {{(s.MassStorageMode ? "checked" : "")}}> <b>Mass Storage + relay</b> (camera USB mode Mass Storage, set M / Bulb / ISO / DNG on the camera):
              any length 1-{{s.MaxBulbSeconds:0}} s timed by the relay; USB switched by a Yepkit YKUSH XS; ISO from the camera (NINA's gain has no effect)</label>
            <label>Relay COM port (LCUS-1) <input type="text" name="relay" value="{{E(s.RelayPort)}}"></label>
            <label>ykushcmd.exe (empty = on the PATH) <input type="text" name="ykush" value="{{E(s.YkushPath)}}"></label>
            <label><input type="checkbox" name="delcard" {{Chk(s.DeleteFromCard)}}> Delete each image from the card after reading it</label>
          </fieldset>
          <label><input type="checkbox" name="failfast" {{Chk(s.FailLostFramesFast)}}> Report a lost frame at once (ImageReady + download error; NINA moves on instead of waiting exposure + 400 s)</label>
          <label><input type="checkbox" name="queue" {{Chk(s.QueueStartWhileBusy)}}> Queue a new exposure while the camera finishes an un-abortable one (instead of "busy")</label>
          <label><input type="checkbox" name="savedng" {{Chk(s.SaveDng)}}> Also save the camera DNG</label>
          <label>DNG folder <input type="text" name="dngfolder" value="{{E(s.DngFolder)}}"></label>
          <label>HTTP port <input type="text" name="port" value="{{s.Port}}"></label>
          <label><input type="checkbox" name="remote" {{Chk(s.AllowRemote)}}> Allow access from other computers</label>
          <button type="submit">Save</button>
        </form>
        <p>Camera (fp L, or fp with firmware 5.00+) must be in USB mode <b>Camera Control</b>, on a <b>USB 2.0</b> port, with the
        libusb-win32 driver (Zadig) installed for that model. Connect one camera at a time. New bodies appear here after their first connect.
        Logs: {{E(System.IO.Path.Combine(DriverSettings.Folder, "logs"))}}</p>
        <footer style="margin-top:2.5em;border-top:1px solid #ccc;padding-top:.6em;font-size:.85em;color:#555">
          Sigma fp / fp L Alpaca driver {{CameraDevice.DriverVersion}} &middot; &copy; 2026 Mark Ford &middot; MIT licence &middot;
          Developed with <a href="https://claude.com/claude-code">Claude Code</a> (Anthropic) &middot;
          <a href="{{CameraDevice.ProjectUrl}}">project page</a><br>
          Not affiliated with SIGMA Corporation. "SIGMA", "fp" and "fp L" are trademarks of SIGMA Corporation.
        </footer>
        </body></html>
        """;
    }
}

/// <summary>For --simulate: the simulated camera's bytes are not a DNG, so fake a small raw frame.</summary>
public static class SimulatedDecoder
{
    public static RawImage Decode(byte[] _)
    {
        int w = CameraDevice.SensorWidth, h = CameraDevice.SensorHeight;
        var px = new ushort[w * h];
        var rnd = new Random(1);
        Parallel.For(0, h, y =>
        {
            var r = new Random(y);
            for (int x = 0; x < w; x++) px[y * w + x] = (ushort)(1024 + r.Next(0, 40));
        });
        for (int i = 0; i < 300; i++) // a few "stars"
        {
            int sx = rnd.Next(10, w - 10), sy = rnd.Next(10, h - 10);
            for (int dy = -3; dy <= 3; dy++) for (int dx = -3; dx <= 3; dx++)
                px[(sy + dy) * w + sx + dx] = (ushort)Math.Min(16383, px[(sy + dy) * w + sx + dx] + 12000 * Math.Exp(-(dx * dx + dy * dy) / 3.0));
        }
        return new RawImage { Width = w, Height = h, Pixels = px, BitsPerSample = 14, WhiteLevel = 16383, BlackLevel = 1024, CfaPattern = [0, 1, 1, 2], Make = "SIGMA", Model = "SIGMA fp L (simulated)" };
    }
}
