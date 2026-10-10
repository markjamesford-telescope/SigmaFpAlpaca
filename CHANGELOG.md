# Changelog

## 1.0.0 (2026-10-10)
First public release.
- ASCOM Alpaca ICameraV4 driver for SIGMA fp L and fp (colour and mono-converted bodies).
- Direct USB control (PTP vendor protocol over libusb-win32); timeouts, watchdog, automatic reconnect.
- Exposures: 1/8000-30 s and the 300 s camera bulb under USB control.
- Mass Storage mode: any exposure from 1 s to 300 s (60/120/180 s ...). The camera exposes on its own, timed by an LCUS-1
  relay on the remote jack while a Yepkit YKUSH XS switches its USB port off; the driver then reads the DNG from the card.
  Warns when the camera is not set to Bulb; reports the camera's ISO (from the DNG) as Gain. Switch modes on the setup
  page (the driver restarts itself).
- Managed DNG decoder (bit-exact with LibRaw); ImageBytes transfer.
- Per-body settings by serial number (mono/colour, learned raw size); per-model default ISO.
- Gain as ISO list (old-driver compatible) or as ISO value.
- Lost frames reported immediately (sticky Error); StartExposure queued while an un-abortable capture finishes.
- After a USB drop, the shot that was in flight is collected and cleared before the session can close (a session closed
  over an uncollected shot was the likely cause of a 0x6004 camera lock-up seen in the field).
- On disconnect the camera is left set to "save in camera", so it is usable on its own afterwards.
- Camera problems (e.g. 'switch it off and on') shown on the setup page.
- Camera restart detected at once (PTP 'session not open' triggers an immediate reconnect).
- Per-client connections; Alpaca discovery; browser setup page.
- Windows service with automatic restart; self-contained installer.
- DngToTiff: interpolation-free DNG to 16-bit TIFF, display/linear/raw modes, flat-field correction.
- Passes ASCOM ConformU 4.5 (no errors, warnings or issues) in USB control mode.
