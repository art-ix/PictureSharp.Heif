# HEIF container and catalogue

A HEIF file is a set of items: photos, hidden tiles, thumbnails, metadata and auxiliary images (alpha, depth, mattes,
gain maps). Examples assume `HeifFormat` is registered in `ImageConfiguration.Default`.

## Identify

```csharp
ImageInfo info = Image.Identify("photo.heic");
Console.WriteLine($"{info.Width} × {info.Height}, {info.Color.BitsPerSample}-bit, alpha {info.Color.HasAlpha}, items {info.ContainerItemCount}");
```

`Identify` reads the primary image's size after rotation and cropping and its colour layout without decoding pixels,
so it does not prove the bitstream is decodable. `info.Color` gives the model (`Gray` for 4:0:0, `Rgb` otherwise),
channels with the `auxl` alpha (straight, or premultiplied with `prem`), bit depth, ICC presence and the nclx colour
description as `Cicp`, also for grids and `iden` items. `Compression` is `Unknown`, since HEVC may or may not be
lossless, and `Resolution` is null. `ContainerItemCount` counts every item; `FrameCount` is 1.

<a id="catalogue-and-image-selection-062"></a>

## Catalogue and image selection

`HeifDocument` describes every item and its relationships without decoding pixels:

```csharp
var document = await HeifDocument.OpenAsync("photo.heic");
foreach (HeifItemInfo photo in document.TopLevelImages)
    Console.WriteLine($"{photo.Id}: {photo.DisplaySize}, thumbnails {photo.Thumbnails.Count}, auxiliary {photo.AuxiliaryImages.Count}");

HeifItemInfo primary = document.PrimaryImage;
using Image image = document.LoadImage(primary);
ImageMetadata metadata = document.ReadMetadata(document.TopLevelImages[^1]);
HeifItemInfo? matte = primary.FindAuxiliaryImage(HeifAppleAuxiliaryTypes.PortraitEffectsMatte);
```

- `Items` lists every item by ID, unsupported ones included. `TopLevelImages` keeps only visible photos (no tiles,
  thumbnails, auxiliary images or `tmap`); `PrimaryImage` is always the file's primary item.
- Each item has `Id`, `Type` (`HeifItemTypes`), `Name`, `IsHidden`, `References` (`HeifReferenceTypes`) and, when
  known, `DisplaySize` and `BitsPerChannel`. `IsSupported` means the declared type and properties are decodable; the
  bitstream may still use unsupported features.
- Relationships return items of the same document: `SourceImages` (grid tiles, `dimg`), `Thumbnails` (`thmb`),
  `AuxiliaryImages` (`auxl`, plus ISO gain maps) and `MetadataItems` (`cdsc`). `FindAuxiliaryImage` and
  `FindDepthImage` pick one auxiliary image.
- `Auxiliary` gives the role (alpha, depth, disparity, gain map or other) from the aux type alone, with the original
  `Type` and raw `Subtype`.
- `LoadImage`, `LoadPreview`, `Identify`, `ReadMetadata`, `LoadDepth`, `LoadPortraitMaps` and `Render` take an item of this
  document and their own `DecoderOptions`. An item of another document or a non-image throws `ArgumentException`;
  `GetItem` throws for an unknown ID, `TryGetItem` returns false.
- `Open` takes a path, stream or memory, `OpenAsync` a path or stream. The input is copied (up to
  `HeifDocumentOptions.MaxInputBytes`), so the document holds no file handle and needs no disposal; streams stay open.
  Dispose the images you load.

## Parsing and limits

- Supported: `hvc1`, `grid` and `iden` items, 16- and 32-bit item IDs, `iloc` versions 0–2. Unknown optional boxes
  and properties are skipped.
- `NotSupportedException`: sequences, protected or external items, implicit extent lengths, unknown essential
  properties and fractional crops. `ImageFormatException`: invalid bounds, missing fields and cyclic references.
- Parsing never recurses into arbitrary boxes and allows at most 65,536 items, 262,144 boxes and 32 reference levels.
- `MaxPixels` counts the stored image, its tiles and alpha once each; cropping does not reduce it. ICC, EXIF and XMP
  count against `MaxMetadataBytes` unless `IgnoreMetadata` is set.

The format follows ISO/IEC 14496-12 and 23008-12 ([overview](https://nokiatech.github.io/heif/technical.html)).

## Reference tools and corpus

Oracle tests compare with HM, libde265, libheif 1.20.2, x265 and kvazaar. After permission to download the pinned
sources (`scripts/heif-reference-versions.tsv`):

```bash
scripts/fetch-heif-reference.sh --approved
scripts/build-heif-reference.sh
python3 tests/assets/generate-corpus.py
LD_LIBRARY_PATH=reference/tools/lib dotnet test -c Release --treenode-filter '/*/*/*/*[Category=Oracle]'
```

The build needs Linux, C/C++ compilers, make, CMake, NASM, pkg-config and PNG/JPEG headers. `PICTURESHARP_HEIF_TOOLS`
moves the tools (default `reference/tools`). Missing tools skip Oracle tests, unless
`PICTURESHARP_HEIF_REQUIRE_ORACLES=1` makes them fail.

The committed corpus is synthetic, with SHA-256 and expected properties in its manifest. Private photos and movies in
`samples/` are used only by `Category=Local` tests and are ignored by Git, because they may contain location data.
