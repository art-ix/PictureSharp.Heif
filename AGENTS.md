# Working on PictureSharp.Heif

PictureSharp.Heif is a format plugin for [PictureSharp](https://github.com/art-ix/PictureSharp) that reads and writes
HEIC images (HEVC in the HEIF container) entirely in C#. It is a separate repository and package with its own version;
it uses PictureSharp only through its public NuGet API.

Before starting, read [plan.md](plan.md) to identify the current version and its definition of done (DoD), then read
the latest entries in [progres.md](progres.md). Append progress after significant steps and before ending a session:
changes, decisions, test and benchmark results, remaining work, and the next step. Preserve earlier entries.
`plan.md` tracks scope and status; `progres.md` is a local log and must stay out of commits. Changes the plugin needs
in PictureSharp itself go to the core repository's `plan.md` (sibling folder `../picturesharp`), never into this
repository as copies of internal code.

## Required constraints

- Target `net10.0` with C# 14 (`LangVersion=14.0`); support .NET 10 and later.
- `src/PictureSharp.Heif` depends only on the `PictureSharp` package (minimum version in `Directory.Build.props`) and
  the BCL: no other `PackageReference`, native libraries or P/Invoke (`System.IO.Compression` is allowed for
  decompression, decided by the user on 2026-10-17). Use only public
  PictureSharp API; no `InternalsVisibleTo`, reflection or copied internal code. An architecture test checks this.
- Do not use the `unsafe` keyword. Use `Span<T>`, `ref`, `Unsafe.Add` and `MemoryMarshal`.
- Implement the plugin through `PictureSharp.Formats`: `IImageFormatModule`, `IImageFormatDecoder`,
  `IImageFormatEncoder<TOptions>` and `ImageDecodeContext`. Public API uses `PictureSharp.Formats.Heif` (`HeifFormat`,
  `HeifEncoderOptions`, extension blocks such as `SaveAsHeic`); codecs and the container parser are `internal`.
- Return `Image` pixels as `Rgba32` with straight (unpremultiplied) alpha; 10-bit samples are reduced to 8 bits with
  documented rounding.
- Call `ImageDecodeContext.ReservePixels` / `ReserveMetadata` before allocating and check the cancellation token
  between tiles and CTU rows. Corrupt or unrecognized input must throw `ImageFormatException`; valid input with
  unsupported features (inter slices, RExt, SCC, image sequences) must throw `NotSupportedException`. Malicious input
  must never expose unchecked exceptions such as `IndexOutOfRangeException`.
- Apply container transforms (`irot`, `imir`, `clap`) in the decoder and set `Metadata.Orientation` so that
  PictureSharp's `ApplyExifOrientation` does not rotate the image a second time. Express nclx colour (no ICC) through
  `ImageMetadata.Chromaticities`/`Gamma`; PQ/HLG are not supported.

## Licensing and patents

- PictureSharp.Heif uses MIT. Port only from MIT-compatible sources: the ITU-T H.265 specification, the HM reference
  software (BSD-3) and kvazaar (BSD-3); the container follows ISO/IEC 23008-12 and ISO/IEC 14496-12.
- libheif, libde265 (LGPL) and x265 (GPL) are oracles only. Never port or copy their code.
- Maintain `THIRD-PARTY-NOTICES.txt` for ported sources; the NuGet package includes `LICENSE` and the notices.
- HEVC is covered by patent pools. The README states this; publishing the package is the user's decision.

## Code and layout

Follow `.editorconfig`. Enable nullable annotations, `TreatWarningsAsErrors` and XML documentation for public API
(`GenerateDocumentationFile`). Use English for identifiers, comments and XML docs; project documents may use Polish.

- Keep types focused; add interfaces or layers only for a concrete need. Match filenames to the main type; name partial
  files `Type.Responsibility.cs`, for example `HevcSliceDecoder.Cabac.cs`.
- Separate SIMD methods into partial files such as `Type.Simd.cs` or `Type.X86.cs` (and ARM equivalents). Keep scalar
  code readable without intrinsics.
- Keep hot loops allocation-free. Rent temporary buffers from `ArrayPool<T>.Shared`; allocate image pixels with
  `GC.AllocateUninitializedArray`. Use `[MethodImpl(MethodImplOptions.AggressiveInlining)]` for short hot-path methods.
- Write sparse comments that explain why or identify the port source. Declare constant tables as
  `static ReadOnlySpan<T> Name => [ ... ];`.

| Path | Purpose |
| --- | --- |
| `src/PictureSharp.Heif/` | Plugin: HEIF container, HEVC intra decoder and encoder, colour conversion |
| `tests/PictureSharp.Heif.Tests/` | TUnit tests |
| `tests/assets/` | Corpus, reference data and generation scripts |
| `benchmarks/` | BenchmarkDotNet |
| `docs/` | User guide; examples checked by tests |
| `docs/releases/` | Release notes (`release-x.y.z.md`) |
| `reference/` | Uncommitted reference sources and tools, outside the build |

## Porting reference code

Pin HM, kvazaar and the oracle tools (libheif with libde265, x265 and kvazaar encoders) in `reference/`. Treat the
sources as read-only; downloading them requires user approval. Build the oracle tools with
`scripts/build-heif-reference.sh` (`heif-dec`, `heif-enc`, `heif-info`, HM `TAppDecoder`); override the directory with
`PICTURESHARP_HEIF_TOOLS`.

- Port function by function. Add a source comment above each port, for example
  `// port: HM source/Lib/TLibCommon/TComTrQuant.cpp xIT`. Preserve source names where practical, using PascalCase.
- Start with scalar code; add `Vector128`/`Vector256` only afterward. Tests must prove SIMD and scalar results are
  identical, and results must not depend on the thread count.
- Preserve C/C++ semantics: integer promotions, clipping functions rather than casts, arithmetic shifts and the exact
  rounding of the specification.

## Build and verification

```bash
dotnet build -c Release
dotnet test -c Release
dotnet test -c Release --treenode-filter "/*/*/*/*[Category=Oracle]"
```

Tests use TUnit on Microsoft.Testing.Platform (`global.json` selects it for `dotnet test`), so filters use
`--treenode-filter` and `[Property("Category", …)]`. Oracle tests compare with external tools and report as skipped
(`Skip.Unless`) when those tools are missing. `Category=Slow` marks fuzzing and thread-count tests. Collections compare
with `IsEquivalentTo(expected, CollectionOrdering.Matching)`. Close a version only when its tests pass and its DoD is met.

- Decoded YCbCr must match HM and libde265 bit for bit (tolerance 0); RGB must match `heif-dec` under the same colour
  conversion. Record SHA-256 references in the corpus.
- Files written by the encoder must decode with libheif and HM; our decoder must reproduce HM's output on them.
- Commit the synthetic corpus in `tests/assets/corpus/`. Private iPhone photos stay ignored and uncommitted; use them
  only for `Category=Local` tests and local benchmarks (they may contain EXIF GPS data).
- Obtain user approval before downloading external corpora (for example the JCT-VC conformance bitstreams).
- Test against the PictureSharp package from NuGet or a local feed, never a project reference to the core sources.

## Git and versioning

- Commit and push only when the user explicitly asks.
- `VERSION` contains the plugin's own SemVer version, starting at 0.1.0; `Directory.Build.props` applies it and the
  minimum PictureSharp version.
- Never commit `progres.md`, `reference/`, `tests/assets/external/`, `bin/` or `obj/`.
- ARM64 hardware: Orange Pi 5 at `ssh ubuntu@192.168.8.124`; use it only when the user allows it, in a separate copy
  synced with rsync (excluding `bin/`, `obj/` and `reference/`).
