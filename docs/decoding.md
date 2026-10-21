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
  camera calibration and the private `RenderingParameters` as bytes
  ([observations](measurements/portrait-0.6.7-rendering-parameters.md)). It is null without Apple XMP. `ToFloat` maps a
  sample linearly from `IntMinValue`–`IntMaxValue` to `FloatMinValue`–`FloatMaxValue`.
- `Auxiliary.Kind` follows the container's aux type only. iPhones store disparity under the generic depth type, so the
  kind is `Depth` while `AppleDepth.Kind` is `Disparity`.
- Mattes are found by type with `FindAuxiliaryImage(HeifAppleAuxiliaryTypes.PortraitEffectsMatte)` (also skin, hair,
  teeth, glasses and sky) and load with `LoadImage` as greyscale images at half size.
- Limits are checked before allocating; other items throw `ArgumentException`.

## Rendering edits

Two approximations fitted to Photos exports restore what a converted photo loses: the Portrait background blur and
the sliders of Photos' "Adjust" panel. `HeifEdits` describes both, and one call loads the photo (with its depth and
matte when a blur is requested) and renders them into a new image:

```csharp
HeifDocument document = HeifDocument.Open("IMG_8083.HEIC");
using Image result = document.Render(document.PrimaryImage, new HeifEdits
{
    Adjustments = new HeifPhotosAdjustments { Brightness = 25, Warmth = 10 },
    Portrait = new HeifPortraitOptions { Aperture = 2.8f },       // null: no blur
});
```

- **Order.** The sliders run first and the blur second, on the edited pixels, so the blurred background mixes the
  edited colours. For the opposite order, render the stages in two calls.
- **Several variants.** `LoadPortraitMaps` reads the depth and matte once; `ApplyHeifEdits` renders any loaded
  `Image` with them, so variants do not decode the file again:

  ```csharp
  using Image photo = document.LoadImage(document.PrimaryImage);
  using HeifPortraitMaps maps = document.LoadPortraitMaps(document.PrimaryImage);
  using Image wide = photo.ApplyHeifEdits(new HeifEdits { Portrait = new() { Aperture = 1.4f } }, maps);
  using Image refocused = photo.ApplyHeifEdits(new HeifEdits { Portrait = new() { FocusPoint = new(400, 900) } }, maps);
  using Image edited = photo.ApplyHeifEdits(new HeifEdits { Adjustments = new() { Saturation = -100 } }); // no maps needed
  ```

  `new HeifPortraitMaps(depth, matte)` wraps a depth image and matte obtained otherwise; the maps dispose the matte.
- **Input and output.** The photo must have one frame and be in display orientation, as loaded. Metadata and alpha
  are kept; an attached HDR gain map is removed. Empty edits return an unchanged copy. `MaxPixels` limits the
  working buffers (the result, an intermediate image when both stages run, and the blur's buffers of about twice
  the photo) before they are allocated. The result does not depend on `MaxDegreeOfParallelism`; the token is
  checked between row bands.
- **Errors.** A blur requested for a photo without depth, or `ApplyHeifEdits` with a blur and no maps, throws
  `ArgumentException`. A depth or matte from the file that does not fit the photo throws `ImageFormatException`;
  the same from the caller throws `ArgumentException`.

### Portrait effect

iPhone keeps a Portrait photo sharp and blurs the background only when Photos displays it, so `Image.Load` and
conversion to JPEG give the photo without the blur. `HeifEdits.Portrait` approximates the effect:

