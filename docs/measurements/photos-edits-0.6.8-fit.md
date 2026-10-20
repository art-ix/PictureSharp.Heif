# Photos edits 0.6.8 — first fits from single-adjustment pairs

The analysis behind `HeifPhotosEdit` (2026-10-20). The renderer uses the fitted curves averaged over the scenes
(`scripts/make-photos-tables.py` generates `Codecs/HeifPhotosTables.cs`). Reproduce the fit with

```bash
python3 scripts/fit-photos-edits.py samples/edit/street samples/edit/stadion samples/edit/perla
```

(about 15 minutes on 16 threads; it decodes with the oracle `heif-dec` into the git-ignored
`artifacts/photos-edits-fit/` cache). Numbers only, no pixels:
[per-pair residuals](photos-edits-0.6.8-fit.tsv), [summary](photos-edits-0.6.8-summary.tsv),
[fitted parameters](photos-edits-0.6.8-curves.tsv), [model selection](photos-edits-0.6.8-select.tsv),
[all candidates](photos-edits-0.6.8-candidates.tsv).

## Data

| Scene | Original | Exports | Adjustments and slider values |
| --- | --- | --- | --- |
| `street` (IMG_8113, backlit sunset, 3024×4032 after rotation) | `IMG_8113.HEIC` | 51 + 1 repeat | exposure, brightness, shadows, contrast, black point, saturation: −100/−50/−25/+25/+50/+100; brilliance, highlights, vibrance, warmth, tint, vignette: ±50; sharpness, definition, noise reduction: +50 |
| `stadion` (IMG_7758, night, street lights, 3024×4032) | `IMG_7758.HEIC` | 22 | exposure: −100…+100 (six values); brightness, brilliance, shadows, highlights: ±50/±100 |
| `perla` (IMG_7725, daylight, blue sky, bright can, 4284×5712) | `IMG_7725.HEIC` | 21 | exposure, highlights: −100…+100 (six values); shadows, brightness, contrast: ±50; definition: 25/50/100 |

The analysis is closed on these three scenes (decision of the user, 2026-10-20). The street definition export was
renamed from the misspelt `szczegolowossc50` to `IMG_8113_szczegolowosc50.heic`.

Values come only from `edits.tsv` (regenerated with `scripts/make-edits-tsv.py`). `IMG_8113_nasycenie.heic` and
`IMG_8113_ref.heic` are byte copies of the original and are skipped. `IMG_8113_ekspozycja25_2.heic` is a second,
independent export of exposure +25; it reproduces the first (mean luma −0.54 vs −0.53) and gives the
**export-to-export noise floor: rms8 0.77, rms1 2.18, max 44 levels**. Because a zero-edit export is the original
byte for byte, there is no baseline offset to remove (the 0.6.7 "export offset" does not apply to these pairs).
Originals carry Apple Photographic Styles metadata (`tag:apple.com,2023:photo:metadata:styles`); the exports carry
the `tmap` brand, `clli` and XMP instead. Only the decoded SDR primary image is compared; gain maps are not.

## Method

- **Metric.** `rms8` = RMS over 8×8 block means of the 8-bit sRGB difference (suppresses HEVC noise; floor 0.77).
  `rms1` = full-resolution RMS (used for the spatial filters, floor 2.18). `baseline` = unedited original vs export.
  `3D LUT` = in-sample 32³ RGB lookup table, the best any global per-pixel colour map can do (it overfits a little and
  is not a model).
- **Candidates** (all fitted by weighted least squares on full-resolution pixels, 33-knot piecewise-linear curves):
  `curve_rgb` one tone curve applied to R, G, B in sRGB; `curve_rgb3` separate R, G, B curves; `luma_add` an sRGB
  luma curve whose delta is added to all channels; `luma_ratio_lin` a linear-light gain keyed by luma (hue- and
  ratio-preserving); `curve_rgb_bands` `curve_rgb` plus a gain per Gaussian band (σ = 1…128 px, shared by the
  channels, i.e. sharpening/local contrast); `chroma_srgb`/`chroma_lin` luma curve plus a chroma gain keyed by
  saturation `(max−min)/max`; `matrix_lin`/`gain_lin` 3×3 / diagonal linear-light matrix; `radial_*` a gain keyed by
  the normalised radius (circular = half-diagonal, elliptical = per axis), in linear light or sRGB.
