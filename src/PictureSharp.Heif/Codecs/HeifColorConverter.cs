using System.Runtime.Intrinsics;

namespace PictureSharp.Formats.Heif.Codecs;

// YCbCr 4:2:0 / 4:0:0 to 8-bit RGB, matching libheif 1.20 (heif-dec) output, which the oracle tests verify:
// - chroma is upsampled by sample replication (nearest neighbour), except when a clean aperture starts at an odd
//   column or row: then chroma is first upsampled to 4:4:4 with the centred bilinear filter
//   (9a + 3b + 3c + d + 8) >> 4 over the whole image, replicating edge samples;
// - 8-bit full-range input with nearest-neighbour chroma uses fixed-point coefficients round(k * 256) and
//   (v + 128) >> 8;
// - every other case uses float32, with luma and chroma scales 1.1689 and 1.1429 for limited range, rounding with
//   floor(x + 0.5);
// - monochrome images copy luma to R, G and B; alpha planes are taken from luma as is.
// Samples above 8 bits are reduced with round(v * 255 / (2^bitDepth - 1)), like PictureSharp's 16-bit PNG reduction.
internal sealed partial class HeifColorConverter
{
    private readonly bool fullRange;
    private readonly int crR8;
    private readonly int cbG8;
    private readonly int crG8;
    private readonly int cbB8;
    private readonly float crR;
    private readonly float cbG;
    private readonly float crG;
    private readonly float cbB;

    public HeifColorConverter(int matrix, bool fullRange)
    {
        (double kr, double kb) = matrix switch
        {
            1 => (0.2126, 0.0722),
            2 or 5 or 6 => (0.299, 0.114),
            4 => (0.30, 0.11),
            7 => (0.212, 0.087),
            9 => (0.2627, 0.0593),
            _ => throw new NotSupportedException($"HEIF matrix coefficients {matrix} are not supported."),
        };
        this.fullRange = fullRange;
        double kg = 1 - kr - kb;
        crR8 = (int)Math.Round(2 * (1 - kr) * 256, MidpointRounding.ToEven);
        cbG8 = (int)Math.Round(2 * kb * (1 - kb) / kg * 256, MidpointRounding.ToEven);
        crG8 = (int)Math.Round(2 * kr * (1 - kr) / kg * 256, MidpointRounding.ToEven);
        cbB8 = (int)Math.Round(2 * (1 - kb) * 256, MidpointRounding.ToEven);
        float krf = (float)kr;
        float kbf = (float)kb;
        float kgf = 1f - krf - kbf;
        crR = 2f * (1f - krf);
        cbG = 2f * kbf * (1f - kbf) / kgf;
        crG = 2f * krf * (1f - krf) / kgf;
        cbB = 2f * (1f - kbf);
    }

    public void Convert(HeifPlanes planes, Rgba32[] destination, bool bilinear, ParallelOptions parallel)
    {
        int width = planes.Width;
        if (planes.ChromaFormatIdc == 0)
        {
            ConvertGray(planes, destination, parallel);
            return;
        }

        HeifParallel.ForRows(planes.Height, width, parallel, (start, end) =>
        {
            for (int y = start; y < end; y++)
            {
                parallel.CancellationToken.ThrowIfCancellationRequested();
                Span<Rgba32> row = destination.AsSpan(y * width, width);
                if (bilinear)
                {
                    ConvertBilinearRow(planes, row, y);
                }
                else if (planes.BitDepth == 8 && fullRange)
                {
                    if (Vector128.IsHardwareAccelerated)
                    {
                        ConvertRow8Vector(planes, row, y);
                    }
                    else
                    {
                        ConvertRow8(planes, row, y, 0);
                    }
                }
                else
                {
                    ConvertRow(planes, row, y);
                }
            }
        });
    }

    // Alpha planes carry the alpha value in luma, without range conversion; the value goes to R.
    public static void ConvertAlpha(HeifPlanes planes, Rgba32[] destination, ParallelOptions parallel) =>
        ConvertGray(planes, destination, parallel);

