# Installation

[← Overview](../index.md)

Takes about 15 minutes. You need administrator rights on the PC once.

## 1. Camera settings (on the camera)
1. **Firmware:** fp L 3.00 or newer; fp 5.00 or newer (Sigma's camera-control support).
2. **Setup menu > USB Mode: Camera Control**, set as the *default*. If the camera asks
   "select connection mode" each time it is plugged in, the PC won't see it as a camera.
3. Battery charged, or a USB-C power source. The camera stays on while in use.

## 2. Use a USB 2.0 port
Plug the camera into a **USB 2.0** port, a USB 2.0 hub, or through a USB 2.0 cable.
**USB 3 (SuperSpeed) ports do not work** with the libusb-win32 driver used here: the camera is
detected, but every transfer fails. USB 2.0 still downloads a 61 MP frame in about 2.5-6 s.

What counts is the **speed the camera connects at**, not just the socket: the fp and fp L have USB 3-capable sockets, so a USB 3 cable (USB-C to USB-C, or USB-A marked SS / 3.x) in a USB 3 socket (blue inside, or marked SS) connects at SuperSpeed and fails. Any of these gives a working USB 2.0 connection:
- a socket without SS markings (black inside);
- a **USB 2.0 hub** in between (recommended on the mount anyway);
- a USB 2.0-only cable (no SS / 3.x markings).

The symptom: Windows shows the camera under *libusb-win32 devices*, but connecting fails with `-5 ... could not clear halt`. It can also seem to depend on the camera: a body that worked yesterday fails today in a different socket or with a different cable.

**Fix the cable in place at the camera.** The fp / fp L USB-C socket does not hold the plug firmly:
if the camera or cable moves (slews, meridian flips, a cable dragging on the mount), the connection
drops for a moment. The driver reconnects by itself, but the frame in progress is lost.
- Clamp or tie the cable to the camera cage, the L-bracket or the dovetail, close to the plug, so
  the plug cannot flex and the cable's weight does not hang on it. A short right-angle USB-C lead
  helps.
- Leave some slack between the camera and the mount for slews.
- Use a good cable of **4 m or less**: 5 m is the USB 2.0 limit and proved unreliable in testing; a powered
  USB 2.0 hub on the mount is better than a long cable.
- A cable that has been pulled or bent a lot can start failing under load (downloads stall after the
  first 8 MB). If that happens, replace it ([Troubleshooting](Troubleshooting.md)).

In testing, a clamped cable ran all night (274 frames) without a single drop. The same setup with
the camera loose on the bench dropped whenever the camera was moved.

## 3. Install the libusb-win32 driver for the camera (Zadig), once per camera model
Windows binds the camera to its own MTP driver, which does **not** work for camera control (the
camera keeps disconnecting). Replace it with libusb-win32:

1. Download **Zadig** from <https://zadig.akeo.ie/> and run it (accept the admin prompt).
2. Connect the camera (on, USB mode Camera Control).
3. Zadig: **Options > List All Devices** (tick it).
4. Pick the camera in the drop-down and check the **USB ID**:
   | Camera | USB ID |
   |---|---|
   | SIGMA fp L | `1003` `C442` |
   | SIGMA fp | `1003` `C432` |
5. Set the target driver (right of the green arrow) to **libusb-win32**.
6. Click **Replace Driver** (or *Install Driver*) and wait for "installed successfully".
7. Unplug and replug the camera.

Check: in Device Manager the camera now appears under **libusb-win32 devices**.

> If you used the older ASCOM.DSLR-based fp L driver, this was already done for the fp L. A
> second fp of the same model needs no further Zadig step.

**Undo (back to Windows' MTP driver):** Device Manager > the camera > Uninstall device > tick
"Delete the driver software for this device" > replug.

## 4. Install the driver (Windows service)
1. Download the release zip from the project's **Releases** page and extract it, for example
   to `C:\Temp\SigmaFpAlpaca`.
2. Open **Terminal (Admin)** (Win+X) in that folder and run:
   ```
   powershell -ExecutionPolicy Bypass -File .\Install-SigmaFpAlpaca.ps1
   ```
   Add `-AllowRemote` if NINA runs on a different computer.
3. It installs to `C:\Program Files\SigmaFpAlpaca` and registers the service
   **"Sigma fp L Alpaca driver"**, which starts with Windows, needs no login and restarts itself
   after a failure. It also puts a **"Sigma fp L driver setup"** shortcut on the desktop.
4. It ends with a green **Installed. Service 'SigmaFpAlpaca' is running.**

Settings and logs: `C:\ProgramData\SigmaFpAlpaca` (`settings.json`, `logs\driver-YYYYMMDD.log`).

**Update:** run the installer of the new release the same way (settings are kept).
**Uninstall:** `powershell -ExecutionPolicy Bypass -File .\Uninstall-SigmaFpAlpaca.ps1`
(add `-RemoveData` to delete settings and logs too).

## 5. First connection
1. Open the setup page (desktop shortcut, or <http://127.0.0.1:11111/setup>).
2. Connect from NINA or SharpCap (see [Using with NINA and SharpCap](Using-with-NINA-and-SharpCap.md)).
3. On first connect the body is registered by serial number as **Colour**. For a
   **mono-converted** body, set it to **Monochrome** on the setup page.
4. Take one frame: the driver learns the body's exact raw size. Reconnect once in the client.

Only **one camera at a time** should be connected to the PC.

## 6. Optional: Mass Storage mode for 60-300 s exposures
Under USB control the camera offers 1/8000-30 s and a fixed 300 s bulb. For any length from 1 s to 300 s
(60, 120, 180 s ...) you need a Yepkit **YKUSH XS** (switchable USB port; install its `ykushcmd` tool, no driver
needed) and an **LCUS-1** relay on the camera's remote jack. Hardware, wiring, camera and driver settings, and the
start/stop sequence: [Mass Storage mode](Mass-Storage-mode.md).

