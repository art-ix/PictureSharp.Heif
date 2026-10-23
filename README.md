# PictureSharp.Heif

**HEIC/HEIF plugin for [PictureSharp](https://github.com/art-ix/PictureSharp), entirely in C#.**

Reads and writes HEIC on .NET 10+ with PictureSharp 0.7.6+. No native libraries or P/Invoke.

```bash
dotnet add package PictureSharp.Heif
```

## Use

Register the format once; `Image.Load`, `Identify`, `ReadMetadata` and `Save` then handle `.heic`:

```csharp
using PictureSharp;
using PictureSharp.Formats.Heif;

ImageConfiguration.Default = ImageConfiguration.Default.ToBuilder().AddFormat(new HeifFormat()).Build();

using var image = Image.Load("photo.heic");     // rotation, crop, alpha, ICC, EXIF and XMP applied
image.Mutate(x => x.Resize(1600, 0));
image.SaveAsHeic("small.heic", new HeifEncoderOptions { Quality = 60 });
```

A library can pass its own configuration through `DecoderOptions.Configuration` and `HeifEncoderOptions.Configuration`
instead of changing the default.

## Inside the file

An iPhone HEIC holds more than one picture: tiles, thumbnails, a depth map, mattes and an HDR gain map. `HeifDocument`
lists them without decoding pixels:

```csharp
var document = await HeifDocument.OpenAsync("IMG_6655.HEIC");
HeifItemInfo photo = document.PrimaryImage;

using Image preview = document.LoadPreview(photo, new(320, 240));        // embedded thumbnail when one fits
HeifDepthImage depth = document.LoadDepth(photo.FindDepthImage()!);       // raw depth or disparity samples
using Image map = depth.ToImage();                                         // the depth map as a greyscale image

using var withMap = Image.Load("IMG_6655.HEIC", new DecoderOptions { LoadGainMap = true });
using HdrImage hdr = withMap.ToHdr();                                     // HDR from the gain map

LivePhotoPair pair = LivePhotoPair.Read("IMG_8113.HEIC", "IMG_8113.MOV"); // matched by ContentIdentifier
```

## Portrait blur and Photos edits

iPhone stores a Portrait photo sharp and blurs the background only on display, and Photos keeps the edits of its
"Adjust" sliders in its library, so a converted photo has neither. Two approximations fitted to Photos exports bring
them back in one call, sliders first and then the blur:

```csharp
using Image edited = document.Render(photo, new HeifEdits
{
    Adjustments = new HeifPhotosAdjustments { Brightness = 25, Warmth = 10 },
    Portrait = new HeifPortraitOptions { Aperture = 2.8f },               // blur from the disparity map and matte
});
```

The slider values are not stored in a readable form, so the application supplies them. Neither result matches Photos
exactly.

## Support

| | |
| --- | --- |
| Read | HEVC Main, Main 10 and Main Still Picture, 4:2:0 and monochrome; grids, alpha, rotation/mirror/crop, ICC and nclx, EXIF/XMP (also compressed), Apple and ISO gain maps, thumbnails, depth with Apple's description, Live Photo pairing |
| Write | HEVC Main 8-bit 4:2:0, grids, alpha, ICC/EXIF/XMP, thumbnails, ISO gain maps (from HEIC or Ultra HDR JPEG) |
| Render (approximate) | Portrait background blur, the 15 Photos "Adjust" sliders |
| Not supported | image sequences, 4:2:2/4:4:4, PQ/HLG, AVIF, writing Apple-only gain maps or depth, metadata editing, Portrait Lighting, Photos filters and crop, Live Photo export |

Limits (`MaxPixels`, `MaxInputBytes`, `MaxMetadataBytes`), cancellation and `TargetSize` work as for built-in formats.
Decoded pixels match the HM reference decoder and libheif.

Guides: [decoding](https://github.com/art-ix/PictureSharp.Heif/blob/main/docs/decoding.md) ·
[encoding](https://github.com/art-ix/PictureSharp.Heif/blob/main/docs/encoding.md) ·
[container and catalogue](https://github.com/art-ix/PictureSharp.Heif/blob/main/docs/container.md)

[0.6.9 release notes](https://github.com/art-ix/PictureSharp.Heif/blob/main/docs/releases/release-0.6.9.md): `HeifEdits`, one call for edits and the Portrait blur (breaking change).

## License and patents

MIT. Ported code follows H.265, HM and kvazaar (BSD-3); see `THIRD-PARTY-NOTICES.txt`. libheif, libde265 and x265 are
used only as test oracles. HEVC may require a patent license, which this package does not include.

## Build

```bash
dotnet build -c Release
dotnet test -c Release
```
