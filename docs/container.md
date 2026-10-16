# HEIF container identification (0.1.0)

Register the plugin and call the public PictureSharp API:

```csharp
using PictureSharp;
using PictureSharp.Formats.Heif;

var configuration = ImageConfiguration.Default.ToBuilder().AddFormat(new HeifFormat()).Build();
var options = new DecoderOptions { Configuration = configuration, MaxPixels = 100_000_000 };
ImageInfo info = Image.Identify(File.ReadAllBytes("photo.heic").AsMemory(), options);
Console.WriteLine($"{info.Width} × {info.Height}, {info.BitsPerChannel} bits, alpha: {info.HasAlpha}, items: {info.ContainerItemCount}");
```

`Identify` reads the primary image's dimensions after container transforms, bit depth and alpha declaration without
reading HEVC pixels. `FrameCount` is 1 for a still image; tiles, metadata and auxiliary items are not animation frames.
`ContainerItemCount` (PictureSharp 0.6.9) reports every `iinf` entry: the primary image, grid tiles, alpha, thumbnails
and Exif/XMP items. `Orientation` is normal because `irot`/`imir`/`clap` have
already been accounted for. `TargetSize` does not change identification of stored dimensions.

The parser handles `hvc1`, `grid` and `iden`, item IDs in 16- and 32-bit boxes, and `iloc` versions 0–2 with file-relative
and `idat`-relative extents with explicit nonzero lengths. Implicit extent lengths raise `NotSupportedException`. Transform properties follow `ipma` association order. Unknown optional boxes and properties
are skipped; unknown essential image properties, sequences, protected/external items and
fractional-pixel clean apertures raise `NotSupportedException`. Invalid bounds, missing required fields and cyclic
references raise `ImageFormatException`. HEVC payload validity and profile support are not established by `Identify`; even a profile unsupported for decoding can be identified.

Structural parsing never recursively descends into arbitrary boxes. It permits up to 65,536 items, 262,144 boxes,
262,144 combined extent/reference/association/NAL entries, 32 reference levels and 32,767 properties. Input payloads
remain borrowed memory slices. `MaxPixels` covers stored primary, tile and alpha dimensions, counting each required item
once and including the grid canvas. Cropping cannot reduce this charge. ICC/EXIF/XMP byte counts obey `MaxMetadataBytes`
unless `IgnoreMetadata` is true. `Decode` runs the same validation before decoding; see [decoding](decoding.md).

Container design follows ISO/IEC 14496-12 and ISO/IEC 23008-12. The
[Nokia HEIF technical overview](https://nokiatech.github.io/heif/technical.html) describes the item/property/reference model.
No implementation was ported from an external codec library for this stage.

## Reference tools and corpus

Source revisions are pinned in `scripts/heif-reference-versions.tsv`. After obtaining permission to download them:

```bash
scripts/fetch-heif-reference.sh --approved
scripts/build-heif-reference.sh
python3 tests/assets/generate-corpus.py
dotnet test -c Release --treenode-filter '/*/*/*/*[Category=Oracle]'
```

The build requires Linux, C/C++ compilers, make, CMake, NASM, pkg-config and PNG/JPEG development libraries. It builds in
`reference/build`, preserving the original checkouts in `reference/src`. `PICTURESHARP_HEIF_TOOLS` selects an absolute
installation directory containing `bin/heif-info`, `bin/heif-enc`, `bin/heif-dec` and `bin/TAppDecoder` (default:
`reference/tools`). `HEIF_BUILD_JOBS` defaults to 4. `PICTURESHARP_HEIF_REQUIRE_ORACLES=1` makes missing tools fail in CI;
local runs skip unavailable oracles explicitly.

The checked-in corpus contains synthetic gradients only. Its manifest records SHA-256 and expected dimensions, depth,
alpha and encoder. Rebuilding the corpus on a different native toolchain may change bitstream hashes; review and update
the manifest together with the files. Managed tests always check the committed bytes; Oracle tests query `heif-info`
independently. HEVC and RGB references are described in [decoding](decoding.md).

Private files in `samples/` are exercised only by `Category=Local` tests. Both lowercase and uppercase HEIC/HEIF
extensions are ignored by Git. Reference libheif is pinned to 1.20.2 because 1.17.6 rejects recent iPhone files with
multiple auxiliary references. No private metadata is written into the committed corpus.
