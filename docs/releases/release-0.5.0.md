# PictureSharp.Heif 0.5.0

The first public release: a HEIC/HEIF format plugin for PictureSharp 0.7.1 and later, written entirely in C# with no
native libraries.

- **Reading:** HEVC Main, Main Still Picture and Main 10 (4:2:0 and monochrome), tile grids, alpha, rotation, mirroring
  and cropping, ICC and nclx colour, EXIF and XMP. Decoded YCbCr matches the HM reference decoder bit for bit (also on the
  first pictures of 145 JCT-VC conformance streams) and RGB matches libheif's `heif-dec`.
- **Writing:** `SaveAsHeic` with HEVC intra Main 8-bit 4:2:0, a single picture or a tile grid, alpha, ICC, EXIF and XMP.
  At equal PSNR the files are 4.6% smaller than x265 `medium` on the Kodak set.
- **HDR:** Apple and ISO 21496-1 gain maps through `DecoderOptions.LoadGainMap` and `ToHdr`; `Image.ReadMetadata` without
  decoding pixels.
- **Speed:** tiles decode in parallel and the transforms, reconstruction and colour conversion use `Vector128`/`Vector256`
  paths with results identical to the scalar code. A 12 MP iPhone photo decodes in 109 ms on 16 threads (libheif 237 ms);
  see [Decoding](../decoding.md#speed).

**Not supported:** image sequences, Live Photos, depth maps, 4:2:2/4:4:4, PQ/HLG, AVIF, writing gain maps and
`Image.EditMetadata`.

**Validation:** all tests pass on linux-x64 and on ARM64 (Orange Pi 5, RK3588), with vector paths and with
`DOTNET_EnableHWIntrinsic=0`; decoded pixels and encoded bytes are identical on both architectures. Not run on Windows
or macOS.

HEVC is covered by patents licensed through patent pools; this package does not include a patent license.