- **Aperture and focus.** The blur grows with the distance from the focus and as the aperture opens (f/1.4–f/16;
  default: the photo's `SimulatedAperture`). By default the focus covers the subject under the portrait matte, from
  its median depth to its nearest parts (without a matte: the nearest tenth of the scene), so a raised hand or a thin
  nearest object stays sharp. `FocusPoint` focuses on the single depth plane under that point.
- **People and outlines.** People under the matte near the focus stay sharp and their colour does not bleed into the
  background; people far behind the focus are blurred like their surroundings, and people in front of it get less
  blur at wide apertures (fading out by f/8). Along the outline the subject and the background keep their own depth,
  background seen between single hairs takes the hair's colour, and the outline is slightly softened at wide
  apertures. Pixels in focus are copied unchanged.
- **Foreground, grain and bokeh.** Scenery in front of the focus (a table, the ground) is blurred less at wide
  apertures, as in Photos; in portraits of objects (no person matte) what lies just in front of the focus stays
  sharp. Blurred areas get fine grain that grows with ISO. Small bright points become hard-edged discs; in night
  shots (ISO × exposure time from EXIF; without EXIF: daylight at ISO 64) clipped lamps become bright cat's-eye
  discs, oval towards the edges like a real lens's bokeh.

Apple's algorithm is private, so this is an approximation calibrated in 0.6.7 against 66 Photos exports of 15
portraits (f/1.4–f/16, three lenses); against 0.6.6 the background improved by 1.07 dB PSNR and the subject by about
4.3 dB. Portrait Lighting is not reproduced. At f/1.4 a 12 MP photo renders in about 0.4 s on all threads (2.5 s on
one) and a 48 MP photo in 2.2 s (i5-12500H). Measurements: [calibration](measurements/portrait-0.6.7-calibration.md),
[focus range](measurements/portrait-0.6.7-focus-range.md), [edges and hair](measurements/portrait-0.6.7-edges.md),
[bokeh and grain](measurements/portrait-0.6.7-bokeh.md).

### Photos adjustments

Edits made with the sliders of Photos' "Adjust" panel stay in the Photos library: the original HEIC holds no slider
values, and the iOS 27 `.AAE` sidecar holds only the portrait effect. `HeifEdits.Adjustments` re-applies slider
values that the application supplies, on the scale shown in Photos. Any loaded image can be edited, not only HEIC:

```csharp
using Image photo = Image.Load("IMG_8113.HEIC");
using Image edited = photo.ApplyHeifEdits(new HeifEdits { Adjustments = new() { Brightness = 25, Warmth = 10, Vignette = 30 } });

// Records are immutable: derive variants of a recipe with `with`.
var recipe = new HeifPhotosAdjustments { Warmth = 40, Vibrance = 30, Contrast = 20 };
using Image mono = photo.ApplyHeifEdits(new HeifEdits { Adjustments = recipe with { Saturation = -100 }, MaxDegreeOfParallelism = 2 },
    cancellationToken: cancellationToken);
```

| Slider | Range | Positive values (negative values do the opposite) | Match with Photos |
| --- | --- | --- | --- |
| `Warmth` | −100…100 | warmer (yellow-red) | close |
| `Tint` | −100…100 | magenta | close |
| `Vignette` | −100…100 | darker corners; the centre is kept | close |
| `Brightness` | −100…100 | brighter mid-tones | close |
| `Contrast` | −100…100 | steeper tone curve | close |
| `Saturation` | −100…100 | more colour; −100 is greyscale | approximate |
| `Vibrance` | −100…100 | more colour in muted tones | approximate |
| `BlackPoint` | −100…100 | crushes shadows to black | approximate |
| `Shadows` | −100…100 | lifts dark tones | approximate |
| `Highlights` | −100…100 | lifts bright tones | approximate |
| `Brilliance` | −100…100 | brightens with local contrast | approximate |
| `Definition` | 0…100 | local contrast | approximate |
| `Exposure` | −100…100 | brighter | rough: Photos adapts it to the scene |
| `Sharpness` | 0…100 | sharper edges | rough: Photos is edge-adaptive |
| `NoiseReduction` | 0…100 | smoother fine detail | rough: Photos is edge-adaptive |

- **Values.** 0 leaves a slider off; when all are 0 (`IsEmpty`), the colours are unchanged. Values outside the range
  throw `ArgumentOutOfRangeException`.
- **Fit.** Each slider was fitted to Photos exports of three iPhone photographs (sunset, night, daylight) edited with
  that slider alone, at ±25, ±50 and ±100 (25, 50 and 100 for the 0…100 sliders); values in between are
  interpolated. Exposure can end up farther from Photos than the unedited photo.
- **Combining sliders.** They are applied in a fixed order: per-channel tone curves, band filters computed from the
  original (sharpness, definition, noise reduction, brilliance, shadows), contrast, saturation, vibrance, vignette.
  Whether Photos uses the same order has not been measured.
- **Cost.** About 0.2 s for 12 MP with tone and colour sliders, about 1 s when a band filter is used (i5-12500H). The
  band filters need about 24 bytes of working memory per pixel.

Starting points for common goals, shown with pictures in the [0.6.8 release details](releases/release-0.6.8-details.md):

| Goal | Sliders |
| --- | --- |
| Brighten a backlit photo | `Shadows = 60, Brightness = 30, Highlights = -30, Vibrance = 15` |
| Lift a shaded scene | `Shadows = 50, Brilliance = 30, Brightness = 10, Warmth = 10` |
| Bring out detail | `Definition = 60, Sharpness = 40, Contrast = 15, Brilliance = 20` |
| Deepen the sky | `Highlights = -60, Contrast = 10, Vibrance = 25` |
| Autumn warmth | `Warmth = 35, Vibrance = 40, Saturation = 10, Vignette = 25` |
| Black and white | `Saturation = -100, Contrast = 35, Definition = 40, BlackPoint = 10` |
| Soft matte look | `Contrast = -30, BlackPoint = -40, Saturation = -15, Warmth = 15` |

The release details also compare renders with Photos exports. Residuals per pair and the confidence of each slider:
[photos-edits-0.6.8-fit.md](measurements/photos-edits-0.6.8-fit.md).

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

Decoding to `Rgba32` on an i5-12500H, milliseconds on 1 / 16 threads:

| Photo | 0.6.5 | 0.6.6 |
| --- | ---: | ---: |
| 12 MP iPhone grid | 663 / 116 | 568 / 103 |
| 24 MP iPhone grid | 1178 / 187 | 938 / 161 |
| 48 MP grid | 1090 / 170 | 916 / 146 |
| 48 MP single picture, WPP | 1115 / 1097 | 848 / 173 |

Grids decode tile by tile in parallel. A single picture coded with wavefronts (WPP) parses its CTB rows in parallel
since 0.6.6; other single pictures decode sequentially. Vector and scalar code give the same pixels, and the pixels
do not depend on the thread count. [0.6.6 release notes](releases/release-0.6.6.md).

Comparison with libheif (libde265) in 0.5.0, best of five runs; ARM64 has not been remeasured since:

| Photo | i5-12500H, 1 thread | 16 threads | libheif, 16 threads | Orange Pi 5 (ARM64), 8 threads | libheif |
| --- | --- | --- | --- | --- | --- |
| 12 MP iPhone (48 tiles) | 646 ms | 109 ms | 237 ms | 347 ms | 820 ms |
| 24 MP iPhone (45 tiles) | 1195 ms | 193 ms | 394 ms | 651 ms | 2701 ms |
| 48 MP (192 tiles) | 1009 ms | 165 ms | 416 ms | 718 ms | 2392 ms |
| 48 MP (one picture, sequential then) | 1096 ms | 1092 ms | 938 ms | 2658 ms | 4151 ms |

Raw data: `docs/measurements/speed-x64.tsv` and `speed-arm64.tsv`.

## Verification

- HEVC: 43 HM/kvazaar streams, the HEIC corpus and 145 JCT-VC conformance streams (`Category=Conformance`) match HM
  bit for bit.
- RGB: every corpus file matches `heif-dec` (colour spaces, ranges, bit depths, alpha, grids, transforms, crops,
  metadata); `ToHdr` matches the reconstruction from the map `heif-dec` writes.
- Depth samples match HM and libde265 (`Category=Oracle`).
- Portrait rendering: subject pixels stay bit-exact, background detail drops as the aperture opens, and output is the
  same for any thread count.
- Photos adjustments: synthetic tests check the identity, ranges, the direction of each slider, alpha, cancellation
  and thread-count independence.
- `Category=Local` repeats the checks on private iPhone photos and compares Portrait renderings and slider renders
  with Photos exports; `Category=Slow` fuzzes the decoder.
