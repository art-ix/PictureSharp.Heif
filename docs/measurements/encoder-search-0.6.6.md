# Encoder search experiments (2026-10-19)

Anchor: commit `e076511`, with the same encoding decisions as 0.6.5. Measurements use the existing 24-image Kodak
suite at Quality 30, 45, 60 and 75, default adaptive quantization and tile size, one encoding thread. Each point has
one warm-up followed by one timed encode. Decoding and quality measurement are outside the timer. Full-photo speed
checks use more repetitions. No new corpus was downloaded.

RGB PSNR and luma SSIM are measured against the input. SSIM uses 8×8 windows with stride 4 and is expressed as
−10 log10(1 − SSIM), matching the existing measurement tests. BD-rate uses cubic interpolation of log bytes against
each quality metric over the common quality interval. A positive value means larger files at equal measured
quality, not that the image loses that percentage of quality. Kodak results do not replace visual review.

Reproduce each revision into a separate output directory, then run sequentially to avoid CPU contention:

```bash
dotnet build benchmarks/PictureSharp.Heif.Speed -c Release -o /tmp/heif-probe
dotnet /tmp/heif-probe/PictureSharp.Heif.Speed.dll quality tests/assets/external/kodak/kodim*.png --runs 1 --threads 1 > /tmp/quality.tsv
python3 scripts/compare-encoder-quality.py baseline.tsv candidate.tsv
```

The `quality` command also accepts other image inputs. Its reports contain measurements only, no source pixels or
metadata. The comparison requires the same images and four matching quality points in both reports.

## Explored changes

- Two SATD candidates at every block size, plus MPM candidates only when their SATD cost is at most 1.5 times the
  best preselected cost. Four-image screen (Kodak 01/05/13/23): mean 1.161× speed, +0.221% PSNR BD-rate,
  +0.323% SSIM BD-rate. This screen is not a full-corpus quality result.
- The above plus ordinary quantization during luma mode selection, RDOQ for the selected mode and its transform
  tree, and shifts/masks for power-of-two indexing in RDOQ. All 24 images: mean 1.342× speed, +0.844% PSNR BD-rate,
  +1.117% SSIM BD-rate. Worst same-setting RGB PSNR change: −0.115 dB.
- Also skipping NxN on residual-free 8×8 leaves: rejected. The four-image screen improved mean speed only from
  1.347× to 1.365×, while PSNR BD-rate increased from +0.765% to +0.920%.

## Selected implementation

One SATD candidate plus competitive MPMs, ordinary quantization for luma mode trials, full RDOQ for the winner
and residual-tree search. Chroma continues to use RDOQ. Intermediate reconstruction copies are skipped when the
winning mode will be recoded. RDOQ power-of-two coordinate division uses masks and shifts (identical arithmetic).

All 24 Kodak images / 96 quality points:

| Measure | Result |
| --- | ---: |
| Mean per-image speedup | 1.411× (29.1% less time) |
| Mean RGB PSNR BD-rate | +0.949% |
| Mean luma SSIM BD-rate | +1.262% |
| Worst per-image PSNR BD-rate | +1.661% |
| Worst per-image SSIM BD-rate | +1.865% |
| Largest same-setting PSNR drop | 0.131 dB |

Raw reports: `encoder-bdrate-0.6.6-baseline.tsv`, `encoder-bdrate-0.6.6-fast.tsv`; per-image comparisons:
`encoder-bdrate-0.6.6-fast-summary.tsv`. Intermediate two-candidate and screening results are also retained.
The user's revised requirement permits non-identical bytes and a small quality trade-off. The old +0.5% BD-rate
gate is not met; these measurements explicitly quantify the trade-off instead of treating it as a pass.

## Validation

Release build: zero warnings/errors. Targeted encoder, HEIF encoding, thumbnails and SIMD tests: 53/53 both with
hardware intrinsics and with `DOTNET_EnableHWIntrinsic=0`. This includes reconstruction equality, odd sizes,
monochrome, alpha, adaptive quantization, thread-count independence and HM decoding. Added explicit reconstruction
coverage with RDOQ disabled, exercising the retained best-trial copy path.

Nine files (Kodak 01/13/23 × Q30/50/80) have identical SHA-256 between SIMD and scalar encoding, and before/after
replacing the temporary CABAC context heap copy with stack storage. That final allocation change preserves the
quality measurements above. At the end of the encoder experiment, ARM64 and the full release suite had not been rerun; release preparation
validation is recorded separately in the 0.6.6 release notes.

The first libheif test attempt loaded the system library because `LD_LIBRARY_PATH` was relative to the test
process's directory. Using the absolute `reference/tools/lib` path fixes the tool setup; no oracle sources or
binaries were modified.

Oracle suite with the corrected library path: 11/11 passed, including libheif/HM compatibility and the HDR
regression on the private photo set. No skipped oracle tests.

## Full-photo confirmation

Private 12 MP photo, Q50, best of two encodes after warm-up, sequential benchmark processes:

| Measure | e076511 | Optimized |
| --- | ---: | ---: |
| One encoding thread | 30.761 s | 21.828 s (1.409×) |
| All encoding threads | 4.200 s | 2.858 s (1.470×) |
| Allocated memory, one thread | 388.9 MiB | 322.1 MiB (−17.2%) |
| Encoded bytes | 1,773,678 | 1,769,599 |

The allocation reduction is from stack scratch storage in `CodingUnitBits`. The final one-thread time improves
by 29.0%; the original 1.5× single-thread target is not yet reached. This session does not close the 0.6.6 DoD.
Raw data: `speed-encoder-x64-0.6.6.tsv` (numbers only; the private input stays ignored).

## Release decision

On 2026-10-19 the user inspected full-resolution before/after PNGs at Q50 and reported no visible difference,
accepted this encoder trade-off and requested preparation of version 0.6.6 with a local commit and no push.
The original 1.5× and +0.5% gates remain unmet as documented above; ARM64 and decoder memory work are outstanding.

After local preparation, the user authorized push and release publication of version 0.6.6 on 2026-10-19.