- **Selection.** Per adjustment, the candidate with the lowest checkerboard holdout (fit on alternate 256-px tiles,
  score on the others) over the training pairs of both scenes. Held-out slider values never enter the selection.
- **Slider holdout (fixed in advance).** Parameters are stored relative to the identity, so interpolation is linear.
  `held-out`: ±25 predicted as half of the ±50 fit (training = ±50, ±100). `midpoint`: ±50 predicted as half of the
  ±100 fit (a second holdout; it is the only one on `stadion` for brightness, brilliance, shadows and highlights).
  `antisym`: for ±50-only sliders, each sign predicted by negating the other (tests the sign convention and symmetry;
  the model family was selected on both pairs, a small leak). `transfer`: the same adjustment and value fitted on the
  other scene — a fixed, scene-independent curve would transfer. Single-value sliders have only the tile holdout.
- **Post-hoc (labelled, not a clean holdout).** After seeing exposure ±25, a model "fixed even part + odd part linear
  in v" was tried (`posthoc_evenodd` in the TSV). It is reported only to show that it does not explain the data.

## Colour space and structure the data shows

- **Tone sliders act per channel on sRGB values, not on luminance.** Per-channel curves (`curve_rgb`, `curve_rgb3`)
  beat the hue-preserving `luma_ratio_lin` and `luma_add` for exposure, brightness, shadows, black point, brilliance;
  darkening sliders raise `(max−min)/max` saturation of the darks strongly (black point +50: +0.38 at luma 48–64),
  which a luminance gain cannot do. Contrast is the exception: `luma_add` (luma curve, delta added to all channels)
  wins (holdout 2.52 vs 3.07). Separate R, G, B curves win for exposure, brightness, black point, highlights,
  warmth and tint — the channels respond differently (exposure −100 on `street`: ΔR −23.8, ΔB −18.7), consistent with
  a curve applied in another (linear or camera) space; this is left as a curve per channel for now.
- **Most sliders are global per-pixel maps.** For brightness, contrast, black point, saturation, warmth, tint,
  highlights and the exposure pairs on `stadion`, the 3D LUT bound is 1.1–1.7 (near twice the noise floor) and the
  chosen model is within 0.1–0.9 of it. A Gaussian base layer (local tone mapping keyed by blurred luma, σ 8–512 px)
  was tried during exploration and made every tone slider worse.
- **Colour-dependent response on `perla`.** For exposure and highlights on the daylight scene no candidate comes near
  the 3D-LUT bound (exposure −100: best `luma_ratio_lin` 7.0, `curve_rgb3` 8.6, bound 2.3; highlights −100:
  `curve_rgb3` 14.0, bound 2.1): the response depends on the colour (blue sky vs. the white/red can), not only on the
  channel value or the luma. A global per-pixel map would still work; the per-channel curve does not.
- **Local components.** Brilliance (bound 4.2–4.6), shadows + (bound 2.9, positive values add band gains up to 0.45
  at σ 2–64 px: local contrast), exposure on `street` (bound 2.4–3.2, a smooth spatial residual of ±3 levels from
  bright sky to the dark ground that has the same shape for +25 and −100), vignette (radial by construction) and the
  three filters are not global maps.
- **Spatial filters.** Sharpness +50 is strong and edge-adaptive (thin leaves against the sky become solid dark, grain
  on the asphalt is amplified); the linear band model reaches rms1 13.9 against a baseline of 24.2 (−43 %), so much of it
  is not linear unsharp masking. Noise reduction +50 removes the finest bands (gains −0.29 at σ 1, −0.16 at σ 2) and
  lifts the darkest luma bins by ~5 levels. Definition +50 is a broad local-contrast boost (band gains 0.06–0.16 at σ 2–64) with
  a small tone change (darks −4.5, highlights +7).

## Sign conventions (checked)

