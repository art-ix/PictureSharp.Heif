# PictureSharp.Heif: development plan

## Status and goal

**Current version: 0.6.6, prepared 2026-10-19 (not published); minimum PictureSharp 0.7.6.** A C# format plugin with no native libraries, with its own HEVC intra decoder and encoder.

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
Apple portrait effect (disparity description and depth-of-field rendering). Delivered in releases 0.6.1–0.6.7,
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
| 0.6.6 | Faster HEVC decoder, WPP for single pictures, faster intra search and fewer encoder allocations | 0.7.6 | Prepared locally; not published |
| 0.6.7 | Portrait effect calibration on more photos, analysis of Apple's rendering parameters, SIMD | 0.7.6 (assumed) | Planned |
| 0.7.0 | Integration, regressions, documentation and package for the whole scope | 0.7.6 (assumed) | Planned |
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

**Release preparation (2026-10-19):** version 0.6.6 is prepared locally at the user's request after acceptance of
before/after PNGs (no visible difference reported). Encoder: 12 MP Q50, 30.761→21.828 s on one thread,
4.200→2.858 s on all threads; allocations 388.9→322.1 MiB. Decoder: 12/24 MP 568/938 ms on one thread,
103/161 ms on all threads; 48 MP WPP 848/173 ms. API and dependency versions are unchanged.
Release build: zero warnings/errors; 703/705 tests passed with the two known Local portrait failures below
(no skips; Category=Measurement excluded). NuGet and symbol packages built and inspected.
This is preparation of the measured implementation, not a claim that the original DoD is fully met: the 1.5×
single-thread encoder target and +0.5% BD-rate gate were missed; ARM64, lower decoder allocations, fused colour
conversion/rotation and the 48 MP peak-memory measurements remain outstanding. No push, tag or publication.
Two Local portrait test failures reproduce unchanged on 0.6.5 with the current private samples (missing aperture
and insufficient background improvement on IMG_6854); tracked with the 0.6.7 calibration work.
See [release notes](docs/releases/release-0.6.6.md).

### 0.6.7 — Portrait effect calibration and Apple's rendering parameters

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
- **Calibration tooling:** a `Category=Local` measurement that fits Apple's blur radius per tile against disparity (as
  done for 0.6.5 with disc kernels) and writes per-photo and pooled results to `docs/measurements/portrait-*.tsv`
  (numbers only, no pixels or locations); the Local comparison (PSNR/SSIM of background, subject and a band along the
  matte edge) reports every export, plus the 0.6.5 model as the baseline.
- **Model questions to settle with the data:** the aperture law (radius ∝ 1/N, or another curve), the focal-length
  factor (φ^1.5 fitted on two lenses), the in-focus zone and any saturation of the largest radii; blur of objects in
  front of the focus; focus selection (median under the matte, the face, or the nearest subject); matte gating and the
  semantic mattes (hair) at edges; behaviour without a person matte; the kernel (disc vs Apple's shape, highlight
  boost of bright bokeh, grain over blurred areas). Each change is kept only when it improves the pooled result.
- **`depthBlurEffect:RenderingParameters`:** analyse the private blob across all photos. Known so far (four photos,
  iPhone 17 Pro): a 16-byte header (`REND`, version 7, size, 1) followed by 8-byte entries (u16 key, u16 type 1 =
  float32 or 2 = int32, 4-byte value), 168 entries; values differ between lens configurations, not between photos of
  one lens, and keys 400–453 appear only in one photo (possibly face-related). Catalogue keys and values per photo,
  correlate them with lens, model, scene and the fitted blur parameters, and document what is known and unknown. Use a
  value in rendering only when the correlation holds on held-out photos; otherwise keep the blob raw. A parsed view in
  the public API (entries as key/type/value, without meanings) is decided after the analysis. Format inspection of
  the user's own files only; no Apple code or binaries.
- **Engineering carried over from 0.6.5:** SIMD (`Vector128`/`Vector256`) for layer building and the per-pixel gather,
  identical to scalar on x64 and ARM64; `MathF.Pow(φ, 1.5)` replaced by `φ·√φ` for bit-identical results across
  platforms; lower allocation of `RenderPortrait` (0.41–1.1 GiB in 0.6.5); the ARM64 run and the package consumer with
  Native AOT.
- **Out of scope:** Portrait Lighting, writing the effect or depth back to HEIC, claims of matching Photos.
- **DoD:** calibration report on at least 15 originals with exports covering at least three lenses and three
  apertures; pooled background PSNR better than the 0.6.5 model, no pair worse by more than 0.5 dB, subject not worse
  than the unblurred photo; aperture law confirmed against exports at several apertures; blob findings in
  `docs/decoding.md` (or a separate note) with confidence levels; SIMD identical to scalar on x64 and ARM64 and
  thread-count independence; time and memory on 12/24/48 MP; tests, XML docs, guide, API snapshot (if the API
  changes). Publication on a separate user decision.

### 0.7.0 — integration and closing the scope

- No new features: regression of the catalog, thumbnails, depth, portrait effect, HDR and Live Photo; a corpus with combinations of
  thumbnails, alpha, depth, gain maps and vendor auxiliary images; memory/time measurement of opening the catalog of
  large files.
- 0.6.0 API compatibility, architecture, SIMD/scalar and thread-count determinism.
- Validation of the NuGet package and examples; documentation of capabilities and limitations.
- **DoD:** the 0.6.1–0.6.7 criteria met, documentation consistent with the package, no regressions. Only then update
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
- Writing HDR gain maps (after the core 0.8.0 SPI), depth and arbitrary auxiliary images; user-supplied thumbnails;
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

Used: 0.7.1 (`AttachGainMap` with `HdrInfo.Detected`), 0.7.6 (`FormatDecoderOptions`). Planned in core 0.8.0: gain map
in plugin encoders and ISO serialization, possibly `ReportStoredSize` for thumbnails — only once a need is shown. New
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

- [0.6.6](docs/releases/release-0.6.6.md): faster HEVC decoding and encoding; prepared locally, not published.

- [0.6.5](docs/releases/release-0.6.5.md): Apple Portrait effect (`HeifAppleDepthInfo`, `RenderPortrait`, `HeifPortrait`, `HeifDepthImage.ToImage`).
- [0.6.4](docs/releases/release-0.6.4.md): raw depth samples (`LoadDepth`, `HeifDepthImage`) and Live Photo pairing (`LivePhotoPair`).
- [0.6.3](docs/releases/release-0.6.3.md): `HeifDocument` API correction (incompatible with 0.6.2), `ReadMetadata`, `FindThumbnail`.
- [0.6.2](docs/releases/release-0.6.2.md): catalog of photos and relations, preview selection and thumbnail generation.

- [0.6.1](docs/releases/release-0.6.1.md): compressed XMP (`deflate`) within the `MaxMetadataBytes` limit.
- [0.6.0](docs/releases/release-0.6.0.md): item selection through `HeifDecoderOptions.ItemId`, minimum PictureSharp 0.7.6.
- [0.5.0](docs/releases/release-0.5.0.md): first public release, SIMD and parallelism.
- 0.5.1 was not carried out; the compressed XMP plan was moved to 0.6.1.
- Earlier versions (0.1.0–0.4.0, not released publicly): [docs/history.md](docs/history.md).
