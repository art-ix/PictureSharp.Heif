# PictureSharp.Heif

**HEIC/HEIF plugin for [PictureSharp](https://github.com/art-ix/PictureSharp), entirely in C#.**

Reads and writes HEIC on **.NET 10+**. Requires PictureSharp 0.7.6+; no native libraries or P/Invoke.

```bash
dotnet add package PictureSharp.Heif
```

## Register

Register once to use `.heic` files with `Image.Load`, `Identify` and `Save`:

```csharp
using PictureSharp;
using PictureSharp.Formats.Heif;

ImageConfiguration.Default = ImageConfiguration.Default.ToBuilder()
    .AddFormat(new HeifFormat()).Build();
```

## Decode

```csharp
using var image = Image.Load("photo.heic");
image.Mutate(x => x.Resize(1600, 0));
image.Save("photo.jpg");
```

Container rotation, mirroring and cropping are applied; alpha, ICC, EXIF and XMP are preserved.
`Image.Identify` and `Image.ReadMetadata` read dimensions or metadata without decoding pixels.

### Photos and previews

List photos separately from tiles and auxiliary images, then load a photo or embedded preview:

```csharp
var document = await HeifDocument.OpenAsync("burst.heic",
    new HeifDocumentOptions { MaxInputBytes = 64 * 1024 * 1024 }); // 64 MiB
foreach (var photo in document.TopLevelImages)
    Console.WriteLine($"{photo.Id}: {photo.Name}, {photo.DisplaySize}");

using Image full = document.LoadImage(document.PrimaryImage);
using Image preview = document.LoadPreview(document.PrimaryImage, new System.Drawing.Size(320, 240));
ImageMetadata metadata = document.ReadMetadata(document.TopLevelImages[^1]); // EXIF/XMP of another photo
```

Opening copies the input without decoding pixels. Reads take an item of the document and independent `DecoderOptions`;
`Identify` and `ReadMetadata` describe one photo without decoding it.
`LoadPreview` selects a matching thumbnail (`FindThumbnail` tells which) or resizes the photo. Embedded previews may differ in framing and colour;
use `LoadImage` with `TargetSize` for the photo itself.

`Open` accepts paths, streams or memory; `OpenAsync` accepts paths or streams. Caller streams remain open.
The document owns its buffer and needs no disposal; dispose returned images. `Items` includes unsupported entries,
while `PrimaryImage` identifies the default photo independently of catalogue filtering. Relationships such as
`Thumbnails`, `AuxiliaryImages`, `MetadataItems` and `SourceImages` return the related items directly.

## Encode

```csharp
image.SaveAsHeic("photo.heic", new HeifEncoderOptions
{
    Quality = 60,
    ThumbnailMaxDimension = 320
});
```

Preserves alpha, ICC, EXIF and XMP. Thumbnails keep proportions without enlarging and exclude EXIF/XMP.
Defaults: quality 50, thumbnails disabled.

## HDR

Load Apple/ISO gain maps to reconstruct and tone-map HDR:

```csharp
using var photo = Image.Load("photo.heic", new DecoderOptions { LoadGainMap = true });
using HdrImage hdr = photo.ToHdr();
using Image sdr = hdr.ToneMap();
```

## Support and limits

- **Read:** HEVC Main, Main Still Picture and Main 10; 4:2:0/monochrome, grids, alpha, transforms, ICC/nclx, EXIF/XMP.
- **Write:** HEVC Main 8-bit 4:2:0, grids, alpha, metadata and thumbnails.
- **Unsupported:** sequences, Live Photos, depth interpretation, 4:2:2/4:4:4, PQ/HLG, AVIF, gain-map writing and metadata editing.

Pixel/input/metadata limits, cancellation and `TargetSize` apply. `HeifDecoderOptions.ItemId` selects individual items
through `DecoderOptions.FormatOptions`. Decoded pixels are checked against HM and libheif.

[Decoding](https://github.com/art-ix/PictureSharp.Heif/blob/main/docs/decoding.md) ·
[Encoding and performance](https://github.com/art-ix/PictureSharp.Heif/blob/main/docs/encoding.md) ·
[Catalogue, relationships and limits](https://github.com/art-ix/PictureSharp.Heif/blob/main/docs/container.md)

## License and patents

MIT; ports follow H.265, HM and kvazaar (BSD-3). See `THIRD-PARTY-NOTICES.txt`.
libheif, libde265 and x265 are test oracles only. HEVC patent licensing may be required separately;
this package includes no patent license.

## Build

```bash
dotnet build -c Release
dotnet test -c Release
```
