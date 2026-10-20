# PictureSharp.Heif 0.6.8 — Photos adjustment sliders

Re-applies the sliders of Apple Photos' "Adjust" panel to a decoded photograph. Requires PictureSharp 0.7.6 and
.NET 10. Adds public API; existing API is unchanged. Release date: 2026-10-20.

Details, the comparison gallery and recipes: [release-0.6.8-details.md](release-0.6.8-details.md).

## New API

```csharp
using Image edited = HeifPhotosEdit.Render(photo, new HeifPhotosAdjustments { Exposure = 20, Warmth = -10, Vignette = 30 });
```

`HeifPhotosAdjustments` holds all fifteen sliders on the scale shown in Photos: exposure, brilliance, highlights,
shadows, contrast, brightness, black point, saturation, vibrance, warmth, tint and vignette (−100 to 100), and
sharpness, definition and noise reduction (0 to 100). Photos keeps these edits in its library (the iOS 27 `.AAE`
holds only the portrait effect), so the application supplies the values. [Guide](../decoding.md#photos-adjustments).

## How it was made

Three iPhone photographs (a sunset street, a night scene and a daylight scene) were exported from Photos 94 times,
each time with one slider changed. Every slider was fitted to these pairs and checked on slider values and scenes
left out of the fit. The data show that tone sliders are curves per channel on sRGB values, contrast is a curve on
luma, saturation and vibrance scale the distance from grey, vignette is a radial gain in linear light, and the
detail sliders are Gaussian band filters. [Measurements](../measurements/photos-edits-0.6.8-fit.md).

| Match with Photos | Sliders |
| --- | --- |
| Close: global or consistent across scenes | warmth, tint, vignette, brightness, contrast |
| Approximate | saturation, vibrance, black point, shadows, definition, highlights, brilliance |
| Rough, to be refined | exposure (Photos adapts it to the scene), sharpness and noise reduction (edge-adaptive in Photos) |

On 86 of the 94 pairs, compared at 2 MP, the render is closer to the Photos export than the unedited photo. The
exceptions are seven exposure pairs (small values) and noise reduction at block scale. These pairs were used for
fitting; errors on held-out values and scenes are in the measurements.

## Speed and determinism

About 0.2 s for 12 MP with tone and colour sliders and about 1 s with band filters (sharpness, definition, noise
reduction, brilliance, shadows); 1.9 s for 24 MP with all sliders (i5-12500H, all threads). The result does not
depend on the thread count.

## Known limitations

Exposure, highlights, brilliance and shadows adapt to the scene in Photos and are only approximated. Combined
sliders use a fixed order that has not been compared with Photos. Photos filters (presets), crop and straighten are
not supported. No identity with Photos is claimed.
