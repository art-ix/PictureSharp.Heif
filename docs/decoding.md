# Decoding HEIC

After registering `HeifFormat`, `Image.Load` decodes the primary image of a HEIF file into straight-alpha `Rgba32`:

```csharp
using PictureSharp;
using PictureSharp.Formats.Heif;

var configuration = ImageConfiguration.Default.ToBuilder().AddFormat(new HeifFormat()).Build();
using Image image = Image.Load(File.ReadAllBytes("photo.heic").AsSpan(), new DecoderOptions { Configuration = configuration });
Console.WriteLine($"{image.Width} × {image.Height}, ICC: {image.Metadata.IccProfile is not null}");
```

## What is decoded

- **HEVC:** an intra decoder ported from the HM 16.20 reference software, with vector paths (`Vector128`/`Vector256`)
  that give the same results as its scalar code: Main, Main 10 and Main Still Picture,
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
- **Compressed XMP:** an XMP item with `content_encoding` `deflate` is decompressed (a zlib stream, or raw deflate
  without the zlib header). Its decompressed size counts against `MaxMetadataBytes`, and a stream that would exceed
  the limit raises `ImageFormatException` before more is allocated. Another encoding, or a corrupt or truncated
  stream, leaves the image readable without that XMP block. With `IgnoreMetadata` the XMP is not decompressed.
- **Item selection:** the primary item (`pitm`), or the image item `HeifDecoderOptions.ItemId` names (passed in
  `DecoderOptions.FormatOptions`, PictureSharp 0.7.6): another top-level image, a thumbnail, a grid tile or an
  auxiliary image. Alpha, metadata and the gain map are those that refer to the selected item, and `Identify`,
  `Decode` and `ReadMetadata` all use it. `ContainerItemCount` still counts every item. A missing ID or an EXIF/XMP
  item raises `ImageFormatException`.
- **Library options:** `TargetSize`, `ApplyExifOrientation`, `ColorConversion`, `MaxPixels`, `MaxInputBytes`,
  `MaxMetadataBytes`, `IgnoreMetadata` and cancellation behave as for the built-in formats.
- **Threads:** grid tiles decode in parallel and colour conversion and transforms run in row bands, up to
  `DecoderOptions.MaxDegreeOfParallelism`. The pixels, and the error a damaged file raises, do not depend on it.

## HDR gain maps

With `DecoderOptions.LoadGainMap`, the decoder attaches the primary image's HDR gain map (PictureSharp 0.7.1 SPI), so
`Image.Hdr`, `HasHdr`, `ToHdr` and `ToneMap` work as for JPEG files; `Identify` reports the same `ImageInfo.Hdr`.

- **ISO 21496-1:** a `tmap` item whose `dimg` references are the primary image and the map. Its payload is a version
  byte (0) and the ISO metadata; its `colr prof` is the alternate rendition's ICC profile. Other versions raise
  `NotSupportedException` through `Image.Hdr`.
- **Apple:** an auxiliary image of type `urn:com:apple:photo:2020:aux:hdrgainmap` (`auxl` to the primary image). The
  headroom comes from the Apple MakerNote in the primary image's EXIF, or from the map's XMP without it.
- iPhones since iOS 18 describe one map both ways; the ISO description wins, as in PictureSharp's JPEG reader, and
  invalid ISO metadata never falls back to the Apple model. `HdrInfo.Selected` is the ISO model and `HdrInfo.Detected`
  reports both.