    private static void ConvertGray(HeifPlanes planes, Rgba32[] destination, ParallelOptions parallel)
    {
        int maximum = (1 << planes.BitDepth) - 1;
        HeifParallel.ForRows(planes.Height, planes.Width, parallel, (start, end) =>
        {
            for (int y = start; y < end; y++)
            {
                Span<Rgba32> row = destination.AsSpan(y * planes.Width, planes.Width);
                for (int x = 0; x < planes.Width; x++)
                {
                    byte v = Reduce(planes.Luma(x, y), planes.BitDepth, maximum);
                    row[x] = new Rgba32(v, v, v, 255);
                }
            }
        });
    }

    // 8-bit full range, nearest-neighbour chroma: fixed point. Converts row[start..].
    internal void ConvertRow8(HeifPlanes planes, Span<Rgba32> row, int y, int start)
    {
        int cy = y >> 1;
        for (int x = start; x < row.Length; x++)
        {
            int yValue = planes.Luma(x, y) << 8;
            int cb = planes.Chroma(1, x >> 1, cy) - 128;
            int cr = planes.Chroma(2, x >> 1, cy) - 128;
            row[x] = new Rgba32(
                Clip8((yValue + (crR8 * cr) + 128) >> 8),
                Clip8((yValue - (cbG8 * cb) - (crG8 * cr) + 128) >> 8),
                Clip8((yValue + (cbB8 * cb) + 128) >> 8),
                255);
        }
    }

    private void ConvertRow(HeifPlanes planes, Span<Rgba32> row, int y)
    {
        int bitDepth = planes.BitDepth;
        int maximum = (1 << bitDepth) - 1;
        int cy = y >> 1;
        for (int x = 0; x < row.Length; x++)
        {
            row[x] = ConvertFloat(planes.Luma(x, y), planes.Chroma(1, x >> 1, cy), planes.Chroma(2, x >> 1, cy), bitDepth, maximum);
        }
    }

    private void ConvertBilinearRow(HeifPlanes planes, Span<Rgba32> row, int y)
    {
        int maximum = (1 << planes.BitDepth) - 1;
        int chromaWidth = (planes.Width + 1) >> 1;
        int chromaHeight = (planes.Height + 1) >> 1;
        int cy = y >> 1;
        int ny = Math.Clamp((y & 1) == 0 ? cy - 1 : cy + 1, 0, chromaHeight - 1);
        for (int x = 0; x < row.Length; x++)
        {
            int cx = x >> 1;
            int nx = Math.Clamp((x & 1) == 0 ? cx - 1 : cx + 1, 0, chromaWidth - 1);
            int cb = ((9 * planes.Chroma(1, cx, cy)) + (3 * planes.Chroma(1, cx, ny)) + (3 * planes.Chroma(1, nx, cy))
                + planes.Chroma(1, nx, ny) + 8) >> 4;
            int cr = ((9 * planes.Chroma(2, cx, cy)) + (3 * planes.Chroma(2, cx, ny)) + (3 * planes.Chroma(2, nx, cy))
                + planes.Chroma(2, nx, ny) + 8) >> 4;
            row[x] = ConvertFloat(planes.Luma(x, y), cb, cr, planes.BitDepth, maximum);
        }
    }

    private Rgba32 ConvertFloat(int luma, int cbSample, int crSample, int bitDepth, int maximum)
    {
        float half = 1 << (bitDepth - 1);
        float yValue = luma;
        float cb = cbSample - half;
        float cr = crSample - half;
        if (!fullRange)
        {
            yValue = (yValue - (16 << (bitDepth - 8))) * 1.1689f;
            cb *= 1.1429f;
            cr *= 1.1429f;
        }

        return new Rgba32(
            Reduce(Round(yValue + (crR * cr), maximum), bitDepth, maximum),
            Reduce(Round(yValue - (cbG * cb) - (crG * cr), maximum), bitDepth, maximum),
            Reduce(Round(yValue + (cbB * cb), maximum), bitDepth, maximum),
            255);
    }

    private static int Round(float value, int maximum)
    {
        float rounded = MathF.Floor(value + 0.5f);
        return rounded <= 0 ? 0 : rounded >= maximum ? maximum : (int)rounded;
    }

    private static byte Reduce(int value, int bitDepth, int maximum) =>
        bitDepth == 8 ? (byte)value : (byte)(((value * 255) + (maximum >> 1)) / maximum);

    private static byte Clip8(int value) => (byte)(value < 0 ? 0 : value > 255 ? 255 : value);
}
