# PictureSharp.Heif: version history 0.1.0–0.6.0

An archive of closed stages moved from `plan.md` (2026-10-18). The current scope and status are in
[plan.md](../plan.md), release notes in [releases/](releases/). Content preserved without substantive changes.

## DoD of closed versions

- **0.1.0:** project `src/PictureSharp.Heif` and TUnit tests on the PictureSharp 0.6.9 package, an architecture test
  (only `PictureSharp` + BCL, public API only), CI. A box parser with limits and no recursion beyond a set depth;
  `IsSupported` for the `heic`/`heix`/`mif1` brands; `Identify` (dimensions after transforms, alpha, depth, item count
  in `ImageInfo.ContainerItemCount`); `Decode` → `NotSupportedException` until 0.2.0. Parser fuzzing
  (`Category=Slow`), synthetic corpus from `heif-enc`, script `scripts/build-heif-reference.sh`.
- **0.2.0:** YCbCr bit-exact with HM and libde265; RGB matching `heif-dec`; grids, alpha, transforms, 10-bit,
  monochrome, nclx/ICC, EXIF/XMP; corrupt and truncated streams without uncontrolled exceptions; `TargetSize`,
  `ApplyExifOrientation` and `ColorConversion` through the core without double rotation.
- **0.3.0:** files read by libheif and HM, our decoder identical to HM on our files, the quality/size criterion set
  before implementation, result independent of the thread count, `SaveAsHeic`/`ToHeic`, options.
- **0.4.0:** `ToHdr` for HEIC with an Apple and an ISO map within the core's reference budgets;
  `Image.ReadMetadata` without HEVC decoding.
- **0.5.0:** SIMD x64/ARM64 bit-identical to scalar, parallel tiles, 12/48 MP measurements against libheif.
- **0.6.0:** `HeifDecoderOptions : FormatDecoderOptions` with `ItemId` (`uint?`, null = `pitm`, 0 →
  `ArgumentOutOfRangeException`); the selected image item (top-level, thumbnail, tile, auxiliary image) is decoded like
  the primary item: transforms, colour, `auxl` alpha, `cdsc` Exif/XMP, gain map — in `Identify`, `Decode` and
  `ReadMetadata`. A missing item or an Exif/XMP item → `ImageFormatException`; `ContainerItemCount` unchanged.
  Minimum PictureSharp 0.7.6. Tests: synthetic `Identify` (alpha, thumbnail, Exif, missing ID), decoding the
  `grid-2x1` tile = the left 64 columns of the grid bit for bit; API snapshot; README ("Choosing an image item") and
  `docs/decoding.md`.

## Implementation status 0.1.0 (2026-10-16)

- Done: skeleton/package, `ftyp` detection (including 64-bit sizes), a bounded box parser, `Identify` for
  `hvc1`/`grid`/`iden`, transforms in `ipma` order, alpha, depth, input/metadata/pixel limits, reference cycles and
  depth.
- Done: architecture tests and the public API snapshot; 49/49 Release tests locally, including 12,000 mutations, Oracle
  and Local. Synthetic corpus: 7 files with SHA-256 (x265/kvazaar, 8/10-bit, alpha, cropping, grid, rotation/mirror).
  `heif-info` confirms the corpus dimensions/depth/alpha, and `heif-dec` reads all its files.
- Done: approved download and build of the pinned tools (HM 16.20, kvazaar 2.3.1, x265 3.4, libde265 1.0.15,
  libheif 1.20.2), corpus regeneration scripts, documentation and the CI workflow. The workflow still needs to be run
  on GitHub; the local build has 0 warnings/errors, `dotnet pack` produces valid nupkg/snupkg.
- Local: all 3 private photos from `samples/` identified consistently with libheif 1.20.2. The files stay ignored
  (including upper-case extensions); they are not part of the corpus or the package.
- Done: dependency on the PictureSharp 0.6.9 package from nuget.org (`PictureSharpVersion`, the nupkg depends on
  0.6.9); `Identify` sets `ImageInfo.ContainerItemCount` to the number of `iinf` entries (tiles, alpha, thumbnails and
  Exif/XMP items; `FrameCount` stays 1). The corpus manifest contains an `itemCount` computed independently by the
  generator and consistent with `heif-info`; synthetic tests (1/2/3 items), corpus and Local. The plugin's public API
  is unchanged.
