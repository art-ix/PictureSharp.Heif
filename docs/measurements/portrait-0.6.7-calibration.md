# Portrait 0.6.7 — E2 calibration and foreground correction

The E1 reference stays immutable. These measurements use the same 15 original / 66 export pairs; they are
additional screening tools, not replacements for the native-render quality gates in [E1](portrait-0.6.7.md).

## Radius probe

`PortraitDiscProbe` is independent of the renderer's depth model, matte protection and pyramid. It evaluates a
constant antialiased disc in linear sRGB: `weight = clamp(radius - distance + 0.5, 0, 1)`. Double-precision row prefix
sums cover the disc interior and individual taps cover its rim. A 16-bit linear-to-byte LUT encodes RGB output.
Tests check a constant at image borders, the zero-radius identity, analytic linear-light mixing, and equivalence
with direct kernel weights on an impulse.

The input is reduced to a longer side of 768 pixels. The radius grid is
`0, .75, 1, 1.25, 1.5, 2, 2.5, 3, 4, 5, 6, 8, 10, 12, 16, 20, 24` pixels. A 32-pixel image margin prevents
kernel clipping; each 32×32 tile contributes 64 RGB samples at stride 4. The E1 tone LUT is applied after blur,
identically for every candidate radius. Fits with no valid E1 LUT remain raw diagnostics and are excluded from
normalized fitting. Each tile reports depth quantiles, original luma standard deviation, mean portrait matte,
best radius, its contiguous near-minimum interval, and RGB MSE. That interval uses best MSE + max(.25, 5% of MSE);
it is a sensitivity range, not a statistical confidence interval.

