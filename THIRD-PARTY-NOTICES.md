# Third-party notices

The Sigma fp / fp L Alpaca driver is MIT-licensed (see `LICENSE`). It uses, but does not contain
the source of, the following components. The self-contained release includes their binaries.

| Component | Use | Licence |
|---|---|---|
| .NET 8 runtime and ASP.NET Core | runtime, HTTP server | MIT |
| ASCOM.Common.Components, ASCOM.Alpaca.Device (ASCOM Initiative) | Alpaca types, discovery responder | MIT |
| Serilog, Serilog.AspNetCore, Serilog.Sinks.File, Serilog.Sinks.Console | logging | Apache-2.0 |
| Microsoft.Extensions.Hosting.WindowsServices, Microsoft.Extensions.Logging.Abstractions, System.IO.Ports | Windows service hosting, logging, serial port | MIT |
| xUnit, Xunit.SkippableFact, Microsoft.AspNetCore.Mvc.Testing (tests only) | unit tests | Apache-2.0 / MIT |

**Not distributed with this project:**
- **libusb-win32** (`libusb0.sys` / `libusb0.dll`, LGPL) is installed separately by the user with
  [Zadig](https://zadig.akeo.ie/) (GPL). The driver calls `libusb0.dll` at run time.

**Trademarks:** "SIGMA", "fp" and "fp L" are trademarks of SIGMA Corporation. This project is
independent and is not affiliated with, endorsed by or supported by SIGMA Corporation. ASCOM and
Alpaca are trademarks of the ASCOM Initiative.

**Acknowledgements:**
- The earlier community ASCOM driver for the Sigma fp L (Cloudy Nights forum, 2022), which showed
  that the fp L can be driven over USB for astrophotography.
- The open-source PTP projects *ptpy* and *sigma_ptpy*, whose existence documented that the fp
  series speaks a PTP vendor protocol. No code from them is included. This driver is an
  independent C# implementation, verified on real cameras.
