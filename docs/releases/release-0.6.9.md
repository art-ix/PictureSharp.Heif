# PictureSharp.Heif 0.6.9 — one call for edits and the Portrait blur

Photos edits can now be applied before the Portrait blur in one call. **Breaking change from 0.6.8.** Requires
PictureSharp 0.7.6 and .NET 10. Release date: 2026-10-21.

`HeifEdits` describes the Photos sliders and the Portrait blur together; `HeifDocument.Render` loads a photo and
applies both, sliders first, so the blur mixes the edited colours. Rendered pixels are unchanged: each stage gives
the same result as in 0.6.8, bit for bit.

```csharp
HeifDocument document = HeifDocument.Open("IMG_8083.HEIC");
using Image result = document.Render(document.PrimaryImage, new HeifEdits
{
    Adjustments = new HeifPhotosAdjustments { Brightness = 25, Warmth = 10 },
    Portrait = new HeifPortraitOptions { Aperture = 2.8f },
});
```

- **New:** `HeifEdits` (`Adjustments`, `Portrait`, `MaxPixels`, `MaxDegreeOfParallelism`); `HeifDocument.Render`;
  `HeifDocument.LoadPortraitMaps` and `HeifPortraitMaps` (depth and matte, loaded once for several variants);
  `Image.ApplyHeifEdits` for any loaded image.
- **Order:** sliders, then blur. For the opposite order, call `ApplyHeifEdits` twice.
- **Limits:** `HeifEdits.MaxPixels` covers both stages, including the intermediate image when both run.

## Migrating from 0.6.8

| 0.6.8 | 0.6.9 |
| --- | --- |
| `document.RenderPortrait(photo, portrait, options)` | `document.Render(photo, new HeifEdits { Portrait = portrait ?? new() }, options)` |
| `HeifPortrait.Render(image, depth, matte, portrait)` | `image.ApplyHeifEdits(new HeifEdits { Portrait = portrait ?? new() }, new HeifPortraitMaps(depth, matte))` (the maps dispose the matte) |
| `HeifPhotosEdit.Render(image, adjustments)` | `image.ApplyHeifEdits(new HeifEdits { Adjustments = adjustments })` |
| `HeifPortraitOptions.MaxPixels`, `.MaxDegreeOfParallelism` | `HeifEdits.MaxPixels`, `.MaxDegreeOfParallelism` |
| `HeifPhotosAdjustments.MaxDegreeOfParallelism` | `HeifEdits.MaxDegreeOfParallelism` |
| loading depth and matte by hand to edit before the blur | `document.Render(photo, new HeifEdits { Adjustments = …, Portrait = … })` |

[Rendering edits](../decoding.md#rendering-edits).

## Known limitations

The order (sliders, then blur) has not yet been compared with a Photos export that combines both. Both stages
remain approximations; no identity with Photos is claimed.
