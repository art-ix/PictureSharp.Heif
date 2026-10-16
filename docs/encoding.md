# Encoding HEIC (0.3.0)

```csharp
using PictureSharp;
using PictureSharp.Formats.Heif;

ImageConfiguration.Default = ImageConfiguration.Default.ToBuilder().AddFormat(new HeifFormat()).Build();
using Image image = Image.Load("photo.jpg");
image.SaveAsHeic("photo.heic", new HeifEncoderOptions { Quality = 60 });
```

`SaveAsHeic`, `SaveAsHeicAsync` and `ToHeic` use `ImageConfiguration.Default`, so `HeifFormat` must be registered there;
`Image.Save("photo.heic")` picks the encoder from the extension.

## Options

| Option | Default | Meaning |
| --- | --- | --- |
| `Quality` | 50 | 0–100; QP = round(0.51 × (100 − Quality)) |
| `AlphaQuality` | null (= `Quality`) | quality of the alpha plane |
| `TileSize` | 512 | larger images become a grid of tiles of this size (a multiple of 32, 64–4096); 0 writes one image |
| `MaxDegreeOfParallelism` | -1 | tiles encoded in parallel; the output is identical for every value |
| `Metadata` | all | ICC, EXIF and XMP blocks to write |

## What is written

- **Colour:** BT.601 full range 4:2:0 (`colr nclx` 1/13/6, or `colr prof` when the image has an ICC profile; the VUI
  carries the same matrix and range), chroma averaged over 2×2 blocks. Odd sizes are coded one sample larger and
  cropped with a clean aperture.
- **HEVC:** Main profile, one IDR picture per tile, CTB 32, intra 2N×2N coding units of 32, 16 and 8 and N×N at 8,
  transform units equal to coding units, deblocking enabled. Decisions follow HM's intra search: SATD pre-selection of
  all 35 luma modes, rate-distortion choice among the best candidates and the most probable modes with CABAC rate
  estimation, five chroma modes and a rate-distortion split decision for each coding unit.
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
- Compression is measured against x265 `medium` through `heif-enc` on 768×512 crops of the private photos at qualities
  30/45/60/75 (`Category=Local`, results in `docs/measurements/encoder-bdrate-x265-medium.tsv`): the Bjøntegaard delta
  rate is −1.2% on RGB PSNR and +9.2% on luma SSIM (the 0.3.0 criterion allows +50%). A 12 MP photo encodes in about
  6 s on 16 threads with this scalar encoder.
