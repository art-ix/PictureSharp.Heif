# Portrait 0.6.7 — E0/E1 measurement protocol

The reference is the portrait model from `v0.6.6` (`f22ee6ea1672059df7bedb2160b67515c12cf419`).
`HeifPortrait`, its options, the renderer and its row scheduler are identical between `v0.6.5` and `v0.6.6`.
`HeifDocument`, `HeifDepthImage` and `HeifAppleDepthInfo` also have no changes between these tags.
The tests keep a separate, namespace-adapted copy of these four rendering files under
`tests/PictureSharp.Heif.Tests/Portrait/BaselineV066/`. `source.json` pins the original and adapted SHA-256 values.
This is test-only reference code from this repository, outside the production assembly; do not tune it.

## Inputs and privacy

The catalogue scans only `samples/portrait/<scene>/`. Original names and aperture/refocus exports are parsed
case-insensitively, with duplicate logical variants rejected. Unrecognized HEICs and ambiguous originals fail the
scan. Movies, descriptions and local manifests are listed as ignored. The existing case-only pair in `portret_4x`
has an explicit mapping: `IMG_8083.HEIC` is the original and `IMG_8083.heic` is edited export `e1`, f/4.5.
No input photographs are renamed or modified. The old root-level 6854 and 6986 pairs are excluded.

The private frozen manifest is `samples/portrait/portrait-0.6.7-baseline-inputs.json` (ignored by Git).
It contains relative filenames, input checksums, ROI/focus definitions, tone LUTs, auxiliary and decoded pixel
checksums, native render checksums and measurements. Keep a private backup of this file with the photographs;
without it the regression test is skipped, not passed. Public reports contain scene labels, camera/lens information,
numeric measurements and protocol checksums; no image pixels, focus coordinates or GPS metadata.
Camera model and physical focal length use the public EXIF API. Equivalent focal length is read from EXIF tag
`0xa405` by a small test helper, because the minimum core package does not expose this tag as a typed property.

The catalogue audit loads each original, depth map and available portrait/hair/sky mattes, checks their display
proportions and the photograph's orientation, and checks the declared display size of every export. The measurement
run additionally decodes every export and checks its dimensions. These checks do not prove subpixel registration.
Originals, depth and mattes remain in display orientation after the plugin's container transforms.

## Fixed protocol: `portrait-e1-v1`

- Decode with PictureSharp 0.7.6 and `HeifFormat`, `ApplyExifOrientation=true`, `LoadGainMap=false`, no target size
  and the default colour conversion. Render at native size, one thread, explicit aperture from the filename.
- Default variants retain the renderer's default focus. Refocus uses a manually selected point on the target named
  in `opis.txt`; it is **provisional until E2**, not an inferred Apple focus disparity. Edited exports are separate.
- Measure at a common longer side of 1536 pixels (never upscale), using PictureSharp's default resize after
  native rendering. Both inputs and every renderer use the same reduction. These are screening measurements;
  the texture and edge values must not be interpreted as full-resolution measurements or performance benchmarks.
- Resample portrait/hair masks bilinearly. Background is portrait matte <26; subject is >230. The hair region has
  hair matte >0 and portrait matte <230. The edge band has Manhattan distance 2–24 from the 50% matte boundary,
  scaled from a longer side of 4032. Missing masks produce absent regions, not a synthetic full-frame mask.
- For object scenes, including the false person matte in `liscie_2x`, local manual rectangles define representative
  subject and background regions. The false matte is still passed to the renderer and can be evaluated against
  the independent background ROI. The focused ROI is a separate, fixed sharp patch for each focus variant.
- RGB MSE counts all three channels equally, ignores alpha, and gives PSNR = `10 log10(65025 / MSE)`.
  SSIM uses BT.601 luma, 8×8 nonoverlapping windows, population variance, C1=6.5025 and C2=58.5225.
  Every pixel in a window must belong to the region. Report pixel and window counts; zero MSE gives `Infinity`,
  an empty region/window set gives `N/A`. Empty required background or focus regions fail the run.
- Tone normalization fits one affine curve per channel using only the original/reference pixels in the fixed
  sharp patch. Reject source or target values outside 4–251; require at least 256 samples/channel and source
  variance ≥16. Reject slopes outside 0.5–2, offsets outside ±64, and sharp-patch RGB RMSE >5. Round and clamp the
  resulting 256-entry LUT. Apply the same cached LUT to the unblurred photograph and every renderer. A rejected fit
  leaves normalized metrics `N/A` with its status and residual; raw measurements remain available. This simple fit
  approximates the export's brightness shift and does not explain its cause or reproduce arbitrary edits.
- The background texture proxy is the population standard deviation of luma minus its 7×7 box average. Only
  neighbourhoods wholly within background contribute. Report original, rendered and reference values, also after
  tone normalization where available. This includes scene detail and is not a pure estimate of sensor noise.
- Aggregate MSE over pairs equally within a scene, then equally over scenes, and only then convert to PSNR.
  Report all clean pairs, clean default focus, provisional clean refocus, and edited pairs separately, with groups
  for scene, camera, aperture and focus variant. Every aggregate reports its actual pair and scene coverage.

The TSV has one row per export, with raw and normalized original/render MSE, PSNR and SSIM for all five regions,
plus texture values. The companion summary uses the aggregation above. The JSON companion identifies the protocol,
source, runtime, package, image processing settings, and SHA-256 of both TSVs and the private manifest. Timings and
allocated bytes in the per-pair table are diagnostic, not controlled performance results.

