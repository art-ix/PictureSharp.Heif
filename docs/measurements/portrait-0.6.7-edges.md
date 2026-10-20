# Portrait 0.6.7 — E4 matte edges and hair

Accepted on 2026-10-19. The E1 protocol, ROIs, tone LUTs, focus settings and pixel inputs are unchanged; the frozen
[baseline](portrait-0.6.7.md) is not overwritten. This step follows the accepted
[default focus range](portrait-0.6.7-focus-range.md) (E3).

## Problem

The depth map is about five times coarser than the photograph and the portrait matte, so near a person's outline a
depth sample mixes the person and the background. In E3 the radius came from that mixed depth: hoods and hair showed
a stair-stepped edge and a light rim, and partly matted pixels were blurred or kept depending on the mix rather than on
the matte. Photos keeps a smooth matte edge with single hairs.

## Change

All of it is inside `HeifPortraitRenderer`; there is no new public API, dependency or EXIF use, and scenes without a
matte render bit for bit as before.

1. **Depth split.** For every depth sample, matte-weighted means within 7×7 samples give a subject disparity
   (weight m²) and a background disparity (weight (1 − m)²). Inside the person the sample's own value stays; the
   estimate takes over towards the outline. The background estimate is used only next to the matte. Per pixel the
   radius is interpolated between the background and subject radii by the matte value, and protection follows the
   subject radius. A false matte on the background therefore gets the background's depth and is blurred.
2. **Unmixing of strands.** A partly protected pixel mixes a hair with the sharp background behind it. Its
   background share is replaced by the subject colour nearby: I + (1 − p)(F − B), with F and B the subject- and
   background-weighted means in a 3 px disc.
3. **Outline.** Unmixing is limited to strands: its strength is clamp((0.5 − Ws) / 0.25, 0, 1), with Ws the mean
   protection in that disc (about one half on a solid outline, little around a strand). On a solid outline, once the
   radius exceeds the matte fade start (4 px at 4032 px), the subject side is softened towards the subject-only mean in
   a 2 px disc. Photos softens these outlines at wide apertures too, and a crisp outline was the main loss on the
   validation scenes.

## Variants and selection

Variants were measured on the 66 main pairs. Several were rejected:

- Smoothing depth everywhere: a thin object was lost.
- Protection taken from the mixed radius: regressions at f/1.4.
- Matte-weighted strand depth (v5, v6).
- Colour-guided joint bilateral upsampling of the disparity (v7): it does not separate wet strands behind the head
  (`kapiel_2x`) from a child's hair in front of a far background of similar colour (`rynek_2x refocus_fontanna`).
- Outline variants with no softening (v8) or 1 px softening (v9r1): weaker than 2 px on edges and hair.

The thresholds 0.25/0.5 and the softening rule were fixed before measuring; only the disc radius (1 or 2 px) was
chosen from the main-pair results. Tables of every variant are kept privately in `artifacts/portrait-e4/`.

## Gate exceptions (user decisions, 2026-10-19)

- **Edge and hair bands at f/8–f/16.** Fully protecting partly matted pixels at narrow apertures loses up to 1 dB
  against E1. E1 kept these pixels blurred only because their mixed depth produced a large radius. Side-by-side
  boards of the affected pairs (`rynek_2x` refocus f/8 and f/16, `selfie` f/16, `swiatla_2x` f/16) were judged
  visually.
- **`kapiel_2x` default f/4.5, edge −0.56 dB.** Wet strands behind the ear lie farther than the face but share its
  coarse depth, so they stay sharp while Photos blurs them. None of the depth sources tried separated them from strands
  in front of a far background without regressing other scenes.

Both exceptions are encoded in `HeifPortraitLocalTests.EdgeBandException`. Every other region of every pair stays
under the −0.5 dB limit.

## Native results (66 pairs)

Normalized PSNR, equal-scene pooling. The clean cohort has 10 scenes and 52 pairs.

