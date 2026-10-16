# PictureSharp.Heif

**HEIC/HEIF plugin for [PictureSharp](https://github.com/art-ix/PictureSharp), written entirely in C#.**

PictureSharp.Heif adds reading and writing of HEIC images (HEVC in the HEIF container), the default photo format of
iPhones, to PictureSharp on **.NET 10 and later**. Like PictureSharp itself, it has no native libraries and no
P/Invoke; it depends only on the `PictureSharp` package.

> **Status: 0.3.0 in development, not yet published.** Identification, decoding (scalar HEVC intra decoder, bit-exact
> with HM) and encoding are implemented.

## Planned scope

| Area | Read | Write |
| --- | --- | --- |
| Container | HEIF still images: `grid` tiles, `irot`/`imir`/`clap`, alpha, thumbnails, ICC/nclx, EXIF, XMP | Single image, tile grid, alpha, ICC/nclx, EXIF, XMP |
| HEVC | Intra pictures, Main / Main Still Picture / Main 10, 4:2:0 and monochrome | Intra, Main 8-bit 4:2:0 |
| HDR | iPhone gain maps (Apple and ISO 21496-1) through PictureSharp's `ToHdr`/`ToneMap` | Planned later |

Not planned: image sequences, Live Photos, depth maps, 4:2:2/4:4:4 (RExt), PQ/HLG and AVIF.

## Identify an image

```csharp
using PictureSharp;
using PictureSharp.Formats.Heif;

var configuration = ImageConfiguration.Default.ToBuilder().AddFormat(new HeifFormat()).Build();
ImageInfo info = Image.Identify(File.ReadAllBytes("photo.heic").AsMemory(),
    new DecoderOptions { Configuration = configuration });
Console.WriteLine($"{info.Width} × {info.Height}, {info.BitsPerChannel} bits, alpha: {info.HasAlpha}");
```

See [container identification](docs/container.md) for limits, supported container features and oracle tooling.

## Decode

```csharp
using PictureSharp;
using PictureSharp.Formats.Heif;

ImageConfiguration.Default = ImageConfiguration.Default.ToBuilder()
    .AddFormat(new HeifFormat())
    .Build();

using var image = Image.Load("IMG_0001.heic");
image.Mutate(x => x.Resize(1600, 0));
image.Save("photo.webp");
```

The decoder applies the container's rotation, mirroring and cropping, attaches alpha, and copies ICC, EXIF and XMP;
see [decoding](docs/decoding.md) for colour conversion and supported features.

## Encode

```csharp
using var image = Image.Load("photo.png");
image.SaveAsHeic("photo.heic", new HeifEncoderOptions { Quality = 60 });
```

Large images are stored as 512×512 tiles, alpha as an auxiliary image and the EXIF orientation as container rotation;
see [encoding](docs/encoding.md).

After registration, PictureSharp's limits (`MaxPixels`, `MaxInputBytes`, `MaxMetadataBytes`), cancellation, thumbnails
(`TargetSize`), colour conversion and metadata apply to HEIC as to the built-in formats.

## Versions

PictureSharp.Heif has its own version numbers, starting at 0.1.0. Each release states the minimum PictureSharp
version it needs; HDR gain maps and metadata-only reads require PictureSharp 0.7.0 or later.

## Compatibility and verification

Container identification is tested against a committed synthetic corpus from x265 and kvazaar and compared with
`heif-info`. Decoded YCbCr matches the HM reference decoder (and libde265) bit for bit on 43 coding-tool streams and the
HEIC corpus; RGB output matches `heif-dec` (libheif 1.20.2) exactly. Encoded files decode identically in HM and
libheif; at equal PSNR they are about the size of x265 `medium` output (Bjøntegaard delta rate −1.2%).

## Licensing and patents

PictureSharp.Heif is licensed under MIT. Ported code comes only from MIT-compatible sources (the ITU-T H.265
specification, HM and kvazaar, both BSD-3); see `THIRD-PARTY-NOTICES.txt`. libheif, libde265 and x265 are used only
as test oracles.

HEVC is covered by patents licensed through patent pools. This package does not include a patent license; check
whether your use requires one before shipping it.

## Building from source

```bash
dotnet build -c Release
dotnet test -c Release
```
