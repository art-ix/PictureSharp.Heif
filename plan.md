# PictureSharp.Heif: development plan

## Status and goal

**Current version: 0.6.9, release 2026-10-21; minimum PictureSharp 0.7.6.** A C# format plugin with no native libraries, with its own HEVC intra decoder and encoder.

- Reading: HEIF container (`heic`/`heix`/`mif1`), HEVC Main/Main 10/Main Still Picture 4:2:0 and 4:0:0, grids, `iden`,
  `irot`/`imir`/`clap`, alpha (`auxl`, `prem`), nclx/ICC, Exif/XMP, Apple/ISO gain maps (`ToHdr`), `ReadMetadata`
  without HEVC decoding, item selection through `HeifDecoderOptions.ItemId`, compressed XMP (`deflate`), raw depth
  samples (`LoadDepth`), Apple depth description (`AppleDepth`) and Portrait effect rendering (`RenderPortrait`), Live
  Photo pairing with MOV (`LivePhotoPair`). P/B slices, RExt, SCC, PQ/HLG, sequences →
  `NotSupportedException`.
- Writing: HEVC Main 8-bit 4:2:0, alpha 4:0:0, tile grid, nclx/ICC, Exif/XMP (`SaveAsHeic`, `HeifEncoderOptions`).
- SIMD on x64/ARM64 bit-identical to scalar, parallel tiles, result independent of the thread count.

**Goal for 0.7.0 (scope simplified 2026-10-18):** a catalog of the file contents (photos, thumbnails, auxiliary
images), reading and generating thumbnails, raw depth samples, Live Photo pair detection and, added 2026-10-18, the
Apple portrait effect (disparity description and depth-of-field rendering) and, added 2026-10-19, Photos edits
described by hand and fitted from pairs, and Photographic Styles. Delivered in releases 0.6.1–0.6.11 (0.6.10: gain map writing, added 2026-10-23),
followed by the integration release 0.7.0. Writing collections, HEIF animations, depth interpretation beyond Apple
portrait disparity and Live Photo export are moved out of 0.7.0.

User decisions (2026-10-16): our own C# port of the HEVC intra decoder and encoder (not a libheif wrapper); a separate
repository and package with its own SemVer starting at 0.1.0; dependency solely on the `PictureSharp` NuGet package.
Changes needed in the library itself are planned by the core (`../picturesharp/plan.md`).

Working rules: [AGENTS.md](AGENTS.md). Local work log: `progres.md` (not committed). History of closed versions
0.1.0–0.6.0 (DoD, results, measurements, encoder criteria): [docs/history.md](docs/history.md).

## Versions

| Version | Scope | Minimum PictureSharp | Status |
| --- | --- | --- | --- |
| 0.1.0 | HEIF container, detection, `Identify`, limits, parser fuzzing | 0.6.9 | Closed locally |
| 0.2.0 | HEVC intra decoder, grid, colour, alpha, transforms, ICC/EXIF/XMP | 0.6.9 | Closed locally |
| 0.3.0 | HEVC intra encoder Main 8-bit 4:2:0, alpha, grid, metadata | 0.6.9 | Closed locally |
| 0.4.0 | Apple/ISO gain map → `ToHdr`, `ReadMetadata` without decoding | 0.7.1 | Closed locally |
| 0.5.0 | SIMD and parallelism, measurements against libheif, ARM64 | 0.7.1 | Released 2026-10-17 (first public release) |
| 0.6.0 | `HeifDecoderOptions.ItemId` through `DecoderOptions.FormatOptions` | 0.7.6 | Released 2026-10-18 |
| 0.6.1 | Compressed XMP: zlib/deflate and limits | 0.7.6 | Released 2026-10-18 |
| 0.6.2 | `HeifDocument` catalog, references, auxiliary images, reading and generating thumbnails | 0.7.6 | Released 2026-10-18 |
| 0.6.3 | `HeifDocument` API correction (incompatible with 0.6.2) | 0.7.6 | Released 2026-10-18 |
| 0.6.4 | Raw depth samples, Live Photo pair detection | 0.7.6 | Released 2026-10-18 |
| 0.6.5 | Apple portrait effect: disparity description, depth-of-field rendering | 0.7.6 | Released 2026-10-18 |
| 0.6.6 | Faster HEVC decoder, WPP for single pictures, faster intra search and fewer encoder allocations | 0.7.6 | Release 2026-10-19 |
| 0.6.7 | Portrait focus, edges, bokeh calibration, rendering-parameter analysis, renderer performance (SIMD, memory) | 0.7.6 | Release 2026-10-20 |
| 0.6.8 | Photos edits annotated by hand (`edits.tsv`) and fitted from pairs; `HeifPhotosEdit` with all sliders | 0.7.6 | Release 2026-10-20 |
| 0.6.9 | Rendering API: `HeifEdits` (sliders, then Portrait blur) in one call; replaces `HeifPortrait`, `HeifPhotosEdit`, `RenderPortrait` (incompatible with 0.6.8) | 0.7.6 | Release 2026-10-21 |
| 0.6.10 | Writing HDR gain maps: `tmap` item through the core's `GetGainMap`, `HeifEncoderOptions.GainMap` | 0.8.0 | Implemented locally (2026-10-23); waits for core 0.8.0 on NuGet |
| 0.6.11 | Photographic Styles changed in Photos: the styles item and hand-annotated style changes | 0.8.0 (assumed) | Planned |
| 0.7.0 | Integration, regressions, documentation and package for the whole scope | 0.8.0 (assumed) | Planned |
| later | See "Beyond 0.7.0" | To be determined | Beyond 0.7.0 |

The numbers define order, not dates. Each version requires its own DoD (below) and the common quality gate; robustness
tests (corrupt/truncated data, limits, cancellation) are written in the stage that adds the given path. Each version
is published only on the user's decision.

## Plan 0.6.1–0.7.0

### 0.6.1 — compressed XMP

- An XMP `mime` item with `content_encoding` = `deflate` is decompressed through `ZLibStream` (falling back to
  `DeflateStream` when the zlib header is missing) and made available in `Metadata.Xmp` for `Decode` and
  `ReadMetadata`.
- The decompressed size is accounted by `ReserveMetadata` and bounded: a decompression bomb →
  `ImageFormatException`, with no allocation beyond the limit.
- An unknown encoding or a corrupt XMP stream does not block the image: the block is skipped (today the whole file
  ends with `NotSupportedException`). Writing is unchanged (uncompressed XMP); no new public API.
- **DoD:** content = source XMP, corrupt data, decompression bomb, `IgnoreMetadata`, reading through `Load` and
  `ReadMetadata`; synthetic `infe` and `heif-enc` files if the oracle generates compressed metadata; fuzzing of the
  compressed block; API snapshot unchanged.