| Slider | Positive value | Negative value |
| --- | --- | --- |
| Vignette | darkens corners: linear gain 0.73 at r = 0.7, 0.40 at 0.85, 0.19 at the corner (+50) | brightens corners: 1.07 / 1.56 / 1.70 (−50); centre (r < 0.55) untouched |
| Contrast | S-curve: darks −10.5 at 64, highlights +5.8 at 240 (+25) | flattens: darks lifted (+37.7 at 64), highlights lowered (−39.3 at 240) at −100 |
| Black point | crushes: input ≤ ~30 maps to 0 already at +25 (Δ@32 −28.4) | lifts blacks: +27 at 16 (−25), +61 at 16 (−100) |
| Warmth | warmer: R +7.5, B −9.6 at 128 (+50) | cooler: R −9.8, B +8.5 (−50) |
| Tint | magenta: R +3.1, G −4.4, B +2.8 (+50) | green: R −5.1, G +2.9, B −4.1 (−50) |
| Saturation | chroma gain ×1.27…1.47 (+50), ×2.0 (+100, ×1.43 for already saturated colours) | ×0.45 (−50); −100 gives grey (gain 0.00) |
| Vibrance | favours dull colours: ×1.64 at saturation 0.1, ×1.01 at 0.75 (+50) | ×0.70 at 0.1, ×0.80 at 0.75 (−50) |
| Shadows | lifts darks and mids up to luma ~200 (+49 at 128, +100) | darkens below ~100 (−59 at 64, −100), barely touches highlights |
| Highlights | lifts 128–220 (+5.6 at 192 on `street`, +16.7 on `stadion`, +50) | lowers them (−7.1 / −15.7, −50) |

Exposure is **not monotonic in the slider** for small positive values on either scene: +25 and +50 lower the
highlights (Δ@240 −12.6 and −4.3 on `street`, −18.1 and −20.9 on `stadion`) while mid-tones barely move; only +100
lifts the image. Negative values darken everything and jump already at −25 (`stadion` mean −13.3 at −25, −19.5 at
−50). The repeat export proves this is real, not noise.

## Residuals per pair

rms8 in 8-bit levels; `dY` = mean luma change of the export. Prediction columns use parameters not fitted on that
pair (see Method). Every in-sample fit is closer to the export than the unedited original except noise reduction at
rms8 (2.22 vs 1.87; at full resolution 6.13 vs 8.15, i.e. it gets the fine scale right and adds coarse error).