- The map is rendered like any image item, with its own transforms (iPhone maps carry the primary image's `irot`), at
  its stored size; a monochrome map becomes gray RGB. Its compressed data is copied at load time and decoded on
  demand, so the input buffer may be reused. The base image keeps its ICC profile or nclx colour also with
  `IgnoreMetadata`, because HDR reconstruction needs it.
- Invalid or unsupported gain map metadata never fails the load: `Image.Hdr.CanDecode` is false and `ToHdr` throws
  `ImageFormatException` (corrupt) or `NotSupportedException`.

## Metadata without decoding

`Image.ReadMetadata` reads the ICC profile (or the nclx/VUI colour as `Chromaticities`/`Gamma`), EXIF and XMP from the
container without decoding HEVC, and reports exactly what `Load` sets, including the `TopLeft` orientation.
`Image.EditMetadata` is not supported yet and throws `NotSupportedException`.

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

## Speed

On an Intel Core i5-12500H (16 threads), decoding to `Rgba32` compared with libheif 1.20.2 (libde265, decoding to
RGBA), best of five runs:

| Photo | PictureSharp.Heif, 1 thread | all threads | libheif, 1 thread | all threads |
| --- | --- | --- | --- | --- |
| 12 MP iPhone (grid of 48 tiles) | 646 ms | 109 ms | 613 ms | 237 ms |
| 24 MP iPhone (grid of 45 tiles) | 1195 ms | 193 ms | 981 ms | 394 ms |
| 48 MP (grid of 192 tiles) | 1009 ms | 165 ms | 973 ms | 416 ms |
| 48 MP (one picture, x265) | 1096 ms | 1092 ms | 935 ms | 938 ms |

On an Orange Pi 5 (ARM64, Rockchip RK3588, 4 + 4 cores), compared with the Ubuntu packages of libheif 1.17.6 and
libde265 1.0.15:

| Photo | PictureSharp.Heif, 1 thread | all threads | libheif, 1 thread | all threads |
| --- | --- | --- | --- | --- |
| 12 MP iPhone (grid of 48 tiles) | 1593 ms | 347 ms | 2222 ms | 820 ms |
| 24 MP iPhone (grid of 45 tiles) | 2733 ms | 651 ms | 4471 ms | 2701 ms |
| 48 MP (grid of 192 tiles) | 2618 ms | 718 ms | 4335 ms | 2392 ms |
| 48 MP (one picture, x265) | 2885 ms | 2658 ms | 4123 ms | 4151 ms |

A single HEVC picture decodes on one thread. The vector paths give the same pixels as the scalar code on both
architectures. The full data is in `docs/measurements/speed-x64.tsv` and `speed-arm64.tsv`.

## Verification

- `tests/assets/generate-hevc-corpus.py` encodes 43 raw HEVC streams with HM and kvazaar that cover the coding tools and
  records SHA-256 hashes of the YCbCr planes decoded by HM; libde265 must agree unless its own picture-hash check
  rejects its output. HEVC items of the HEIC corpus are recorded the same way. The tests decode each stream and compare.
- `tests/assets/generate-corpus.py` records the SHA-256 of `heif-dec` output for every corpus file (after the reductions
  above); `Decode` must match it. The corpus covers BT.601/709/2020, full and limited range, 8 and 10 bits, VUI-only
  and absent colour signalling, monochrome, alpha (straight, premultiplied, 10-bit), grids, all transforms and
  even/odd clean apertures, ICC, EXIF and XMP.
- `Category=Conformance` tests decode the first picture of each JCT-VC HEVC v1 conformance bitstream (147 streams from the
  ITU-T server, downloaded with `scripts/fetch-jctvc-conformance.py --approved` into the ignored
  `tests/assets/external/jctvc`, SHA-256 pinned in `scripts/jctvc-conformance-v1.sha256`). `tests/assets/make-jctvc-reference.py`
  records HM references for 145 of them (two streams have no hash and fail in libde265); all 145 match bit for bit.
- Gain map files in the corpus (Apple with a MakerNote, ISO with gray and RGB maps, both descriptions with rotation,
  invalid versions and truncated metadata) store `heif-dec`'s output for the map alone; `ToHdr` must equal, bit for
  bit, PictureSharp's reconstruction from the decoded base, that map and the generator's metadata. `Category=Local`
  tests do the same for private iPhone photos with the map `heif-dec --with-aux` writes.
- `Category=Local` tests decode every HEVC item of private iPhone photos against HM and libde265, and whole photos against
  `heif-dec`; `Category=Slow` tests fuzz both the HEVC decoder and the HEIF decode path.