- Private repository `art-ix/PictureSharp.Heif` (2026-10-16), GitHub Actions disabled at the user's request ("no
  pipelines for now"); the workflow `.github/workflows/ci.yml` stays in the repo for later enabling.
- Identification limitations: explicit non-zero extent lengths, whole-pixel `clap`; unsupported features throw
  `NotSupportedException`. `Identify` does not confirm HEVC validity or whether the profile can be decoded. `Decode`
  verifies the container and still throws `NotSupportedException` — pixels are the scope of 0.2.0.

## Implementation status 0.2.0 (2026-10-16)

- Done: bit-exact references — `tests/assets/generate-hevc-corpus.py` (43 HM/kvazaar streams covering the coding tools
  + 10 HEIF corpus items), SHA-256 of YCbCr planes from HM; libde265 must agree unless its own SEI hash fails (2 streams
  with `slice_loop_filter_across_slices_enabled_flag=0`). Tools: dec265 and HM `TAppEncoder` added to
  `scripts/build-heif-reference.sh`.
- Done: scalar HEVC intra decoder in `src/PictureSharp.Heif/Hevc/` (port from HM 16.20, BSD-3, notice in
  THIRD-PARTY-NOTICES): NAL/RBSP, SPS (with VUI/HRD/RPS to skip), PPS, slice header, CABAC, SAO, coding tree, PCM,
  transquant bypass, cu_qp_delta, scaling lists, transform skip, DST/DCT 4–32, intra prediction (35 modes, smoothing,
  strong smoothing), deblocking, SAO, tiles, WPP, slices and dependent segments, Main/Main10/Main Still Picture and
  4:0:0 without RExt tools. P/B slices, 4:2:2/4:4:4, bit depths > 10 and RExt/SCC tools → `NotSupportedException`.
- Results: 43/43 streams and 10/10 HEIF items bit-identical to HM; Local: 423 HEVC items (tiles, thumbnails, auxiliary
  images) from 5 private photos matching HM and libde265; fuzzing with 6000 mutations without uncontrolled exceptions.
- The YCbCr tests link the `Hevc/` sources into the test project (no `InternalsVisibleTo`); the HEVC layer does not
  depend on the container.
- Done: `Decode` — grids assembled in YCbCr, `iden`, `clap`/`irot`/`imir` in `ipma` order (irot counter-clockwise;
  imir mode 0 top–bottom, 1 left–right, as in libheif), alpha from `auxC` (+`prem` → straight alpha like the core's
  `ToStraight`), ICC/EXIF/XMP (EXIF orientation → TopLeft), nclx/VUI → `Chromaticities`/`Gamma` without ICC, PQ/HLG
  with `ToSrgb` → `NotSupportedException`. Colour conversion established empirically against heif-dec 1.20.2 (without
  reading libheif code): 8-bit full range fixed-point `round(k·256)`, the rest `float32` (limited: 1.1689/1.1429),
  chroma NN, and for `clap` with an odd origin bilinear (9,3,3,1) over the whole image; 10→8 bit
  `round(v·255/1023)`. Description: docs/decoding.md.
- Results: 22 HEIC corpus files (BT.601/709/2020, full/limited, 8/10 bit, VUI/no colr, monochrome, straight/premult/
  10-bit alpha, grids, transforms, even/odd clap, ICC/EXIF/XMP) — RGBA identical to heif-dec; Local: 5 iPhone photos
  (12/24 MP) identical to heif-dec, scalar ~1 s/12 MP; 148/148 tests, Oracle 1/1.
- Done (user consent 2026-10-16): 147 JCT-VC HEVC v1 conformance streams (ITU-T, 129 MB of archives, SHA-256 pinned in
  `scripts/jctvc-conformance-v1.sha256`, files in the ignored `tests/assets/external/jctvc`). The first picture of each
  stream: HM references for 145 (RPS_C and VPSSPSPPS_A unresolved by libde265/hash) in
  `tests/assets/jctvc-reference.json`; all 145 bit-exact (`Category=Conformance`).

## Criterion and plan for 0.3.0 (set 2026-10-16, before implementation)