- **Status (2026-10-18):** DoD met except for `heif-enc` files — libheif 1.20.2 has no metadata compression option, so
  the tests use synthetic `infe` (files from the plugin's encoder with the XMP block swapped). A truncated stream is
  treated as corrupt (`DeflateStream` then returns partial data without an error). With `IgnoreMetadata` the XMP is not
  decompressed unless the Apple gain map needs it (the `MaxMetadataBytes` limit still applies then). Full Release tests
  405/406 (1 skipped), 0 warnings.

### 0.6.2 — catalog, references and thumbnails

- `HeifDocument` and immutable item descriptions in `PictureSharp.Formats.Heif`. `Open` from a path, `Stream` and
  memory, and `OpenAsync` from a path/stream; the buffer is bounded by `HeifDocumentOptions.MaxInputBytes`, with no
  pixel decoding. `LoadImage` and both `LoadPreview` variants take independent `DecoderOptions`.
- `PrimaryItemId`, `PrimaryImage`, the item catalog and `TopLevelImages`: ID, type, name and the `hidden` flag from
  `infe`, available dimensions and bit depth. Photo classification takes relations and hiding into account; not every
  `hvc1` is a separate photo. `ContainerItemCount` and `Image.Frames` unchanged.
- References of a specific photo: thumbnails (`thmb`), auxiliary images (`auxl`) with the type and the raw
  `aux_subtype` (including unknown vendors), `dimg` dependencies, `cdsc` metadata. Distinguishing alpha,
  depth/disparity, gain map and other auxiliary images, always with the original type identifier. Decoding any item is
  still done through `ItemId`.
- Cataloguing is separated from decoding: an unsupported additional item stays in the catalog, and only an attempt to
  decode it throws `NotSupportedException`.
- Preview reading: photo ID and target size → the smallest supported thumbnail with a matching aspect ratio (tolerance
  of up to 1 pixel of rounding) covering the size, otherwise decode and scale the photo; the thumbnail's own
  transforms, colour and alpha. `Image.Load(TargetSize)` unchanged (the embedded thumbnail is an explicit choice).
- Thumbnail generation in `SaveAsHeic` by maximum dimension (an option in `HeifEncoderOptions`, off by default):
  aspect ratio preserved, no upscaling, `thmb`, colour and alpha, no copying of EXIF/XMP. No user-supplied thumbnails.
- **DoD:** a correct catalog for photos, tiles, thumbnails, alpha, gain maps, unknown auxiliary images and metadata
  without running the HEVC decoder; multiple photos with shared dependencies without mixing metadata; preview with
  multiple sizes and with no thumbnail, orientations without double rotation; libheif recognizes the generated
  thumbnail and the preview API reads it.

- **Status (2026-10-18):** DoD met locally after the contract correction. Release build with no warnings; full tests
  456/457 (1 optional Kodak measurement skipped), Oracle 6/6 with none skipped. Catalog/I/O tests 35/35, API snapshot
  and package examples up to date. The package consumer and Native AOT x64 cover `OpenAsync`, `PrimaryImage`,
  `LoadImage`, both previews, input ownership, colour/alpha/orientation. libheif recognizes the thumbnail and
  reproduces identical RGBA (including a grid), HM confirms every encoded item. Four comparisons against the 0.6.1 code
  confirm identical output with thumbnail generation disabled. `VERSION` 0.6.2; GitHub Release and NuGet publication
  completed. Release workflow 39149646458: success.

### 0.6.3 — `HeifDocument` API correction

- **`HeifDocument` API correction (incompatible with 0.6.2; released 2026-10-18):** reads take the document's
  `HeifItemInfo` (`LoadImage`, `LoadPreview`, the new `Identify`, `ReadMetadata`, `FindThumbnail`) instead of an ID;
  `PrimaryItemId` and `LoadPreview(Size)` removed (use `PrimaryImage`). References as objects (`SourceImages`,
  `Thumbnails`, `AuxiliaryImages`, `MetadataItems`), `TryGetItem`, `IsSupported`, `Subtype` as
  `ReadOnlyMemory<byte>`, the constants `HeifItemTypes`/`HeifReferenceTypes`. An item that is not an image or belongs
  to another document → `ArgumentException`; reading does not re-check `MaxInputBytes`. Number 0.6.3 chosen by the
  user. [Notes and migration](docs/releases/release-0.6.3.md).
- **Status:** Release build with no warnings, full tests 459/460 (1 optional measurement skipped), Oracle 6/6.

### 0.6.4 — raw depth samples and Live Photo pairs

- `HeifDepthImage`: `ReadOnlyMemory<ushort>`, bit depth, dimensions, parent photo ID, raw `aux_subtype` and the item's
  raw metadata blocks (`cdsc`, XMP). Samples are taken before colour conversion and reduction to 8 bits; grids,
  cropping, rotation and mirroring leave values unchanged, with no gamma and no scaling to the photo size. No
  interpretation of units, calibration or conversion to Z/disparity. Budgets before allocation, cancellation.
- `LivePhotoPair`: an explicitly supplied HEIC/JPEG photo and MOV video (no directory searching). Reads the photo's
  ContentIdentifier (Apple MakerNote; parser as for the gain map in `HeifGainMap`) and the video's
  (`com.apple.quicktime.content.identifier` in `meta`/`keys`), plus the photo's timestamp in the video if present.
  Result: matched, missing metadata, or conflicting IDs; file names do not confirm a pair. The MOV parser reads only
  the necessary boxes, with limits.
- **DoD:** depth samples bit-identical to HM/libde265 (including 10-bit, grids, transforms), limits/cancellation;
  Live Photo: HEIC+MOV and JPEG+MOV, missing and conflicting IDs, corrupt/truncated MOV, MOV parser fuzzing.
- **Status (2026-10-18):** DoD met locally. API: `HeifDocument.LoadDepth` → `HeifDepthImage` (luma samples at the coded
  depth, `Auxiliary`, `PhotoItemId`, the depth item's `Exif`/`Xmp`) and `LivePhotoPair` (`Read`/`ReadAsync` for paths
  and streams, `Status`, both IDs, `StillImageTime`). Depth: 10-bit mono, 8-bit with clap, grid with irot+imir, 10-bit
  4:2:0 with odd clap — identical to HM and libde265 (Oracle). Live Photo: synthetic MOV (moov before/after mdat,
  largesize, ISO/QuickTime meta, udta/meta, stream without seek), HEIC+MOV, JPEG+MOV, missing and conflicting IDs,
  truncations, `moov` limit, fuzzing with 4000 mutations, MOV from ffmpeg; a real pair from an iPhone 17 Pro
  (`Category=Local`, still-image 1.367 s). Three iPhone Portrait photos (`Category=Local`): depth 768×576 8-bit with
  `irot` 0/3 and the item's XMP — samples identical to HM and libde265. Release build 0 warnings, full tests 484/485
  (1 optional measurement skipped), Oracle 11/11, Local 8/8.

### 0.6.5 — Apple portrait effect

Findings on three iPhone Portrait photos (2026-10-18): the main image is sharp and the background blur is an editable
effect that Photos renders at display time. The file keeps what the effect needs: a 768×576 8-bit auxiliary image under
the generic depth type `urn:mpeg:hevc:2015:auxid:2` whose XMP says `apdi:AuxiliaryImageType="disparity"` (stored
`L008`, native Float16 `hdis`, `FloatMinValue`/`FloatMaxValue`), `depthBlurEffect:SimulatedAperture` (f/4.5) and a
private `depthBlurEffect:RenderingParameters` blob, `portraitLightingEffect:EffectStrength`, the
`portraiteffectsmatte` and semantic mattes (hair, skin, teeth, glasses, sky). Converting to JPEG therefore yields the
photo without blur.

- **Apple depth description:** a typed, immutable description read from the depth item's XMP (only the `apdi`,
  `depthData` and `depthBlurEffect` fields above, plus intrinsics as raw values): the declared kind (depth or disparity),
  stored and native formats, float range, accuracy, quality, filtering and simulated aperture. A method maps samples to
  float values (`FloatMinValue` + sample / max × range). Missing or malformed XMP gives no description, never an error.
  `HeifAuxiliaryKind` keeps following aux_type (no behaviour change); the description reports Apple's declared kind.
  The `RenderingParameters` blob stays raw and uninterpreted (private format).
