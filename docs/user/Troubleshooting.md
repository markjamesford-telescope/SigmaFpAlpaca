# Troubleshooting

[← Overview](../index.md)

Start with the log: `C:\ProgramData\SigmaFpAlpaca\logs\driver-YYYYMMDD.log`. It records every
connection, state change, exposure and error with timestamps.

| Symptom | Cause / fix |
|---|---|
| NINA/SharpCap doesn't list the camera | Is the service running (`services.msc` > "Sigma fp L Alpaca driver")? Does <http://127.0.0.1:11111/setup> open? Otherwise add the device manually: host 127.0.0.1, port 11111, camera 0 |
| "No libusb0 device with VID 1003" | Camera off or asleep; USB mode not **Camera Control** (or set to "select on connect"); or libusb-win32 not installed for this model (Zadig, see [Installation](Installation.md)) |
| Camera listed but every transfer fails (-5, "device not functioning") | The camera connected at **USB 3 / SuperSpeed** (USB 3 cable in a USB 3 socket). Use a socket without SS markings, a USB 2.0 hub or a USB 2.0-only cable ([Installation](Installation.md#2-use-a-usb-20-port)) |
| Camera keeps disconnecting every few seconds | Bound to Windows' MTP driver instead of libusb-win32: redo the Zadig step |
| "Image generation failed (0x6004)" on every shot; the camera screen shows an odd mode | The camera was left mid-capture (e.g. a hard USB pull during a bulb). Switch the camera off and on |
| Downloads stall (the log shows the first 8 MB, then a 30 s timeout) or the camera drops off USB now and then | A marginal USB cable or plug. The driver recovers every time, but frames are lost. Try a short cable you trust, plug straight into the PC (no hub), and clamp the cable so the camera's USB-C plug cannot flex. A cable can work for weeks and then fail after being pulled or bent. |
| Calibrated stacks show gradients, leftover dust or vignetting | Lights and calibration frames in different formats (FITS from NINA vs. DNG from the camera). Use one format for all ([details](Using-with-NINA-and-SharpCap.md#nina-3x)) |
| 60 s / 120 s / 180 s refused | Under USB control only <= 30 s or exactly 300 s are possible. Use [Mass Storage mode](Mass-Storage-mode.md) for any length up to 300 s |
| ISO not what you set | NINA's profile "Default gain" overrides the driver's default ISO on connect |
| "Camera is busy" | The camera is still finishing a 300 s bulb (it cannot be aborted). New exposures are queued automatically |
| Another program can't connect | Only one program can use the camera's USB at a time (the old ASCOM.DSLR driver, Sigma software, ...). Several Alpaca clients can share *this* driver |
| Wrong image size after switching bodies | Reconnect in the client after the new body's first image |
| Setup page changes don't apply | Port and network changes need a service restart; body type and gain mode apply to the next image or connection |

## Collecting information for a bug report
1. The driver log for the time of the problem (`C:\ProgramData\SigmaFpAlpaca\logs`).
2. Camera model and firmware, the USB port type, and the client (NINA/SharpCap version).
3. If NINA was involved, the NINA log (`%LOCALAPPDATA%\NINA\Logs`).
