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
files at equal quality:

| Data | RGB PSNR | luma SSIM |
| --- | --- | --- |
| Kodak, 24 images (`Category=Measurement`) | −4.6% | +0.9% |
| 5 crops of private photos (`Category=Local`) | −2.8% | +0.8% |

Steps on Kodak (PSNR / SSIM): 0.3.0 −3.8% / +9.3%; adaptive quantization −0.7% / +4.4% (it trades PSNR for SSIM);
RDOQ and sign hiding −4.0% / +0.6%; SAO −4.6% / +0.7%; residual quadtree −4.7% / +0.6%; faster decisions −4.6% / +0.9%
with 40% less time.

A 12 MP photo encodes in 4.0 s on 16 threads (31 s on one), a 48 MP image in 13.7 s; `heif-enc` with x265 takes 2.0 s
and 5.3 s and writes files 8% and 6% larger. On an Orange Pi 5 (ARM64): 14.0 s and 41.3 s, with identical bytes. Data:
`docs/measurements/`.

HM decodes every written bitstream exactly as PictureSharp.Heif does, and `heif-dec` (libheif 1.20.2) gives the same
pixels, including grids, alpha, crops and all orientations.

### 0.6.6 optimization

The current encoder uses one SATD winner plus competitive neighbouring modes, ordinary quantization during luma
mode selection and full RDOQ for the winner and its transform tree. On 24 Kodak images at Q30/45/60/75 this reduces
encoding time by about 29% (1.41× throughput). Compared with the previous search, equal-quality file size increases
by 0.95% according to RGB PSNR and 1.26% according to luma SSIM. At the same Quality setting, mean PSNR decreases
by 0.047 dB, with a worst case of 0.131 dB. Output bytes change. These are objective measurements, not a visual
quality guarantee. See the [method and complete results](measurements/encoder-search-0.6.6.md).

On the 12 MP photo used for the speed comparison, Q50 encoding takes 21.83 s on one thread (previously 30.76 s)
and 2.86 s on all threads (previously 4.20 s). Allocations fall from 388.9 to 322.1 MiB per encode on one thread.
These are local x64 results; ARM64 has not yet been measured for this change.