- **Portrait mattes:** constants for the Apple matte types and a way to find a photo's portrait and semantic mattes;
  they load as images already (`LoadImage`).
- **Depth-of-field rendering:** an explicit operation that takes the photo, the disparity (resampled to the photo's
  size; the items' own `irot` already aligns them), the optional portrait matte, an aperture (f/1.4–f/16, default the
  photo's `SimulatedAperture`) and a focus (default: the matte-weighted subject disparity, or a point the caller
  gives). Variable-radius disc blur gathered per pixel, growing with the disparity difference from the focus and with
  the aperture; matte-weighted blending keeps the subject sharp. Returns a new SDR image; the original stays unchanged.
  The HDR gain map is not blurred or attached to the result (documented). An approximation: Apple's algorithm is
  private, and no claim of matching Photos is made.
- Placement: the plugin, using only the public PictureSharp processing API. If the blur proves generally useful
  (depth maps from other formats), propose a core processor in `../picturesharp/plan.md` instead of copying code.
- Out of scope: portrait lighting effects, writing the rendered effect or depth back to HEIC (auxiliary writing stays
  beyond 0.7.0), other vendors' depth semantics and metric depth from intrinsics.
- **DoD:** description parsed on the private Portrait photos (`Category=Local`) and on synthetic XMP (absent,
  malformed, depth vs disparity, float ranges, extreme values); float mapping tests. Rendering: pixels with matte 1 and
  zero disparity difference unchanged bit for bit; background high-frequency energy decreasing monotonically as the
  aperture opens; result independent of the thread count; SIMD (if added) identical to scalar on x64 and ARM64;
  `MaxPixels` checked for the resampled map and buffers before allocation; cancellation between row bands; corrupt
  depth or matte → `ImageFormatException`; time and memory measured on 12 and 24 MP photos. Reference renderings:
  the user's private pairs in `samples/` (`IMG_xxxx.HEIC` original with a sharp main image, `IMG_xxxx_2.HEIC` the
  same photo exported by Photos with the portrait effect rendered, its depth stored pre-rotated) — a
  `Category=Local` test measures how close our rendering at the photo's `SimulatedAperture` comes to Apple's
  (e.g. PSNR/SSIM on background and subject regions, reported per photo; no bit-exactness or parity claim), and
  guides tuning of the blur kernel, radius curve and matte blending. Visual review by the user on
  their own photos (a manual check, not a compatibility claim). XML docs, guide section, API snapshot.

