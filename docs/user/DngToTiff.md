# DngToTiff: DNG to 16-bit TIFF without interpolation

[← Overview](../index.md)

For **mono-converted** fp / fp L bodies (including full-spectrum and IR conversions): their raw
values *are* the image, so normal raw converters' debayering only adds artefacts. DngToTiff
writes the sensor data as 16-bit greyscale TIFFs. It decodes the DNG bit-for-bit like LibRaw and
does no colour interpolation.

## Quick use
Drag DNG files, or a folder, onto `DngToTiff.exe` (or a shortcut to it). TIFFs are written next to
the DNGs. The window stays open to show the result.

## Command line
```
DngToTiff.exe <files-or-folders> [options]
```
| Option | Effect |
|---|---|
| *(none)* | **display-ready**: black level subtracted, the camera's BaselineExposure applied (fp +1.0 EV, fp L about +0.94 EV), sRGB tone curve, cropped to the DNG DefaultCrop |
| `--ev <stops>` | extra exposure, e.g. `1`, `-0.5` |
| `--flat <file.dng>` | flat-field correction (see below) |
| `--linear` | linear light 0-65535, for astro or scientific processing (looks very dark on screen) |
| `--raw` | untouched sensor values (black level kept, 14-bit range) |
| `--full` | keep the full sensor area (no crop) |
| `--out <folder>` | write the TIFFs to another folder |
| `--recurse` | include subfolders |

Examples:
```
DngToTiff.exe D:\Photos\Trip
DngToTiff.exe D:\Photos\Trip --ev 1 --out D:\Photos\Trip\tiff
DngToTiff.exe D:\Photos --recurse --linear --full --out D:\linear
```

## Flat-field correction (`--flat`)
Removes vignetting, the IR "hot spot" and dust spots in one step, the same way astro flats work.
1. Shoot a flat: an evenly lit grey card or white wall, slightly out of focus, with the **same
   camera, lens, aperture and filter**, exposed to about mid-grey.
2. Convert with it:
   ```
   DngToTiff.exe D:\Photos\Trip --flat D:\Photos\Trip\greycard.DNG --out D:\Photos\Trip\tiff
   ```
The flat is black-subtracted, lightly smoothed (keeping dust spots), normalised to the image
centre, and divided out of each frame's **linear** data before exposure and tone curve are
applied. This is more accurate than inverting and blending in an editor. The program prints the
flat's corner fall-off and gain range.

The output is deliberately flat (no contrast curve, like any raw file): add contrast in your editor.
