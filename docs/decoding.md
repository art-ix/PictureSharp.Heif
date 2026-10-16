# Decoding HEIC (0.2.0)

After registering `HeifFormat`, `Image.Load` decodes the primary image of a HEIF file into straight-alpha `Rgba32`:

```csharp
using PictureSharp;
using PictureSharp.Formats.Heif;

var configuration = ImageConfiguration.Default.ToBuilder().AddFormat(new HeifFormat()).Build();
using Image image = Image.Load(File.ReadAllBytes("photo.heic").AsSpan(), new DecoderOptions { Configuration = configuration });
Console.WriteLine($"{image.Width} × {image.Height}, ICC: {image.Metadata.IccProfile is not null}");
```

## What is decoded

- **HEVC:** a scalar intra decoder ported from the HM 16.20 reference software: Main, Main 10 and Main Still Picture,
  4:2:0 and monochrome (4:0:0 without range extension tools), tiles, wavefronts, slices, PCM, scaling lists, transform
  skip, deblocking and SAO. Inter slices, 4:2:2/4:4:4, bit depths above 10 and range extension or screen content tools
  raise `NotSupportedException`.
- **Images:** `hvc1` items, `grid` items (tiles are assembled before colour conversion) and `iden` items. Transforms
  (`clap`, `irot`, `imir`) are applied in association order; `irot` rotates anti-clockwise and `imir` mode 0 exchanges
  top and bottom, mode 1 left and right, as in libheif.
- **Alpha:** the auxiliary alpha image (`urn:mpeg:hevc:2015:auxid:1` or the MPEG-B alpha URN) provides the alpha
  channel. Premultiplied colour (`prem`) is converted to straight alpha with PictureSharp's rounding.
- **Metadata:** ICC (`colr prof`/`rICC`), EXIF and XMP items attached with `cdsc`. Because pixels are already oriented
  by `irot`/`imir`, the EXIF orientation is set to `TopLeft`; `ApplyExifOrientation` therefore never rotates twice.
  Without ICC, CICP primaries other than BT.709 become `Chromaticities`, with a power-law `Gamma` approximation;
  `ColorConversion = ToSrgb` on PQ or HLG content raises `NotSupportedException`.
- **Library options:** `TargetSize`, `ApplyExifOrientation`, `ColorConversion`, `MaxPixels`, `MaxInputBytes`,
  `MaxMetadataBytes`, `IgnoreMetadata` and cancellation behave as for the built-in formats.

## Colour conversion

Colour signalling comes from the item's `colr nclx`, otherwise from the HEVC VUI, otherwise from the HEVC defaults
(BT.601, limited range). Matrix coefficients 1, 2, 4, 5, 6, 7 and 9 are supported. The conversion reproduces
`heif-dec` from libheif 1.20.2 exactly:

| Case | Chroma upsampling | Arithmetic |
| --- | --- | --- |
| 8-bit, full range | nearest neighbour | fixed point, coefficients `round(k × 256)`, `(v + 128) >> 8` |
| 8-bit limited range, 10-bit | nearest neighbour | `float`, limited-range scales 1.1689 (luma) and 1.1429 (chroma), `floor(x + 0.5)` |
| `clap` starting at an odd column or row | bilinear `(9a + 3b + 3c + d + 8) >> 4` on the whole image | `float` as above |

Monochrome images copy luma to R, G and B. 10-bit results are reduced to 8 bits with `round(v × 255 / 1023)`, the
same rounding PictureSharp uses for 16-bit PNG.

## Verification

- `tests/assets/generate-hevc-corpus.py` encodes 43 raw HEVC streams with HM and kvazaar that cover the coding tools and
  records SHA-256 hashes of the YCbCr planes decoded by HM; libde265 must agree unless its own picture-hash check
  rejects its output. HEVC items of the HEIC corpus are recorded the same way. The tests decode each stream and compare.
- `tests/assets/generate-corpus.py` records the SHA-256 of `heif-dec` output for every corpus file (after the reductions
  above); `Decode` must match it. The corpus covers BT.601/709/2020, full and limited range, 8 and 10 bits, VUI-only
  and absent colour signalling, monochrome, alpha (straight, premultiplied, 10-bit), grids, all transforms and
  even/odd clean apertures, ICC, EXIF and XMP.
- `Category=Local` tests decode every HEVC item of private iPhone photos against HM and libde265, and whole photos against
  `heif-dec`; `Category=Slow` tests fuzz both the HEVC decoder and the HEIF decode path.