- **Status (2026-10-18):** released (tag v0.6.5, GitHub Release, NuGet). API: `HeifAppleDepthInfo` (`FromXmp`, `ToFloat`),
  `HeifDepthImage.AppleDepth` and `ToImage` (greyscale view of the map, added at the user's request),
  `HeifAppleAuxiliaryTypes`, `HeifItemInfo.FindAuxiliaryImage`/`FindDepthImage`, `HeifPortrait.Render`,
  `HeifDocument.RenderPortrait`, `HeifPortraitOptions` (aperture, focus point, `MaxPixels`, parallelism). Radius
  model fitted on the four pairs: r = 0.0055 · L · φ^1.5 · ((4.5 / N) · |D − Df| − 0.02), with L the longer side, φ the
  focal length over the sensor's longer side from the intrinsics and D disparity in 1/m; the matte protects only
  pixels with a small radius (a person far behind the focus is blurred, as in Photos). Layers: radii ≤ 3 px gathered
  directly, larger ones from a 2×2 box pyramid (Half storage), normalized convolution without the protected subject,
  linear light. Local: background PSNR 22.2→32.9, 21.7→33.3, 21.1→32.6, 27.6→40.0 dB, SSIM 0.52–0.70 → 0.96–0.98,
  subject unchanged (−0.5 to +11 dB); `HeifPortrait.Render` 0.27–0.38 s at 12 MP and 0.50–0.53 s at 24 MP,
  `RenderPortrait` with decoding 0.37–0.90 s and 0.41–1.1 GiB allocated (i5-12500H). Full tests 513/514 (Kodak
  measurement skipped) with Oracle and Local, 0 warnings. Released without further tests at the user's decision:
  scalar only (no SIMD, so no SIMD/ARM64 identity to check), no ARM64 run and no package consumer/Native AOT rerun.
  [Release notes](docs/releases/release-0.6.5.md).

### 0.6.6 — HEVC decoding and encoding speed

Planned 2026-10-19 at the user's request (the portrait calibration moved to 0.6.7). Starting point, 0.5.0 on x64
(`docs/measurements/speed-x64.tsv`, i5-12500H): decoding 12 MP 646 / 109 ms (1 / 16 threads; libheif 613 / 237),
24 MP 1195 / 193 ms (981 / 394), a 48 MP single picture 1096 ms with no parallelism (libheif 935); encoding 12 MP
31 s / 4.0 s (heif-enc x265 2.0 s), 48 MP 13.7 s (5.3 s). Profile at 0.5.0: CABAC residual parsing ~40% of decoding,
RDOQ and bit counting ~40% of encoding.

- **Baseline and profiling first:** rerun `speed-x64.tsv` on 0.6.5 (the decoder gained depth, thumbnails and
  portrait paths since 0.5.0) and collect a per-stage profile (CABAC, prediction, transforms, deblocking, SAO, colour
  conversion, transforms of the picture; encoder: mode decision, RDOQ, bit estimation, reconstruction). Optimizations
  are taken in order of measured share, not of this list.
- **Decoder, single thread:** CABAC engine and residual coding — renormalization by bit counting, batched bypass bins
  (signs, remainders), context state in local variables, `coeff_abs_level_remaining` without per-bin loops, significance
  map by subblock without bounds checks; intra prediction and reference sample filtering with SIMD; deblocking and SAO
  with SIMD (edge filtering per 4-sample segment, SAO band/edge per row); fewer passes over the picture (deblocking and
  SAO per CTU row while it is in cache).
- **Decoder, parallelism:** WPP (`entropy_coding_sync_enabled_flag`) and tiles within a single picture when the
  stream uses them; otherwise row-pipelined deblocking/SAO behind CTU parsing so a single non-grid picture (x265,
  other encoders) uses more than one core. Colour conversion fused with rotation/mirroring and with the RGBA write
  (one pass, no intermediate planes); YCbCr→RGB SIMD also for limited range and 10-bit.
- **Encoder:** RDOQ with incremental rate estimates (tables per context state instead of recomputing), early exits for
  all-zero blocks and in mode decision (SATD threshold, skipping 4×4 when 8×8 is flat), bit counting from fractional
  CABAC tables, SIMD for intra prediction of candidates; WPP inside a tile for 48 MP and single-picture output;
  pooled buffers per tile (no per-CU allocation). An optional speed/compression trade-off (`HeifEncoderOptions` preset)
  only if the default cannot get closer to x265 without losing compression; decided after measurement.
- **Encoder update (2026-10-19):** the user permits changed bytes and a small quality trade-off. A measured
  candidate uses one SATD winner plus competitive MPMs and applies full RDOQ after luma mode selection. Kodak:
  1.411× mean speed, +0.949% PSNR / +1.262% SSIM BD-rate; worst same-setting PSNR loss 0.131 dB. This exceeds the
  original +0.5% gate below and does not by itself meet the 1.5× full-photo target. See
  [experiment report](docs/measurements/encoder-search-0.6.6.md). The user accepted the visual comparison and
  requested release preparation on 2026-10-19; the original performance gates remain documented below.
- **Memory:** allocations of `Decode` and `SaveAsHeic` per MP (`ArrayPool`, plane reuse between tiles), peak RSS on
  48 MP.
- **Invariants:** decoded pixels bit-identical to 0.6.5 (corpus, HM hashes, private photos, `ToHdr`); encoder output
  byte-identical to 0.6.5 for every optimization that does not change decisions; a change in decisions only with a
  BD-rate report (`docs/measurements/encoder-bdrate-*`) no worse than +0.5% and the user's approval. SIMD = scalar on
  x64 and ARM64, `DOTNET_EnableHWIntrinsic=0` run, result independent of the thread count.
- **Out of scope:** new coding tools or profiles (inter, RExt, 10-bit encoding), a GPU path, native code.
- **DoD:** single-thread decoding at or below libheif on 12/24/48 MP (x64), all-thread decoding of a single 48 MP
  picture at least 2× faster than 0.6.5 when the stream allows parallel parsing (WPP/tiles) or row pipelining;
  single-thread encoding of 12 MP at least 1.5× faster than 0.6.5 with identical bytes (or the approved BD-rate change);
  allocations not higher than 0.6.5; measurements in `docs/measurements/speed-*-0.6.6.tsv` on x64 and ARM64 with
  libheif/heif-enc as the reference; full tests and Oracle, 0 warnings; `docs/decoding.md`/`docs/encoding.md` speed
  sections updated. Publication on a separate user decision.

**Release decision (2026-10-19):** the user requested push and release of version 0.6.6 after acceptance of
before/after PNGs (no visible difference reported). Encoder: 12 MP Q50, 30.761→21.828 s on one thread,
4.200→2.858 s on all threads; allocations 388.9→322.1 MiB. Decoder: 12/24 MP 568/938 ms on one thread,
103/161 ms on all threads; 48 MP WPP 848/173 ms. API and dependency versions are unchanged.
Release build: zero warnings/errors; 703/705 tests passed with the two known Local portrait failures below
(no skips; Category=Measurement excluded). NuGet and symbol packages built and inspected.
This is preparation of the measured implementation, not a claim that the original DoD is fully met: the 1.5×
single-thread encoder target and +0.5% BD-rate gate were missed; ARM64, lower decoder allocations, fused colour
conversion/rotation and the 48 MP peak-memory measurements remain outstanding. The user authorized push, the v0.6.6 tag and release publication on 2026-10-19.
Two Local portrait test failures reproduce unchanged on 0.6.5 with the current private samples (missing aperture
and insufficient background improvement on IMG_6854); tracked with the 0.6.7 calibration work.
See [release notes](docs/releases/release-0.6.6.md).

### 0.6.7 — Portrait effect calibration and Apple's rendering parameters

E0/E1 completed 2026-10-19: the catalogue and frozen baseline cover 15 originals / 66 exports (13 clean scenes /
55 pairs, two edited scenes / 11 pairs). The test-only reference preserves the 0.6.6 renderer, identical to the
0.6.5 portrait model. Native rendering, 1536-pixel measurement long side; fixed private ROI/focus/LUT manifest.
See `docs/measurements/portrait-0.6.7.md` and `portrait-0.6.7-baseline*` for the protocol and results.
Refocus points are provisional until E2; five edited refocus pairs have no reliable tone normalization.
The current model does not meet the focused-subject preservation DoD; calibration and SIMD work remain open.
E2 measurements now cover 23,232 tiles over all 66 exports; the global fitted radius model is deferred because
scene-held-out regressions remain. A first E3 correction reduces person-matte foreground blur at wide apertures,
fading out by f/8: all 66 native pairs pass regional regression checks; clean refocus subject PSNR improves by
2.745 dB and edges by 2.155 dB, with background essentially unchanged. Full results and limitations:
`docs/measurements/portrait-0.6.7-calibration.md`. The subsequent capture-focus experiment validated sensor-to-display
mapping on all 15 scenes, but the SubjectArea-centre policy regressed 14 pairs and was rejected; 29 explicit refocus
renders stayed byte-identical. See `docs/measurements/portrait-0.6.7-capture-focus.md`. A joint focus/radius
screening selected a default focus *range* (median to 97.5th percentile under the matte, 90th to 99.5th scene
percentile without one; explicit `FocusPoint` keeps a single plane), accepted 2026-10-19. By the user's decision the
three leaf scenes (low-light test shots) form a separate `low-light-test` cohort outside the clean aggregates; every
pair still passes the −0.5 dB regional gate. Clean cohort (10 scenes / 52 pairs) versus E1: background +0.092 dB
(the background DoD condition, now asserted by the Local test), subject +2.59, edges +1.11, hair +0.41, focused patch
+4.79 dB; 29 refocus renders byte-identical. See `docs/measurements/portrait-0.6.7-focus-range.md`.
E4 (matte edges and hair) accepted 2026-10-19: subject/background depth split along the matte outline, unmixing of
strands from the background behind them and a softened outline at wide apertures. Clean cohort versus E1: background
+0.097, subject +2.61, edges +1.21, hair +0.80, focused patch +4.87 dB. By the user's decisions edge/hair bands at
f/8–f/16 and `kapiel_2x` f/4.5 edges are reported outside the per-pair gate. Two held-out iPhone 13 Pro scenes
(`samples/portrait-validation/`, own frozen baseline, 12 pairs) pass the gate. See
`docs/measurements/portrait-0.6.7-edges.md`.
E5 (bokeh and texture) closed 2026-10-19: ISO-scaled grain, hard discs for point lights, less blur for scenery in
front of the focus outside the matte at wide apertures, a radius knee for the 2× crop lens, and in night shots
(ISO × exposure time) clipped lamps scattered as cat's-eye discs after Apple's highlight-recovery patent. Clean cohort
versus E1: background +1.07, subject +3.82, edges +1.67, hair +1.36, focused patch +5.17 dB; highlight PSNR
22.68 → 24.71; subject worse than unblurred 11 → 8 region-pairs. The gate passes with E4's exceptions only;
validation passes. See `docs/measurements/portrait-0.6.7-bokeh.md`. Open: subject worse than unblurred in 8
region-pairs (later reduced to 6), strands behind the head. No publication yet.
DoD follow-up 2026-10-19: in object portraits a no-blur zone in front of the focus (5 px at f/4.5) fixes słupek
refocus f/8–f/16; subject worse than unblurred 8 → 6 region-pairs (leżak, słupek f/16, basen, rynek −0.01 dB).
E6/E7 completed 2026-10-20: parallel light detection and scatter, SIMD disc rows and layer convolution, pooled buffers;
a render is 2.2–3.1× faster on one thread and 2–15× with all threads (48 MP f/1.4: 32.1 → 2.2 s), allocation mostly
−30…60 %, peak −3…+7 % (night scene +11–19 %). `φ·√φ` replaces `MathF.Pow`; `Vector3/4.Lerp`, which fuse to FMA on
some CPUs, were replaced by separately rounded operations (metrics move ≤ 3.4·10⁻⁵ dB). Renders are bit-identical for
any thread count, SIMD and scalar, x64 with and without FMA, Native AOT and ARM64 (136/136). Tests: x64 731 passed /
10 skipped (opt-in tools), Local 12/12 (66-pair gate, validation), ARM64 719 passed / 21 skipped (Oracle tools absent
there: reported gap); NuGet consumer with Native AOT linux-x64 without warnings. See
`docs/measurements/portrait-0.6.7-engineering.md`. New basen exports deferred to 0.6.8 (`samples/portrait-0.6.8/`).
**Release decision (2026-10-20):** the user requested the release of 0.6.7 on GitHub and NuGet. The DoD condition
"subject not worse than the unblurred photo" (6 region-pairs) and strands behind the head remain open and are
documented as known limitations; the release is made by the user's decision with these exceptions.
See [release notes](docs/releases/release-0.6.7.md) and [details](docs/releases/release-0.6.7-details.md).

Planned 2026-10-18 at the user's request. 0.6.5 fitted the blur to four photos from one iPhone (2× and 4× lenses,
all at f/4.5), so the radius curve, the aperture law and the matte gating rest on little data. The user will add more
Portrait photos with Photos exports.