## E1 frozen results (2026-10-19)

The audit found **15 originals and 66 exports**, covering EXIF equivalents 24/48/100/30 mm (1×/2×/4×/front),
7/12/24 MP photographs and five aperture values from f/1.4 to f/16. All original/depth/matte proportions agree;
all photographs decode in TopLeft orientation; all 66 decoded exports match their original's native dimensions.
There are 13 unedited scenes (55 pairs) and two edited scenes (11 pairs).

| Cohort | Scenes / pairs | Background PSNR, raw | Background PSNR, normalized |
| --- | --- | --- | --- |
| All clean pairs | 13 / 55 | 26.758 dB | 26.444 dB |
| Clean, default focus | 13 / 31 | 26.719 dB | 26.407 dB |
| Clean, provisional refocus | 4 / 24 | 31.766 dB | 31.758 dB |
| Edited | 2 / 11 | 23.005 dB | 25.416 dB (2 scenes / 6 pairs) |

All 55 clean pairs have valid tone support. Five edited `wlosy/refocus_lodz` pairs have a sharp-patch residual
of 6.73–7.35 levels, exceeding the fixed 5-level limit; their normalized metrics are absent. They remain in the raw
report. Twelve object-scene pairs have no applicable edge/hair metric; their entries are `N/A`, not zero.
Every pair has a nonempty independently defined background and focused ROI.

The unblurred original scores 20.041 dB on normalized background for the clean cohort, compared with 26.444 dB
for the old renderer. This is a starting reference, **not improvement of 0.6.7 over 0.6.5**. The focused-region
measurements also expose existing weaknesses: clean default-focus PSNR is 43.352 dB unblurred versus 29.640 dB
rendered after normalization. In particular, the original sharp patches in `liscie_1x`, `liscie_2x`, `slupek_1x`
and `selfie` become noticeably worse under the old focus/radius/matte model. These numbers motivate E2/E3;
they do not pass the version's subject-preservation DoD.

The immutable outputs are:

- [Per-export baseline](portrait-0.6.7-baseline.tsv) — 66 rows, 94 columns.
- [Scene-balanced summaries](portrait-0.6.7-baseline-summary.tsv).
- [Protocol and checksums](portrait-0.6.7-baseline.json).

E1 verification passed on x64: all **66 native render SHA-256 values match** the current unmodified production
renderer, and all per-export measurement fields reproduce exactly (apart from diagnostic time/allocation columns).
Release build: zero warnings/errors. Portrait tests: 29 passed, two intentional opt-in measurement skips;
infrastructure tests: 10 passed. The separate baseline creation test also passed. This does not claim ARM64
verification or completion of the later calibration DoD.

## Freeze and regression

First prepare `artifacts/portrait-0.6.7-roi.json`: an array of scene definitions, with normalized rectangles
`[x,y,width,height]`, optional object `Background`/`Subject` rectangle arrays, and a `Focus` dictionary keyed by
`default` or the exact refocus variant. Each focus entry contains optional `Point: [x,y]`, `Sharp: [x,y,w,h]`, and
`Confidence`. These coordinates are local measurement data. Validate the patches visually and against the tone
preflight **before** freezing; do not select them using a candidate renderer's score.

```bash
# Synthetic tests, then catalogue audit with private, metadata-free previews under artifacts/.
dotnet test -c Release --treenode-filter "/*/*/PortraitInfrastructureTests/*"
dotnet test -c Release --treenode-filter "/*/*/HeifPortraitAuditTests/Audit_PrivateCatalogAndPreviews"

# Optional preflight for the local ROI definitions.
PICTURESHARP_HEIF_PORTRAIT_TONE_AUDIT=1 dotnet test -c Release \
  --treenode-filter "/*/*/HeifPortraitAuditTests/Audit_SharpRoiToneSupport"

# Create once: uses the frozen renderer and refuses to overwrite any baseline artifact.
PICTURESHARP_HEIF_FREEZE_PORTRAIT=1 dotnet test -c Release \
  --treenode-filter "/*/*/HeifPortraitLocalTests/Freeze_Portrait066Baseline"

# E1 verification: compare current native output hashes with all frozen native output hashes.
PICTURESHARP_HEIF_VERIFY_PORTRAIT_FREEZE=1 dotnet test -c Release \
  --treenode-filter "/*/*/HeifPortraitLocalTests/PrivateSamples_AgreeWithFrozenPortraitBaseline"

# Later: current renderer vs fixed baseline. Writes only artifacts/portrait-0.6.7-current*.
dotnet test -c Release --treenode-filter "/*/*/HeifPortraitLocalTests/PrivateSamples_AgreeWithFrozenPortraitBaseline"
```

The ordinary regression rejects changed files, decoded pixels, masks or ROI maps; reuses the frozen LUTs and focus
settings; and rejects a background or focused-region regression >0.5 dB per pair (normalized where valid, raw
otherwise). E1 can equal its reference: strict pooled improvement and preservation vs the unblurred focused subject
are later E3 acceptance gates. A person matte includes people on several depth planes and must not be treated as
an unconditional sharpness requirement during refocus. Edited pairs remain diagnostics, outside the primary DoD.

If E2 changes focus/ROI, keep this baseline and create a separately named protocol revision using the frozen model.
Do not replace the E1 rows, refit the LUT per renderer, or compare results with different masks/processing settings.