| adjustment | scene | value | role | dY | baseline | 3D LUT | fit | tiles | held-out (±25 from ±50) | midpoint (±50 from ±100) | antisym | transfer |
|---|---|--:|---|--:|--:|--:|--:|--:|--:|--:|--:|--:|
| black_point | street | -100 | train | 41.09 | 47.11 | 1.63 | 1.70 | 1.71 |  |  |  |  |
| black_point | street | -50 | train | 26.57 | 31.38 | 1.42 | 1.41 | 1.41 |  | 8.15 |  |  |
| black_point | street | -25 | test | 16.81 | 20.45 | 1.31 | 1.21 | 1.21 | 5.04 |  |  |  |
| black_point | street | +25 | test | -14.19 | 16.32 | 1.65 | 1.65 | 1.64 | 6.18 |  |  |  |
| black_point | street | +50 | train | -20.69 | 23.20 | 1.85 | 1.98 | 1.99 |  | 9.55 |  |  |
| black_point | street | +100 | train | -27.15 | 30.10 | 2.08 | 2.43 | 2.46 |  |  |  |  |
| brightness | perla | -50 | train | -20.84 | 23.46 | 1.46 | 3.66 | 3.68 |  |  | 7.80 | 9.44 |
| brightness | perla | +50 | train | 14.81 | 16.50 | 1.32 | 2.13 | 2.16 |  |  | 7.19 | 7.23 |
| brightness | stadion | -100 | train | -53.48 | 60.86 | 1.91 | 4.25 | 4.38 |  |  |  | 7.45 |
| brightness | stadion | -50 | train | -17.68 | 20.66 | 1.10 | 1.97 | 2.01 |  | 10.31 |  | 4.96 |
| brightness | stadion | +50 | train | 13.06 | 14.97 | 1.23 | 1.28 | 1.32 |  | 2.07 |  | 3.87 |
| brightness | stadion | +100 | train | 23.44 | 26.77 | 1.44 | 1.97 | 2.03 |  |  |  | 4.21 |
| brightness | street | -100 | train | -38.49 | 43.68 | 2.46 | 4.25 | 4.18 |  |  |  | 8.30 |
| brightness | street | -50 | train | -13.04 | 15.16 | 1.40 | 2.02 | 2.00 |  | 7.10 |  | 3.15 |
| brightness | street | -25 | test | -5.80 | 6.76 | 1.30 | 1.21 | 1.20 | 1.48 |  |  |  |
| brightness | street | +25 | test | 4.98 | 5.74 | 1.41 | 1.22 | 1.22 | 1.31 |  |  |  |
| brightness | street | +50 | train | 9.20 | 10.48 | 1.52 | 1.50 | 1.49 |  | 1.88 |  | 2.47 |
| brightness | street | +100 | train | 16.74 | 18.87 | 1.77 | 2.02 | 2.02 |  |  |  | 4.26 |
| brilliance | stadion | -100 | train | -32.39 | 37.60 | 7.75 | 10.55 | 10.47 |  |  |  |  |
| brilliance | stadion | -50 | train | -12.95 | 14.20 | 3.19 | 3.72 | 3.72 |  | 6.30 |  | 4.29 |
| brilliance | stadion | +50 | train | 11.71 | 13.38 | 2.99 | 3.54 | 3.56 |  | 3.83 |  | 4.25 |
| brilliance | stadion | +100 | train | 25.25 | 27.70 | 4.53 | 5.83 | 5.91 |  |  |  |  |
| brilliance | street | -50 | train | -10.36 | 11.99 | 4.55 | 4.34 | 4.30 |  |  | 5.25 | 5.31 |
| brilliance | street | +50 | train | 8.30 | 9.81 | 3.84 | 3.82 | 3.80 |  |  | 4.50 | 4.93 |
| contrast | perla | -50 | train | 4.85 | 13.12 | 1.25 | 2.36 | 2.34 |  |  | 3.27 | 4.13 |
| contrast | perla | +50 | train | -6.10 | 12.89 | 1.39 | 2.35 | 2.34 |  |  | 2.95 | 4.25 |
| contrast | street | -100 | train | 16.63 | 26.31 | 2.42 | 3.09 | 3.39 |  |  |  |  |
| contrast | street | -50 | train | 9.23 | 13.20 | 1.63 | 1.91 | 2.00 |  | 2.48 |  | 3.95 |
| contrast | street | -25 | test | 4.74 | 6.51 | 1.48 | 1.23 | 1.25 | 1.23 |  |  |  |
| contrast | street | +25 | test | -4.98 | 6.68 | 1.31 | 1.19 | 1.19 | 1.28 |  |  |  |
| contrast | street | +50 | train | -10.44 | 13.60 | 1.46 | 1.81 | 1.80 |  | 2.59 |  | 2.82 |
| contrast | street | +100 | train | -19.79 | 24.77 | 1.80 | 2.85 | 2.88 |  |  |  |  |
| definition | perla | +25 | test | -0.95 | 2.78 | 2.08 | 1.55 | 1.57 | 1.56 |  |  |  |
| definition | perla | +50 | train | -1.91 | 5.49 | 3.56 | 2.90 | 2.94 |  | 2.91 |  | 3.82 |
| definition | perla | +100 | train | -3.94 | 10.98 | 6.81 | 5.73 | 5.82 |  |  |  |  |
| definition | street | +50 | train | -2.10 | 5.65 | 4.61 | 3.75 | 3.74 |  |  |  | 3.98 |
| exposure | perla | -100 | train | -21.03 | 25.03 | 2.25 | 8.63 | 8.67 |  |  |  | 39.51 |
| exposure | perla | -50 | train | -4.03 | 8.88 | 2.12 | 5.07 | 5.14 |  | 8.83 |  | 30.68 |
| exposure | perla | -25 | test | 2.61 | 6.07 | 1.91 | 3.73 | 3.81 | 6.19 |  |  | 26.47 |
| exposure | perla | +25 | test | 8.49 | 8.97 | 1.78 | 2.80 | 2.84 | 4.70 |  |  | 16.40 |
| exposure | perla | +50 | train | 24.40 | 24.34 | 1.88 | 3.57 | 3.59 |  | 6.21 |  | 28.80 |
| exposure | perla | +100 | train | 54.11 | 54.47 | 2.09 | 7.03 | 7.10 |  |  |  | 33.95 |
| exposure | stadion | -100 | train | -32.51 | 37.02 | 1.41 | 1.79 | 1.75 |  |  |  | 20.69 |
| exposure | stadion | -50 | train | -19.49 | 22.28 | 1.63 | 1.94 | 1.89 |  | 4.39 |  | 18.55 |
| exposure | stadion | -25 | test | -13.29 | 15.52 | 1.67 | 1.89 | 1.92 | 4.75 |  |  | 16.88 |
| exposure | stadion | +25 | test | -2.52 | 4.69 | 1.48 | 1.47 | 1.50 | 3.77 |  |  | 10.63 |
| exposure | stadion | +50 | train | 0.51 | 3.76 | 1.46 | 1.45 | 1.46 |  | 10.22 |  | 19.45 |
| exposure | stadion | +100 | train | 18.75 | 19.30 | 1.65 | 1.89 | 1.94 |  |  |  | 24.56 |
| exposure | street | -100 | train | -23.07 | 25.62 | 2.55 | 4.28 | 4.29 |  |  |  | 13.26 |
| exposure | street | -50 | train | -11.04 | 12.32 | 2.42 | 3.05 | 3.12 |  | 4.94 |  | 9.80 |
| exposure | street | -25 | test | -6.31 | 7.48 | 2.45 | 2.73 | 2.79 | 3.15 |  |  | 8.85 |
| exposure | street | +25 | repeat | -0.54 | 2.90 |  |  |  | 4.40 |  |  | 7.44 |
| exposure | street | +25 | test | -0.53 | 2.90 | 2.51 | 2.70 | 2.77 | 4.38 |  |  | 7.43 |
| exposure | street | +50 | train | 5.48 | 6.14 | 2.67 | 2.88 | 2.90 |  | 7.80 |  | 11.36 |
| exposure | street | +100 | train | 24.32 | 25.85 | 3.16 | 3.62 | 3.56 |  |  |  | 11.74 |
| highlights | perla | -100 | train | -8.36 | 20.82 | 2.07 | 14.02 | 14.15 |  |  |  | 19.82 |
| highlights | perla | -50 | train | -5.22 | 8.82 | 1.39 | 4.90 | 5.14 |  | 5.84 |  | 7.58 |
| highlights | perla | -25 | test | -1.57 | 2.56 | 1.25 | 1.33 | 1.36 | 2.00 |  |  |  |
| highlights | perla | +25 | test | 1.58 | 2.61 | 1.23 | 1.40 | 1.43 | 2.30 |  |  |  |
| highlights | perla | +50 | train | 5.82 | 9.11 | 1.26 | 4.25 | 4.33 |  | 6.52 |  | 7.70 |
| highlights | perla | +100 | train | 3.82 | 8.77 | 1.27 | 5.24 | 5.36 |  |  |  | 7.92 |
| highlights | stadion | -100 | train | -3.01 | 7.59 | 1.24 | 2.64 | 2.63 |  |  |  | 7.03 |
| highlights | stadion | -50 | train | -2.44 | 5.03 | 1.12 | 1.63 | 1.63 |  | 2.34 |  | 3.37 |
| highlights | stadion | +50 | train | 2.99 | 5.82 | 1.10 | 1.76 | 1.81 |  | 4.19 |  | 3.80 |
| highlights | stadion | +100 | train | 1.78 | 3.96 | 1.04 | 1.51 | 1.57 |  |  |  | 3.30 |
| highlights | street | -50 | train | -1.30 | 2.64 | 1.35 | 1.28 | 1.26 |  |  | 1.41 | 2.13 |
| highlights | street | +50 | train | 1.04 | 2.41 | 1.35 | 1.43 | 1.41 |  |  | 1.64 | 2.66 |
| noise_reduction | street | +50 | train | -0.03 | 1.87 | 2.99 | 2.22 | 2.21 |  |  |  |  |
| saturation | street | -100 | train | -1.70 | 11.77 | 0.68 | 2.06 | 2.10 |  |  |  |  |
| saturation | street | -50 | train | -0.98 | 6.40 | 1.07 | 1.44 | 1.46 |  | 1.46 |  |  |
| saturation | street | -25 | test | -0.38 | 3.51 | 1.21 | 1.22 | 1.21 | 1.16 |  |  |  |
| saturation | street | +25 | test | 0.14 | 3.24 | 1.58 | 2.17 | 2.16 | 2.14 |  |  |  |
| saturation | street | +50 | train | 0.11 | 6.44 | 1.97 | 4.02 | 3.99 |  | 4.24 |  |  |
| saturation | street | +100 | train | 0.57 | 12.73 | 2.42 | 4.74 | 4.67 |  |  |  |  |
| shadows | perla | -50 | train | -8.55 | 14.30 | 1.61 | 2.95 | 2.98 |  |  | 9.76 | 4.97 |
| shadows | perla | +50 | train | 7.41 | 8.75 | 2.59 | 2.95 | 3.00 |  |  | 9.80 | 5.67 |
| shadows | stadion | -100 | train | -26.35 | 31.20 | 1.51 | 2.72 | 2.80 |  |  |  | 3.69 |
| shadows | stadion | -50 | train | -13.59 | 16.26 | 1.28 | 5.08 | 5.12 |  | 6.32 |  | 7.40 |
| shadows | stadion | +50 | train | 9.81 | 10.61 | 2.28 | 2.67 | 2.64 |  | 7.60 |  | 3.70 |
| shadows | stadion | +100 | train | 32.58 | 35.71 | 6.36 | 8.04 | 7.92 |  |  |  | 8.98 |
| shadows | street | -100 | train | -24.74 | 28.02 | 2.05 | 2.73 | 2.74 |  |  |  | 3.12 |
| shadows | street | -50 | train | -12.91 | 15.51 | 1.89 | 3.34 | 3.32 |  | 4.16 |  | 5.31 |
| shadows | street | -25 | test | -4.04 | 5.62 | 1.42 | 2.30 | 2.28 | 3.42 |  |  |  |
| shadows | street | +25 | test | 2.20 | 2.63 | 1.55 | 1.28 | 1.28 | 2.21 |  |  |  |
| shadows | street | +50 | train | 7.88 | 8.86 | 3.02 | 2.85 | 2.82 |  | 5.69 |  | 3.26 |
| shadows | street | +100 | train | 24.94 | 28.09 | 7.62 | 7.49 | 7.42 |  |  |  | 8.92 |
| sharpness | street | +50 | train | 0.40 | 9.63 | 10.72 | 7.53 | 7.63 |  |  |  |  |
| tint | street | -50 | train | 0.66 | 3.07 | 1.35 | 1.39 | 1.40 |  |  | 1.63 |  |
| tint | street | +50 | train | -1.31 | 2.77 | 1.38 | 1.37 | 1.38 |  |  | 1.56 |  |
| vibrance | street | -50 | train | -0.57 | 2.37 | 1.29 | 1.48 | 1.49 |  |  | 1.85 |  |
| vibrance | street | +50 | train | 0.32 | 3.21 | 1.65 | 2.17 | 2.18 |  |  | 2.61 |  |
| vignette | street | -50 | train | 2.63 | 7.60 | 5.99 | 2.31 | 2.40 |  |  | 6.44 |  |
| vignette | street | +50 | train | -5.38 | 11.16 | 7.19 | 1.02 | 1.02 |  |  | 6.24 |  |
| warmth | street | -50 | train | -0.26 | 5.47 | 1.54 | 2.23 | 2.24 |  |  | 2.40 |  |
| warmth | street | +50 | train | -0.58 | 5.21 | 1.39 | 1.97 | 1.99 |  |  | 2.15 |  |

