# Portrait 0.6.7 — E5 bokeh and texture

Closed on 2026-10-19. The E1 protocol, ROIs, tone LUTs, focus settings and pixel inputs are unchanged; the frozen
[baseline](portrait-0.6.7.md) is not overwritten. This step follows [matte edges and hair](portrait-0.6.7-edges.md)
(E4). The per-pair gate is unchanged: no pair or region may lose more than 0.5 dB against E1, with only E4's two
exceptions. No new exception was added.

## Metrics fixed before changes

The E5 metrics were fixed before any model change. They use clean pairs with a tone LUT, the background ROI,
normalized values and equal-scene pooling, computed by `scripts/analyze-portrait-bokeh.py` from render dumps
(`PICTURESHARP_HEIF_PORTRAIT_DUMP`).

- **Highlight PSNR:** RGB error on background pixels at or above the reference's 99th luma percentile.
- **Tail:** |log2| of the render/reference ratio of (P99.5 − P50) background luma, i.e. how bright highlights stand
  out.
- **Grain:** |log2| of the render/reference ratio of the background high-pass deviation, from the measurement TSV.

Much of the reference "grain" turned out to be HEVC artifacts of the exports: blocky and directional patterns that
survive downscaling unchanged. The grain level was therefore set from native measurements, not tuned to this metric
(plan 9.1, point 8).

## Changes

All changes are inside `HeifPortraitRenderer` (`.Bokeh.cs` for grain and lights) and `HeifPortrait`, which now
passes ISO and exposure time from EXIF. There is no new public API or dependency. A render still does not depend on
the thread count.

| Change | What it does | Evidence |
| --- | --- | --- |
| Grain | Luma noise from the pixel position (sum of four hashed bytes, 2×2 mean, neighbour correlation ≈ 0.5). Level 0.6·(ISO/64)^0.28 sRGB codes × (1 − protection) × min(1, r / 2 px), added in linear light. | Photos' native high-pass deviation in blurred areas: 0.52–0.66 codes at ISO 50–80, 1.35 at ISO 1250. |
| Daytime point lights | Level-1 texels brighter than 1.25× (fully 2×) the mean of the surrounding 33×33 texels, with luma ≥ 0.25 linear: their light above the surroundings leaves the layers and is scattered as hard discs of their own radius. Light near clipping is scaled by up to 1.5 for discs of 8–24 px. | Drops in the rynek fountain became separate discs; the hard edge is ~6 px against 34 px for gathered layers (synthetic test). |
| Foreground outside the matte | Radius in front of the focus range × 0.5 at f/1.4–f/2.8, fading to × 1 at f/8. People under the matte keep E3's treatment. | E2 tiles in front of the focus: Photos ≈ 0 up to a model radius of ~16 px, then 7–10 px (kąpiel 0.47, rynek refocus 0.60). The swiatla table now looks like Photos'. |
| 2× lens knee | With a focal ratio of 1.2–1.6 from intrinsics (the 2× crop of the main camera), radii beyond L/126 grow at half the rate. | E2 2× tiles: Photos/model ≈ 0.92 up to 32 px, 0.76 above (rynek and swiatla f/1.4: 52–63 px against 62–73; leżak f/4.5: 31.5 against 43). |
| Night lamps | After Apple's highlight-recovery patent [US10992845](https://patents.google.com/patent/US10992845): in night shots (ISO × seconds from 5, fully from 20), background pixels behind the focus that are near white (brightest channel 0.95–0.99, darkest 0.5–0.65) and form a dense clipped core leave the layers. The core test needs ≥ 0.8→0.97 of a 9×9-texel window and ≤ 0.25→0.15 of an 81×81 one, which separates lamps from lit leaves and a lit umbrella. Their light × 6 is scattered as cat's-eye discs: a disc cut by the same disc shifted towards the centre by 0.8·r at the corner. Pixels on a person's outline take less of it. | Photos' lamp bokeh in swiatla: cream-coloured, oval and tilted tangentially (mechanical vignetting). |

Each step passed the full 66-pair gate when it was added. Clean cohort (10 scenes, 52 pairs), normalized PSNR in dB:

| Step | Background | Subject | Edges | Hair | Focused patch |
| --- | ---: | ---: | ---: | ---: | ---: |
| E4 (accepted before) | 30.546 | 34.817 | 29.301 | 29.034 | 42.182 |
| + grain | 30.534 | 34.805 | 29.296 | 29.028 | 42.174 |
| + daytime point lights | 30.589 | 34.800 | 29.431 | 29.280 | 42.245 |
| + clipped point gain | 30.896 | 34.813 | 29.439 | 29.329 | 42.245 |
| + foreground outside the matte | 31.284 | 35.780 | 29.662 | 29.548 | 42.478 |
| + 2× knee | 31.499 | 36.035 | 29.809 | 29.663 | 42.478 |
| + night lamps (final) | 31.515 | 36.025 | 29.757 | 29.598 | 42.478 |

The night lamps change only the night scenes (swiatla, liscie_4x). On those, the per-pixel metric penalises sharp discs
that do not land exactly where Photos puts them, so the last row trades a little edge and hair score on swiatla for
the visual match. That trade was judged on full-resolution boards, as the user decided for night scenes; the gate
still passes without exceptions.

## Rejected variants

- **Global highlight boost** of near-white pixels before blurring: −5 to −15 dB in most pairs. It whitened the
  stadium sky and smeared the lamps.
- **Harder discs for all layers** (finer pyramid levels): better averages, but foliage scenes lost 0.6–1.5 dB where
  the radius is too small.
