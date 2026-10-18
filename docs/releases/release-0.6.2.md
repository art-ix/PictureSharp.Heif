# PictureSharp.Heif 0.6.2

Select photos and embedded previews without guessing item IDs. Requires PictureSharp 0.7.6 or later.

A HEIF file can contain photos, hidden tiles, thumbnails and auxiliary images. `HeifDocument` now exposes their roles
and relationships without decoding pixels. Open from a path, stream or memory; `OpenAsync` supports paths and streams.

```csharp
var document = await HeifDocument.OpenAsync("photo.heic");
using Image photo = document.LoadImage(document.PrimaryImage.Id);
using Image preview = document.LoadPreview(new System.Drawing.Size(320, 240));
```

Register `HeifFormat` first. Each read accepts independent `DecoderOptions`. The document owns its input copy and
leaves caller streams open. [Catalogue API](../container.md#catalogue-and-image-selection-062).

- **Preview selection:** chooses the smallest supported thumbnail covering the requested size with matching
  proportions; otherwise resizes the photo. Embedded previews can differ in framing, compression and colour.
  Use `LoadImage` with `TargetSize` for a resized original. [Decoding](../decoding.md#embedded-previews).
- **Thumbnail writing:** set `HeifEncoderOptions.ThumbnailMaxDimension` to a positive maximum side. The thumbnail
  keeps proportions, colour and alpha, excludes EXIF/XMP and never enlarges. Default: disabled.
  [Encoding](../encoding.md#embedded-thumbnail).
- **Limits:** metadata from unrelated photos no longer consumes the selected photo's budget. Unsupported additional
  items remain in the catalogue and are rejected only when selected for decoding.

**Validation:** Release build without warnings; 456 tests passed, one optional Kodak measurement skipped; Oracle 6/6.
Libheif recognizes generated thumbnails and matches their RGBA pixels; HM matches every coded item. Package consumer
and Native AOT verified on linux-x64. Writing without thumbnails remains byte-identical in four regression cases.