- **Reference set (supplied by the user, private, `samples/`, never committed):** each original `IMG_xxxx.HEIC` with
  one or more Photos exports `IMG_xxxx_2.HEIC`, `IMG_xxxx_3.HEIC`, … Wanted coverage: every lens offering Portrait (1×,
  2×, tele, front camera); the same photo exported at several apertures (e.g. f/1.4, f/2.8, f/4.5, f/8, f/16) and with
  a changed focus point; scenes with point lights (bokeh highlights), fine hair, glasses, several people at different
  depths, foreground objects in front of the subject, and Portrait photos of pets or objects (no person matte); other
  iPhone models when available. First check whether an export records the changed aperture and focus in its own depth
  XMP; if not, the file name or a sidecar list (`samples/portrait-pairs.tsv`: export, aperture, focus point) names them.
  Layout agreed 2026-10-19 (the user replaces the portrait samples): one folder per scene and camera,
  `samples/portrait/<scene>-<1x|2x|4x>/` (`selfie` for the front camera), with a free-text `opis.txt`. Each lens gets its own scenes (the user does not shoot one scene
  with several lenses); the focal-length factor is fitted across scenes, the disparity carrying the distance. Photos exports HEIF in three forms, used as:
  the original (`IMG_xxxx.HEIC`, once per photo, iPhone name; with `IMG_xxxx.AAE` when available) — the input; the
  portrait applied (`IMG_xxxx_<variant>.HEIC`) — the blur reference, lossless of JPEG artefacts and in the container
  we decode; the final look (`IMG_xxxx_<variant>_final.HEIC`) — the reference for edits and styles, needed only for
  `e`/`s` variants and once for a `ref` to confirm it equals the portrait-applied form when nothing else is edited.
  The earlier `_2` files match the portrait-applied form (blur rendered, depthBlurEffect XMP kept); `IMG_6854_2`
  matches the final look (Clean Up, colours, no depthBlurEffect XMP). Variants: `ref` (no change), `a<f-number>`
  (aperture only, e.g. `a1.4`, `a16`), `f<n>` (focus point only, described in `opis.txt`), `e<n>` (Photos edits, plus
  `edits.tsv` of that edit state), `s<n>` (style change, 0.6.8); combinations join with `_` (`a2.8_f1`).
  The variant's settings come from the name and `opis.txt`: checked on the first delivery (`ogrod_4x`, IMG_6011,
  2026-10-19), the portrait-applied exports at f/2.8, f/4.5 and f/8, with and without refocus, all keep
  `SimulatedAperture` 4.5, a byte-identical `RenderingParameters` blob and the same disparity (max 0.346); they carry
  no mattes and no styles item (the original has portrait, hair and other semantic mattes). Folder separators `-` and
  `_` are both accepted; `refocus`, `refocus<n>` or a named target (`refocus_chlopiec`, `refocus_fontanna`) is accepted for `f<n>`; when the original was taken at the
  exported aperture, the matching `a<N>` export without refocus serves as `ref`. 1× and 2× come from the main camera (6.765 mm, 2× a
  crop), 4× from the telephoto (16.891 mm); EXIF (35 mm equivalent 24/48/100) is authoritative, the folder name is for
  people. Non-portrait samples (iPhone 13 Pro, Live Photo with `.MOV`, gain maps) stay directly in `samples/`, where
  the other Local tests look (they do not recurse). The Local portrait tests are rewritten for this layout (they
  currently pair only `IMG_xxxx_2`).
- **Export brightness (measured 2026-10-19 on all 15 scenes):** even without edits, Photos exports are slightly
  brighter than the decoded original, almost equally in R, G and B (mean +1 to +11 levels, e.g. basen +1, selfie
  +10); cause not yet known (export tone rendering, gain map, style). Edited scenes stand out by unequal channels:
  `wlosy` (filter, +28/+16/+6) and `portret_4x` (slider edits, +7/+10/+12). The comparison therefore fits a
  per-channel tone curve from original to export on sharp regions before measuring blur, and reports scenes with a
  filter separately.
- **Calibration tooling:** a `Category=Local` measurement that fits Apple's blur radius per tile against disparity (as
  done for 0.6.5 with disc kernels) and writes per-photo and pooled results to `docs/measurements/portrait-*.tsv`
  (numbers only, no pixels or locations); the Local comparison (PSNR/SSIM of background, subject and a band along the
  matte edge) reports every export, plus the 0.6.5 model as the baseline.
- **Model questions to settle with the data:** the aperture law (radius ∝ 1/N, or another curve), the focal-length
  factor (φ^1.5 fitted on two lenses), the in-focus zone and any saturation of the largest radii; blur of objects in
  front of the focus; focus selection (median under the matte, the face, or the nearest subject); matte gating and the
  semantic mattes (hair) at edges; behaviour without a person matte; the kernel (disc vs Apple's shape, highlight
  boost of bright bokeh, grain over blurred areas). Each change is kept only when it improves the pooled result.
- **Findings on IMG_6854 (2026-10-19, 0.6.6 renderer vs the Photos export):**
  - *Hair edges:* Photos keeps stray hair strands over the blurred background sharp; 0.6.6 blurs them away
    (one strand over dark trees: original 26–210, Photos 70–176, ours 50–134 grey levels). The portrait matte covers
    the strand (174/255 at 1512×2016), but `Protection` fades out once the radius from the bilinearly upsampled
    576×768 disparity exceeds `matteHigh` (8 px at 4032), and that disparity sees the strand as background. To try: let
    the matte (and the semantic hair matte, unused so far, also 1512×2016) set the strand's disparity or radius instead
    of gating on the background radius; upsample the matte edge-aware (guided by the photograph) rather than
    bilinearly; measure on the matte-edge band.
  - *Grain:* Photos keeps fine texture in blurred areas; high-pass deviation (7×7 box residual) in flat blurred sky
    0.72 original / 0.38 Photos / 0.12 ours, blurred trees 2.48 / 0.78 / 0.28 — about 3× smoother than Photos. To try:
    re-add the photograph's noise (or synthetic grain matched to its level) in proportion to the blur.
  - *Not a clean reference:* `IMG_6854_2` was also edited with Clean Up (object removed bottom left; XMP
    `photoshop:Credit=Apple Photos Clean Up`, IPTC `compositeWithTrainedAlgorithmicMedia`) and with filters and
    manual colour tuning (mean RGB +33/+19/+7; confirmed by the user), and has no depthBlurEffect XMP and a regenerated
    disparity (max 0.37 vs 0.73). Its global PSNR/SSIM are not comparable: exclude it from pooled numbers (or detect
    Clean Up / missing `SimulatedAperture` in the export and skip), use it only for local edge and grain checks. This
    explains the two Local portrait failures noted in 0.6.6.