| Region | E1 | E3 (accepted before) | E4 | E4 − E1 | E4 − E3 |
| --- | ---: | ---: | ---: | ---: | ---: |
| Background | 30.449 | 30.541 | 30.546 | +0.097 dB | +0.005 dB |
| Subject | 32.204 | 34.794 | 34.817 | +2.613 dB | +0.023 dB |
| Edges | 28.092 | 29.204 | 29.301 | +1.209 dB | +0.096 dB |
| Hair | 28.239 | 28.645 | 29.034 | +0.795 dB | +0.389 dB |
| Focused patch | 37.308 | 42.099 | 42.182 | +4.874 dB | +0.083 dB |

- **Edited scenes** (background and focus in the gate, other regions reported): edges +0.343 dB and hair +0.641 dB
  against E3, the remaining regions within ±0.02 dB.
- **Low-light test cohort:** unchanged.
- **Worst gated per-pair regressions against E1:**
  - background −0.12 dB (`rynek_2x refocus_fontanna` f/4.5);
  - subject −0.27 dB (`kapiel_2x` f/4.5);
  - edges −0.28 dB (`ogrod_4x` f/1.4);
  - hair −0.17 dB (`rynek_2x refocus_fontanna` f/4.5);
  - focused patch −0.05 dB (`basen_4x` f/4.5).
- **Subject or focused patch worse than the unblurred original:** 11 region-pairs, down from 13 in E3. The remaining
  ones are `slupek_1x` f/8–f/16, `lezak_2x` f/4.5, `basen_4x` f/4.5 and `rynek_2x refocus_fontanna` f/1.4–f/4.5.
  This DoD condition stays open; it comes from the focus zone and the slope in front of the focus, not from the matte.

Tables: [per export](portrait-0.6.7-edges.tsv), [summary](portrait-0.6.7-edges-summary.tsv),
[provenance and hashes](portrait-0.6.7-edges.json).

## Held-out validation (12 pairs)

The user added two scenes from an iPhone 13 Pro (iOS 17, REND version 6):

- `wlosy2`: front TrueDepth selfie with wind-blown curly hair against the sky, f/1.4–f/16.
- `wlosy3`: 3× tele, backlit flyaway hair, f/1.4–f/16 plus a refocus on a palm at f/2.8 and f/4.5.

They live in `samples/portrait-validation/`, which calibration tools do not scan. They have their own frozen 0.6.6
baseline ([TSV](portrait-0.6.7-validation-baseline.tsv), [summary](portrait-0.6.7-validation-baseline-summary.tsv),
[JSON](portrait-0.6.7-validation-baseline.json)) and their own Local test with the same per-pair gate.

Earlier variants (v4–v7) were run on these scenes to diagnose a subject loss at `wlosy3` f/1.4. The loss was the crisp
1–3 px outline described above; boards are kept privately in `artifacts/portrait-e4/val-wlosy3-f1.4/`. The outline
change was then developed on the main pairs only, and the selected variant was run on validation once.

| Region | E1 | E3 | E4 | E4 − E1 |
| --- | ---: | ---: | ---: | ---: |
| Background | 35.089 | 35.102 | 35.112 | +0.023 dB |
| Subject | 33.647 | 38.367 | 38.395 | +4.747 dB |
| Edges | 28.921 | 29.892 | 29.966 | +1.045 dB |
| Hair | 32.530 | 32.577 | 32.620 | +0.090 dB |
| Focused patch | 35.062 | 45.545 | 45.545 | +10.483 dB |

All 12 pairs pass the gate. The worst gated region is `wlosy3` f/1.4 subject at −0.30 dB; before the outline change
v4 gave −0.86 dB there. With two scenes this is a sanity check, not a statistical result.

Tables: [per export](portrait-0.6.7-edges-validation.tsv), [summary](portrait-0.6.7-edges-validation-summary.tsv).

## Open after E4

- Strands behind the head take the head's depth (`kapiel_2x`).
- Subject worse than unblurred in 11 region-pairs (focus zone and foreground slope).
- E5: bokeh shape, highlights and grain.
- E6: performance; the depth split and the per-pixel discs on the outline add work only near the matte.
- E7: release validation.
