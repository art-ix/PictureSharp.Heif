# HEIF container catalogue and identification

Register the plugin and call the public PictureSharp API:

```csharp
using PictureSharp;
using PictureSharp.Formats.Heif;

var configuration = ImageConfiguration.Default.ToBuilder().AddFormat(new HeifFormat()).Build();
var options = new DecoderOptions { Configuration = configuration, MaxPixels = 100_000_000 };
ImageInfo info = Image.Identify(File.ReadAllBytes("photo.heic").AsMemory(), options);
Console.WriteLine($"{info.Width} × {info.Height}, {info.BitsPerChannel} bits, alpha: {info.HasAlpha}, items: {info.ContainerItemCount}");
```

`Identify` reads the primary image's dimensions after container transforms, bit depth and alpha declaration without
reading HEVC pixels. `FrameCount` is 1 for a still image; tiles, metadata and auxiliary items are not animation frames.
`ContainerItemCount` (PictureSharp 0.6.9) reports every `iinf` entry: the primary image, grid tiles, alpha, thumbnails
and Exif/XMP items. `Orientation` is normal because `irot`/`imir`/`clap` have
already been accounted for. `TargetSize` does not change identification of stored dimensions.

The parser handles `hvc1`, `grid` and `iden`, item IDs in 16- and 32-bit boxes, and `iloc` versions 0–2 with file-relative
and `idat`-relative extents with explicit nonzero lengths. Implicit extent lengths raise `NotSupportedException`. Transform properties follow `ipma` association order. Unknown optional boxes and properties
are skipped; unknown essential image properties, sequences, protected/external items and
fractional-pixel clean apertures raise `NotSupportedException`. Invalid bounds, missing required fields and cyclic
references raise `ImageFormatException`. HEVC payload validity and profile support are not established by `Identify`; even a profile unsupported for decoding can be identified.

Structural parsing never recursively descends into arbitrary boxes. It permits up to 65,536 items, 262,144 boxes,
262,144 combined extent/reference/association/NAL entries, 32 reference levels and 32,767 properties. Input payloads
remain borrowed memory slices. `MaxPixels` covers stored primary, tile and alpha dimensions, counting each required item
once and including the grid canvas. Cropping cannot reduce this charge. ICC/EXIF/XMP byte counts obey `MaxMetadataBytes`
for the selected image and its dependencies, unless `IgnoreMetadata` is true. `Decode` runs the same validation before decoding; see [decoding](decoding.md).