- **Criterion (user decision):** at equal PSNR (RGB against the source, decoded by heif-dec) and equal SSIM, the HEIC
  file size ≤ 1.5× the x265 `medium` file (heif-enc), measured as BD-rate over 4 quality points on test images (private
  `Category=Local` photos and possibly Kodak with consent). Result independent of the thread count.
- **Correctness:** every file read by libheif (heif-dec) and HM; encoder reconstruction = our decoder's output = HM.
- **Order:** bitstream writing and CABAC → intra encoder (CU/mode decisions through SATD+RD, quantization, then RDOQ,
  SAO, TU split) → HEIF writer (512×512 grid, 4:0:0 alpha, ICC/nclx, EXIF/XMP) → `SaveAsHeic`/`HeifEncoderOptions`.

## Implementation status 0.3.0 (2026-10-16)

- Done: HEVC intra encoder in `src/PictureSharp.Heif/Hevc/Encoder/` (port from HM: CABAC and bit estimation, syntax
  writing, forward transforms, quantization, mode/CU decisions): CTB 32, CU 32/16/8 + NxN, TU = CU, SATD (35 modes) →
  RD with CABAC estimation, 5 chroma modes, CU split through RD, deblocking; SAO/RDOQ/sign hiding/RQT disabled (room
  for improvements). Intra prediction shared with the decoder (`HevcIntraPredictor`).
- Done: HEIF writer (`HeifWriter`), `HeifImageEncoder` (BT.601 full 4:2:0, chroma 2×2, 512 grid, 4:0:0 alpha RExt
  Monochrome in the same split, odd dimensions through `clap`, EXIF orientation → `irot`/`imir` with EXIF TopLeft,
  ICC → `colr prof`, otherwise nclx 1/13/6 full, Exif/XMP with `cdsc`), public `HeifEncoderOptions` (Quality,
  AlphaQuality, TileSize, MaxDegreeOfParallelism) and `SaveAsHeic`/`SaveAsHeicAsync`/`ToHeic`; docs/encoding.md.
- Results: HM decodes the streams identically to our decoder, without deblocking the decoder = encoder
  reconstruction; heif-dec reads the files (single, odd, grids, alpha, alpha in a grid, orientations, alpha+rotation)
  identically to us; result independent of the thread count. Measurement (Local, 5 crops 768×512, Q 30/45/60/75):
  BD-rate −1.18% (PSNR RGB) and +9.16% (SSIM luma) against x265 medium
  (`docs/measurements/encoder-bdrate-x265-medium.tsv`); 12 MP in ~5.6 s on 16 threads.
- Core API gap (to report to the PictureSharp plan): writing (`Save`/`Encode`/`SaveAsX`) uses only
  `ImageConfiguration.Default`; the plugin cannot save through a configuration passed the way
  `DecoderOptions.Configuration` is.

## Encoder improvements after 0.3.0 (2026-10-16, unreleased)

- Variance-based AQ (kvazaar VAQ, `cu_qp_delta`, `HeifEncoderOptions.AdaptiveQuantizationStrength` = 1), RDOQ and sign
  data hiding (HM), SAO with the decision on the post-deblocking image (two slice-writing passes), TU split (RQT, depth
  2), speed-ups (butterfly transforms, shared reference samples, coarse SATD, 4/2 RD candidates, early termination of
  CU splitting).
- BD-rate against x265 medium (PSNR RGB / SSIM luma): Kodak −4.63% / +0.85% (was −3.84% / +9.25%), private crops
  −2.84% / +0.77% (was −1.18% / +9.16%). 12 MP in ~5.6 s (after RQT it was 9.2 s).
- Further candidates: psy-rd, SIMD (SATD, transforms, RDOQ), parallelism within a tile (WPP).

## Implementation status 0.4.0 (2026-10-17)

- Dependency on PictureSharp 0.7.1 from nuget.org; write tests through `EncoderOptions.Configuration` (without
  replacing `ImageConfiguration.Default`, apart from one default-configuration test).
- Rendering separated from the decoder: `HeifImageNode` (item description: NAL, grid, `iden`, transforms, nclx; the
  detached version copies the bytes) and `HeifRenderer` without a decode context; decode results unchanged.