## Held-out error and confidence per adjustment

`ratio` = sum of prediction rms8 / sum of baseline rms8 over the predicted pairs (lower is better, 1 = no better than
leaving the photo unedited); `worse` = predicted pairs no closer than the unedited photo.

| Adjustment | Model | In-sample fit vs baseline | Slider holdout (ratio, worse) | Scene transfer (ratio, worse) | Determined from these scenes? | Confidence |
| --- | --- | --- | --- | --- | --- | --- |
| warmth | `curve_rgb3` | 2.10 vs 5.34 | antisym 0.43, 0 | — (one scene) | global white-balance shift; sign and magnitude symmetric | **high** (shape), medium (non-linear in v untested: only ±50) |
| tint | `curve_rgb3` | 1.38 vs 2.92 | antisym 0.55, 0 | — | global; roughly symmetric (baseline 3.07 at −50 vs 2.77 at +50) | **high** (shape), medium (magnitude law) |
| vignette | `radial_lin_circ` | 1.66 vs 9.38 | antisym 0.68, 0 | — | geometric, circular in the half-diagonal; −50 and +50 are not mirror images | **high** for ±50, low between (needs ±25/±100) |
| sharpness | `curve_rgb_bands` | rms1 13.9 vs 24.2 | tiles 7.63 (rms8) | — | global operation, but non-linear/edge-adaptive; the linear model cuts rms1 by 43 % | **low** (model), high (that it is image-independent) |
| noise reduction | `curve_rgb_bands` | rms1 6.13 vs 8.15; rms8 2.22 vs 1.87 | tiles 2.21 | — | global, fine-scale; worse than unedited at rms8 | **low** |
| definition | `curve_rgb_bands` | 3.4–3.8 vs 5.7–6.4 | held-out 0.56, 0; midpoint 0.53, 0 (`perla`) | 0.70, 0 | local contrast + tone; linear in strength on `perla`, moderate transfer | medium |
| brightness | `curve_rgb3` | 2.0–2.9 vs 16.8–30.8 | held-out 0.22, 0; midpoint 0.35, 0; antisym 0.38, 0 (`perla`) | **0.16–0.35, 0** | the curve transfers across a sunset, a night and a daylight scene; non-linear in v (±50 ≠ ½·±100) | **high** for shadows/mid-tones, medium for highlights (192: +22 / +13 / +7 at +50) |
| black point | `curve_rgb3` | 1.73 vs 28.1 | held-out 0.31, 0; midpoint 0.32, 0 | — | clipping point and lift likely depend on the histogram | medium |
| contrast | `luma_add` | 2.0–2.4 vs 13.0–15.2 | held-out 0.19, 0; midpoint 0.19, 0; antisym 0.24, 0 | 0.25–0.32, 0 | luma curve, pivot ~140; transfers between `street` and `perla` with moderate error | medium–high |
| saturation | `chroma_srgb` | 2.61 vs 7.35 | held-out 0.49, 0; midpoint 0.44, 0 | — | chroma gain law clear; 3D-LUT bound 1.49 shows a hue/luma dependence the model lacks | medium |
| vibrance | `chroma_srgb` | 1.82 vs 2.79 | antisym 0.80, 0 | — | saturation-dependent gain; asymmetric in sign (likely skin/hue protection) | low–medium |
| shadows | `curve_rgb_bands` | 2.9–4.6 vs 11.5–23.4 | held-out 0.68, 0; midpoint 0.40–0.52, 0; antisym 0.85, 1 worse (`perla`) | 0.26–0.47, 0 | the negative side transfers well; positive side has local contrast and is weaker than the negative one; ±25 poorly predicted | medium (−), low–medium (+) |
| highlights | `curve_rgb3` | 1.35–5.2 vs 2.5–8.8 | held-out 0.83, 0 (`perla`); antisym 0.60, 0; midpoint 0.60–0.69, 0 | 0.73–0.87 (`stadion`, `perla`), 1.10 / 3 worse (`street`) | magnitude follows the bright area (`street` 2.5, `stadion` 5.6, `perla` 8.8 baseline); +100 weaker than +50 on both `stadion` and `perla`; colour-dependent on `perla` | **low** (scene-adaptive) |
| brilliance | `curve_rgb_bands` | 4.1–5.9 vs 10.9–23.2 | antisym 0.45, 0; midpoint 0.37, 0 | 0.31–0.47, 0 | local tone mapping (3D-LUT bound 4.2–4.6); not a global curve | **low** |
| exposure | `curve_rgb3` | 1.7–5.1 vs 13.4–21.3 | held-out 0.42 (`stadion`), 0.72, 1 worse (`perla`), **0.90, 2 worse** (`street`); midpoint 0.45–0.69 | **0.79–1.08, 21 of 38 transfers (incl. the repeat) worse than unedited** | opposite behaviour by scene: highlight-protecting at small + values at sunset/night, but strong lift on the daylight `perla` (+25: mean +8.5, +100: +54) where −25 barely changes the mean (+2.6); post-hoc even/odd model does not fix `street` (1.00) | **low** |

