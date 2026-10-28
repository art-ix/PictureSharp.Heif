# PictureSharp.Heif 0.7.0

Read and write 10-bit HEIC with PictureSharp 0.9.0. Requires .NET 10 and core 0.9.0 or later.
Earlier plugin builds cannot be registered with core 0.9.0.

- Enable `DecoderOptions.PreserveBitDepth` to keep color or alpha above 8 bits in `Rgba64`, including attached
  gain maps. Default loading stays 8-bit, with the same results as before.
- Save `Rgba64` directly as HEVC Main 10 or Monochrome 10. Alpha and re-encoded gain maps can also use 10 bits.
  Encoding remains lossy; 16-bit storage is rounded directly to 10 bits.
- `HeifEncoderOptions.BitDepth` selects `Auto`, `UpTo8` or `Ten`. `Auto` uses 10 bits when needed and 8 bits for
  exact 8-bit content. See the [encoding guide](../encoding.md).
- Direct pixel access uses the typed core API, for example `GetPixelSpan<Rgba64>()`.
  A 16-bit pixel counts twice against `MaxPixels`.

The iPhone Photos-style editing API still uses 8-bit images: convert to `Rgba32` before `ApplyHeifEdits`.
`HeifDocument.Render` also uses 8-bit photograph and matte samples. HEVC above 10 bits remains unsupported.

Validation: 795 functional Release tests passed, including reference-tool checks. HM decodes the written 8-bit and
10-bit YUV exactly like this decoder. All 56 corpus read/write comparisons retain the earlier 8-bit output.
See the [verification record](../measurements/core-0.9.0-step5.json) for scope and performance measurements.
