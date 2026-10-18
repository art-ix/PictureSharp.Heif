# PictureSharp.Heif 0.6.5

Apple Portrait effect. Requires PictureSharp 0.7.6. Existing API and decoding are unchanged; this release only adds
API.

iPhone keeps a Portrait photo sharp and blurs the background only when Photos displays it, so a loaded or converted
HEIC had no blur. The plugin now reads Apple's depth description and renders an approximation of the effect.

```csharp
var document = await HeifDocument.OpenAsync("IMG_6655.HEIC");
using Image portrait = document.RenderPortrait(document.PrimaryImage);                        // photo's own f-number
using Image wide = document.RenderPortrait(document.PrimaryImage, new() { Aperture = 1.4f });

HeifDepthImage depth = document.LoadDepth(document.PrimaryImage.FindDepthImage()!);
float? aperture = depth.AppleDepth?.SimulatedAperture;
using Image map = depth.ToImage();                                                           // the depth map, greyscale
```

- **Portrait rendering:** `HeifDocument.RenderPortrait` and `HeifPortrait.Render` blur each pixel more the farther it
  is from the focus and the wider the aperture (`HeifPortraitOptions`: f/1.4–f/16, focus point, limits, threads). The
  portrait matte keeps people near the focus sharp; in-focus pixels are copied unchanged. The HDR gain map is not
  attached to the result.
- **Depth description:** `HeifDepthImage.AppleDepth` (or `HeifAppleDepthInfo.FromXmp`) reads the declared kind, value
  range, `SimulatedAperture`, raw calibration and the private rendering parameters; `ToFloat` converts samples to
  disparity in 1/m.
- **Depth map image:** `HeifDepthImage.ToImage` shows the samples as a greyscale image.
- **Mattes:** `HeifAppleAuxiliaryTypes` names Apple's portrait and semantic mattes; `HeifItemInfo.FindAuxiliaryImage`
  and `FindDepthImage` find them.
- **Not identical to Photos:** Apple's algorithm is private. Fitted to four photos exported by Photos, the background
  PSNR rose from 21–28 dB without blur to 33–40 dB with the subject unchanged. Bokeh shapes, highlights and Portrait
  Lighting are not reproduced. 12 MP renders in about 0.3 s, 24 MP in about 0.5 s (i5-12500H).

[Depth samples](../decoding.md#depth-samples) · [Portrait effect](../decoding.md#portrait-effect)

**Validation:** Release build without warnings; 513 tests passed (one optional Kodak measurement skipped), including
Oracle and Local checks on eight Portrait photos and four Photos exports. Synthetic tests cover XMP parsing,
bit-exact subject pixels, background detail decreasing as the aperture opens, identical output for any thread count,
limits, cancellation and corrupt depth. ARM64 and the Native AOT package consumer were not rerun for this release.
