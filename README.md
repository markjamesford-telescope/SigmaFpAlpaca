# Sigma fp / fp L ASCOM Alpaca camera driver

A reliable, open-source **ASCOM Alpaca** camera driver for the **SIGMA fp L** and **SIGMA fp**
(stock colour or mono-converted), built for unattended astrophotography with **NINA**, SharpCap
and other Alpaca clients.

It replaces the earlier community driver, which froze when the camera's USB connection reset
during an exposure.

## Highlights
- **Direct camera control** (Sigma's PTP vendor protocol over libusb), with no helper program per frame.
- **Never hangs the client:** one camera thread, a timeout on every USB transfer, and a watchdog.
- **Self-healing:** a lost USB connection is reported within seconds and **re-established
  automatically**, and the sequence continues. No NINA restart.
- **Fast:** a 10 s frame is delivered about 16 s after start; a 61 MP image reaches NINA in about 0.15 s.
- **Exact data:** in-process DNG decoder, bit-for-bit identical to LibRaw, no processing.
- **Mono and colour bodies**, recognised by serial number; mono bodies are never debayered.
- **Exposures** from 1/8000 s to 30 s plus the camera's 300 s bulb under USB control, or **any length from 1 s to 300 s** (60, 120, 180 s ...) in [Mass Storage mode](docs/user/Mass-Storage-mode.md) with a Yepkit YKUSH XS switchable USB port and a USB relay (both needed; together **< 100 EUR**).
- Passes the ASCOM **ConformU** conformance checker with **no errors, warnings or issues** (USB control mode).
- Runs as a **Windows service**: starts with Windows, restarts itself, no window to keep open.
- Bonus tool **DngToTiff**: DNG to 16-bit TIFF **without interpolation**, with flat-field
  correction, for mono-converted cameras.

## Documentation
| | |
|---|---|
| [Installation](docs/user/Installation.md) | camera settings, USB 2.0, libusb-win32 via Zadig, service install |
| [Using with NINA and SharpCap](docs/user/Using-with-NINA-and-SharpCap.md) | gain/ISO, exposure lengths, mono/colour, behaviour on errors |
| [Mass Storage mode](docs/user/Mass-Storage-mode.md) | 60 / 120 / 180 s and any length up to 300 s: YKUSH XS + relay, wiring, camera settings, start/stop sequence |
| [DngToTiff](docs/user/DngToTiff.md) | interpolation-free TIFFs, exposure, flat-field correction |
| [Troubleshooting](docs/user/Troubleshooting.md) | common problems and fixes |
| [Developer notes](docs/developer/Architecture.md) | architecture, building, tests, [protocol notes](docs/developer/Protocol.md) |

## Requirements
- SIGMA **fp L** (firmware 3.00+) or **fp** (firmware 5.00+), USB mode **Camera Control**.
- Windows 10/11 x64, a **USB 2.0** port (USB 3 SuperSpeed ports do not work with libusb-win32).
- A good USB cable, **clamped at the camera**: the fp / fp L USB-C socket does not hold the plug
  firmly, and any movement drops the connection ([details](docs/user/Installation.md#2-use-a-usb-20-port)).
- **libusb-win32** bound to the camera (one-off with [Zadig](https://zadig.akeo.ie/)).
- NINA 3.x, SharpCap 4.x, or any ASCOM Alpaca client.
- A USB cable to the camera of **4 m or less** (5 m is the USB 2.0 limit and proved unreliable).
- **Optional, for 60-300 s exposures** ([Mass Storage mode](docs/user/Mass-Storage-mode.md)), all of these are needed
  (together **< 100 EUR**):
  - a Yepkit **YKUSH XS** switchable USB port **and** its command-line tool **`ykushcmd.exe`** (free download from Yepkit);
  - a **USB-A to USB Mini-B** cable for the YKUSH XS (its PC-side socket is Mini-B, as on older external hard drives; often not included);
  - an **LCUS-1** USB relay;
  - the **JJC remote cable** for the fp (Cable-I3) and a 3.5 mm stereo plug with screw terminals.

  Without the YKUSH XS only **USB control** is available (1/8000-30 s and 300 s). Another switchable USB port
  (e.g. Pegasus, Acroname) would need a small driver change: possible on request (open an issue).

## Quick start
1. Camera: USB mode **Camera Control**; connect it to a **USB 2.0** port with a good cable of **4 m or less**, and clamp the cable at the camera so the plug cannot move.
2. [Zadig](https://zadig.akeo.ie/): install **libusb-win32** for the camera (USB ID `1003:C442` fp L, `1003:C432` fp).
3. Download the release, then in **Terminal (Admin)**: `powershell -ExecutionPolicy Bypass -File .\Install-SigmaFpAlpaca.ps1`
4. NINA: Equipment > Camera > refresh > choose the camera > Connect.
5. Mono-converted body? Set it to *Monochrome* at <http://127.0.0.1:11111/setup>.
6. Need 60, 120 or 180 s? Set up [Mass Storage mode](docs/user/Mass-Storage-mode.md): needs **both** a Yepkit YKUSH XS (with `ykushcmd.exe`) **and** an LCUS-1 relay.

Details: [Installation](docs/user/Installation.md).

## Status and tested setups
| Camera | Status |
|---|---|
| fp L, mono-converted | in use: NINA sequences (all night, 274 frames without a failure), USB-pull/power-cycle/cable-swap recovery, ConformU pass |
| fp L, colour | NINA in Mass Storage mode: autofocus with 5 s frames, 16 x 120 s in a row (each delivered 129 s after start) |
| fp, colour | tested: connection, frames, raw size 6064 x 4042 |
| fp, mono-converted | via DngToTiff; driver support as for the colour fp |

## Building from source
.NET 8 SDK, then `dotnet test` (about 130 tests) and
`powershell -File tools\install\Build-Release.ps1` (self-contained release into `publish\`).
See [developer notes](docs/developer/Architecture.md).

## Credits and licence
© 2026 Mark Ford. **MIT licence** (see [LICENSE](LICENSE)). Developed with
[Claude Code](https://claude.com/claude-code) (Anthropic).
Thanks to the authors of the earlier community fp L driver and of the open-source PTP projects
that showed the way; see [THIRD-PARTY-NOTICES](THIRD-PARTY-NOTICES.md).

*Not affiliated with SIGMA Corporation. "SIGMA", "fp" and "fp L" are trademarks of SIGMA Corporation.*