- Gain maps (`HeifGainMap`, `HeifGainMapImage`): ISO 21496-1 from the `tmap` item (`dimg` = [primary, map], version
  byte 0, alternate ICC from `colr`), Apple from the auxiliary image `urn:com:apple:photo:2020:aux:hdrgainmap`
  (MakerNote from the base's EXIF, the map's XMP). ISO wins, a malformed ISO does not fall back to Apple; errors go
  through `DeclareGainMap`. The map is rendered with its own transforms, decoded lazily from a copy of the data; the
  base colour is preserved under `IgnoreMetadata`. `Identify` with `LoadGainMap` reports `ImageInfo.Hdr`.
- `HeifFormat.MetadataCodec` (`HeifMetadataCodec`): `ReadMetadata` without HEVC decoding, the same blocks as `Load`
  (shared `HeifMetadataReader`, colour from nclx/VUI through reading the SPS); `EditMetadata` →
  `NotSupportedException`.
- Corpus: 6 files with a gain map (Apple with MakerNote, ISO grey and RGB, both descriptions with `irot`, version 1,
  truncated metadata) with a reference map from heif-dec (`*.map.png`) and metadata in the manifest. `ToHdr` bit-equal
  to the reference plugin (base from decoding, map from heif-dec, generator metadata). Local: 5 iPhone photos (3 ISO,
  2 Apple) bit-equal to the reference with the map from `heif-dec --with-aux`. Fuzzing covers
  `Identify`/`ReadMetadata`/`ToHdr` with `LoadGainMap`.
- Results: 383 tests (382 passed, 1 skipped: the AQ sweep requiring `PICTURESHARP_HEIF_SWEEP=1`), Oracle 4/4, build
  0 warnings, `dotnet pack` with the PictureSharp 0.7.1 dependency.
- SPI gap closed in core 0.7.1: the plugin passes all detected models through `AttachGainMap(metadata, map, detected)`
  and `DeclareGainMap`, so HEIC from iOS 18 reports `Iso21496, Apple` like the JPEG from the same photo. The corpus
  manifest has a `detected` field; the Local test checks the set of models of the iPhone photos.

## Implementation status 0.5.0 (2026-10-17, closed locally)

- Parallelism: grid tiles decoded in parallel (the first tile sets the format, the rest paste into disjoint areas; on
  error `Break` and the exception of the first faulty tile in order, as sequentially), colour conversion, rotation and
  mirroring in row bands (`HeifParallel.ForRows`); `DecoderOptions.MaxDegreeOfParallelism` passed to the renderer and
  gain map decoding.
- SIMD (`Vector128`, `Vector256` from 8x8, in `*.Simd.cs` files): inverse DCT and forward transform as matrix
  products (the same integer sums as the HM butterflies, skipping zero rows/columns), dequantization, sample
  reconstruction (`HevcSamples`), difference and squared error in the encoder, SATD 4x4, YCbCr→RGB 8-bit full range.
- Without SIMD: zeroing only written coefficients, direct lookup of the last scan position, RDOQ without clearing arrays
  (zeroing a position when visited), rotation reading source rows.
- x64 verification: decoding of the corpus and 5 photos (with `ToHdr`) and 11 encodings byte-identical to 0.4.0, also
  with `DOTNET_EnableHWIntrinsic=0`; full tests 389/390 (1 skipped AQ sweep) on SIMD and scalar paths; `HevcSimdTests`
  (5 vector/scalar comparisons) and 2 tests of decode independence from the thread count (including corrupt tiles).
- x64 measurements (`docs/measurements/speed-x64.tsv`): decoding 12 MP 646 ms / 109 ms (1 / 16 threads; libheif
  613 / 237), 24 MP 1195 / 193 (981 / 394), 48 MP grid 1009 / 165 (973 / 416); encoding 12 MP 31 s / 4.0 s (was
  36.6 / 4.7; heif-enc x265 2.0 s, file +8%), 48 MP 13.7 s (heif-enc 5.3 s, +6%).
- ARM64 (Orange Pi 5, RK3588, with user consent; copy `~/heif-0.5.0` with photos): build 0 warnings, tests 379/390 on
  NEON and with `DOTNET_EnableHWIntrinsic=0` (11 skipped: oracles without tools on the board and the AQ sweep); 77
  decode and encode hashes identical to x64 in both modes. `Vector256` is not accelerated there, so the 128-bit paths
  run.
