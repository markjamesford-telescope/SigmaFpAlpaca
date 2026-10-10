# Architecture and development

[← Overview](../index.md)

.NET 8, solution `SigmaFpAlpaca.sln`.

| Project | Role |
|---|---|
| `src/SigmaFp.Camera` | USB (libusb-win32), PTP framing, Sigma vendor messages, camera worker thread, state machine, reconnect, simulator, bulb relay (simulator only: the cameras ignore the remote jack under USB control) |
| `src/SigmaFp.Imaging` | managed DNG reader and lossless-JPEG (ITU T.81 process 14) decoder |
| `src/SigmaFp.Alpaca` | ASP.NET Core Alpaca server (ICameraV4), discovery, setup page, settings, Windows service |
| `tools/CameraCli` | hardware test host (timed frames, fault tests) |
| `tools/DngToTiff` | interpolation-free DNG to TIFF converter with flat-field correction |
| `tools/install` | release build and install/uninstall scripts |
| `tests/*` | xUnit tests (camera layer against a simulator; protocol bytes; PTP framing; DNG decoding; HTTP API) |

## Design rules
1. **One camera worker thread** does all USB and serial I/O. Everything else posts work to it.
2. **Timeouts everywhere:** every USB transfer has one, and a watchdog abandons a work item (and
   its thread) that exceeds its limit, then reconnects. A stuck native call never hangs a client.
3. **Explicit state machine:** Disconnected > Idle > Exposing > Downloading > Idle, plus Error.
4. **USB loss** is reported as Error, then reconnected with backoff (1, 2, 5, 10 s); no client restart.
5. **Never close the camera session during a capture.** A camera abandoned mid-bulb refuses all
   captures until power-cycled; a disconnect during a capture is deferred.
6. **Never issue a USB port reset**; recover by closing and reopening.
7. **Raw data untouched:** only DNG decoding; no black subtraction, scaling or debayering.
8. `ImageReady` becomes true only after the full image is decoded in memory.
9. Per-client connections (Alpaca ClientID): the camera is released when the last client disconnects.

## Exposure handling
| Request | Method |
|---|---|
| <= 30 s | camera-timed shutter (nearest APEX step), Snap non-AF |
| = 300 s | Bulb + Snap non-AF; the camera ends it at its own limit (cannot be aborted) |
| 60-180 s (any 1-300 s) | Mass Storage mode: `MassStorageSession` (USB port off via YKUSH XS, relay bulb via `MassStorageTrigger`, port on, newest DNG from the card via `WindowsCardLocator`); under USB control not possible (the camera ignores the remote jack) |

A lost frame keeps CameraState = Error until the next StartExposure, so clients do not wait for
their timeout. StartExposure while an un-abortable capture is still finishing is queued.

## Building and testing
```
dotnet test                                                       # ~100 tests, ~15 s
powershell -File tools\install\Build-Release.ps1                  # tests + self-contained publish\
src\SigmaFp.Alpaca\bin\...\SigmaFp.Alpaca.exe --simulate          # driver with a simulated camera
```
Environment overrides (tests): `SIGMAFP_SETTINGS=<path>`, `SIGMAFP_SIMULATE=1`, `SIGMAFP_NODISCOVERY=1`.

Conformance: `conformu conformance http://127.0.0.1:11111/api/v1/camera/0` and
`conformu alpacaprotocol ...` (ASCOM ConformU 4.5).

## Camera facts (measured)
| | fp L | fp |
|---|---|---|
| USB ID | 1003:C442 | 1003:C432 |
| Raw (DNG) size | 9536 x 6344 | 6064 x 4042 |
| DefaultCrop | 9520 x 6328 | 6000 x 4000 |
| Pixel | 3.76 um | 5.98 um |
| Raw data | 14-bit, black 1024, white 16383, RGGB | same |
| DNG encoding | 256 x 256 tiles, lossless JPEG | lossless JPEG |
| BaselineExposure | +0.94 EV | +1.0 EV |
| Camera bulb limit | 300 s | 300 s (assumed same) |

See [Protocol notes](Protocol.md) for the wire format.
