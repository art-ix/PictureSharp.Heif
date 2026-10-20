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

## What is written

- **Colour:** BT.601 full range 4:2:0 (nclx, or the ICC profile when present), chroma averaged over 2×2 pixels. Odd
  sizes are coded one sample larger and cropped.
- **HEVC:** Main profile, one intra picture per tile, coding units of 32, 16 and 8. Decisions follow HM's intra search
  with a faster pre-selection; kvazaar's adaptive quantization, HM's RDOQ with sign hiding, deblocking and SAO.
- **Alpha:** a monochrome auxiliary image, written only when some pixel is transparent.
- **Orientation:** the EXIF orientation becomes container rotation and mirroring, and the written EXIF says
  `TopLeft`, so viewers show the image as the original was shown.
- **Metadata:** ICC, EXIF and XMP. Animations keep the first frame.

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