- ARM64 measurements (`docs/measurements/speed-arm64.tsv`, libheif 1.17.6 from Ubuntu packages): decoding 12 MP
  1593 / 347 ms (1 / 8 threads; libheif 2222 / 820, scalar 1710 / 407), 24 MP 2733 / 651 (4471 / 2701), 48 MP grid
  2618 / 718 (4335 / 2392); encoding 12 MP 62.6 s / 14.0 s, 48 MP 41.3 s, bytes as on x64. No heif-enc with x265 on
  the board.
- DoD met: SIMD = scalar bit for bit on x64 and ARM64, parallel tiles, 12/48 MP measurements against libheif.
- Further candidates (beyond DoD): CABAC residual parsing (~40% of decoding), RDOQ and bit counting (~40% of encoding),
  WPP within a single image, merging rotation with colour conversion.

## Container model cleanups (2026-10-17, unreleased)

A refactoring with no change in results (decoding and encoder bytes identical to commit 1bfaef8 across the whole
corpus, in 8 orientations, with and without a grid); the `Hevc/` layer (ports) unchanged.
- Domain language: `HeifFourCc` names box, item, property, reference and brand codes instead of `0x…` numbers.
- Values: transforms `HeifRotation`/`HeifMirror`/`HeifCleanAperture` (read once in the container, the same types in
  the decoder and encoder, `Payload()` when writing), `HeifNclx`, `HeifGridLayout`.
- Render nodes as types (`HeifCodedNode`, `HeifGridNode`, `HeifIdentityNode`) instead of an enum with optional fields;
  the renderer and `HeifCanvas.Apply` use pattern matching.
- `HeifItem` without validation state (DFS state and description memory live in the container), `Targets`/`RefersTo`
  instead of repeated `TryGetValue`; `HeifMetadataReader.ApplyColour`/`ApplyDescriptive` instead of a flag; alpha in
  `HeifCanvas.SetAlpha`.

## Decoding scope — state at 0.6.0 (before the 0.6.1–0.7.0 plan)

- Container: `ftyp` (`heic`, `heix`, `mif1`), `meta` with `hdlr pict`, `pitm`, `iinf`/`infe`, `iloc` (versions 0–2,
  methods 0 and 1), `iref` (`dimg`, `auxl`, `thmb`, `cdsc`, `prem`), `iprp`/`ipco`/`ipma` (`hvcC`, `ispe`, `colr`
  nclx/prof, `pixi`, `irot`, `imir`, `clap`, `auxC`), `idat`. `grid` items (iPhone tiles) and `iden`; transforms in
  `ipma` order. Establish the relation to `ApplyExifOrientation` (HEIF applies `irot`/`imir`, EXIF orientation is
  informational).
- HEVC: Main, Main Still Picture and Main 10 profiles; 4:2:0 and 4:0:0 chroma. VPS/SPS/PPS, slice segments, CABAC,
  coding tree, intra prediction (35 modes), transform skip, scaling lists, 4–32 transforms and the 4×4 DST,
  dequantization, deblocking, SAO, tiles and WPP, PCM. Inter slices, 4:2:2/4:4:4 (RExt) and SCC →
  `NotSupportedException`.
- Colour: YCbCr → RGB per nclx (BT.601/709/2020 matrices, full/limited range) with exactly described rounding; ICC into
  metadata; 10-bit → 8-bit like 16-bit PNG. Alpha from the auxiliary image (auxC alpha) as straight alpha, with
  premultiplication undone for `prem`.
- Metadata: Exif items (with offset prefix), XMP and ICC in `ImageMetadata`; `Identify` without HEVC decoding;
  the `thmb` thumbnail available through `ItemId`.
- HDR: Apple (`urn:com:apple:photo:2020:aux:hdrgainmap`) and ISO (`tmap`) gain maps attached through the SPI from core
  0.7.0, so `ToHdr`/`ToneMap` work for HEIC. In 0.6.0 there is no semantic handling of depth and portrait mattes,
  Live Photos and HEIF sequences; extensions are described by the 0.6.1–0.7.0 plan. AVIF remains outside the 0.7.0
  plan.
