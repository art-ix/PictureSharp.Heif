# PictureSharp.Heif 0.6.6

Faster HEVC decoding and encoding. Requires PictureSharp 0.7.6 and .NET 10. Public API is unchanged.
Release date: 2026-10-19.

## Decoding

CABAC residual parsing batches bypass bits and avoids unpredictable arithmetic-decoder branches. SAO processes
row interiors with SIMD; rotation uses row blocks for cache locality. Supported single-picture WPP streams decode
CTB rows in parallel, followed by parallel row-band deblocking and SAO. Invalid WPP entry points fall back to
sequential decoding. Decoded pixels remain identical to 0.6.5, including HDR gain-map reconstruction.

| Input | 0.6.5, 1 / 16 threads | 0.6.6, 1 / 16 threads |
| --- | ---: | ---: |
| 12 MP iPhone grid | 663 / 116 ms | 568 / 103 ms |
| 24 MP iPhone grid | 1178 / 187 ms | 938 / 161 ms |
| 48 MP grid | 1090 / 170 ms | 916 / 146 ms |
| 48 MP single picture, WPP | 1115 / 1097 ms | 848 / 173 ms |

Decoder timings were recorded during the decoder optimization stage on an i5-12500H. The WPP case improves
all-thread decoding by 6.3×. Parallelism is available only for supported streams; grids still parallelize by tile.

## Encoding

The luma search evaluates fewer candidates, uses ordinary quantization during mode selection and applies full
RDOQ to the selected mode and transform tree. Competitive neighbouring modes remain candidates. Chroma retains
RDOQ. Reference samples are reused; temporary CABAC contexts use stack storage.

12 MP photo, Quality 50, best of two encodes after warm-up on the same x64 machine:

| Measure | Before | 0.6.6 |
| --- | ---: | ---: |
| One thread | 30.761 s | 21.828 s |
| All threads | 4.200 s | 2.858 s |
| Allocations, one thread | 388.9 MiB | 322.1 MiB |
| Encoded size | 1,773,678 bytes | 1,769,599 bytes |

Single-thread encoding takes 29% less time and allocates 17% less memory. Encoded bytes change. Across 24 Kodak
images at Q30/45/60/75, the average equal-quality file-size cost is +0.949% by RGB PSNR and +1.262% by luma SSIM.
At the same quality setting, mean PSNR drops 0.047 dB and the largest drop is 0.131 dB. The user accepted a
full-resolution before/after Q50 comparison with no visible difference reported.

[Encoder methodology and raw results](../measurements/encoder-search-0.6.6.md) ·
[Encoding guide](../encoding.md) · [Decoding guide](../decoding.md)

## Validation and remaining work

The decoder stage passed 704 of 705 tests with SIMD and scalar execution (one optional measurement skipped).
The final encoder passed 53 targeted tests with SIMD and scalar execution, and 11 Oracle tests without skips.
Nine encoded files match between SIMD and scalar paths. Release preparation: Release build with zero warnings/errors; 703 of 705 tests passed (no skips), with the two
pre-existing Local portrait failures described below. The two `Category=Measurement` comparison/sweep tests were
excluded; the encoder's Kodak comparison was measured separately at all 96 quality points. The Oracle checks are
included in this broader run. The 0.6.6 NuGet and symbol packages were built and inspected for version, dependency,
DLL/XML, README, LICENSE and notices.

The original 1.5× single-thread encoder goal and +0.5% BD-rate gate were not reached; the user accepted the measured
trade-off. ARM64 was not rerun for these changes. Decoder allocation reduction, fused colour conversion/rotation,
48 MP peak-memory measurement and a new package consumer/Native AOT run remain outstanding. This release
preparation does not claim completion of the original optimization plan.

Two existing Local portrait tests also fail on the unchanged 0.6.5 release (`9f20f72`) with the current private
photo set: an auxiliary description has no simulated aperture, and the `IMG_6854` reference pair improves
background PSNR from 19.34 to 20.68 dB instead of the test's required +5 dB. Both failures reproduce with exactly
the same results in 0.6.6. They belong to the outstanding portrait calibration work; the release is not presented
as having a completely green Local suite.
