# Mass Storage mode: any exposure length (60 s, 120 s, 180 s ...)

[← Overview](../index.md)

Under normal USB control the fp / fp L only offer 1/8000-30 s and a fixed 300 s bulb
([why](Using-with-NINA-and-SharpCap.md#nina-3x)). **Mass Storage mode** gets round that: the camera works on its
own while it exposes, a small relay on its remote jack times the bulb, and the driver reads the finished DNG from
the camera's card over USB. NINA sees a normal camera that takes **any exposure from 1 s to 300 s**.

> Tested with an fp L in NINA: autofocus with 5 s frames (12.4 s each) and 16 x 120 s in a row, each delivered
> about 129 s after it started (about 92 % of the time spent exposing).

## How it works
For every frame the driver:
1. switches the camera's USB port **off** (a switchable USB port: the camera then behaves as if unplugged);
2. waits 2 s, then **closes the relay** for the requested time (the camera exposes in Bulb) and opens it again;
3. waits 4 s, switches the USB port **on**: the camera's card appears as a drive;
4. reads the new DNG, switches the port **off** again and hands the image to NINA.

Overhead: about **8 s per frame** (2 s before, about 6 s after the exposure).

## What you need
**All of these are needed** (together **< 100 EUR**; the YKUSH XS was about 50 EUR plus shipping). Without the
YKUSH XS and its `ykushcmd.exe`, only USB control is available (1/8000-30 s and 300 s). The driver controls the
YKUSH XS through `ykushcmd.exe`; another switchable USB port (e.g. Pegasus, Acroname) would need a small driver
change: possible on request (open an issue).

| Part | Notes |
|---|---|
| **Yepkit YKUSH XS** | one-port USB switch; switches power and data. Its Windows tool `ykushcmd` (zip from the [product page](https://www.yepkit.com/product/300115/YKUSHXS), "Documents and Resources") needs no driver. Unzip it to a fixed folder, e.g. `C:\Tools\ykush\`. **Connectors:** to the PC a **USB Mini-B** socket (the small trapezoid plug of older external hard drives and cameras; not Micro-B), to the camera a normal **USB-A** socket. |
| **USB cable PC -> YKUSH XS: USB-A to USB Mini-B** (USB 2.0) | easy to overlook: often not included, and not the same as the common Micro-B. |
| **USB cable YKUSH XS -> camera: USB-A to USB-C** | **4 m or less**, good quality, clamped at the camera. |
| **LCUS-1 USB relay** (CH340) | appears as a COM port (Device Manager > Ports). Keep it in the same USB socket: the COM number follows the socket. |
| **JJC remote cable for the fp / fp L** (Cable-I3) | 4-pole plug for the camera, 3-pole 3.5 mm socket for the release. |
| **3.5 mm stereo plug with screw terminals** | connects the relay to the JJC socket. |

### Wiring
```
PC ── USB 2 socket ──(A to Mini-B)── YKUSH XS ──(A to C, max 4 m)── camera
PC ── any USB ─────── LCUS-1:  NO  ── plug TIP
                               COM ── plug SLEEVE
                               NC  ── (nothing)      plug RING: not connected
                      plug ── JJC socket ── JJC cable ── camera remote jack
```
- **NC must stay empty.** A wire on NC holds the shutter pressed whenever the relay is off: the camera starts an
  endless exposure as soon as the plug goes in.
- **Rest test before the first use:** relay unplugged from the PC, push the plug fully into the JJC socket:
  **nothing may happen.** If the camera starts exposing, the wiring or the plug is wrong (wire on NC, stray strand).
- Push the plug all the way in; a half-seated plug can make the camera ignore the relay or keep exposing.
- Outdoors, put the YKUSH XS and the LCUS-1 in small weatherproof boxes (IP65), with the cables entering from
  below and some strain relief. Extensions on the release cord are fine (tested with 5 m); keep the USB cable
  to the camera at **4 m or less** and of good quality.

## Camera settings (on the camera; the driver cannot set them in this mode)
- **USB mode: Mass Storage.**
- **Exposure mode M, shutter "B" (Bulb)**, ISO as you want it, image quality **DNG**.
- Cable release function: **Shutter** (not REC).
- **Auto Power Off: Off** (camera menu). A sleeping camera ignores both the relay and the USB port, so it cannot
  be woken from the PC. After a pause of more than a minute (start of a sequence, long autofocus or flip) the
  driver checks that the camera answers before it exposes, and refuses the exposure at once with "The camera did
  not answer: it is probably asleep" instead of losing the frame.
- If the camera is not on Bulb, it exposes its own shutter time; the driver compares the DNG's exposure time with
  the request and warns ("The camera exposed 20 s instead of 5 s: set the shutter to Bulb") in the log and on
  the setup page.

## Driver settings (setup page, <http://127.0.0.1:11111/setup>)
1. Disconnect the camera in NINA first.
2. **Connection mode: Mass Storage + relay.**
3. **Relay COM port**: e.g. `COM9`. **ykushcmd.exe**: e.g. `C:\Tools\ykush\ykushcmd.exe`.
4. Optional: **Delete each image from the card after reading it** (otherwise the card fills up, about 30-45 MB per frame).
5. **Save.** The driver restarts itself (about 10 s). Then connect in NINA.

To go back to normal USB control: camera USB mode **Camera Control**, setup page **USB control**, Save.

## In NINA
- Any exposure from **1 s to 300 s**; abort and stop work (the relay simply opens).
- **ISO comes from the camera.** NINA's gain setting has no effect in this mode. After each image the driver
  reads the ISO the camera used from the DNG and reports it as Gain, so from the **second** frame of a session the
  FITS header carries the real ISO (the first frame shows the default ISO from the setup page). After changing ISO
  on the camera, the first new frame still shows the old value.
- **Autofocus** works with short exposures; every frame costs about 8 s extra, so a 9-point run with 6 s
  exposures takes about 2 minutes.
- Plate solving: as usual.
- **Flats:** exposures must be **at least 1 s**. Set NINA's flat wizard minimum to 1 s and dim the panel (or add
  a diffuser) if it is too bright.
- **Calibration frames in one format:** lights, darks, flats and bias either all as FITS from NINA, or all as DNG
  (e.g. darks taken on the camera alone with DNG lights). Mixing them gives wrong calibration
  ([details](Using-with-NINA-and-SharpCap.md#nina-3x)).

## Connecting, disconnecting and switching off
**Start:**
1. Camera on, USB mode Mass Storage, M / Bulb / ISO / DNG set, Auto Power Off = Off. Jack plug in (rest test passed).
2. YKUSH and relay plugged into the PC. NINA: **Equipment > Camera > Connect.**
   The driver switches the port on once, finds the card, switches the port off; the camera stays on its own.

**End of the night (in this order):**
1. **Stop the sequence** in NINA and wait until the last image has arrived (camera state Idle).
2. **Equipment > Camera > Disconnect** in NINA. The driver leaves the USB port **off** and the relay **open**.
3. **Pull the jack plug** out of the camera (or unplug the relay from the PC only *after* step 2).
4. **Switch the camera off.**
5. Close NINA / unplug cables as you like.

**Avoid:**
- switching the camera off or pulling its battery while an exposure runs, or in the few seconds after it while the
  card is mounted;
- unplugging the relay from the PC while NINA is still connected;
- "ejecting" the camera's drive in Windows (the driver handles the USB port itself);
- separating or re-plugging the release cord during a session: every (un)plug can fire the camera, and an extra
  picture taken during an exposure could be delivered instead of the right one (extensions are fine, e.g. 5 m).

**Emergency stop:** pull the jack plug first (that ends any exposure), then switch the camera off.

## Troubleshooting
| Symptom | Cause / fix |
|---|---|
| "The camera's card did not appear as a drive" on connect | Camera off, not in USB mode Mass Storage, or not connected through the YKUSH XS; check `ykushcmd ykushxs -g`. |
| "The camera did not answer: it is probably asleep" | The camera went to sleep (auto power off) or is off. Press a button to wake it, set Auto Power Off to Off, start again. |
| "The camera's card did not appear within 30 s after the exposure" | The camera fell asleep during a pause, or was switched off / lost power. As above. |
| "No new DNG on the card after the exposure" | The camera did not fire. Most likely: the plug is not fully in the JJC socket, or the wiring is wrong (TIP to NO, SLEEVE to COM, NC empty; repeat the rest test). Less likely: the camera's cable release function is set to REC instead of Shutter, or the camera was still busy. |
| Exposure does not stop / starts when the plug goes in | Shutter line closed at rest: wire on NC or a stray strand in the plug. Pull the plug, fix the wiring, repeat the rest test. |
| Warning "exposed X s instead of Y s" | The camera is not set to Bulb. |
| Images from another card | Not possible: the driver only accepts a USB disk from SIGMA (card readers are ignored). |
