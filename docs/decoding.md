# Decoding HEIC

Examples assume `HeifFormat` is registered in `ImageConfiguration.Default` (see the README).

```csharp
using Image image = Image.Load("photo.heic");                              // primary image, straight-alpha Rgba32
ImageInfo info = Image.Identify("photo.heic");                             // size and bit depth, no pixel decoding
ImageMetadata metadata = Image.ReadMetadata("photo.heic");                 // ICC, EXIF, XMP, no pixel decoding
```

## What is decoded

- **HEVC:** an intra decoder ported from HM 16.20: Main, Main 10 and Main Still Picture, 4:2:0 and monochrome, with
  tiles, wavefronts, slices, PCM, scaling lists, transform skip, deblocking and SAO. Inter slices, 4:2:2/4:4:4, more
  than 10 bits, range extensions and screen content tools throw `NotSupportedException`.
- **Images:** `hvc1`, `grid` and `iden` items. `clap`, `irot` and `imir` are applied in container order, as in libheif.
  The EXIF orientation is then set to `TopLeft`, so `ApplyExifOrientation` never rotates twice.
- **Alpha:** the auxiliary alpha image; premultiplied alpha (`prem`) becomes straight alpha.
- **Metadata:** ICC and the EXIF and XMP items of the image. XMP compressed with `deflate` is decompressed within
  `MaxMetadataBytes`; a corrupt or unknown encoding drops only that block. Without ICC, the nclx colour becomes
  `Chromaticities` and an approximate `Gamma`. `EditMetadata` is not supported.
- **Other items:** `HeifDecoderOptions.ItemId` (in `DecoderOptions.FormatOptions`) selects another photo, a thumbnail,
  a tile or an auxiliary image for `Identify`, `Load` and `ReadMetadata`. `HeifDocument` does the same by item; see
  [catalogue](container.md#catalogue-and-image-selection-062).
- **Options:** `TargetSize`, `ApplyExifOrientation`, `ColorConversion`, the limits, `IgnoreMetadata` and cancellation
  behave as for built-in formats. Tiles decode in parallel up to `MaxDegreeOfParallelism`; the result never depends on
  it.

## Embedded previews

`Image.Load` with `TargetSize` decodes the whole photo first. `LoadPreview` uses an embedded thumbnail when one is
large enough:

```csharp
var document = await HeifDocument.OpenAsync("photo.heic");
using Image preview = document.LoadPreview(document.PrimaryImage, new(320, 240));
```

It picks the smallest thumbnail with the photo's proportions that covers the size, and otherwise resizes the photo;
the result fits inside the size without enlarging. `FindThumbnail` returns the choice without decoding. A thumbnail
may differ from the photo in framing and colour; use `LoadImage` with `TargetSize` when the photo itself is needed.

## HDR gain maps

With `DecoderOptions.LoadGainMap`, the gain map is attached to the image, so `ToHdr` and `ToneMap` work as for JPEG:

```csharp
using var photo = Image.Load("photo.heic", new DecoderOptions { LoadGainMap = true });
using HdrImage hdr = photo.ToHdr();
using Image sdr = hdr.ToneMap();
```

Both kinds are read: ISO 21496-1 (a `tmap` item) and Apple's (auxiliary image `urn:com:apple:photo:2020:aux:hdrgainmap`
with the headroom from the MakerNote). When iOS 18+ stores both, ISO wins, as in PictureSharp's JPEG reader. A damaged
or unsupported map never fails the load: `Image.Hdr.CanDecode` is false and `ToHdr` throws.

## Depth samples

Portrait photos carry a depth (on iPhone: disparity) map as an auxiliary image:

```csharp
HeifDepthImage depth = document.LoadDepth(document.PrimaryImage.FindDepthImage()!);
using Image map = depth.ToImage();                                         // greyscale, near is bright
float? centre = depth.AppleDepth?.ToFloat(depth.Samples.Span[depth.Samples.Length / 2]);   // disparity in 1/m
```

- `Samples` are the decoded luma values at the coded bit depth (8 or 10), with grids and transforms applied but nothing
  scaled or converted. `Exif` and `Xmp` are the depth item's own metadata.
- `AppleDepth` reads Apple's XMP: the declared kind (`Depth` or `Disparity`), the value range, `SimulatedAperture`, raw
  camera calibration and the private `RenderingParameters` as bytes. It is null without Apple XMP. `ToFloat` maps a
  sample linearly from `IntMinValue`–`IntMaxValue` to `FloatMinValue`–`FloatMaxValue`.
- `Auxiliary.Kind` follows the container's aux type only. iPhones store disparity under the generic depth type, so the
  kind is `Depth` while `AppleDepth.Kind` is `Disparity`.
- Mattes are found by type with `FindAuxiliaryImage(HeifAppleAuxiliaryTypes.PortraitEffectsMatte)` (also skin, hair,
  teeth, glasses and sky) and load with `LoadImage` as greyscale images at half size.
- Limits are checked before allocating; other items throw `ArgumentException`.

## Portrait effect

iPhone keeps a Portrait photo sharp and blurs the background only when Photos displays it, so `Image.Load` and
conversion to JPEG give the photo without the blur. `RenderPortrait` approximates the effect and returns a new image:

```csharp
using Image portrait = document.RenderPortrait(document.PrimaryImage);                        // photo's own f-number
using Image wide = document.RenderPortrait(document.PrimaryImage, new() { Aperture = 1.4f }); // stronger blur
using Image refocused = document.RenderPortrait(document.PrimaryImage, new() { FocusPoint = new(400, 900) });
```