Summary: **well determined**: warmth, tint, vignette at ±50 (one scene, global operations), brightness and, with
moderate error, contrast (both transfer across the three scenes). **Determined in shape, model inadequate**:
sharpness, noise reduction — global filters, but non-linear; a linear band model is not enough. Definition is medium
(linear in strength on `perla`). **Scene-adaptive, need more scenes**: exposure (opposite behaviour on the daylight
scene; most transfers are worse than leaving the photo unedited), highlights, brilliance, shadows (+), black point,
saturation, vibrance.

## Additional samples needed

1. **More scenes for the adaptive sliders**: at least four more originals spanning histograms (high-key/snow or
   overcast sky, low-key indoor, a portrait with skin, a saturated scene with foliage/flowers, a flat midday scene),
   each with exposure, brilliance, highlights, shadows, contrast, black point at −100/−50/−25/+25/+50/+100. The
   street scene has few bright areas, so highlights there is nearly flat.
2. **Finer slider steps where the response is non-linear**: exposure at ±10, ±15, ±35, ±75 on two scenes (to locate
   the non-monotonic region); brightness ±75; vignette, warmth, tint, vibrance, brilliance at ±25 and ±100 (only ±50
   exists on `street`).
3. **0…100 sliders at more strengths**: sharpness and noise reduction at 25 and 100 (only +50 exists; definition
   has 25/50/100 on `perla`), and a low-light scene for noise reduction (`stadion`).
