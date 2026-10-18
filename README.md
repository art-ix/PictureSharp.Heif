# PictureSharp.Heif

**HEIC/HEIF plugin for [PictureSharp](https://github.com/art-ix/PictureSharp), written entirely in C#.**

Reads and writes HEIC, the default photo format of iPhones, on **.NET 10 and later**. No native libraries and no
P/Invoke: the only dependency is the `PictureSharp` package (0.7.6 or later).

```bash
dotnet add package PictureSharp.Heif
```

## 1. Register the plugin

Register the format once at startup. After that, `Image.Load`, `Image.Identify` and `Save` handle `.heic` files like
any built-in format.

```csharp
using PictureSharp;
using PictureSharp.Formats.Heif;

ImageConfiguration.Default = ImageConfiguration.Default.ToBuilder()
    .AddFormat(new HeifFormat())
    .Build();
```

## 2. Decode

```csharp
using var image = Image.Load("IMG_0001.heic");
Console.WriteLine($"{image.Width} × {image.Height}");

image.Mutate(x => x.Resize(1600, 0));
image.Save("photo.jpg");
```

Rotation, mirroring and cropping from the file are already applied, and the image keeps its alpha, ICC profile, EXIF and
XMP. To read only the size or metadata without decoding pixels, use `Image.Identify("IMG_0001.heic")` or
`Image.ReadMetadata("IMG_0001.heic")`.

### Choosing an image item

A HEIF file can hold more images than the primary one: other photos, thumbnails, grid tiles, auxiliary images. Select
one by its item ID with a `HeifDecoderOptions` record in `DecoderOptions.FormatOptions` (PictureSharp 0.7.6); `heif-info`
lists the IDs.

```csharp
var second = new DecoderOptions { FormatOptions = [new HeifDecoderOptions { ItemId = 2 }] };
using var image = Image.Load("burst.heic", second);
ImageInfo info = Image.Identify("burst.heic", second);   // the size of item 2
```

| Record | Property | Selects |
| --- | --- | --- |
| `HeifDecoderOptions` | `ItemId` | The image item to decode (`uint?`); null, the default, is the primary item |

The selected item is decoded with its own transforms, colour, alpha, EXIF/XMP and gain map, exactly like a primary
item; `Image.Identify` and `Image.ReadMetadata` describe it too. ID 0 throws `ArgumentOutOfRangeException`; an ID
that is not in the file or names an EXIF or XMP item throws `ImageFormatException`. Other formats ignore the record,
so one `DecoderOptions` can carry settings for several plugins.

## 3. Encode

```csharp
using var image = Image.Load("photo.png");
image.SaveAsHeic("photo.heic");                                          // quality 50
image.SaveAsHeic("small.heic", new HeifEncoderOptions { Quality = 35 }); // smaller file
```

Alpha, ICC, EXIF and XMP are written too. Every option is described in [encoding](https://github.com/art-ix/PictureSharp.Heif/blob/main/docs/encoding.md).

## HDR photos

iPhone photos carry an HDR gain map. Ask for it while loading, then reconstruct or tone-map the HDR image:

```csharp
using var photo = Image.Load("IMG_0001.heic", new DecoderOptions { LoadGainMap = true });
using HdrImage hdr = photo.ToHdr();
using Image sdr = hdr.ToneMap();
```

## Supported

- **Reading:** HEVC Main, Main Still Picture and Main 10 (4:2:0 and monochrome), tile grids, alpha, rotation, mirroring
  and cropping, ICC/nclx colour, EXIF, XMP, HDR gain maps (Apple and ISO 21496-1).
- **Writing:** HEVC Main 8-bit 4:2:0, a single image or a tile grid, alpha, ICC, EXIF, XMP.
- **Not supported:** image sequences, Live Photos, depth maps as depth data (an auxiliary depth image decodes as a
  plain image through `HeifDecoderOptions.ItemId`), 4:2:2/4:4:4, PQ/HLG, AVIF, writing gain maps and
  editing metadata in place (`Image.EditMetadata`).

PictureSharp's limits (`MaxPixels`, `MaxInputBytes`), cancellation and thumbnails while loading (`TargetSize`) work for
HEIC as for the built-in formats.

## More

- [Decoding](https://github.com/art-ix/PictureSharp.Heif/blob/main/docs/decoding.md): colour conversion, HDR gain maps, metadata-only reads.
- [Encoding](https://github.com/art-ix/PictureSharp.Heif/blob/main/docs/encoding.md): options, file layout, quality and speed measurements.
- [Container](https://github.com/art-ix/PictureSharp.Heif/blob/main/docs/container.md): identification, limits and the test oracles.

Decoded pixels match the HM reference decoder and libheif's `heif-dec` exactly; encoded files decode identically in
both.

## License and patents

MIT. Ported code comes only from MIT-compatible sources (the ITU-T H.265 specification, HM and kvazaar, both BSD-3); see
`THIRD-PARTY-NOTICES.txt`. libheif, libde265 and x265 are used only as test oracles.

HEVC is covered by patents licensed through patent pools. This package does not include a patent license; check
whether your use requires one before shipping it.

## Building from source

```bash
dotnet build -c Release
dotnet test -c Release
```