- The blur radius grows with the distance from the focus and as the aperture opens (f/1.4–f/16; default: the photo's
  `SimulatedAperture`). The focus defaults to the subject under the portrait matte.
- The matte keeps people near the focus sharp and their colour out of the background; people far behind the focus are
  blurred like their surroundings. Pixels in focus are copied unchanged.
- Metadata and alpha are kept; the HDR gain map is not attached to the result. `HeifPortrait.Render` does the same for
  a photo, depth and matte loaded separately.
- `MaxPixels` limits the working buffers (about twice the photo), and the result does not depend on the thread count.

Apple's algorithm is private, so this is an approximation fitted to photos exported by Photos with the effect: their
background PSNR rose from 21–28 dB without blur to 33–40 dB, with the subject unchanged. Bokeh shapes, highlights and
Portrait Lighting are not reproduced. Rendering takes about 0.3 s for 12 MP and 0.5 s for 24 MP (i5-12500H).

## Live Photo pairs

`LivePhotoPair` checks whether a photo (HEIC or JPEG) and a MOV belong together:

```csharp
LivePhotoPair pair = await LivePhotoPair.ReadAsync("IMG_8113.HEIC", "IMG_8113.MOV");
if (pair.Status == LivePhotoPairStatus.Matched)
    Console.WriteLine($"Still image at {pair.StillImageTime?.TotalSeconds:0.000} s");
```

The photo's ContentIdentifier (Apple MakerNote) is compared with the movie's
(`com.apple.quicktime.content.identifier`): `Matched`, `MissingIdentifier` or `ConflictingIdentifiers`. File names are
never used. `StillImageTime` is the time of the still image in the movie, or null. Only the movie's headers up to
`moov` are read (within `MaxMetadataBytes`); a corrupt movie throws `ImageFormatException`.

## Colour conversion

Colour comes from the item's nclx, else the HEVC VUI, else BT.601 limited range. Matrices 1, 2, 4, 5, 6, 7 and 9 are
supported, and the result equals `heif-dec` (libheif 1.20.2) exactly:

| Case | Chroma upsampling | Arithmetic |
| --- | --- | --- |
| 8-bit full range | nearest | fixed point, `round(k × 256)`, `(v + 128) >> 8` |
| 8-bit limited range, 10-bit | nearest | `float`, `floor(x + 0.5)` |
| crop starting at an odd column or row | bilinear `(9a + 3b + 3c + d + 8) >> 4` | `float` |

Monochrome becomes grey RGB; 10-bit values become 8-bit with `round(v × 255 / 1023)`.

## Speed

Version 0.6.6, i5-12500H, decoding to `Rgba32` (milliseconds, 1 / 16 threads):

| Photo | 0.6.5 | 0.6.6 |
| --- | ---: | ---: |
| 12 MP iPhone grid | 663 / 116 | 568 / 103 |
| 24 MP iPhone grid | 1178 / 187 | 938 / 161 |
| 48 MP grid | 1090 / 170 | 916 / 146 |
| 48 MP single picture, WPP | 1115 / 1097 | 848 / 173 |

CABAC residual parsing, SAO and rotation are faster. A supported single-picture WPP stream parses CTB rows in
parallel; deblocking and SAO also run over row bands. Malformed entry points fall back to sequential parsing.
Decoded pixels match 0.6.5 and do not depend on the thread count. These are results recorded during the decoder
stage; ARM64 has not been remeasured for 0.6.6. [Release details](releases/release-0.6.6.md).

Earlier 0.5.0 measurements, best of five runs, against libheif with libde265:

| Photo | i5-12500H, 1 thread | 16 threads | libheif 16 threads | Orange Pi 5 (ARM64), 8 threads | libheif |
| --- | --- | --- | --- | --- | --- |
| 12 MP iPhone (48 tiles) | 646 ms | 109 ms | 237 ms | 347 ms | 820 ms |
| 24 MP iPhone (45 tiles) | 1195 ms | 193 ms | 394 ms | 651 ms | 2701 ms |
| 48 MP (192 tiles) | 1009 ms | 165 ms | 416 ms | 718 ms | 2392 ms |
| 48 MP (one picture) | 1096 ms | 1092 ms | 938 ms | 2658 ms | 4151 ms |

The earlier single-picture measurement used sequential parsing. Version 0.6.6 can parallelize supported WPP
streams; other streams retain sequential parsing. Vector and scalar code give the same pixels. Earlier data:
`docs/measurements/speed-x64.tsv` and `speed-arm64.tsv`.

## Verification

- HEVC: 43 HM/kvazaar streams, the HEIC corpus and 145 JCT-VC conformance streams (`Category=Conformance`) match HM
  bit for bit.
- RGB: every corpus file matches `heif-dec` (colour spaces, ranges, bit depths, alpha, grids, transforms, crops,
  metadata); `ToHdr` matches the reconstruction from the map `heif-dec` writes.
- Depth samples match HM and libde265 (`Category=Oracle`).
- Portrait rendering: subject pixels stay bit-exact, background detail drops as the aperture opens, and output is the
  same for any thread count.
- `Category=Local` repeats the checks on private iPhone photos and compares Portrait renderings with Photos exports;
  `Category=Slow` fuzzes the decoder.
