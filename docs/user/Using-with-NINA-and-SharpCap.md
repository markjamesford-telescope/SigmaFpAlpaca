# Using with NINA and SharpCap

[← Overview](../index.md)

The driver is an **ASCOM Alpaca** device: clients find it automatically on the same PC.
No ASCOM Chooser or COM registration is needed.

## NINA (3.x)
1. **Equipment > Camera**, click the refresh button, choose the camera (it shows the connected
   model, e.g. *SIGMA fp L*), **Connect**.
2. **Gain = ISO.** Two modes (setup page, "Gain in NINA"):
   - **List** (default): NINA shows 0-28, an index into
     6, 12, 25, 50, 100, 125, ... 25600. **Gain 10 = ISO 400** (same as the old driver).
   - **ISO**: NINA shows and records the ISO itself (Gain 400). In-between values snap to the
     nearest ISO the camera supports. Use real ISO values in sequences.
3. **Default ISO per model** (setup page; fp L 400, fp 1250) is set when the camera connects.
   **NINA's own Options > Equipment > Camera > Default gain overrides it.** Use one NINA
   profile per camera, or leave NINA's default gain empty.
4. **Exposure lengths:** 1/8000 s to 30 s (nearest shutter step), or exactly **300 s** (the camera's
   own bulb limit; it cannot be aborted). Other lengths are refused with a clear message.

   **Why not 60, 120 or 180 s here?** Over USB the camera only offers timed shutter speeds up to 30 s
   and a bulb that always runs to 300 s, and it ignores its remote jack while a PC controls it.
   **For any length from 1 s to 300 s use [Mass Storage mode](Mass-Storage-mode.md)**: the camera
   exposes on its own (relay on the remote jack, USB switched off), and the driver then reads the DNG
   from the card.
5. **Mono-converted bodies** report *Monochrome*: NINA does not debayer, and FITS files have no
   `BAYERPAT`. Colour bodies report RGGB.
6. FITS headers carry everything NINA knows (target, filter, gain, exposure, HFR, ...). The driver
   can additionally save the camera's DNG (setup page, off by default).

**Calibration frames: one format for everything.** Lights, darks, flats and bias must come from the same
source. NINA's FITS contain the full raw frame (9536 x 6344 on the fp L) with the black level (1024) still in the
data; a raw reader opening the camera's DNG may subtract the black level and crop to the DNG's default crop
(9520 x 6328). Mixing them (e.g. FITS lights with darks shot on the camera alone as DNG) gives an offset after dark
subtraction, wrong flat correction and frames that do not line up. Either take all calibration frames through NINA
(FITS), or use DNGs throughout (the camera's own files, or the driver's DNG copies: setup page "Also save the camera DNG").

**Good to know**
- If the USB connection drops, the frame is reported as failed within seconds; the driver
  reconnects by itself and the sequence continues. No NINA restart needed.
- Stopping a sequence cannot stop a running 300 s camera bulb. If you restart the sequence while
  the camera is still busy, the new exposure is **queued** and starts as soon as the camera is
  free (NINA just sees a longer exposure).
- Use "Continue on error" with care: a dead camera then fails every item quickly.

## SharpCap (4.x, 64-bit)
Camera > the Alpaca device > connect. Live view and captures work. Exposure-length rules are
the same as above.

## Other Alpaca clients
Anything that speaks ASCOM Alpaca ICameraV4 works. The driver passes the ASCOM ConformU
conformance checker without errors or issues. Address: `http://127.0.0.1:11111`, camera device 0.
