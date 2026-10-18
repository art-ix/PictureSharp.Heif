# Decoding HEIC

After registering `HeifFormat`, `Image.Load` decodes the primary image into straight-alpha `Rgba32`:

```csharp
using PictureSharp;
using PictureSharp.Formats.Heif;

var configuration = ImageConfiguration.Default.ToBuilder().AddFormat(new HeifFormat()).Build();
using Image image = Image.Load(File.ReadAllBytes("photo.heic").AsSpan(), new DecoderOptions { Configuration = configuration });
Console.WriteLine($"{image.Width} × {image.Height}, ICC: {image.Metadata.IccProfile is not null}");
```

## What is decoded

- **HEVC:** an intra decoder ported from HM 16.20, with vector paths (`Vector128`/`Vector256`) identical to its scalar
  code: Main, Main 10 and Main Still Picture, 4:2:0 and monochrome, tiles, wavefronts, slices, PCM, scaling lists,
  transform skip, deblocking and SAO. Inter slices, 4:2:2/4:4:4, more than 10 bits and range extension or screen
  content tools raise `NotSupportedException`.
- **Images:** `hvc1`, `grid` (tiles assembled before colour conversion) and `iden` items. `clap`, `irot` and `imir`
  apply in association order; `irot` turns anti-clockwise and `imir` mode 0 swaps top and bottom, mode 1 left and
  right, as in libheif.
- **Alpha:** the auxiliary alpha image (`urn:mpeg:hevc:2015:auxid:1` or the MPEG-B alpha URN). Premultiplied colour
  (`prem`) becomes straight alpha with PictureSharp's rounding.
- **Metadata:** ICC (`colr prof`/`rICC`) and the EXIF and XMP items linked by `cdsc`. Pixels are already oriented, so
  the EXIF orientation is set to `TopLeft` and `ApplyExifOrientation` never rotates twice. Without ICC, non-BT.709
  primaries become `Chromaticities` with an approximate `Gamma`; `ColorConversion = ToSrgb` on PQ or HLG raises
  `NotSupportedException`. `Image.ReadMetadata` returns the same metadata without decoding HEVC; `EditMetadata` is not
  supported.
- **Compressed XMP:** `content_encoding` `deflate` (zlib, or raw deflate) is decompressed within `MaxMetadataBytes`;
  a stream that would exceed it raises `ImageFormatException` before allocating more. Another encoding or a corrupt
  stream drops only that XMP block. `IgnoreMetadata` skips decompression.
- **Item selection:** the primary item (`pitm`), or the image item `HeifDecoderOptions.ItemId` names in
  `DecoderOptions.FormatOptions`: another photo, a thumbnail, a tile or an auxiliary image, with its own alpha,
  metadata and gain map, for `Identify`, `Load` and `ReadMetadata`. A missing ID or a non-image item raises
  `ImageFormatException`.
- **Options and threads:** `TargetSize`, `ApplyExifOrientation`, `ColorConversion`, the pixel, input and metadata
  limits, `IgnoreMetadata` and cancellation behave as for built-in formats. Tiles decode in parallel, and conversion
  and transforms run in row bands, up to `MaxDegreeOfParallelism`; neither the pixels nor the error for a damaged file
  depend on it.

## Embedded previews

`Image.Load` with `TargetSize` decodes the full image before resizing. To use an embedded thumbnail:

```csharp
var document = await HeifDocument.OpenAsync("photo.heic");
var options = new DecoderOptions { Configuration = configuration };
var size = new System.Drawing.Size(320, 240);
HeifItemInfo? thumbnail = document.FindThumbnail(document.PrimaryImage, size); // null: the photo is resized
using Image preview = document.LoadPreview(document.PrimaryImage, size, options);
```

`LoadPreview` fits inside the target without enlarging. It picks the smallest supported thumbnail that covers the
needed size with the photo's proportions (one pixel of rounding allowed; ties take the lower ID). Without one, or
when the chosen thumbnail's bitstream turns out unsupported, it resizes the photo; corrupt data, limits and
cancellation are reported. `FindThumbnail` returns the choice without decoding.

The preview keeps its own colour, alpha, transforms and metadata, and may differ from the photo in framing and
compression; use `LoadImage` with `TargetSize` when the photo itself is required. Each read takes independent
`DecoderOptions` (null means core defaults); the item overrides `ItemId`, and previews also override `TargetSize`.
An item that is not an image, or belongs to another document, throws `ArgumentException`. Returned images belong to
the caller.

