# Portrait 0.6.7 — capture-focus experiment

The unconditional replacement of default focus by the transformed EXIF `SubjectArea` centre is **rejected**.
It exceeds the existing −0.5 dB regional regression limit in 14 of 66 pairs (33 region checks), including clean
photographs. The production focus policy remains unchanged; the accepted wide-aperture foreground correction stays.
The presence of this EXIF field in every original does not establish that its centre reproduces Photos' portrait focus.

## Candidate and coordinate checks

The experiment changes only null `FocusPoint`: use the `SubjectArea` centre, then the existing median of its 5×5
depth neighbourhood. All 29 explicit refocus pairs retain their original points. The existing radius law, masks,
foreground correction and tone handling remain unchanged. `SubjectArea` size is not used to estimate disparity.

`PortraitCaptureFocus` is a **test-only diagnostic**, not an automatic production feature. It reads TIFF byte
orders II/MM, the EXIF sub-IFD, SHORT `SubjectArea` with 2/3/4 components and SHORT/LONG pixel dimensions. Missing,
truncated, duplicate or invalid required entries yield no candidate. It requires EXIF dimensions to match `ispe`,
then maps pixel coordinates through `irot`, `imir` and integral `clap` in **association order**, before resizing.
Cropped-out points and aspect mismatches are rejected. The private-file experiment supports direct/grid photographs;
derived identity chains are not inferred. Production container validation precedes test property extraction.

Synthetic checks cover every rotation and mirror, transform order, crop rejection, resizing, both TIFF byte orders,
every area shape, truncated input and invalid offsets/counts/coordinates. A separate private audit compares the C#
mapping against an independently implemented mapping on all 15 scenes; every point and display size agrees. The
supplied primary images use rotation 0 or 3 and no primary mirror/crop, so synthetic coverage is necessary for the
remaining transforms. This validates coordinates, rather than the semantic choice of portrait focus.

## Native image results

The candidate renders all 66 images at native resolution. Evaluation retains the frozen E1 input hashes, pixel
inputs, fixed ROI, tone LUTs and 1536-pixel measurement long side. Each clean pair is checked separately in background,
subject, edge, hair and focus regions. Edited pairs qualify only for background/focus checks. Normalized metrics are
used where E1 supplied a valid LUT; otherwise the existing raw diagnostics apply. These examples are normalized:

| Scene / aperture / region | Delta vs E1 | Finding |
| --- | --- | --- |
| lezak_2x / f4.5 / focused patch | −15.607 dB | The capture centre lands in the space behind the foreground objects. |
| basen_4x / f4.5 / subject | −9.920 dB | A point within the person does not preserve the subject with the current model. |
| selfie / f2.8 / hair | −6.779 dB | Moving focus worsens the existing edge/hair problem. |
| liscie_1x / f2.8 / background | −7.135 dB | The focused patch improves by +13.545 dB, but background regresses. |
| liscie_2x / f4.5 / subject | +18.033 dB | Substantial local improvement is insufficient to accept a universal policy. |

The same 14 pairs fail against the previously accepted E3 renderer. All 29 explicit refocus renders remain
byte-identical at native resolution to that renderer. A successful **measurement test** means the experiment was
completed; it does not mean this candidate passed the quality gate. The analysis report explicitly records rejection.

The candidate exposes two separate problems: the recorded centre can miss the intended foreground object, and
changing focus can improve the sharp patch while worsening the background radius. Focus and radius calibration
therefore need a joint experiment. Selecting scene-specific exceptions after seeing these results would not
establish a general solution. E3 remains open; there is still no evidence requiring a public `FocusDisparity` option.

Numeric results: [per-pair regional comparison](portrait-0.6.7-capture-focus.tsv),
[equally weighted scene summaries](portrait-0.6.7-capture-focus-summary.tsv),
[decision and checksums](portrait-0.6.7-capture-focus.json).
Private coordinates, EXIF and detailed input manifests remain under ignored `artifacts/` and `samples/`.

## Reproduce

With the private 15-scene corpus and frozen E1 manifest present, first obtain accepted-render hashes with
`HeifPortraitLocalTests.PrivateSamples_AgreeWithFrozenPortraitBaseline` if the local current manifest is absent.
The experiment derives its points from the original files; an independent private mapping, when available, is also
checked. Then run:

```bash
PICTURESHARP_HEIF_CAPTURE_FOCUS=1 dotnet test -c Release \
  --treenode-filter "/*/*/PortraitCalibrationTests/Measure_CaptureFocusCandidate"
python3 scripts/analyze-portrait-capture-focus.py --output /tmp/portrait-capture-review
```

The analysis uses Python's standard library. It verifies pair sets, native refocus hashes, unchanged inputs and the
renderer provenance, then writes new tables. It refuses to overwrite existing reports; use another output directory.
The frozen baseline is never a destination for capture-focus experiments.
