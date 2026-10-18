# Encoding HEIC

```csharp
using PictureSharp;
using PictureSharp.Formats.Heif;

ImageConfiguration.Default = ImageConfiguration.Default.ToBuilder().AddFormat(new HeifFormat()).Build();
using Image image = Image.Load("photo.jpg");
image.SaveAsHeic("photo.heic", new HeifEncoderOptions { Quality = 60 });
```

`SaveAsHeic`, `SaveAsHeicAsync`, `ToHeic` and `Image.Save("photo.heic")` use `ImageConfiguration.Default` unless the
options carry a configuration:

```csharp
var configuration = ImageConfiguration.Default.ToBuilder().AddFormat(new HeifFormat()).Build();
image.SaveAsHeic("photo.heic", new HeifEncoderOptions { Quality = 60, Configuration = configuration });
```

## Options

| Option | Default | Meaning |
| --- | --- | --- |
| `Quality` | 50 | 0–100; QP = round(0.51 × (100 − Quality)) |
| `AdaptiveQuantizationStrength` | 1 | 0–3; variance adaptive quantization per 32×32 block (0 disables it) |
| `AlphaQuality` | null (= `Quality`) | quality of the alpha plane |
| `TileSize` | 512 | larger images become a grid of tiles of this size (a multiple of 32, 64–4096); 0 writes one image |
| `MaxDegreeOfParallelism` | -1 | tiles encoded in parallel; the output is identical for every value |
| `ThumbnailMaxDimension` | 0 | optional embedded thumbnail: positive maximum side, keeping aspect ratio without enlarging |
| `Metadata` | all | ICC, EXIF and XMP blocks to write |

## What is written

- **Colour:** BT.601 full range 4:2:0 (`colr nclx` 1/13/6, or `colr prof` with an ICC profile; the VUI matches),
  chroma averaged over 2×2 blocks. Odd sizes are coded one sample larger and cropped with `clap`.
- **HEVC:** Main profile, one IDR picture per tile, CTB 32, intra coding units of 32, 16 and 8 (N×N at 8), a residual
  quadtree up to two splits deep, deblocking. Mode decisions follow HM's intra search with a faster pre-selection: SATD
  over planar, DC and every fourth angle, refined near the best, then rate-distortion choice among the best candidates
  and most probable modes with CABAC rate estimates; splits are decided by rate-distortion cost and stop early when the
  unsplit unit needs no residual.
- **Rate-distortion tools:** kvazaar's variance adaptive quantization (per-CTB QP offset of strength × (ln block
  variance − ln image variance), so flat areas get finer quantization), HM's RDOQ with sign data hiding, and SAO chosen
  per CTB with left and upper merges.
- **Alpha:** a monochrome auxiliary image `urn:mpeg:hevc:2015:auxid:1`, tiled like the colour image, written only when
  the pixels have transparency.
- **Orientation:** the EXIF orientation becomes `irot`/`imir` and the written EXIF says TopLeft, so libheif,
  PictureSharp.Heif and Apple Photos show the image as the original was shown.
- **Metadata:** EXIF (`exif_tiff_header_offset` 0) and XMP items linked with `cdsc`. Animations keep the first frame.

## Embedded thumbnail

```csharp
image.SaveAsHeic("photo.heic", new HeifEncoderOptions
{
    Configuration = configuration,
    Quality = 60,
    ThumbnailMaxDimension = 320
});
```

`ThumbnailMaxDimension` adds a `thmb` thumbnail that keeps proportions without enlarging (smaller photos get one at
their own size). It uses the same quality settings and stores upright pixels with its own colour and alpha; ICC
follows `Metadata`, EXIF and XMP are left out. 0 disables it. Read it with `HeifDocument.LoadPreview`.

## Verification

- HM decodes the bitstreams exactly as PictureSharp.Heif does.
- `heif-dec` (libheif 1.20.2) decodes the files to the same pixels, including grids, alpha, clean apertures and every
  orientation.
- Bjøntegaard delta rate against x265 `medium` through `heif-enc` at qualities 30/45/60/75 (negative means smaller files
  at equal quality):

  | Data | RGB PSNR | luma SSIM | Results |
  | --- | --- | --- | --- |
  | Kodak, 24 images 768×512 (`Category=Measurement`) | −4.6% | +0.9% | `docs/measurements/encoder-bdrate-kodak-x265-medium.tsv` |
  | 5 crops 768×512 of private photos (`Category=Local`) | −2.8% | +0.8% | `docs/measurements/encoder-bdrate-x265-medium.tsv` |

  On Kodak (PSNR / SSIM): 0.3.0 −3.8% / +9.3%; adaptive quantization −0.7% / +4.4% (it trades PSNR for SSIM);
  RDOQ and sign hiding −4.0% / +0.6%; SAO −4.6% / +0.7%; residual quadtree −4.7% / +0.6%; faster decisions
  −4.6% / +0.9% with 40% less time. Kodak: `scripts/fetch-kodak.py --approved`.
- Speed: a 12 MP photo encodes in 4.0 s on 16 threads (31 s on one) and a 48 MP image in 13.7 s, against 2.0 s and
  5.3 s for `heif-enc` with x265, whose files are 8% and 6% larger. On an Orange Pi 5 (ARM64, 8 cores): 14.0 s and
  41.3 s, with identical bytes. Vector paths give the scalar results. Data: `docs/measurements/speed-x64.tsv`,
  `speed-arm64.tsv`.
