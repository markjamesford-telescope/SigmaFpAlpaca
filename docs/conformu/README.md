# ConformU results

ASCOM Conform Universal **4.5.0** (build 53834), 2026-10-03, against the driver running on the
**real Sigma fp L** (USB 2.0 port), `http://127.0.0.1:11111/api/v1/camera/0`.

| Test | Result | Files |
|---|---|---|
| Device conformance (ICameraV4) | **PASS: no errors, warnings or issues; all members within target response times** (incl. a real 2 s exposure + ImageArray/ImageArrayVariant) | `conformance.log`, `conformance.json` |
| Alpaca protocol | **0 errors, 0 issues** (information messages are expected refusals, e.g. "Camera is busy") | `alpacaprotocol.log`, `alpacaprotocol.json` |

| Device conformance, **mono body** (SensorType Monochrome) with NINA connected at the same time | **PASS: no errors, warnings or issues** | `conformance-mono.log`, `conformance-mono.json` |

Fixes made to get there (first runs):
- PUT form parameter names are case-sensitive (GET query names are not); upper-case device
  type or method in the URL -> HTTP 400.
- A new connection starts clean: LastExposureDuration/StartTime, ImageReady and ImageArray
  error until a new exposure is taken.
- AbortExposure -> NotImplemented when CanAbortExposure is false (no bulb cable).
- NumX/NumY/StartX/StartY setters accept any value; StartExposure rejects bad subframes
  with InvalidValue.
- PercentCompleted returns 0 before any exposure.
- Per-client connections (Alpaca ClientID): the camera is released only when the last client
  disconnects. Before this, ConformU disconnecting also disconnected NINA (NINA reconnected
  itself within 0.4 s, which made ConformU see Connected=True).

Re-run: `conformu conformance http://127.0.0.1:11111/api/v1/camera/0 -n conformance.log -r conformance.json`