- **`depthBlurEffect:RenderingParameters`:** inventory completed on the 15 supplied originals (2026-10-19):
  1352-byte `REND` version-7 blobs, a 16-byte header and 167 eight-byte entries (u16 key, u16 type, 4-byte payload;
  observed type 1 float32 and type 2 int32). All 167 keys are present in every original. Every export preserves its
  original's blob. Values do vary within a lens, particularly keys 400–403 and 450–453, including object-only
  scenes; the earlier four-photo lens-only/face-only hypotheses are superseded. Inventory and confidence levels
  are in `docs/decoding.md`. No value is used by the renderer because no meaning or predictive relation has been
  validated on independent scenes; a parsed public view remains deferred. Own-file format inspection only.
- **Engineering carried over from 0.6.5 (E6/E7):** SIMD (`Vector128`/`Vector256`) for layer building and the per-pixel gather,
  identical to scalar on x64 and ARM64; `MathF.Pow(φ, 1.5)` replaced by `φ·√φ` for bit-identical results across
  platforms; lower allocation of `RenderPortrait` (0.41–1.1 GiB in 0.6.5); the ARM64 run and the package consumer with
  Native AOT.
- **Out of scope:** Photos edits (0.6.8), Photographic Styles (0.6.10), Portrait Lighting and Clean Up
  (generative fill), writing the effect or depth back to HEIC, claims of matching Photos. Edited exports already
  supplied (`portret_4x`, IMG_8083) are kept for 0.6.8 and used in 0.6.7 only where the edit does not matter.
- **DoD:** calibration report on at least 15 originals with exports covering at least three lenses and three
  apertures; pooled background PSNR better than the 0.6.5 model, no pair worse by more than 0.5 dB, subject not worse
  than the unblurred photo; aperture law confirmed against exports at several apertures; blob findings in
  `docs/decoding.md` (or a separate note) with confidence levels; thread-count independence; tests, XML docs,
  guide, API snapshot (if the API changes); SIMD identical to scalar on x64 and ARM64; time, allocation and peak
  memory on 12/24/48 MP; full Release, Oracle and Local tests, a NuGet consumer with Native AOT and a physical ARM64
  run (a missing ARM64 run or skipped Oracle is reported as a validation gap, not a pass). Publication on a separate
  user decision.

### 0.6.8 — Photos edits described by hand

Added 2026-10-19; **rewritten 2026-10-20**. The first plan read the edits from `.AAE` sidecars. Inspection of the
supplied files (`samples/edit/IMG_8017`, `IMG_8140`, iOS 27.0.1, build 23G83) showed that this does not work: the
`.AAE` is an XML plist whose `adjustmentData` is raw-deflate JSON (`adjustments[]` with `identifier`, `formatIdentifier`,
`settings`), but both files hold only the capture-time `DepthEffect` of `com.apple.camera` (aperture, `focusRect`,
faces, `depthInfo`) — no tone, colour, filter or style adjustments, and no crop. The Photos exports differ from the
originals by lighting and colour (IMG_8140: mean +35 levels, 128×170 px centre crop; IMG_8017: warmer and more
saturated) that no sidecar records. Edits made in Photos also produced no `.AAE` with the usual export routes
(`tower/IMG_7678`: exports only, neither the original nor the exports carry adjustment data). Public documentation of
`adjustmentData` and of any iOS 26/27 change was not found. So edits are **annotated by hand and fitted from pairs**.

- **Annotation instead of a sidecar:** each edited export is described by the user in `samples/edit/<scene>/edits.tsv`
  (private, never committed): `export`, `original`, `adjustment`, `value` (the slider value as shown in Photos,
  −100…+100 where the slider has it; sharpness, definition and noise reduction run 0…100 only), one row per adjustment, plus `filter` rows (name and strength) and `crop` rows
  when known. File names carry the variant as before (`IMG_xxxx_e<n>.HEIC`; the earlier `tower/IMG_7678_e50` and
  `_e-50` read as „exposure ±50”, to be confirmed in `edits.tsv`). Two files that differ only in letter case
  (`.HEIC`/`.heic`) are renamed on import (`IMG_xxxx.HEIC`, `IMG_xxxx_e1.heic`).
- **Sidecar kept for what it does hold:** a small read-only reader for the `.AAE` `DepthEffect` (`aperture`,
  `focusRect`, `depthInfo`, faces) is still worth having: it gives the portrait renderer the aperture and focus the
  photographer set at capture and re-edit time (BCL only: plist, `DeflateStream`, `JsonDocument`; corrupt input →
  `ImageFormatException`; unknown adjustments are reported, not applied). It is not needed to fit the edits and does
  not block them. Unscheduled until a concrete need.
- **API:** decided after the first fits. Likely a plain description of adjustments the caller sets by hand
  (record with the supported sliders, applied to a decoded photograph by a rendering method), with no dependency on
  any file; values come from the application or from `edits.tsv` in the tests.
- **Rendering:** approximate each adjustment on the supplied single-adjustment pairs (tone: exposure, brilliance,
  highlights, shadows, contrast, brightness, black point; colour: saturation, vibrance, warmth, tint; sharpness,
  definition, noise reduction, vignette; filters) and fit its curve, as for the blur. Order of operations and colour
  space follow what the data shows. The export brightness offset seen even without edits (see 0.6.7) is measured
  first and removed from the fit. Crop and straighten only when `edits.tsv` states them. Clean Up and Portrait
  Lighting are not reproduced.
- **Reference pairs:** the original and the export with one adjustment at a time at a few strengths
  (−100/−50/+50/+100; 25/50/100 for the 0…100 sliders) plus every filter, so each curve can be fitted separately, then combined edits to check the
  order. `edits.tsv` is the only source of the values; an export without it is not used for fitting.
- **Samples so far:** `street/IMG_8113` and `stadion/IMG_7758` (single-adjustment series, `edits.tsv` generated by
  `scripts/make-edits-tsv.py` from the Polish file names), `tower/IMG_7678` (original, `_e50`, `_e-50` — values to confirm), `IMG_8017` and `IMG_8140`
  (original, export, `.AAE` with `DepthEffect` only; colour and brightness change unexplained), `portret_4x`
  (IMG_8083, slider edits, values requested in `opis.txt`).
- **Status 2026-10-20 (first fits, analysis only, no API):** `scripts/fit-photos-edits.py` fits candidate models
  (per-channel sRGB curves, luma curves, linear-light gains/matrices, chroma gain by saturation, radial gain, Gaussian
  band gains) to `street` (IMG_8113, 51 single-adjustment exports of 15 sliders + 1 repeat), `stadion` (IMG_7758,
  22 exports: exposure, brightness, brilliance, shadows, highlights) and `perla` (IMG_7725, 21 exports: exposure,
  highlights, shadows, brightness, contrast, definition); the analysis is closed on these three scenes. Report
  `docs/measurements/photos-edits-0.6.8-fit.md` with residuals per pair, slider holdout (±25 from ±50, ±50 from
  ±100, sign negation), scene transfer and confidence levels. Findings: tone sliders act per channel on sRGB values
  (contrast on luma); repeat-export noise floor rms8 0.77; well determined: warmth, tint, vignette (±50), brightness
  (transfers between scenes, ratio 0.16–0.35), contrast with moderate error (0.25–0.32); filters (sharpness, noise reduction, definition) are global but
  non-linear (linear band model insufficient; noise reduction worse than unedited at 8×8 scale); scene-adaptive:
  exposure (opposite behaviour by scene: highlight-protecting at small + values at sunset/night, strong lift in
  daylight; 21 of 38 scene transfers worse than unedited), highlights (scales with the bright area, colour-dependent
  on `perla`), brilliance (local), shadows +, black point, saturation, vibrance; definition medium. DoD not met yet: more
  scenes, finer slider steps, 0…100 sliders at 25/100 and combined edits are needed (list in the report); the API
  shape (fixed vs scene-adaptive operations) is decided after a leave-one-scene-out fit.
