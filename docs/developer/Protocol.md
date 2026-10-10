# Sigma fp-series USB protocol notes

[← Overview](../index.md)

Interoperability notes for the camera-control mode of the SIGMA fp / fp L, as implemented in
`src/SigmaFp.Camera`. They describe what the cameras do on the wire, verified on real fp and fp L
bodies.

## Transport
- USB **still-image class** interface; standard **PTP** (ISO 15740) bulk containers:
  `[length u32][type u16: 1 command, 2 data, 3 response][code u16][transaction id u32][params]`.
- Driver: **libusb-win32** (`libusb0`). Works on USB 2.0 (high-speed, 512-byte packets). On USB 3
  SuperSpeed ports every transfer fails. Windows' MTP driver makes the camera re-enumerate every
  few seconds.
- Session: GetDeviceInfo (0x1001), OpenSession (0x1002, transaction id 0), vendor operations,
  CloseSession (0x1003). Response 0x2001 = OK.
- A data phase whose length is a multiple of the packet size ends with a zero-length packet;
  one bulk read can contain the end of one container and the start of the next.

## Vendor operations used
| Code | Operation | Data |
|---|---|---|
| 0x9035 | ConfigApi (param 0): enter camera-control mode | in: directory (tag 1 model, 2 serial, 3 firmware) |
| 0x9012 / 0x9016 | Get / Set data group 1 (shutter, ISO) | see below |
| 0x9017 | Set data group 2 (exposure mode, image quality) | |
| 0x9018 | Set data group 3 (destination: PC) | |
| 0x9024 | Set data group 4 (DNG bit depth) | |
| 0x9032 | Set focus group | directory format |
| 0x901B | Snap command | `[0][mode][amount][0]` |
| 0x9015 | Capture status (param: image id) | `[_][id][head][tail][status u16][dest][_]` |
| 0x901C | Clear capture entry (param: image id) | 10 zero bytes |
| 0x902D | Picture file info | address @12, size @16, format @28, width/height @32/34, names from @36 |
| 0x9022 | Partial picture file (params: address, offset, length) | `[acquired u32][image bytes][~28-33 byte trailer]` |
| 0x902F | Close application (leave camera-control mode) | 10 zero bytes |

**Data group SET messages:** `[0x00][field-present u16, big-endian][fields present, in order][0x00]`.
The camera's own GET replies put the payload length in byte 0 and a checksum in the last byte; it
accepts zeros in both positions on SET.

Examples:
- Group 1, shutter Bulb + ISO manual 400: `00 19 00 08 00 30 00`
- Group 2, manual exposure + DNG: `00 04 80 04 10 00`
- Group 3, save to PC: `00 00 80 02 00`
- Snap non-AF: `00 02 01 00`

**Codes:** shutter is 8-bit APEX in 1/3 steps (0x10 = 30 s ... 0xA0 = 1/8000 s, 0x08 = Bulb);
ISO is 8-bit APEX (0x30 = ISO 400; 0x20 = 100).

## Capture flow
1. Set groups 2, 1, 4, 3 (mode, shutter/ISO, DNG depth, destination).
2. Read capture status (id 0) for head/tail, clear stale entries, and remember the tail as this shot's id.
3. Snap non-AF (0x02).
4. Poll capture status for that id: 0x0001 exposing, 0x0004 processing, 0x0005 or 0x8003 ready;
   0x6xxx failure (0x6001 AF failure, 0x6004 image generation failed).
5. Picture file info, then partial reads in chunks (8 MB): write exactly `acquired` bytes per chunk.
6. Clear the capture entry.

## Long exposures (measured)
| Method | Result |
|---|---|
| Bulb + Snap Start Capture (0x06) / Stop Capture (0x07) | fails with 0x6001 (AF failure), also with manual focus set |
| Bulb + Snap non-AF | exposes until the camera's bulb limit (**300 s**); Stop Capture and a second release are ignored |
| Bulb + external release on the 3.5 mm jack | **does not work:** ignored in API mode; outside API mode the jack works but the image goes to the card, which Camera Control mode does not expose (GetObjectHandles = 0) and Mass Storage mode cannot shoot. StartCapture (0x06) fails with 0x6001 AF failure, also without a lens and after StartAF |

Closing the session or the USB connection while the camera is exposing leaves it refusing
captures (0x6004) until power-cycled.
