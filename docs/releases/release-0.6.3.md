# PictureSharp.Heif 0.6.3

Simpler `HeifDocument` API. **Breaking change from 0.6.2**, released the same day. Requires PictureSharp 0.7.6.

Reads now take an item of the document instead of an ID, so photos, thumbnails and metadata can be navigated
without lookups. Image decoding and the file format are unchanged.

```csharp
var document = await HeifDocument.OpenAsync("photo.heic");
using Image photo = document.LoadImage(document.PrimaryImage);
using Image preview = document.LoadPreview(document.PrimaryImage, new System.Drawing.Size(320, 240));
ImageMetadata metadata = document.ReadMetadata(document.TopLevelImages[^1]);
```

- **New:** `Identify` and `ReadMetadata` describe any photo without reopening the file; `FindThumbnail` tells which
  thumbnail `LoadPreview` would use; `TryGetItem`; `HeifItemInfo.IsSupported`; `HeifItemTypes` and
  `HeifReferenceTypes` constants.
- **Errors:** a non-image item or an item from another document throws `ArgumentException`. Reads no longer check
  `DecoderOptions.MaxInputBytes` again; `HeifDocumentOptions.MaxInputBytes` limits the input on opening.

## Migrating from 0.6.2

| 0.6.2 | 0.6.3 |
| --- | --- |
| `LoadImage(id, options)` | `LoadImage(document.GetItem(id), options)` |
| `LoadPreview(size)` | `LoadPreview(document.PrimaryImage, size)` |
| `LoadPreview(id, size)` | `LoadPreview(document.GetItem(id), size)` |
| `PrimaryItemId` | `PrimaryImage.Id` |
| `ThumbnailItemIds`, `AuxiliaryItemIds`, `MetadataItemIds`, `SourceItemIds` | `Thumbnails`, `AuxiliaryImages`, `MetadataItems`, `SourceImages` (items; use `.Id` for IDs) |
| `Auxiliary.Subtype` (`IReadOnlyList<byte>`) | `ReadOnlyMemory<byte>` |
| `ImageFormatException` for a non-image item | `ArgumentException` |

When a selected thumbnail's bitstream turns out unsupported, `LoadPreview` now falls back directly to the photo.
[Catalogue API](../container.md#catalogue-and-image-selection-062), [previews](../decoding.md#embedded-previews).

**Validation:** Release build without warnings; 459 tests passed, one optional Kodak measurement skipped; Oracle 6/6.
