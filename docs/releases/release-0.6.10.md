# PictureSharp.Heif 0.6.10 — writing HDR gain maps

HEIC files can now be saved with their HDR gain map, so an iPhone photo stays HDR after resizing or cropping, and an
Ultra HDR JPEG can become an HDR HEIC. Requires PictureSharp 0.8.0 and .NET 10. Release date: 2026-10-23. No breaking
changes; with the default options the written files are byte for byte those of 0.6.9.

```csharp
var load = new DecoderOptions { LoadGainMap = true };
using Image photo = Image.Load("IMG_8115.HEIC", load);
photo.Mutate(x => x.Resize(photo.Width / 2, photo.Height / 2));
photo.SaveAsHeic("small.heic", new HeifEncoderOptions { GainMap = HeifGainMapMode.Preserve });
```

- **New:** `HeifEncoderOptions.GainMap` (`HeifGainMapMode.None` — the default —, `Preserve`, `Require`, as
  `JpegGainMapMode` in PictureSharp) and `GainMapQuality` (default 90).
- **Layout:** as iPhones write it — a hidden map image, a `tmap` item with the ISO 21496-1 metadata deriving from the
  photo and the map, an `altr` group and the `tmap` brand; an ICC profile on the `tmap` item when the gain is applied in
  primaries other than the photo's. The embedded thumbnail has no map.
- **Copied or encoded:** a map loaded from HEIC is copied without re-encoding while the image is neither rotated nor
  edited. Otherwise it is encoded in the geometry of the written pixels, gray maps as monochrome images and RGB maps as
  4:2:0.
- **Not written:** Apple-only maps (iOS 14–17): `Preserve` skips them, `Require` throws `NotSupportedException`.

**Known limitations:** RGB maps lose chroma detail (the encoder has no 4:4:4); iPhones write gray maps. libheif 1.20
does not interpret `tmap` items, so it shows these files as SDR.

**Validation:**
- Synthetic corpus: unedited maps copied byte for byte, edited, oriented and Ultra HDR JPEG maps reload with the same
  metadata; gray maps within 3 codes (measured 1), the synthetic RGB map within 16 (measured 14). Display P3
  multiplication primaries survive through the `tmap` ICC profile.
- 40 private iPhone photos (single images and grids, Photos exports): unedited maps identical after reloading;
  resized to a quarter, within 4 codes (measured 2–4).
- `heif-dec` decodes the base of the written files exactly like the plugin, and HM decodes the written map streams.
- 84 files written with the default options from the corpus are identical to 0.6.9.
