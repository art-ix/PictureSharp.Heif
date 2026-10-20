# PictureSharp.Heif 0.6.7 — portraits, light, and bokeh

Better and faster portrait rendering. Requires PictureSharp 0.7.6 and .NET 10. Public API is unchanged.
Release date: 2026-10-20.

Details, the iPhone / 0.6.6 / 0.6.7 comparison gallery and limitations: [release-0.6.7-details.md](release-0.6.7-details.md).

## Portrait quality

The renderer was calibrated against iPhone Photos exports: 15 originals and 66 exports at f/1.4–f/16, three lenses.

- **Focus range:** the default focus covers more of the subject's depth; an explicit `FocusPoint` still selects one plane.
- **Outlines and hair:** subject and background depth are split along the matte, and strands are cleaned of the
  background behind them.
- **Bokeh:** point highlights get defined discs; night lamps get brighter discs, oval (cat's-eye) near the frame edges.
- **Foreground and texture:** less blur in front of the focus at wide apertures, slower radius growth for the 2× lens,
  ISO-scaled grain in blurred areas.

Against the 0.6.6 renderer (10 scenes, 52 pairs, normalized PSNR): background +1.07 dB, subject about +4.3 dB, edges
+1.67 dB, hair +1.36 dB. No pair regresses by more than 0.5 dB outside documented exceptions.

## Speed and determinism

| Render (12 MP photo resized) | 0.6.7 before optimization, 1 / all threads | 0.6.7, 1 / all threads |
| --- | ---: | ---: |
| 12 MP, f/1.4 | 6071 / 3370 ms | 2525 / 413 ms |
| 48 MP, f/1.4 | 43387 / 32065 ms | 16367 / 2167 ms |

Lights are detected and scattered in parallel, disc rows and layer building use SIMD, buffers come from `ArrayPool`;
allocation drops by 30–60 % in most cases. Renders are bit-identical for any thread count and on x64 (with or without
FMA), Native AOT and ARM64.

## Validation

x64: 731 tests passed, 10 skipped (opt-in tools), Local 12/12 including the 66-pair gate. ARM64: 719 passed,
21 skipped (no Oracle tools on the device). A Native AOT consumer of the package builds without warnings.
Reports: [performance and validation](../measurements/portrait-0.6.7-engineering.md),
[bokeh](../measurements/portrait-0.6.7-bokeh.md), [method and baseline](../measurements/portrait-0.6.7.md).

## Known limitations

The subject still scores worse than the unblurred photo in six region/pair combinations; strands behind the head in
one scene are not preserved; the night-lamp model needs more scenes. No identity with Apple's renderer is claimed.
