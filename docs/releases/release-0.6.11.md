# PictureSharp.Heif 0.6.11 — PictureSharp 0.8.5

Builds against PictureSharp 0.8.5, which rejects plugins built against older versions, and reports the colour layout of
HEIC files in `Image.Identify`. Requires PictureSharp 0.8.5 and .NET 10. Release date: 2026-10-26. Breaking only
through the core's renames (`HdrInfo` → `GainMapInfo`, `ImageInfo.Hdr` → `GainMap`); pixel results are unchanged.

```csharp
ImageInfo info = Image.Identify("IMG_8115.HEIC");
Console.WriteLine($"{info.Color.Model}, {info.Color.BitsPerSample}-bit, ICC: {info.Color.HasIccProfile}");
Console.WriteLine(info.GainMap is null ? "SDR" : "HDR gain map");
```

- **`Identify` colour layout:** `ImageInfo.Color` gives the model (`Gray` for 4:0:0, `Rgb` otherwise), channels with
  alpha (`auxl`, straight or premultiplied with `prem`), bit depth, ICC presence (without reading the profile, so it
  reserves no metadata) and the nclx colour description as `Cicp`, also for grids and `iden` items deriving from the
  coded image. `Compression` is `Unknown`, since HEVC may or may not be lossless.
- **Gain maps:** `ImageInfo.GainMap` and `GainMapMetadata.ToGainMapInfo` replace the `Hdr` names.
- **Apple-only gain maps written** (iOS 14–17, no ISO metadata): `HeifGainMapMode.Preserve` and `Require` now write them
  as ISO 21496-1 `tmap` items, converted by the core since PictureSharp 0.8.1, instead of skipping them or throwing.
- **Reference tools:** `scripts/build-heif-reference.sh` sets the RPATH relative to the installed files, so the built
  libheif and HM tools keep working when the repository moves.

**Validation:** 800 tests (0 failed, 11 skipped without private files), including 10 for the colour layout in
`Identify`: 4:2:0 and 4:0:0, 8 and 10 bits, alpha and `prem`, ICC and nclx, grids and `iden`.