Of 23,232 tiles, 16,036 have `ok` status. Others are mixed depth (4,941), low texture (1,574), ambiguous radius
(469), or at the radius limit (212). A homogeneous tile requires depth q90−q10 ≤ max(.01, 6% of the map's range)
and luma standard deviation ≥4. More than four grid steps of near-minimum ambiguity is rejected.
14,572 normalized tiles belong to unedited scenes; 6,527 background tiles support the global model experiment.

These are **equivalent radii after downsampling**. Resizing gamma-encoded photographs, mixed depth, occlusion,
semantic protection, texture restoration and a kernel different from a disc can bias the result. A fitted radius
inside a person matte describes the composite appearance, not an isolated optical point-spread function.

## Aperture and focal length

Across five unedited multi-aperture scenes, 2,716 of 2,717 tile series decrease monotonically within .25 px as the
f-number grows. Fits to at least three visibly blurred samples (r≥1 px) give these apparent exponents in r∝N^-q:

| Scene | Series | Median q | q10–q90 |
| --- | --- | --- | --- |
| ogrod_4x | 509 | .886 | .781–.998 |
| rynek_2x | 807 | .856 | .627–1.014 |
| selfie | 301 | .881 | .785–.957 |
| slupek_1x | 628 | .933 | .919–.991 |
| swiatla_2x | 472 | .786 | .652–.937 |

The relationship is approximately inverse aperture, with a flatter measured curve in this probe. Quantization,
small-radius filtering and saturation contribute; the data do not justify declaring an exact universal exponent.
The apparent kernel broadening also scales with focal ratio, but the lenses use different scenes and the front
camera has only one photograph, so focal and resolution effects are not independently identified.

A background-only robust fit of

`r = clamp(768 · K · phi^p · ((4.5/N)^q · |D-Df| - zone), 0, 19.2)`

gives K=.004553, p=1.858, q=.879, zone=.00729. `Df` for this experiment comes from the original's depth under the
fixed E1 sharp patch; it is not fitted to blur on a held-out scene. Scene-held-out mean absolute radius error is
1.046 px versus 1.226 px for the original model/focus and 1.166 px with only the sharp-patch focus substituted.
The global fit worsens held-out selfie (1.086→2.727 px), rzeka (0.513→1.742), basen (0.318→0.590) and liście 1×
(0.169→0.793). **It is not applied to production.** It does not pass the requirement against individual regressions,
and a native image comparison remains necessary even for a better radius fit.

## Focus evidence

[focus.tsv](portrait-0.6.7-focus.tsv) compares the current focus, a 5×5 point inside E1's fixed sharp patch, and
that patch's depth q10/median/q90. Several discrepancies are large: liście 1× .355→.696, liście 2× .291→.764,
słupek .820→1.175, selfie 2.123→2.357. These patches are chosen independently of a candidate renderer, but are
manual calibration evidence, not an automatic focus algorithm.

[focus-minimum.tsv](portrait-0.6.7-focus-minimum.tsv) lists the depth distribution of minimum-radius tiles at the
widest available aperture. Its `confidence` label describes whether the **coarse minimum has a narrow depth span**;
it does not confirm the true Apple focus. Broad plateaus remain ambiguous. Thin leaves and the post can be excluded
by the homogeneous-tile filter, so a narrow minimum can even select a different plane: for these cases use the local
sharp-patch evidence, not the coarse global minimum. The edited boat-refocus series has no reliable normalization
and supplies no normalized minimum-radius estimate.

The current `FocusPoint` already reaches the measured person-refocus plane to the available precision; there is
no demonstrated need for `FocusDisparity`. Default focus selection is still unresolved. A separate metadata audit
found an EXIF SubjectArea in all 15 originals and no SubjectDistance. SubjectArea is in stored sensor coordinates;
container rotation/mirror/crop and possible resizing must be accounted for before using it as a display-space focus.
Its coordinates and raw EXIF are private diagnostics under `artifacts/portrait-focus-metadata/`, not public outputs.

The subsequent [native capture-focus experiment](portrait-0.6.7-capture-focus.md) validates the coordinate mapping
but rejects unconditional use of the SubjectArea centre: 14 pairs exceed the regional regression limit. Focus and
radius need joint calibration; the production default is unchanged.

## Foreground under a person matte

For rynek/chłopiec at f/2.8 the median effective foreground disc is .75 px, versus a geometric prediction of
1.73 px in probe coordinates. Other person/refocus scenes show the same direction. On three held-out scenes,
a fixed half-radius proposal changes foreground radius MAE as follows:

| Held-out scene | Tiles | Original MAE | Half-radius MAE |
| --- | --- | --- | --- |
| ogrod_4x | 200 | 2.976 | .446 |
| rynek_2x | 790 | 2.815 | .595 |
| swiatla_2x | 514 | 4.238 | 1.024 |

The first native experiment applied that reduction at every aperture. It improved refocus subject PSNR but
introduced a 5.29 dB subject regression and 4.99 dB edge regression at f/16 in swiatla. That experiment is rejected.
The current correction is restricted to wide apertures: half nominal radius at f≤2.8, linearly fading to no
correction by f/8, weighted by matte strength and only in front of the focus. The original geometric radius
continues to determine protection and convolution weights; otherwise protection would apply the change twice.
Unmasked rendering and f/8/f/16 are checked byte-identical to the original implementation on a synthetic refocus.
The thread-count comparison and existing limit/cancellation tests also run.

The native corpus comparison is the final gate. For clean pairs the test now rejects >.5 dB regression separately
for background, subject, edges, hair and focused ROI. Edited pairs keep background/focus diagnostics because their
colour edits prevent treating all regions as clean references. Public API and the frozen model remain unchanged.

## Accepted wide-aperture candidate: native results

The final 66-pair run passed the stronger regional regression gate. The frozen E1 protocol, ROIs, tone LUTs,
focus settings and pixel inputs are unchanged. For 55 clean pairs:

| Region / cohort | E1 PSNR | Candidate PSNR | Delta |
| --- | --- | --- | --- |
| Background, all clean | 26.444039 | 26.444164 | +.000125 dB |
| Subject, clean refocus | 27.715956 | 30.461159 | +2.745202 dB |
| Edge, clean refocus | 24.067889 | 26.222633 | +2.154743 dB |
| Focused patch, clean refocus | 43.245471 | 43.245471 | unchanged |

In rynek/chłopiec at f/2.8, subject PSNR is 31.856538→39.457346 dB (+7.600808). The worst clean per-pair
regressions are background −.000123 dB, subject −.114111, edge −.065659, hair −.028394, and focused patch zero.
Background is effectively unchanged; the gain is predominantly the foreground people and their edges. This does
not solve the remaining default-focus problems or meet the full 0.6.7 DoD.

[Candidate per-export results](portrait-0.6.7-foreground-candidate.tsv),
[summary](portrait-0.6.7-foreground-candidate-summary.tsv), and
[source/checksum provenance](portrait-0.6.7-foreground-candidate.json) are retained separately from E1.
Tests: 29 portrait passes with two intentional opt-in skips, 13 infrastructure/calibration passes with three
opt-in skips, four architecture passes, and one public-API snapshot pass. No public API changes.

## Default focus range

The second accepted E3 change replaces the default single focus value with a disparity range up to the subject's
nearest parts; explicit `FocusPoint` keeps one plane. Leaf test shots are reported as a separate low-light cohort
by the user's decision. Results and the cohort revision: [portrait-0.6.7-focus-range.md](portrait-0.6.7-focus-range.md).

## Reproduce

The measurements are opt-in, local and create their tables once; existing measurement tables are not overwritten.
To investigate a changed protocol, use a separately named revision, preserving E1 and these raw tables.

```bash
PICTURESHARP_HEIF_RADIUS=1 PICTURESHARP_HEIF_REND=1 dotnet test -c Release \
  --treenode-filter "/*/*/PortraitCalibrationTests/*"
PICTURESHARP_HEIF_FOCUS=1 dotnet test -c Release \
  --treenode-filter "/*/*/PortraitCalibrationTests/Measure_FocusCandidatesFromSharpRegions"
python3 scripts/analyze-portrait-calibration.py
```

The analysis script needs NumPy/SciPy (the recorded run used 2.4.4/1.17.1). They are analysis dependencies only;
the plugin still depends solely on PictureSharp and the BCL. Reproducing the five analysis TSVs in a separate
output tree gave byte-identical results. The [JSON](portrait-0.6.7-radius-analysis.json) records protocol and hashes.
The [REND inventory](portrait-0.6.7-rendering-parameters.tsv) is interpreted, with confidence levels, in
[portrait-0.6.7-rendering-parameters.md](portrait-0.6.7-rendering-parameters.md).
