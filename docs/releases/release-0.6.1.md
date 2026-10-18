# PictureSharp.Heif 0.6.1

Read compressed XMP. Requires PictureSharp 0.7.6 or later; no new public API.

- **Compressed XMP:** an XMP item whose `infe` declares `content_encoding` `deflate` is decompressed and returned in
  `Metadata.Xmp` by `Image.Load` and `Image.ReadMetadata`. Both zlib streams and raw deflate without the zlib header
  are read. Until now such files failed entirely with `NotSupportedException`. See
  [Decoding](../decoding.md#what-is-decoded).
- **Limits:** the decompressed size counts against `MaxMetadataBytes`. A stream that would exceed the limit, such as a
  decompression bomb, raises `ImageFormatException` before more memory is allocated. With `IgnoreMetadata` the XMP is
  not decompressed, unless an Apple gain map needs it, in which case the limit still applies.
- **Robustness:** an unknown encoding, or a corrupt or truncated stream, no longer blocks the image: it loads without
  that XMP block.
- Writing is unchanged; the encoder stores XMP uncompressed.

**Validation:** all tests pass on linux-x64 against PictureSharp 0.7.6 from nuget.org, including the comparisons with
HM and libheif. libheif's `heif-enc` cannot write compressed metadata, so compressed XMP is tested with synthetic
files: zlib and raw deflate, exact limits, a 256 MiB bomb, unknown encodings, corrupt and truncated streams and 3000
mutated streams. Not run on ARM64, Windows or macOS; the change adds no platform-specific code.

HEVC is covered by patents licensed through patent pools; this package does not include a patent license.
