# Portrait 0.6.7 — E3 default focus range

Accepted on 2026-10-19. The E1 protocol, ROIs, tone LUTs, focus settings and pixel inputs are unchanged; the frozen
[baseline](portrait-0.6.7.md) is not overwritten. This report covers the second accepted E3 change, after the
[wide-aperture foreground correction](portrait-0.6.7-calibration.md#accepted-wide-aperture-candidate-native-results).

## Change

Without `FocusPoint` the focus is a disparity range `[Df, Dn]` instead of a single value:

| Case | Far end `Df` | Near end `Dn` |
| --- | --- | --- |
| Portrait matte (value ≥128 on at least max(16, 0.1 %) depth samples) | median under the matte (as before) | 97.5th percentile under the matte |
| No matte, or too few matte samples | 90th scene percentile (as before) | 99.5th scene percentile |
| Explicit `FocusPoint` | 5×5 median (as before) | equal to `Df` |

`Dn` lies 95 % of the way from `Df` to the top of the distribution; the maximum is never a candidate, so a few
outlying samples cannot widen the range. The distance used by the unchanged radius law is `Df − D` behind the range
and `max(0, D − Dn)` in front of it. Scale, focal exponent, aperture law, zone and the foreground correction are
unchanged; there is no new public API, dependency or use of EXIF.

The variant was selected in a scene-held-out screening of the E2 tiles
([joint holdout](portrait-0.6.7-joint-holdout.tsv), [summary](portrait-0.6.7-joint-summary.tsv),
[JSON](portrait-0.6.7-joint.json), `scripts/analyze-portrait-joint.py`): mean held-out radius error 1.0787 px versus
1.0931 px for the previous renderer, worst scene +0.0006 px. Refitting scale/focal/aperture/zone or SubjectArea
focus regressed single scenes by 2.2–3.3 px and was not used. The screening is exploratory (model family chosen
while looking at the data; the selfie is the only front-camera scene), so the native gate below decides.

## Cohort revision

The three leaf scenes (`liscie_1x/2x/4x`) are test shots of one branch in poor light. After the candidate's results
were known, the user decided on 2026-10-19 to report them as a separate `low-light-test` cohort, like the edited
scenes, outside the clean aggregates. This is a protocol revision made after seeing results and is recorded as
such. Every leaf pair still passes the per-pair −0.5 dB gate on all regions. With the old cohort definition the
background mean would be 26.444039 → 26.419239 dB (−0.0248), caused entirely by `liscie_2x`: its background MSE
(≈1170) dominates the equal-scene mean, and with the rejected false matte the scene's 90th percentile lies on the
background, so the focus range spans background and leaves.

## Native results (66 pairs)

Normalized PSNR, equal-scene pooling, versus E1 (the 0.6.5/0.6.6 model):

| Region | Clean, 10 scenes / 52 pairs: E1 | Accepted | Delta |
| --- | ---: | ---: | ---: |
| Background | 30.449052 | 30.541165 | +0.092 dB |
| Subject | 32.204119 | 34.794094 | +2.590 dB |
| Edges | 28.091975 | 29.204454 | +1.112 dB |
| Hair | 28.238679 | 28.644807 | +0.406 dB |
| Focused patch | 37.308082 | 42.098674 | +4.791 dB |

Low-light test cohort (3 scenes, 3 pairs): background 21.661 → 21.586 dB, subject 24.718 → 41.455 dB, focused
patch 24.256 → 42.884 dB. Edited scenes are unchanged by this step.

Worst per-pair regressions versus E1 (clean and low-light pairs): background −0.086 dB (liscie_2x), subject
−0.263 dB (swiatla_2x f/2.8), edges −0.079 dB and hair −0.194 dB (basen_4x), focused patch −0.040 dB (liscie_4x).
All 29 explicit refocus renders are byte-identical to the previous E3 step. The Local test now also asserts that the
pooled clean background beats E1 under the same cohort definition.

Tables: [per export](portrait-0.6.7-focus-range.tsv), [summary](portrait-0.6.7-focus-range-summary.tsv) (cohort
`low-light-test` listed separately), [provenance and hashes](portrait-0.6.7-focus-range.json).

## Tests

Synthetic tests in `HeifPortraitTests` cover: a subject's nearer part kept sharp by the default focus; an explicit
point selecting one depth plane on either side; without a matte a thin nearest object and the middle ground sharp
at f/1.4, f/4.5 and f/16 with the far ground blurred; a matte too small to describe a subject; 0.25 % of nearer
samples not moving the range; `TargetSize`; and thread-count independence for a matte-less scene. Four of these fail
on the previous E3 renderer. The 66-pair gate passes; other tests 724 passed, 9 skipped (Oracle tools need
`LD_LIBRARY_PATH=reference/tools/lib`).

## Remaining

- "Subject not worse than unblurred" still fails on six clean pairs (leżak f/4.5, słupek f/16 and its refocus
  f/8/f/16, stadion f/4.5, rynek refocus f/8 marginally); a false or rejected matte (liscie_2x) is not detected.
- Visual review of rynek f/2.8 shows Apple's background as lens-like blur: water droplets and bright edges become
  distinct bright discs, while ours is a smooth, low-contrast blur. Highlight boost, a hard-edged disc profile and
  grain are E5 work; this step does not address them.