- **API (decided 2026-10-20 by the user: all sliders now, weaker ones refined later):** `HeifPhotosAdjustments`
  (record with the 15 sliders on the Photos scale, `MaxDegreeOfParallelism`) and `HeifPhotosEdit.Render(photo,
  adjustments)`. Parameters come from `scripts/make-photos-tables.py` (fitted curves averaged over the three scenes,
  generated `Codecs/HeifPhotosTables.cs`), rendered by `Codecs/HeifPhotosRenderer` (per-channel curves, Gaussian bands,
  luma contrast, chroma gain, radial vignette; fixed order, scalar, row-parallel, thread-count independent). Tests:
  `HeifPhotosEditTests` (synthetic: identity, ranges, signs, alpha, determinism, cancellation) and
  `HeifPhotosEditLocalTests` (all 94 annotated pairs decoded within 1632×1632, ≤2 MP, 18 s; warmth, tint, vignette, brightness, contrast must beat the
  unedited photo on every pair — they do; in-sample 86/94 pairs beat it, the exceptions are 7 exposure pairs and noise
  reduction). Guide section in `docs/decoding.md`, release notes `docs/releases/release-0.6.8.md`, API snapshot.
  Speed: 12 MP 0.2 s (tones) / 1 s (with bands), 24 MP 1.9 s.
- **Deferred to a later version:** scene-adaptive exposure, highlights, brilliance, shadows, black point (curve as a
  function of image statistics, leave-one-scene-out); edge-aware sharpness and noise reduction; colour-dependent
  response (perla); order of operations from combined edits; ±25/±100 for the ±50-only sliders; SIMD for the band
  filters; Photos filters, crop and straighten. The DoD point "closer than unedited on every pair" holds for the
  closest five sliders only; the rest are documented as rough.
- **Out of scope:** reading edits from `.AAE`, Photographic Styles (0.6.10), Portrait Lighting and Clean Up
  (generative fill), writing edits back to HEIC or `.AAE`, claims of matching Photos.
- **DoD:** every supplied pair annotated in `edits.tsv`; each supported adjustment fitted on single-adjustment pairs
  and closer to the Photos export than the unedited photo on every edited pair (pooled and per pair); unsupported
  ones listed; findings in `docs/` with confidence levels; tests, XML docs, guide, API snapshot (if the API changes).
  Publication on a separate user decision.

### 0.6.9 — rendering API

Added 2026-10-21 at the user's request: editing before the Portrait blur needed the depth and matte loaded by hand,
and three separate entry points repeated parallelism and limits. User decisions: break compatibility now (as 0.6.3),
a fixed order with the sliders before the blur, Styles moved to 0.6.10.

- **API:** `HeifEdits` (`Adjustments`, `Portrait`, `MaxPixels`, `MaxDegreeOfParallelism`), `HeifDocument.Render`,
  `HeifDocument.LoadPortraitMaps` → `HeifPortraitMaps` (depth + matte, owns the matte), `Image.ApplyHeifEdits`.
  Removed: `HeifPortrait`, `HeifPhotosEdit`, `HeifDocument.RenderPortrait`, `MaxPixels`/`MaxDegreeOfParallelism` in
  `HeifPortraitOptions` and `HeifPhotosAdjustments`. Internal `Codecs/HeifEditPipeline` (order, budget of both
  stages) and `Codecs/HeifPortraitSetup` (code moved unchanged from `HeifPortrait`).
- **Order check:** compare sliders→blur with blur→sliders on `portrait/portret_4x` (IMG_8083, Photos export with
  portrait and slider edits) once the slider values are annotated (`opis.txt` still lacks them).
- **DoD:** pixels of each stage identical to 0.6.8 (SHA-256 of six cases before/after the change); pipeline tests
  (order, reused maps, empty edits, no depth, budget, threads, cancellation); API snapshot; guide, README, release
  notes with a migration table. Publication on a separate user decision.
- **Status 2026-10-21:** implemented; Release build 0 warnings; hashes identical in all six cases; targeted tests
  (`HeifEditsTests`, `HeifPortraitTests`, `HeifPhotosEditTests`, `PublicApiTests`) 53/53. Full suite not run (user
  decision: no rendering changes; the release workflow runs it). Released 2026-10-21 on the user's instruction with the
  order check on `portret_4x` still open (slider values missing).

### 0.6.10 — writing HDR gain maps

Added 2026-10-23 at the user's request ("next stage" after core 0.8.0 stage A): the last item of the core 0.8.0 DoD is
a plugin that writes the attached map through the new SPI. Moved here from "Beyond 0.7.0"; Styles renumbered to
0.6.11, as on 2026-10-21.

- **API:** `HeifEncoderOptions.GainMap` (`HeifGainMapMode` `None` — the default, output unchanged —, `Preserve`,
  `Require`, the semantics of `JpegGainMapMode`) and `GainMapQuality` (0–100, the HEVC quality of a re-encoded map).
