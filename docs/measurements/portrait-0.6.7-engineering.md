# Portrait 0.6.7 — E6 performance and memory, E7 validation

Measured on 2026-10-20. E6 changes only how `HeifPortraitRenderer` computes a render, not what it renders, with two
deliberate exceptions (`φ·√φ` and `Lerp`, below). The E1 baseline, the per-pair gate and the model parameters are unchanged.

## Changes

| Change | Effect on pixels |
| --- | --- |
| Layer building: interior columns convolved by a precomputed tap list (offsets, weights, sequential total), two texels per `Vector256<float>` in `HeifPortraitRenderer.Simd.cs`; image edges keep the bounds-checked loop | none (same per-texel addition order) |
| Direct gathers (radius ≤ 3): interior fast path with the same tap order and precomputed total | none |
| Point lights and night lamps: detection and scatter run on row bands in parallel; each band adds the discs whose extent crosses it, in raster order; disc rows with `Vector<float>`; light map sampled once per row instead of per pixel | none |
| Half → float through a 65 536-entry table; sRGB encode starts its threshold search from a 4096-bucket index | none |
| Highlight, white-sum and point buffers rented from `ArrayPool` and returned in `finally` (also on cancellation and error); compact 24-byte disc records | none |
| Strength `φ^1.5` computed as `φ·√φ` instead of `MathF.Pow` | 24 of 68 renders change by one ulp of strength (6 scenes) |
| `Vector3/Vector4.Lerp` replaced by `a·(1−t) + b·t` with separate rounding | all renders change by rounding amounts (≤ 3.4·10⁻⁵ dB in any pair and region of the gate) |

`MathF.Pow` was the only operation in the radius formula without a correctly rounded result on every platform;
square root and product are correctly rounded by IEEE 754, so x64 and ARM64 compute the same strength. The renders it
changes still pass the frozen 66-pair gate and the 12-pair validation set.

`Vector3.Lerp` and `Vector4.Lerp` (.NET 9 and later) compute `a·(1−t) + b·t` as a fused multiply-add where the CPU has
FMA. Found in E7: the Native AOT consumer (x64 baseline, no FMA) rendered different pixels than the JIT on an AVX2
machine, and so did the JIT with `DOTNET_EnableAVX2=0`. The 0.6.7 E5 code had the same dependence, so it predates E6.
The renderer now uses its own `Lerp` with separately rounded products, and
`PortraitEngineeringTests.Renderer_AvoidsMachineDependentRounding` rejects `Lerp`, `*Estimate`, `FusedMultiplyAdd` and
a new `MathF.Pow` in the renderer sources. The remaining `MathF.Pow` computes the per-render grain level from the
ISO; ARM64 renders, grain included, are identical to x64 (below).

## Bit identity

`benchmarks/PictureSharp.Heif.Speed portraithash` hashes renders of 17 originals at f/1.4, f/4.5 and f/16 with the
default focus and at f/2.8 with the picture centre as focus point, with one thread and with all threads (136 renders).

| Comparison | Result |
| --- | --- |
| E6 without `φ·√φ` vs 0.6.7 E5 code | 136/136 identical |
| E6 final, one thread vs all threads | 68/68 identical |
| E6 final vs E6 with `φ·√φ` only | 68/68 identical |
| E6 before the `Lerp` change vs 0.6.7 E5 code | 44 identical, 24 differ (IMG_6011, 6986, 7019, 7475, 7707, 8129: `φ·√φ` only) |
| E6 final (with `Lerp`), one thread vs all threads | 68/68 identical |
| E6 final: x64 AVX2/FMA vs `DOTNET_EnableAVX2=0` vs `DOTNET_EnableHWIntrinsic=0` (scalar) | 68/68 identical |
| E6 final: Native AOT linux-x64 consumer vs JIT (4 originals, f/2.8 centre focus, 1 and all threads) | 8/8 identical |
| E6 final: ARM64 (Orange Pi 5, .NET 10.0.112) vs x64, 1 and all threads | 136/136 identical |
| E6 final: ARM64 with `DOTNET_EnableHWIntrinsic=0` vs x64 | 68/68 identical |

Unit tests (`PortraitEngineeringTests`): the vector disc row equals the scalar row bit for bit (2000 random rows), the
vector convolution equals the scalar one (300 random cases), a render with point lights and with night lamps does not
depend on the thread count, a render cancelled midway leaves nothing behind (the next render is identical), and the
renderer sources avoid machine-dependent rounding.

## Time, allocation and memory

Best of three runs after a warm-up, AMD64 Linux, .NET 10 Release; `all` = every core. Allocation is per render;
peak is the working-set high-water mark of the first render above the loaded inputs (Linux `clear_refs`). 24 and
48 MP inputs are the 12 MP photograph resized by √2 and 2. "E5" is the 0.6.7 code before E6 (the 0.6.6 renderer plus
E2–E5 calibration).

