# HEIF container catalogue and identification

```csharp
using PictureSharp;
using PictureSharp.Formats.Heif;

var configuration = ImageConfiguration.Default.ToBuilder().AddFormat(new HeifFormat()).Build();
var options = new DecoderOptions { Configuration = configuration, MaxPixels = 100_000_000 };
ImageInfo info = Image.Identify(File.ReadAllBytes("photo.heic").AsMemory(), options);
Console.WriteLine($"{info.Width} × {info.Height}, {info.BitsPerChannel} bits, alpha: {info.HasAlpha}, items: {info.ContainerItemCount}");
```

`Identify` reports the primary image's size after container transforms, bit depth and alpha without reading HEVC
pixels; it does not check that the bitstream itself is decodable. `FrameCount` is 1, `ContainerItemCount` counts every
`iinf` entry (tiles, alpha, thumbnails, metadata), and `Orientation` is normal because transforms are already applied.

## Parsing and limits

- Supported: `hvc1`, `grid` and `iden` items, 16- and 32-bit item IDs, `iloc` versions 0–2 with file- or
  `idat`-relative extents. Transforms follow `ipma` association order; unknown optional boxes and properties are
  skipped.
- `NotSupportedException`: implicit extent lengths, unknown essential properties, sequences, protected or external
  items and fractional clean apertures. `ImageFormatException`: invalid bounds, missing required fields and cyclic
  references.
- Parsing never recurses into arbitrary boxes and allows at most 65,536 items, 262,144 boxes, 262,144 combined
  extent/reference/association/NAL entries, 32 reference levels and 32,767 properties.
- `MaxPixels` covers the stored size of the image, its tiles, grid canvas and alpha, each counted once; cropping does
  not reduce it. ICC, EXIF and XMP bytes count against `MaxMetadataBytes` unless `IgnoreMetadata` is set. Decoding runs
  the same validation first; see [decoding](decoding.md).

The container follows ISO/IEC 14496-12 and 23008-12 ([overview](https://nokiatech.github.io/heif/technical.html)).

<a id="catalogue-and-image-selection-062"></a>

## Catalogue and image selection

A HEIF file mixes photos, hidden tiles, thumbnails, metadata and auxiliary images, so not every `hvc1` item is a photo.
`HeifDocument` reads their descriptions and relationships without decoding pixels:

```csharp
var document = await HeifDocument.OpenAsync("photo.heic", new HeifDocumentOptions { MaxInputBytes = 64L << 20 });
foreach (HeifItemInfo photo in document.TopLevelImages)
    Console.WriteLine($"{photo.Id}: {photo.Name}, {photo.DisplaySize}");

var decodeOptions = new DecoderOptions { Configuration = configuration };
using Image primary = document.LoadImage(document.PrimaryImage, decodeOptions);
using Image preview = document.LoadPreview(document.PrimaryImage, new System.Drawing.Size(320, 240), decodeOptions);
ImageMetadata metadata = document.ReadMetadata(document.PrimaryImage, decodeOptions);
```

- `Items` lists every entry in ID order, unsupported types included. `TopLevelImages` excludes hidden items, tiles,
  thumbnails, auxiliary images and `tmap` descriptors; `PrimaryImage` is always the `pitm` item.
- Each item has its ID, FourCC `Type` (`HeifItemTypes`), UTF-8 `Name`, `IsHidden`, outgoing `References`
  (`HeifReferenceTypes`) and, when known, `DisplaySize` (after transforms) and `BitsPerChannel`. `IsSupported` means
  the declared type, storage and properties are decodable; the bitstream may still use unsupported features.
- `GetItem` throws `ArgumentOutOfRangeException` for an unknown ID; `TryGetItem` returns false.
- `Auxiliary` gives the role (alpha, depth, disparity, gain map or other), the original `Type` and the raw `Subtype`
  bytes. The role follows the aux type alone (see [depth samples](decoding.md#depth-samples)); ISO gain maps have no
  type string and are related to their photo by `tmap`.

| Item property | Relationship |
| --- | --- |
| `SourceImages` | Ordered outgoing `dimg` targets |
| `Thumbnails` | Items whose `thmb` points to this item |
| `AuxiliaryImages` | Items whose `auxl` points to this item, plus ISO gain maps |
| `MetadataItems` | Items whose `cdsc` points to this item |

The lists hold items of the same document; the last three are deduplicated and sorted by ID. Unsupported items stay
in the catalogue and report their limitation only when decoded. Invalid bounds, undefined references and cycles fail
on opening.

`Open` takes a path, stream or `ReadOnlyMemory<byte>`, and `OpenAsync` a path or stream with asynchronous I/O; both
copy the input, so the document holds no file handle and needs no disposal. Streams are read from their current
position, need not be seekable and stay open. `HeifDocumentOptions.MaxInputBytes` limits buffering and is not checked
again by reads. `LoadImage`, `LoadPreview`, `Identify`, `ReadMetadata` and `LoadDepth` take an item of this document
and independent `DecoderOptions`; shared dependencies count once, and other photos' metadata does not use the
selected photo's budget. See [previews](decoding.md#embedded-previews).

## Reference tools and corpus

Source revisions are pinned in `scripts/heif-reference-versions.tsv`. After permission to download them:

```bash
scripts/fetch-heif-reference.sh --approved
scripts/build-heif-reference.sh
python3 tests/assets/generate-corpus.py
dotnet test -c Release --treenode-filter '/*/*/*/*[Category=Oracle]'
```

The build needs Linux, C/C++ compilers, make, CMake, NASM, pkg-config and PNG/JPEG development libraries; it builds in
`reference/build`, leaving `reference/src` untouched. `PICTURESHARP_HEIF_TOOLS` overrides the install directory
(default `reference/tools`); `HEIF_BUILD_JOBS` defaults to 4. Missing tools skip Oracle tests, unless `PICTURESHARP_HEIF_REQUIRE_ORACLES=1` makes
them fail. libheif is pinned to 1.20.2 because 1.17.6 rejects recent iPhone files with multiple auxiliary references.

The committed corpus is synthetic; its manifest records SHA-256 and expected properties. Another toolchain may change
bitstream hashes, so review the manifest with the files. Thumbnail Oracle tests compile
`tests/assets/heif-thumbnail-oracle.c` against the libheif headers and compare RGBA; HM checks every generated item.

Private photos and movies in `samples/` are used only by `Category=Local` tests and are ignored by Git (HEIC, HEIF,
JPEG and MOV in any letter case), because they may contain location data.
