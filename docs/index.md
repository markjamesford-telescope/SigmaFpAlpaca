**A reliable, open-source camera driver for the SIGMA fp L and fp, built for unattended
astrophotography with NINA, SharpCap and any ASCOM Alpaca client. Stock colour and
mono-converted bodies are supported.**

[Download the latest release](https://github.com/markjamesford-telescope/SigmaFpAlpaca/releases) ·
[Installation guide](user/Installation.md) ·
[Source code](https://github.com/markjamesford-telescope/SigmaFpAlpaca)

---

## Why this driver
The earlier community driver for the fp L worked, but froze whenever the camera's USB connection
reset during an exposure: NINA waited forever, and only restarting NINA and power-cycling the
camera helped. This driver was written from scratch to survive whole nights unattended.

| | |
|---|---|
| **Never hangs** | one camera thread, a timeout on every USB transfer, a watchdog |
| **Self-healing** | a lost USB connection is reported within seconds and re-established automatically; the sequence continues |
| **Fast** | 10 s frame delivered about 16 s after start; a 61 MP image reaches NINA in about 0.15 s |
| **Exact data** | DNG decoded in-process, bit-for-bit identical to LibRaw, no processing |
| **Mono and colour** | bodies recognised by serial number; mono-converted bodies are never debayered |
| **Exposures** | 1/8000-30 s and 300 s under USB control; **any length 1-300 s** in Mass Storage mode (Yepkit YKUSH XS + USB relay, together < 100 EUR) |
| **Standards** | ASCOM Alpaca ICameraV4; passes ASCOM ConformU with no errors, warnings or issues (USB control mode) |
| **Hands-off** | runs as a Windows service: starts with Windows, restarts itself |

## Guides
- **[Installation](user/Installation.md)**: camera settings, USB 2.0, the libusb-win32 driver with Zadig, installing the service.
- **[Using with NINA and SharpCap](user/Using-with-NINA-and-SharpCap.md)**: gain/ISO, exposure lengths, mono/colour, what happens on errors.
- **[Mass Storage mode](user/Mass-Storage-mode.md)**: 60 s, 120 s, 180 s or any length up to 300 s with a Yepkit YKUSH XS and an LCUS-1 relay.
- **[DngToTiff](user/DngToTiff.md)**: DNG to 16-bit TIFF without interpolation, with flat-field correction, for mono-converted cameras.
- **[Troubleshooting](user/Troubleshooting.md)**: common problems and their fixes.
- **[Developer notes](developer/Architecture.md)** and **[protocol notes](developer/Protocol.md)**.

## Quick start
1. Camera: **USB mode = Camera Control**; plug it into a **USB 2.0** port with a good cable of **4 m or less**, and **clamp the cable at the camera**: the USB-C socket does not hold the plug firmly, and any movement drops the connection.
2. [Zadig](https://zadig.akeo.ie/): install **libusb-win32** for the camera (`1003:C442` fp L, `1003:C432` fp).
3. Run `Install-SigmaFpAlpaca.ps1` from the release in **Terminal (Admin)**.
4. NINA: *Equipment > Camera > refresh > choose the camera > Connect*.
5. Mono-converted body? Set it to *Monochrome* on the setup page (<http://127.0.0.1:11111/setup>).
6. Need 60, 120 or 180 s? Set up [Mass Storage mode](user/Mass-Storage-mode.md). It needs **both** a Yepkit YKUSH XS (with its free `ykushcmd.exe` tool) **and** an LCUS-1 relay (together < 100 EUR); without them only USB control (up to 30 s and 300 s) is available.

## Tested with
| Camera | |
|---|---|
| SIGMA fp L (mono-converted) | NINA sequences (all night, 274 frames without a failure), USB pull, power-cycle and cable-swap recovery; ConformU |
| SIGMA fp L (colour) | Mass Storage mode in NINA: autofocus with 5 s frames, 16 x 120 s in a row |
| SIGMA fp (colour) | connection, frames, raw size 6064 x 4042 |
| SIGMA fp (mono-converted) | DngToTiff |
| Clients | NINA 3.2, SharpCap 4.1, ASCOM ConformU 4.5 |

---

© 2026 Mark Ford · [MIT licence](https://github.com/markjamesford-telescope/SigmaFpAlpaca/blob/main/LICENSE) ·
Developed with [Claude Code](https://claude.com/claude-code) (Anthropic)

*Not affiliated with SIGMA Corporation. "SIGMA", "fp" and "fp L" are trademarks of SIGMA Corporation.*