- **File layout (as iPhones write it):** the map as a hidden `hvc1` item (4:0:0 for a gray map, otherwise 4:2:0 BT.601
  full range; `colr` nclx; the base's orientation transforms, `clap` for odd sizes); a `tmap` item in `idat`
  (version 0 + `GainMapMetadata.ToIso()`, `ispe` of the displayed base, `colr prof` from `ToAlternateIcc()` when the
  gain is applied in other primaries) deriving from the primary image and the map (`dimg`); an `altr` group
  (`tmap`, primary) and the `tmap` brand. No map in the embedded thumbnail.
- **Copying:** when `GetGainMap` returns this plugin's own map (unedited, not reoriented) and the base is written
  without orientation transforms, the HEVC stream is copied with its own transforms (single image or grid of 8-bit
  single-slice tiles sharing parameter sets); otherwise the map is decoded and encoded again.
- **Core need (closed in core 0.8.0 before its release):** `GainMapMetadata.ToAlternateIcc()` — the plugin's reader
  takes the multiplication primaries from the `tmap` ICC, and `ParseIso` accepts only ICC.
- **DoD:** HEIC→HEIC (corpus and local iPhone files), JPEG (Ultra HDR)→HEIC and edited/oriented maps reload with
  identical metadata and a map within a measured budget; copied maps byte for byte; `ToHdr` of the written file;
  libheif decodes the written files (base) and HM the map stream; modes, corrupt-free robustness of the new path;
  API snapshot, guide, README, release notes. Requires core 0.8.0 on NuGet before publication.

### 0.6.11 — Photographic Styles

Renumbered from 0.6.9 on 2026-10-21 (the rendering API took 0.6.9) and from 0.6.10 on 2026-10-23 (gain map writing
took 0.6.10). A style change becomes `HeifEdits.Style`, a stage between the sliders and the Portrait blur.

Planned 2026-10-19 at the user's request (moved out of 0.6.7, renumbered from 0.6.8 when the edits moved). iOS 18
and later let Photos change a photo's
Photographic Style (and its tone, colour and palette) after capture; the user suspects a changed style, not slider
edits, caused the colour shift of IMG_6854. Builds on the adjustment rendering and the `edits.tsv` annotation of 0.6.8.

- **Known so far (2026-10-19, fifteen originals):** the capture-time style setting looks identical in all of them
  (Apple MakerNote 0x54 the same dictionary; 0x41/0x42 zero), so a later style change, like an edit, is recorded only
  in the library (the `.AAE` samples of 2026-10-20 hold the capture-time `DepthEffect` only). Every original from iOS 18+ carries a styles item
  (`uri tag:apple.com,2023:photo:metadata:styles`, about 58 KB binary plist): tone statistics per region (`6`:
  linear and tone-mapped image, skin, person; percentiles, black/white point), person/skin ratios (`7`), scalars
  (`0`, `2`, `4`, `5`, `h`, `j`, `k`, `e`, `f`, `g`) and blobs (`1` 51 840 B, `3` 516 B, `c`/`d` 2 048 B — likely
  curves or LUTs in half floats). Meanings unconfirmed.
- **Reference set (supplied by the user):** one photo exported several times with only the style changed (every
  preset, plus tone/colour/palette extremes), each described in `edits.tsv`; the same style on photos with and without people
  (skin/person statistics); IMG_6854 to separate style from slider edits.
- **Work:** take the style change from `edits.tsv`; decode the styles item (blob layouts,
  half-float curves/LUTs, their axes) and document it with confidence levels; render a style change as an
  approximation fitted to the exports (likely: undo the capture style using the item, apply the new one), in the
  0.6.8 edit pipeline order the data shows. Format inspection of the user's own files only; no Apple code or binaries.
- **Out of scope:** applying styles to photos without a styles item (iOS 17 and earlier), writing styles back,
  claims of matching Photos.
- **DoD:** styles item parsed on every supplied original (and synthetic corrupt input: `ImageFormatException`);
  style changes annotated for every pair; the rendered style change closer to the Photos export than the unedited
  photo on every style-only pair (pooled and per pair); findings documented; tests, XML docs, guide, API snapshot (if
  the API changes). Publication on a separate user decision.

### 0.7.0 — integration and closing the scope

- No new features: regression of the catalog, thumbnails, depth, portrait effect, HDR and Live Photo; a corpus with combinations of
  thumbnails, alpha, depth, gain maps and vendor auxiliary images; memory/time measurement of opening the catalog of
  large files.
- 0.6.0 API compatibility, architecture, SIMD/scalar and thread-count determinism.
- Validation of the NuGet package and examples; documentation of capabilities and limitations.
- **DoD:** the 0.6.1–0.6.11 criteria met, documentation consistent with the package, no regressions. Only then update
  VERSION and the status in the plan; publication on a separate user decision.

### Common assumptions and boundaries

- The new API belongs to `PictureSharp.Formats.Heif`. Returned images are owned by the user; operations do not close
  user streams.
- The catalog does not allocate pixels. Reading a photo accounts for its dependencies. The raw sample API provides
  equivalent limits before allocation, despite having no core context.
- Buffered input through `Stream` does not mean streaming of large files.

### Beyond 0.7.0

- Writing collections of multiple photos (`HeifCollection`, primary selection, `ipma` > 127) — once a concrete need is
  reported.
- HEIF sequences: reading/writing tracks — together with the HEVC P/B decoder (all-intra frames alone are rare).
- Depth/disparity interpretation beyond the Apple portrait description (other vendors, metric depth, calibration,
  conversions between depth and disparity); portrait lighting effects.
- Live Photo: exporting/writing pairs, video decoding and editing.
- Writing depth and arbitrary auxiliary images; user-supplied thumbnails;
  editing without recompression; overlay, groups and regions; lossless mode, 4:4:4 and 10-bit in the encoder.
- Unscheduled: AVIF, new HEVC profiles, CICP PQ/HLG, files larger than the available buffers.

### Sources for the scope (review 2026-10-18)

- Collections, thumbnails, auxiliary and derived images: [HEIF Technical Information](https://nokiatech.github.io/heif/technical.html).
- Functional comparison (neither a dependency nor a porting source): [libheif-sharp](https://github.com/0xC0000054/libheif-sharp).
- Depth: [Apple — Creating auxiliary depth data manually](https://developer.apple.com/documentation/avfoundation/creating-auxiliary-depth-data-manually).
- Live Photo: [Apple — Capturing and saving Live Photos](https://developer.apple.com/documentation/avfoundation/capturing-and-saving-live-photos).

## Principles

Technical constraints, licences, patents, ports and oracles are described in [AGENTS.md](AGENTS.md). Additions:

- Oracle tools from `scripts/build-heif-reference.sh` (HM 16.20, kvazaar 2.3.1, x265 3.4, libde265 1.0.15,
  libheif 1.20.2); sources in `reference/` are read-only, downloading requires user approval.
- Receiver compatibility (Windows HEIF Image Extensions, Apple Photos) is checked manually when available; a missing
  test is not a pass and does not justify a compatibility claim.
- Repository `art-ix/PictureSharp.Heif`; GitHub Actions disabled at the user's request, the workflow
  `.github/workflows/ci.yml` stays for later enabling.

## Dependencies on the PictureSharp core

Used: 0.7.1 (`AttachGainMap` with `HdrInfo.Detected`), 0.7.6 (`FormatDecoderOptions`), 0.8.0 (`GetGainMap`,
`GainMapMetadata.ToIso()`/`ToAlternateIcc()`, used by 0.6.10). Planned in core later: possibly `ReportStoredSize` for thumbnails — only once a need is shown. New
SPI needs are reported to the core plan; we do not work around them by copying internal code.

## Common quality gate for every version

- Release build with no warnings, full Release tests and Oracle before closing a version. A skipped oracle does not
  confirm compatibility.
- Synthetic corpus, tests of corrupt/truncated data and of limits for new paths.
- New SIMD: identity with scalar on x64 and real ARM64, fallback checks; parallelism: determinism and resource release
  after an error.
- Up-to-date XML docs, public API snapshot, examples, notices and a package with `LICENSE` and
  `THIRD-PARTY-NOTICES.txt`.
- An entry in `progres.md` after each significant step. The plan is updated after the DoD is actually met.

## Changelog

- [0.6.6](docs/releases/release-0.6.6.md): faster HEVC decoding and encoding (2026-10-19).
- [0.6.7](docs/releases/release-0.6.7.md): portrait calibration, faster deterministic portrait rendering (2026-10-20).

- [0.6.5](docs/releases/release-0.6.5.md): Apple Portrait effect (`HeifAppleDepthInfo`, `RenderPortrait`, `HeifPortrait`, `HeifDepthImage.ToImage`).
- [0.6.4](docs/releases/release-0.6.4.md): raw depth samples (`LoadDepth`, `HeifDepthImage`) and Live Photo pairing (`LivePhotoPair`).
- [0.6.3](docs/releases/release-0.6.3.md): `HeifDocument` API correction (incompatible with 0.6.2), `ReadMetadata`, `FindThumbnail`.
- [0.6.2](docs/releases/release-0.6.2.md): catalog of photos and relations, preview selection and thumbnail generation.

- [0.6.1](docs/releases/release-0.6.1.md): compressed XMP (`deflate`) within the `MaxMetadataBytes` limit.
- [0.6.0](docs/releases/release-0.6.0.md): item selection through `HeifDecoderOptions.ItemId`, minimum PictureSharp 0.7.6.
- [0.5.0](docs/releases/release-0.5.0.md): first public release, SIMD and parallelism.
- 0.5.1 was not carried out; the compressed XMP plan was moved to 0.6.1.
- Earlier versions (0.1.0–0.4.0, not released publicly): [docs/history.md](docs/history.md).