## HDR gain maps

With `DecoderOptions.LoadGainMap`, the primary image's gain map is attached, so `Image.Hdr`, `ToHdr` and `ToneMap`
work as for JPEG and `Identify` reports `ImageInfo.Hdr`.

- **ISO 21496-1:** a `tmap` item deriving (`dimg`) from the primary image and the map. Its payload is version 0 and the
  ISO metadata; its `colr prof` is the alternate rendition's profile. Other versions are `NotSupportedException`
  through `Image.Hdr`.
- **Apple:** an auxiliary image `urn:com:apple:photo:2020:aux:hdrgainmap`, with the headroom from the EXIF MakerNote
  (or the map's XMP).
- iOS 18 and later describe one map both ways; ISO wins, as in PictureSharp's JPEG reader, invalid ISO metadata never
  falls back to Apple, and `HdrInfo.Detected` reports both.
- The map is rendered with its own transforms at its stored size (monochrome becomes gray RGB). Its data is copied at
  load time and decoded on demand. The base image keeps its colour description even with `IgnoreMetadata`.
- Invalid or unsupported map metadata never fails the load: `Image.Hdr.CanDecode` is false and `ToHdr` throws.

## Depth samples

Portrait photos store depth or disparity as an auxiliary image. `LoadDepth` returns its raw samples:

```csharp
HeifItemInfo? item = document.PrimaryImage.AuxiliaryImages
    .FirstOrDefault(a => a.Auxiliary?.Kind is HeifAuxiliaryKind.Depth or HeifAuxiliaryKind.Disparity);
if (item is not null)
{
    HeifDepthImage depth = document.LoadDepth(item);
    ushort centre = depth.Samples.Span[(depth.Height / 2 * depth.Width) + (depth.Width / 2)];
    Console.WriteLine($"{depth.Auxiliary.Type}: {depth.Width}x{depth.Height}, {depth.BitDepth}-bit, centre {centre}");
}
```

- Samples are the decoded luma at the coded bit depth (8 or 10), before colour conversion; chroma is ignored. Grids,
  `clap`, `irot` and `imir` move samples without changing them. Nothing is scaled or converted.
- `Auxiliary` keeps the original type and subtype, `Exif` and `Xmp` are the depth item's own `cdsc` blocks (empty with
  `IgnoreMetadata`), and `PhotoItemId` is the `auxl` target.
- Limits are checked before allocating (`MaxPixels` for the item and its tiles, `MaxMetadataBytes`); cancellation and
  `MaxDegreeOfParallelism` apply. Other items throw `ArgumentException`; corrupt or unsupported items throw as
  `LoadImage` does.

**iPhone Portrait photos.** `Auxiliary.Kind` follows the aux type alone. iPhones store **disparity** under the generic
depth type `urn:mpeg:hevc:2015:auxid:2`, so the kind is `Depth`, while the item's XMP says
`apdi:AuxiliaryImageType="disparity"`. Samples are 8-bit quantizations of Float16 values: value = `FloatMinValue` +
sample / 255 × (`FloatMaxValue` − `FloatMinValue`), with the range in the same XMP; the plugin does not apply it. The
main image is sharp: Photos renders the background blur from the disparity, the portrait mattes
(`urn:com:apple:photo:2018:aux:portraiteffectsmatte` and semantic mattes, kind `Other`) and
`depthBlurEffect:SimulatedAperture`, so `Image.Load` and conversion to JPEG give the photo without the blur.

## Live Photo pairs

`LivePhotoPair` checks whether a given photo (HEIC or JPEG) and movie (MOV) form a Live Photo:

```csharp
LivePhotoPair pair = await LivePhotoPair.ReadAsync("IMG_8113.HEIC", "IMG_8113.MOV");
if (pair.Status == LivePhotoPairStatus.Matched)
    Console.WriteLine($"Still image at {pair.StillImageTime?.TotalSeconds:0.000} s");
```

- The photo's ContentIdentifier is tag 0x0011 of the Apple MakerNote, read with `Image.ReadMetadata` (`HeifFormat` is
  added to the configuration when missing); the movie's is `com.apple.quicktime.content.identifier` in `moov/meta` or
  `moov/udta/meta`.
- `Status` is `Matched` for equal identifiers (ordinal), `MissingIdentifier` when either is absent and
  `ConflictingIdentifiers` when they differ. File names are never used.