| Aperture | File | Threads | MP | ms E5 | ms E6 | Speed-up | Alloc MB E5 | Alloc MB E6 | Peak MB E5 | Peak MB E6 |
| --- | --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| f/1.4 | IMG_6986 | 1 | 12.2 | 6071 | 2525 | 2.40 | 91.5 | 63.0 | 164 | 173 |
| f/1.4 | IMG_6986 | all | 12.2 | 3370 | 413 | 8.16 | 91.6 | 63.8 | 242 | 255 |
| f/4.5 | IMG_6986 | 1 | 12.2 | 4133 | 1611 | 2.57 | 91.5 | 62.7 | 162 | 172 |
| f/4.5 | IMG_6986 | all | 12.2 | 1041 | 270 | 3.86 | 91.6 | 63.4 | 240 | 253 |
| f/1.4 | IMG_6986 | 1 | 24.4 | 16315 | 6134 | 2.66 | 173.0 | 113.8 | 417 | 404 |
| f/1.4 | IMG_6986 | all | 24.4 | 10723 | 878 | 12.21 | 174.3 | 235.3 | 499 | 502 |
| f/4.5 | IMG_6986 | 1 | 24.4 | 8433 | 3422 | 2.46 | 173.0 | 113.3 | 412 | 402 |
| f/4.5 | IMG_6986 | all | 24.4 | 2575 | 525 | 4.90 | 174.3 | 115.9 | 495 | 501 |
| f/1.4 | IMG_6986 | 1 | 48.8 | 43387 | 16367 | 2.65 | 335.8 | 213.5 | 850 | 856 |
| f/1.4 | IMG_6986 | all | 48.8 | 32065 | 2167 | 14.80 | 584.7 | 407.2 | 959 | 1014 |
| f/4.5 | IMG_6986 | 1 | 48.8 | 18403 | 7412 | 2.48 | 335.8 | 212.7 | 841 | 859 |
| f/4.5 | IMG_6986 | all | 48.8 | 6538 | 1072 | 6.10 | 335.9 | 324.9 | 947 | 1009 |
| default | IMG_8124 (point lights) | 1 | 24.5 | 9137 | 2963 | 3.08 | 166.9 | 115.5 | 222 | 245 |
| default | IMG_8124 (point lights) | all | 24.5 | 2168 | 458 | 4.73 | 169.4 | 120.2 | 284 | 292 |
| default | IMG_7475 (night lamps) | 1 | 12.2 | 3458 | 1592 | 2.17 | 138.1 | 56.8 | 174 | 207 |
| default | IMG_7475 (night lamps) | all | 12.2 | 648 | 328 | 1.98 | 138.1 | 59.4 | 255 | 282 |
| default | IMG_8060 | 1 | 7.2 | 3084 | 1271 | 2.43 | 56.7 | 40.6 | 109 | 108 |
| default | IMG_8060 | all | 7.2 | 1325 | 236 | 5.61 | 57.9 | 42.4 | 154 | 161 |

Single-threaded renders are 2.2–3.1× faster, multi-threaded 2–15× (the serial point-light scatter dominated at wide
apertures before). Allocation falls by 30–60 % in most rows. Exceptions: 24 MP f/1.4 with all threads allocates more
(235 vs 174 MB; per-band buffers rented beyond the pool's cached size), and peak memory is within −3…+7 % except the
night scene (+11–19 %: the planar light map now coexists with the per-band row buffers). The raw TSV is
[portrait-0.6.7-engineering.tsv](portrait-0.6.7-engineering.tsv).

Not done in E6: an explicit SIMD per-pixel gather (it stays on `System.Numerics.Vector4` with interior fast paths);
the profile after E6 is dominated by layer building and the gather, both already vectorized at 128 bits.

ARM64 (Orange Pi 5, Cortex-A76/A55, 8 cores), same IMG_6986 at 12.2 MP and f/1.4 with the final code: 7495 ms on one
thread (x64 2525 ms), 1688 ms with all threads (x64 413 ms), 63 MB allocated per render, peak 170 MB. The all-threads
peak of that run is not valid (measured after the one-thread render in the same process, with warm pools).

## E7 validation

| Check | Result |
| --- | --- |
| `dotnet build -c Release` (x64, ARM64) | 0 warnings, 0 errors |
| Full Release tests without Local, x64, Oracle tools on `LD_LIBRARY_PATH` | 741 total, 731 passed, 0 failed, 10 skipped (all opt-in measurement tools) |
| `Category=Oracle`, x64 | 11 tests; 10 passed; the Local/Oracle HDR test failed on unsupported gain maps (below), fixed and passing in the Local run |
| `Category=Local`, x64 (66-pair gate, validation set, HM/libde265 private samples, HDR) | 12/12 passed |
| Full Release tests without Local, ARM64 | 740 total, 719 passed, 0 failed, 21 skipped: 10 opt-in tools, 10 Oracle tests (no reference tools built on the device), 1 ffmpeg fixture |
| NuGet consumer, Native AOT linux-x64, local feed | package with LICENSE, notices, README, XML docs and the single dependency PictureSharp 0.7.6; publish without trim/AOT warnings (warnings as errors); render, encode and decode work; pixels identical to the JIT |
| Bit identity across machines | see the table above |

Validation gaps, reported rather than counted as passes:

- Oracle tests did not run on ARM64 (no oracle tools on the device); they ran on x64.
- Native AOT was published and run only for linux-x64, not for linux-arm64.
- The package was built locally with version `0.6.7-e7local2`; `VERSION` stays 0.6.6 until the release.

The HDR test (`PrivateSamples_ToHdrMatchesHeifDecMap`) scans `samples/` recursively and failed on two kinds of files
the plugin correctly reports as unsupported: the iPhone 13 Pro validation photographs (Apple gain map without the
MakerNote headroom) and newer Photos exports (only an ISO 21496-1 map, alternate colour space neither sRGB nor Display
P3). The test now reports both as not compared and checks that the plugin and the test-only parser agree. The frozen
manifest comparison ignores the case of the path (the user renamed `IMG_7019_a4.5.HEIC` to `.heic`); the file hashes
still pin the content. New basen exports (four apertures, five refocus variants) were set aside for 0.6.8 by the
user's decision and are outside the frozen set.