- **Gain for every detected point:** 56 region-pairs below the gate, because textured highlights gained energy.
- **Foreground scale everywhere, or only outside the matte without the aperture fade:** people in front lost up to
  8 dB, and rynek refocus up to 1.6 dB at f/4.5–f/16.
- **Patent-style boost without the night gate, the behind-focus test or the core test:** it brightened daylight whites,
  the table, the lit umbrella and leaves.

Every variant's source, table and boards are kept privately in `artifacts/portrait-e5/`.

## Results

Clean cohort against E1: background +1.066 dB, subject +3.821, edges +1.665, hair +1.359, focused patch +5.170.
Against E4: +0.969 / +1.208 / +0.456 / +0.564 / +0.297.

The edited and low-light cohorts stay within ±0.1 dB of E4. The exceptions are hair in the edited scenes (−0.10 dB)
and the low-light focused patch (−0.05 dB).

Worst gated per-pair regressions against E1:

| Region | Regression | Pair |
| --- | ---: | --- |
| Background | −0.46 dB | ogród f/4.5 |
| Subject | −0.27 dB | kąpiel f/4.5 |
| Edges | −0.24 dB | ogród f/2.8 |
| Hair | −0.35 dB | rynek refocus fontanna f/4.5 |
| Focused patch | −0.09 dB | basen f/4.5 |

Subject or focused patch worse than the unblurred original: 8 region-pairs, down from 11. The remaining ones are
słupek f/8–f/16, leżak f/4.5 and basen f/4.5.

| E5 metric, clean pairs | E4 | E5 |
| --- | ---: | ---: |
| Highlight PSNR, all | 22.676 | 24.707 |
| Highlight PSNR, f ≤ 2.8 | 19.139 | 21.925 |
| \|log2 tail\| | 0.089 | 0.071 |
| \|log2 grain\| | 0.471 | 0.263 |
| \|log2 grain\|, f ≤ 2.8 | 0.719 | 0.322 |

Held-out validation (wlosy2, wlosy3; iPhone 13 Pro), run once at the end: all 12 pairs pass. Against E1: background
+0.763, subject +4.690, edges +0.885, hair +0.097, focused patch +10.483 dB. The worst gated region is wlosy3 f/1.4
subject at −0.41 dB. No pair is worse than the unblurred original.

Tables: [per export](portrait-0.6.7-bokeh.tsv), [summary](portrait-0.6.7-bokeh-summary.tsv),
[validation](portrait-0.6.7-bokeh-validation.tsv), [validation summary](portrait-0.6.7-bokeh-validation-summary.tsv),
[provenance and hashes](portrait-0.6.7-bokeh.json).

## Test infrastructure

- `PICTURESHARP_HEIF_PORTRAIT_JOBS=N` measures scenes and pairs in parallel. Results are bit-identical; only render
  time and allocations become indicative. A full gate now takes about 1.5 min instead of 3.5–7.
- `PICTURESHARP_HEIF_PORTRAIT_SCENES=a,b` screens selected scenes (reports `*-subset*`) during experiments. Acceptance
  always runs every pair.
- `PortraitBokehTests` cover grain (none in focus, ISO scaling, thread independence) and point lights (hard edge,
  conserved light, more light when clipped).

## DoD follow-up: object portraits (2026-10-19)

The DoD condition "subject not worse than unblurred" failed in 8 region-pairs. Diagnosis with radius and protection
maps:

| Pair | Our render | Photos |
| --- | --- | --- |
| słupek refocus on the far person, f/8 and f/16 | The near post gets 9–10 px | No blur |
| leżak (beer glass) | The glass gets 1–4 px; the default focus range does not cover it | Sharp |
| słupek f/16 | 23 % of the post gets about 1 px | No blur |
| basen | The face has 0.97 protection at a 2 px radius | Sharp |

E2 tiles in front of the focus show that Photos leaves scenery sharp until the f/4.5 model radius reaches a few
pixels. In object portraits that holds at every aperture (słupek: no blur up to Δ ≈ 0.3). With people in the picture,
the threshold varies between scenes (rynek refocus fontanna: blur from ~3 px), so no constant zone fits them: 5 px and
3 px zones cost rynek up to 5 and 3 dB.

**Accepted change:** in object portraits (no person matte), the radius in front of the focus range is reduced by a
zone equal to 5 px at f/4.5 (at 4032 px). Scenes with people are bit-identical.

**Rejected:** a 3 px zone behind the focus. It fixed the glass (+6 dB) but cost the słupek and liscie_1x backgrounds
8–10 dB.

Results: the full gate passes with only E4's exceptions, and validation is bit-identical. The clean subject score rises
by 0.475 dB to +4.295 against E1; the other regions change by less than 0.04 dB. Pairs worse than unblurred fall from
8 to 6: słupek refocus f/8 and f/16 are fixed, with that subject at +7.0 and +9.1 dB against E1. Tables:
[per export](portrait-0.6.7-object-front.tsv), [summary](portrait-0.6.7-object-front-summary.tsv).

Still open:
- leżak f/4.5 (glass; default focus of object portraits);
- słupek f/16 (post at about 1 px);
- basen f/4.5 (face protection below 1);
- rynek refocus chłopiec f/8 (−0.01 dB).

## Open after E5

- **Subject worse than unblurred in 8 region-pairs:** the focus zone and the slope in front of the focus.
- **Strands behind the head (kąpiel):** unresolved since E4.
- **2× knee:** it recognises the lens only by its focal ratio, which is fitted to three iPhone 17 Pro scenes.
- **Night lamps:** at f/1.4 they are slightly smaller and softer than Photos'. They are tuned on one night scene (and
  liscie_4x), so new night photographs are the next check.
- **E6 (performance):** the extra passes are night-only highlight sums and sparse scatter. E7: release validation.