Container design follows ISO/IEC 14496-12 and ISO/IEC 23008-12. The
[Nokia HEIF technical overview](https://nokiatech.github.io/heif/technical.html) describes the item/property/reference model.
No implementation was ported from an external codec library for this stage.

## Catalogue and image selection (0.6.2)

A HEIF file can contain photos, hidden tiles, thumbnails, metadata and auxiliary images. Treating every `hvc1` item
as a photo mixes these roles. `HeifDocument` reads their descriptions and relationships without decoding pixels.

```csharp
var document = await HeifDocument.OpenAsync("photo.heic",
    new HeifDocumentOptions { MaxInputBytes = 64L << 20 });
foreach (HeifItemInfo photo in document.TopLevelImages)
    Console.WriteLine($"{photo.Id}: {photo.Name}, {photo.DisplaySize}");

var decodeOptions = new DecoderOptions { Configuration = configuration };
using Image primary = document.LoadImage(document.PrimaryImage, decodeOptions);
using Image preview = document.LoadPreview(document.PrimaryImage, new System.Drawing.Size(320, 240), decodeOptions);
ImageMetadata metadata = document.ReadMetadata(document.PrimaryImage, decodeOptions);
```

- `Items` contains all entries in ID order, including unsupported types. `TopLevelImages` excludes hidden items,
  tiles, thumbnails, auxiliary images and `tmap` descriptors. `PrimaryImage` always returns the `pitm` item.
- Each item preserves its ID, FourCC `Type` (`HeifItemTypes`), UTF-8 `Name`, `IsHidden`, outgoing `References`
  (`HeifReferenceTypes`) and available `DisplaySize`/`BitsPerChannel`. Size includes known transforms; unavailable
  values are null. `IsSupported` reports whether the container declares a type, storage and properties the decoder
  supports; the HEVC bitstream can still use unsupported features.
- `GetItem` throws `ArgumentOutOfRangeException` for an unknown ID; `TryGetItem` returns false.
- `Auxiliary` classifies alpha, depth, disparity, gain maps or other types while preserving `Type` and raw `Subtype`
  bytes (`ReadOnlyMemory<byte>`).
  ISO-only gain maps have no auxiliary type string (`Type` is null); `tmap` relates them to their base photo.
  Depth units and calibration are not interpreted.

| Item property | Relationship |
| --- | --- |
| `SourceImages` | Ordered outgoing `dimg` targets |
| `Thumbnails` | Items whose `thmb` points to this item |
| `AuxiliaryImages` | Items whose `auxl` points to this item, plus ISO gain maps |
| `MetadataItems` | Items whose `cdsc` points to this item |

The lists contain the related `HeifItemInfo` objects of the same document; the last three are deduplicated and
sorted by ID. Unsupported additional items remain in the catalogue;
selecting one for decoding reports its limitation. Invalid bounds, undefined references and cycles fail on opening.

`Open` accepts a path, stream or `ReadOnlyMemory<byte>`; `OpenAsync` accepts a path or stream and uses asynchronous
I/O. Both own a copy of the input. Streams are read from their current position, may be nonseekable, and remain open
on success, error or cancellation. The document retains no file handle and does not require disposal.

`HeifDocumentOptions.MaxInputBytes` limits buffering. Each `LoadImage`, `LoadPreview`, `Identify` and
`ReadMetadata` call takes an item of this document and independent `DecoderOptions`, including configuration and
pixel/metadata budgets; the document stores none of them. A read does not check `MaxInputBytes` again, because the
owned buffer was limited on opening. Shared dependencies count once; other photos' metadata
do not consume the selected photo's budget. Returned images belong to the caller. See [preview selection](decoding.md#embedded-previews).

## Reference tools and corpus

Source revisions are pinned in `scripts/heif-reference-versions.tsv`. After obtaining permission to download them:

```bash
scripts/fetch-heif-reference.sh --approved
scripts/build-heif-reference.sh
python3 tests/assets/generate-corpus.py
dotnet test -c Release --treenode-filter '/*/*/*/*[Category=Oracle]'
```

The build requires Linux, C/C++ compilers, make, CMake, NASM, pkg-config and PNG/JPEG development libraries. It builds in
`reference/build`, preserving the original checkouts in `reference/src`. `PICTURESHARP_HEIF_TOOLS` selects an absolute
installation directory containing `bin/heif-info`, `bin/heif-enc`, `bin/heif-dec` and `bin/TAppDecoder` (default:
`reference/tools`). `HEIF_BUILD_JOBS` defaults to 4. `PICTURESHARP_HEIF_REQUIRE_ORACLES=1` makes missing tools fail in CI;
local runs skip unavailable oracles explicitly.

The checked-in corpus contains synthetic gradients only. Its manifest records SHA-256 and expected dimensions, depth,
alpha and encoder. Rebuilding the corpus on a different native toolchain may change bitstream hashes; review and update
the manifest together with the files. Managed tests always check the committed bytes; Oracle tests query `heif-info`
independently. HEVC and RGB references are described in [decoding](decoding.md).

Private files in `samples/` are exercised only by `Category=Local` tests. Both lowercase and uppercase HEIC/HEIF
extensions are ignored by Git. Reference libheif is pinned to 1.20.2 because 1.17.6 rejects recent iPhone files with
multiple auxiliary references. No private metadata is written into the committed corpus.

Thumbnail Oracle tests also compile the independent `tests/assets/heif-thumbnail-oracle.c` driver with `cc` against
the reference libheif headers. It obtains the thumbnail by its relationship and compares decoded RGBA pixels;
HM verifies every generated coded item, including alpha. These native tools are test-only.
