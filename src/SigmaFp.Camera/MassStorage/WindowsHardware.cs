using System.Diagnostics;
using System.Management;
using Microsoft.Extensions.Logging;

namespace SigmaFp.Camera.MassStorage;

/// <summary>Yepkit YKUSH XS via its command-line tool: <c>ykushcmd ykushxs -u</c> (on) / <c>-d</c> (off).</summary>
public sealed class YkushXsSwitch(string exePath, ILogger log) : IPortSwitch
{
    public void SetPower(bool on)
    {
        string args = $"ykushxs {(on ? "-u" : "-d")}";
        var psi = new ProcessStartInfo(string.IsNullOrWhiteSpace(exePath) ? "ykushcmd" : exePath, args)
        { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        Process p;
        try { p = Process.Start(psi) ?? throw new CameraCommException($"Could not start {psi.FileName}"); }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new CameraCommException($"Could not start the YKUSH tool '{psi.FileName}': {ex.Message} (set its path on the setup page)", ex);
        }
        using (p)
        {
            var output = p.StandardOutput.ReadToEndAsync();
            var error = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(10_000))
            {
                try { p.Kill(); } catch { /* ignore */ }
                throw new CameraCommException($"'{psi.FileName} {args}' did not finish within 10 s");
            }
            string text = (output.Result + " " + error.Result).Trim();
            if (p.ExitCode != 0)
                throw new CameraCommException($"'{psi.FileName} {args}' failed (exit {p.ExitCode}): {text}");
            log.LogDebug("{Cmd} -> {Out}", args, text);
        }
    }
}

/// <summary>Finds the camera's card: a USB disk from vendor SIGMA with a DCIM folder.</summary>
public sealed class WindowsCardLocator : ICardLocator
{
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public CardInfo? Find()
    {
        try
        {
            using var disks = new ManagementObjectSearcher("SELECT DeviceID, Model, PNPDeviceID FROM Win32_DiskDrive WHERE InterfaceType='USB'");
            foreach (ManagementObject disk in disks.Get())
            {
                using (disk)
                {
                    string pnp = disk["PNPDeviceID"] as string ?? "";
                    if (!pnp.Contains("VEN_SIGMA", StringComparison.OrdinalIgnoreCase)) continue;
                    foreach (ManagementObject part in disk.GetRelated("Win32_DiskPartition"))
                        foreach (ManagementObject ld in part.GetRelated("Win32_LogicalDisk"))
                        {
                            string root = (ld["DeviceID"] as string ?? "") + "\\";
                            if (Directory.Exists(Path.Combine(root, "DCIM")))
                                return new CardInfo(root, ModelName(disk["Model"] as string ?? ""), SerialFrom(pnp));
                        }
                }
            }
        }
        catch (ManagementException) { /* WMI hiccup while the device (dis)appears: try again next poll */ }
        catch (IOException) { }
        return null;
    }

    /// <summary>"SIGMA SIGMA fp L USB Device" -> "SIGMA fp L".</summary>
    public static string ModelName(string wmiModel)
    {
        string m = wmiModel.Replace(" USB Device", "", StringComparison.OrdinalIgnoreCase).Trim();
        if (m.StartsWith("SIGMA SIGMA ", StringComparison.OrdinalIgnoreCase)) m = m[6..];
        return m;
    }

    /// <summary>"USBSTOR\DISK&amp;VEN_SIGMA&amp;PROD_SIGMA_FP_L&amp;REV_1.00\0000000012345678&amp;0" -> "12345678" (as over PTP).</summary>
    public static string SerialFrom(string pnpDeviceId)
    {
        string last = pnpDeviceId[(pnpDeviceId.LastIndexOf('\\') + 1)..];
        int amp = last.IndexOf('&');
        if (amp >= 0) last = last[..amp];
        string trimmed = last.TrimStart('0');
        return trimmed.Length > 0 ? trimmed : last;
    }
}