- Limits: `MaxInputBytes`, `MaxPixels` (sum of tiles, alpha and map), `MaxMetadataBytes`, item count, `iref` depth and
  cycles, grid and tile dimensions, NAL sizes. Grid tiles decoded in parallel, result independent of the thread count.

## Encoding scope — baseline scope 0.1.0–0.6.0

- A single image (animation policy as in the other single-image encoders), HEVC Main 8-bit 4:2:0, alpha as a 4:0:0
  auxiliary image, tile grid for large images, nclx/ICC, Exif and XMP. Default colour parameters set per `heif-enc`.
- Intra encoder: `Quality` 0–100 → QP, CTU 32/64, mode selection through SATD and simplified RDO, CABAC, deblocking and
  SAO; deterministic and independent of the thread count (tiles in parallel).
- `HeifEncoderOptions` (working): `Quality`, `TileSize`, `Effort`, `MaxDegreeOfParallelism`. Lossless mode, 4:4:4 and
  10-bit are beyond the first versions. Writing gain maps in HEIC after the core 0.8.0 SPI (decision after the decoder
  stage).

## References and tolerances

Set and recorded before implementing a given version.

- Decoder: YCbCr bit-exact (tolerance 0) with HM and libde265 on JCT-VC intra conformance streams (download with
  consent) and the synthetic corpus from `heif-enc` (x265 and kvazaar) and ffmpeg. RGB from `heif-dec` under the same
  colour conversion: 0, otherwise a set tolerance.
- Synthetic corpus: grids, alpha, rotations/mirrors/clap, 10-bit, monochrome, nclx/ICC, Exif/XMP, Apple and ISO gain
  maps; corrupt, truncated, cyclic references and fuzzing. Private iPhone HEIC photos only locally (`Category=Local`).
- Encoder: files read by libheif and the HM decoder; our decoder identical to HM on our files. A quality/size
  criterion against x265 and kvazaar at equal PSNR/SSIM (e.g. size ≤ 1.5× x265 `medium`) recorded before implementing
  the encoder.
- Receiver compatibility: manually Windows (HEIF Image Extensions) and Apple Photos, when available; absence is not a
  pass.

## Order of work

Per the version table; each stage with tests and an entry in `progres.md`. SIMD and parallelism (IDCT/DST, intra
prediction, deblocking, SAO, YCbCr→RGB) only after the scalar version.

## DoD of the whole (up to 0.5.0)

- Oracles green within the set tolerances; scalar and SIMD identical on x64 and ARM64; result independent of the thread
  count.
- Tests of limits, corrupt data and cancellation; no input yields an uncontrolled exception (fuzzing `Category=Slow`).
- Measurements of decoding 12/48 MP and encoding against libheif (time, allocations, peak memory).
- The `PictureSharp.Heif` package with README (including a patent note), `LICENSE` and notices; architecture test;
  trim/Native AOT consumer; documentation with examples checked by tests; a CLI example with plugin registration.

## Dependencies on the PictureSharp core

Core SPI audit (2026-10-16, state 0.6.8): decoding and writing SDR HEIC is possible on the current public API. Gaps
closed in the core:
- 0.6.9: `ImageInfo.ContainerItemCount` for `Identify` (required by 0.1.0).
- 0.7.0: attaching gain maps by the plugin (`GainMapMetadata`, `GainMapImage`, `ImageDecodeContext.AttachGainMap`), HDR
  base colour from metadata (ICC or `Chromaticities`/`Gamma`), `IImageFormatMetadataCodec` for `ReadMetadata`, a
  documented orientation contract.
- 0.7.1: `AttachGainMap` with full `HdrInfo.Detected` (released 2026-10-17, used since 0.4.0).
- 0.7.6: per-plugin decoder options (`FormatDecoderOptions`, `DecoderOptions.FormatOptions`), used since 0.6.0.
- 0.8.0: gain map in plugin encoders and ISO serialization, possibly `ReportStoredSize` for `thmb` thumbnails — only if
  the plugin shows a need.
- Outside the plan: CICP PQ/HLG (HDR10 HEIC from Android).

New SPI needs are reported to the core plan; we do not work around them by copying internal code.
