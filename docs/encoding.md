# Encoding HEIC

```csharp
using PictureSharp;
using PictureSharp.Formats.Heif;

ImageConfiguration.Default = ImageConfiguration.Default.ToBuilder().AddFormat(new HeifFormat()).Build();
using Image image = Image.Load("photo.jpg");
image.SaveAsHeic("photo.heic", new HeifEncoderOptions { Quality = 60 });
```

Without a configuration in the options, `SaveAsHeic`, `SaveAsHeicAsync` and `ToHeic` use `ImageConfiguration.Default`,
so `HeifFormat` must be registered there; `Image.Save("photo.heic")` picks the encoder from the extension. To leave the
default configuration unchanged, pass one in the options (PictureSharp 0.7.0):

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
| `Metadata` | all | ICC, EXIF and XMP blocks to write |

## What is written

- **Colour:** BT.601 full range 4:2:0 (`colr nclx` 1/13/6, or `colr prof` when the image has an ICC profile; the VUI
  carries the same matrix and range), chroma averaged over 2×2 blocks. Odd sizes are coded one sample larger and
  cropped with a clean aperture.
- **HEVC:** Main profile, one IDR picture per tile, CTB 32, intra 2N×2N coding units of 32, 16 and 8 and N×N at 8,
  a residual quadtree of up to two transform splits below each 2N×2N unit (chosen for the best luma mode by
  rate-distortion cost, as HM's final RQT check), deblocking enabled. Decisions follow HM's intra search with faster
  pre-selection: SATD of planar, DC and every fourth angular mode refined around the best angular modes, a
  rate-distortion choice among the best four candidates (two for 16×16 and 32×32) and the most probable modes with
  CABAC rate estimation, five chroma modes, and a rate-distortion split decision for each coding unit that stops early
  when the unsplit unit needs no residual.
- **Psychovisual and rate-distortion tools:** variance adaptive quantization (kvazaar's VAQ: a QP offset per CTB of
  strength × (ln of the block variance − ln of the image variance), sent with `cu_qp_delta`; flat areas get finer
  quantization), rate-distortion optimized quantization and sign data hiding (HM `xRateDistOptQuant`), and sample
  adaptive offset chosen per CTB from the deblocked picture with merges to the left and upper CTB (HM-style
  statistics and rate-distortion decisions; the slice is entropy-coded again once SAO is decided).
- **Alpha:** a monochrome (4:0:0, RExt Monochrome profile) auxiliary image `urn:mpeg:hevc:2015:auxid:1`, tiled like
  the colour image; transparency is detected from the pixels.
- **Orientation:** the EXIF orientation becomes `irot`/`imir`, and the written EXIF says TopLeft; readers that apply
  container transforms (libheif, PictureSharp.Heif, Apple Photos) show the image as the original was shown.
- **Metadata:** EXIF (with `exif_tiff_header_offset` 0) and XMP items linked to the image with `cdsc`.
- Animations are written from their first frame.

## Verification

- Bitstreams decode in the HM reference decoder exactly as in PictureSharp.Heif's decoder; with deblocking disabled the
  decoder reproduces the encoder's reconstruction bit for bit.
- Files decode in `heif-dec` (libheif 1.20.2) to the same pixels as in PictureSharp.Heif, including grids, alpha,
  clean apertures and every orientation.
- Compression is measured against x265 `medium` through `heif-enc` at qualities 30/45/60/75 as the Bjøntegaard delta
  rate on RGB PSNR and luma SSIM (negative means smaller files than x265 at equal quality; the 0.3.0 criterion allows
  +50%):

  | Data | RGB PSNR | luma SSIM | Results |
  | --- | --- | --- | --- |
  | Kodak, 24 images 768×512 (`Category=Measurement`) | −4.6% | +0.9% | `docs/measurements/encoder-bdrate-kodak-x265-medium.tsv` |
  | 5 crops 768×512 of private photos (`Category=Local`) | −2.8% | +0.8% | `docs/measurements/encoder-bdrate-x265-medium.tsv` |

  Each tool on Kodak (PSNR / SSIM): the 0.3.0 encoder −3.8% / +9.3%; with adaptive quantization −0.7% / +4.4%; plus
  RDOQ and sign hiding −4.0% / +0.6%; plus SAO −4.6% / +0.7%; plus the residual quadtree
  −4.7% / +0.6%; with the faster mode and split decisions −4.6% / +0.9% at 40% less encoding time. Adaptive
  quantization trades PSNR for SSIM by design.
  Kodak images are fetched with `scripts/fetch-kodak.py --approved`. A 12 MP photo encodes in about 5.6 s on 16 threads
  with this scalar encoder.