- `StillImageTime` is the presentation time of the `com.apple.quicktime.still-image-time` sample after the track's
  edit list, or null.
- Only box headers are read up to `moov`, which must fit in `MaxMetadataBytes`; media data is skipped by seeking or
  reading forward. A non-QuickTime, truncated or corrupt movie throws `ImageFormatException`. Streams are read from
  their current position and left open. Writing or decoding the movie is out of scope.

## Colour conversion

Colour comes from the item's `colr nclx`, else the HEVC VUI, else the HEVC defaults (BT.601, limited range). Matrix
coefficients 1, 2, 4, 5, 6, 7 and 9 are supported. The conversion reproduces `heif-dec` from libheif 1.20.2 exactly:

| Case | Chroma upsampling | Arithmetic |
| --- | --- | --- |
| 8-bit, full range | nearest neighbour | fixed point, coefficients `round(k × 256)`, `(v + 128) >> 8` |
| 8-bit limited range, 10-bit | nearest neighbour | `float`, limited-range scales 1.1689 (luma) and 1.1429 (chroma), `floor(x + 0.5)` |
| `clap` starting at an odd column or row | bilinear `(9a + 3b + 3c + d + 8) >> 4` on the whole image | `float` as above |

Monochrome copies luma to R, G and B. 10-bit results become 8-bit with `round(v × 255 / 1023)`, as PictureSharp
does for 16-bit PNG.

## Speed

Decoding to `Rgba32`, best of five runs, against libheif with libde265 decoding to RGBA. Intel Core i5-12500H
(16 threads, libheif 1.20.2):

| Photo | PictureSharp.Heif, 1 thread | all threads | libheif, 1 thread | all threads |
| --- | --- | --- | --- | --- |
| 12 MP iPhone (grid of 48 tiles) | 646 ms | 109 ms | 613 ms | 237 ms |
| 24 MP iPhone (grid of 45 tiles) | 1195 ms | 193 ms | 981 ms | 394 ms |
| 48 MP (grid of 192 tiles) | 1009 ms | 165 ms | 973 ms | 416 ms |
| 48 MP (one picture, x265) | 1096 ms | 1092 ms | 935 ms | 938 ms |

Orange Pi 5 (ARM64 RK3588, 4 + 4 cores, Ubuntu libheif 1.17.6):

| Photo | PictureSharp.Heif, 1 thread | all threads | libheif, 1 thread | all threads |
| --- | --- | --- | --- | --- |
| 12 MP iPhone (grid of 48 tiles) | 1593 ms | 347 ms | 2222 ms | 820 ms |
| 24 MP iPhone (grid of 45 tiles) | 2733 ms | 651 ms | 4471 ms | 2701 ms |
| 48 MP (grid of 192 tiles) | 2618 ms | 718 ms | 4335 ms | 2392 ms |
| 48 MP (one picture, x265) | 2885 ms | 2658 ms | 4123 ms | 4151 ms |

A single HEVC picture decodes on one thread. Vector and scalar paths give the same pixels on both architectures.
Full data: `docs/measurements/speed-x64.tsv` and `speed-arm64.tsv`.

## Verification

- 43 raw HEVC streams from HM and kvazaar (`tests/assets/generate-hevc-corpus.py`) and the HEVC items of the HEIC corpus
  must match HM's YCbCr hashes (libde265 agrees unless its own hash check rejects its output).
- Every corpus file must match `heif-dec` RGB (`tests/assets/generate-corpus.py`): BT.601/709/2020, both ranges, 8 and
  10 bits, VUI-only and absent colour, monochrome, straight, premultiplied and 10-bit alpha, grids, all transforms,
  even and odd clean apertures, ICC, EXIF and XMP.
- `Category=Conformance`: the first picture of 145 JCT-VC HEVC v1 conformance streams matches HM bit for bit
  (`scripts/fetch-jctvc-conformance.py --approved`, hashes in `scripts/jctvc-conformance-v1.sha256`).
- Gain map corpus files and private iPhone photos: `ToHdr` equals PictureSharp's reconstruction from the map
  `heif-dec` writes, bit for bit.
- Depth samples match HM and libde265 for 8- and 10-bit, grids, odd crops and transforms (`Category=Oracle`) and on
  private Portrait photos (`Category=Local`).
- `Category=Local` also checks every HEVC item of private photos against HM and libde265 and whole photos against
  `heif-dec`; `Category=Slow` fuzzes the HEVC decoder and the HEIF decode path, and every run fuzzes the MOV parser.