4. **Repeats**: one more repeat export per scene (e.g. brightness +50) to confirm the noise floor on another scene.
5. **Combinations** (after the single fits): exposure+contrast, brightness+saturation, warmth+vibrance, vignette with
   a tone slider, to establish the order of operations.
6. Optional, to test the colour-space hypothesis: an original in a wide-gamut/neutral grey chart scene with exposure
   ±50 and warmth ±50 (separates per-channel curves in sRGB from operations in linear or camera space).

## Next step for the API design

The prototype shows two kinds of adjustment, and the API should not pretend they are the same:

- **Fixed operations** (warmth, tint, vignette, saturation, brightness, and — once modelled — sharpness, noise
  reduction): a record of slider values (`PhotosAdjustments` with nullable `float` properties in −1…1 or 0…1) and a
  rendering method applying per-channel sRGB curves, chroma gains, a radial linear-light gain and filters in a fixed
  order. Parameters would be tables interpolated in the slider value, like the fitted curves here.
- **Scene-adaptive operations** (exposure, highlights, shadows, brilliance, contrast, black point, vibrance,
  definition): Photos derives their curve from the image. Before exposing them, fit the curve as a function of image
  statistics (luma histogram percentiles, fraction of bright area) across the new scenes, and test it with a
  leave-one-scene-out holdout; exposure needs its own model (it is not a gain).

Concretely, next: (1) collect the samples above; (2) extend `fit-photos-edits.py` with a leave-one-scene-out
predictor whose curve parameters depend on histogram statistics; (3) replace the linear band model for sharpness and
noise reduction with an edge-aware one (e.g. clipped unsharp mask / guided filter) and re-check rms1; (4) only then
fix the slider record, the order of operations (from combined edits) and the rendering method, against the DoD
"closer than unedited on every pair". Claims of matching Photos remain out of scope.
