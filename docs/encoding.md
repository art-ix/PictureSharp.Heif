# Encoding HEIC

```csharp
using Image image = Image.Load("photo.jpg");
image.SaveAsHeic("photo.heic", new HeifEncoderOptions { Quality = 60, ThumbnailMaxDimension = 320 });
```

`SaveAsHeic`, `SaveAsHeicAsync`, `ToHeic` and `Image.Save("photo.heic")` use `ImageConfiguration.Default` unless the
options set `Configuration`.

## Options

| Option | Default | Meaning |
| --- | --- | --- |
| `Quality` | 50 | 0–100; QP = round(0.51 × (100 − Quality)) |
| `AdaptiveQuantizationStrength` | 1 | 0–3; finer quantization in flat areas (0 disables it) |
| `AlphaQuality` | `Quality` | quality of the alpha plane |
| `TileSize` | 512 | larger images become a grid of tiles (multiple of 32, 64–4096); 0 writes one picture |
| `MaxDegreeOfParallelism` | -1 | tiles encoded in parallel; the file is identical for every value |
| `ThumbnailMaxDimension` | 0 | adds an embedded thumbnail with this longer side; 0 adds none |
| `Metadata` | all | ICC, EXIF and XMP blocks to write |
| `GainMap` | `None` | `Preserve` or `Require` writes the attached HDR gain map (see below) |
| `GainMapQuality` | 90 | quality of a gain map that has to be encoded again |

## What is written

- **Colour:** BT.601 full range 4:2:0 (nclx, or the ICC profile when present), chroma averaged over 2×2 pixels. Odd
  sizes are coded one sample larger and cropped.
- **HEVC:** Main profile, one intra picture per tile, coding units of 32, 16 and 8. Decisions follow HM's intra search
  with a faster pre-selection; kvazaar's adaptive quantization, HM's RDOQ with sign hiding, deblocking and SAO.
- **Alpha:** a monochrome auxiliary image, written only when some pixel is transparent.
- **Orientation:** the EXIF orientation becomes container rotation and mirroring, and the written EXIF says
  `TopLeft`, so viewers show the image as the original was shown.
- **Metadata:** ICC, EXIF and XMP. Animations keep the first frame.

## HDR gain map

An image loaded with `DecoderOptions.LoadGainMap = true` (HEIC, or an Ultra HDR JPEG) keeps its ISO 21496-1 gain map
through geometric edits; `GainMap = HeifGainMapMode.Preserve` writes it, `Require` throws when there is none:

```csharp
var load = new DecoderOptions { LoadGainMap = true };
using Image photo = Image.Load("IMG_8115.HEIC", load);
photo.Mutate(x => x.Resize(photo.Width / 2, photo.Height / 2));
photo.SaveAsHeic("small.heic", new HeifEncoderOptions { GainMap = HeifGainMapMode.Preserve });

using Image ultraHdr = Image.Load("ultrahdr.jpg", load);
ultraHdr.SaveAsHeic("ultrahdr.heic", new HeifEncoderOptions { GainMap = HeifGainMapMode.Require });
```

- **Layout (as iPhones write it):** the map as a hidden image, a `tmap` item with the metadata deriving from the photo
  and the map, both in an `altr` group, and the `tmap` brand. When the gain is applied in primaries other than the
  photo's, the `tmap` item carries their ICC profile. The embedded thumbnail has no map.
- **Copy or encode:** a map loaded from HEIC is copied without re-encoding when the image was neither rotated nor
  edited and its EXIF orientation was not changed. Otherwise the map is encoded in the geometry of the written pixels:
  a gray map as a monochrome image, an RGB map as 4:2:0. Measured at the default `GainMapQuality`: gray maps within
  4 codes of the attached map (2–4 on 40 iPhone photos resized to a quarter), RGB maps lose chroma detail (up to 14
  codes on a synthetic map with a different colour in every sample, at every quality).
- **Not written:** Apple-only maps (iOS 14–17, no ISO metadata) — `Preserve` skips them, `Require` throws
  `NotSupportedException`. The default `None` writes an SDR file, byte for byte as before 0.6.10.

## Embedded thumbnail

`ThumbnailMaxDimension` adds a thumbnail that keeps proportions and is never larger than the photo. It is upright,
uses the photo's quality settings, colour and alpha, and has no EXIF or XMP. Read it with `HeifDocument.LoadPreview`.

## Quality and speed

Bjøntegaard delta rate against x265 `medium` (through `heif-enc`) at qualities 30/45/60/75; negative means smaller
files at equal quality. Measured before 0.6.6:

| Data | RGB PSNR | luma SSIM |
| --- | --- | --- |
| Kodak, 24 images (`Category=Measurement`) | −4.6% | +0.9% |
| 5 crops of private photos (`Category=Local`) | −2.8% | +0.8% |

The faster mode search of 0.6.6 traded a little of that for speed: on Kodak, files at equal quality are 0.95%
larger by RGB PSNR and 1.26% by luma SSIM, and at the same `Quality` the mean PSNR is 0.047 dB lower (at most
0.131 dB). [Method and complete results](measurements/encoder-search-0.6.6.md).

A 12 MP photo at Quality 50 encodes in 2.9 s on all 16 threads (21.8 s on one; i5-12500H). Before 0.6.6 a 48 MP
image took 13.7 s, while `heif-enc` with x265 took 2.0 s for 12 MP and 5.3 s for 48 MP and wrote files 8% and 6%
larger. On an Orange Pi 5 (ARM64) the earlier encoder took 14.0 s and 41.3 s and wrote the same bytes as on x64;
48 MP and ARM64 have not been remeasured since. Data: `docs/measurements/`.

HM decodes every written bitstream exactly as PictureSharp.Heif does, and `heif-dec` (libheif 1.20.2) gives the same
pixels, including grids, alpha, crops and all orientations.
