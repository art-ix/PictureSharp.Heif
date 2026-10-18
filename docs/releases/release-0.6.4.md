# PictureSharp.Heif 0.6.4

Depth samples and Live Photo pairs. Requires PictureSharp 0.7.6. Decoding, encoding and the 0.6.3 API are unchanged;
this release only adds API.

iPhone Portrait photos carry a depth map, and Live Photos are a photo plus a separate MOV. The plugin could only decode
the depth item to 8-bit RGBA and could not tell which movie belongs to which photo.

```csharp
var document = await HeifDocument.OpenAsync("IMG_6655.HEIC");
HeifItemInfo? item = document.PrimaryImage.AuxiliaryImages
    .FirstOrDefault(a => a.Auxiliary?.Kind is HeifAuxiliaryKind.Depth or HeifAuxiliaryKind.Disparity);
HeifDepthImage? depth = item is null ? null : document.LoadDepth(item);

LivePhotoPair pair = await LivePhotoPair.ReadAsync("IMG_8113.HEIC", "IMG_8113.MOV");
bool confirmed = pair.Status == LivePhotoPairStatus.Matched;
```

- **Depth:** `HeifDocument.LoadDepth` returns the raw luma samples at the coded bit depth, with grids and transforms
  applied without changing values, plus the auxiliary type and subtype, the photo's item ID and the item's EXIF and
  XMP. Nothing is scaled or interpreted; limits are checked before allocating.
- **Live Photo:** `LivePhotoPair.Read`/`ReadAsync` compare the photo's Apple MakerNote ContentIdentifier with the MOV's
  `com.apple.quicktime.content.identifier` (`Matched`, `MissingIdentifier`, `ConflictingIdentifiers`) and report
  `StillImageTime`. File names are never used; the MOV is read only up to its `moov` box.
- **iPhone Portrait photos** store disparity under the generic depth type, so the kind is `Depth` (Apple's XMP says
  `disparity`). Their main image is sharp: the Photos app renders the blur, so loading or converting gives the photo
  without it.

[Depth samples](../decoding.md#depth-samples) · [Live Photo pairs](../decoding.md#live-photo-pairs)

**Validation:** Release build without warnings; 484 tests passed, one optional Kodak measurement skipped; Oracle 11/11
(depth identical to HM and libde265 for 8/10-bit, grids, odd crops and transforms; ffmpeg MOV metadata). Local checks
on iPhone files: a Live Photo pair and depth of eight Portrait photos. The package was consumed from a local feed,
including Native AOT on linux-x64.
