# PictureSharp.Heif 0.6.0

Choose which image of a HEIF file to decode. Requires PictureSharp 0.7.6 or later.

- **Item selection:** a `HeifDecoderOptions` record in `DecoderOptions.FormatOptions` (the plugin decoder options of
  PictureSharp 0.7.6) selects an image item by its ID instead of the primary item: another top-level image, a
  thumbnail, a grid tile or an auxiliary image. `Image.Load`, `Image.Identify` and `Image.ReadMetadata` all use the
  selected item, with its own transforms, colour, alpha, EXIF/XMP and gain map. See
  [Decoding](../decoding.md#what-is-decoded).

  ```csharp
  using var image = Image.Load("burst.heic", new DecoderOptions { FormatOptions = [new HeifDecoderOptions { ItemId = 2 }] });
  ```

  ID 0 throws `ArgumentOutOfRangeException`; an ID that is not in the file or names an EXIF or XMP item throws
  `ImageFormatException`. Without the record the primary item is decoded exactly as in 0.5.0.

- **System.IO.Compression** is allowed for decompression inside the plugin; nothing uses it yet.

**Validation:** all tests pass on linux-x64 against PictureSharp 0.7.6 from nuget.org, including the comparisons with
HM and libheif; a decoded grid tile equals the matching pixels of the decoded grid bit for bit. Not run on ARM64,
Windows or macOS; the change adds no platform-specific code.

HEVC is covered by patents licensed through patent pools; this package does not include a patent license.
