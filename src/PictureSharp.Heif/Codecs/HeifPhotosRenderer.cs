using System.Runtime.CompilerServices;

namespace PictureSharp.Formats.Heif.Codecs;

// Photos "Adjust" sliders fitted from single-slider export pairs (docs/measurements/photos-edits-0.6.8-fit.md,
// scripts/fit-photos-edits.py; parameters in HeifPhotosTables). Each slider value has a record of piecewise-linear
// curves on 33 knots over 0–255: per-channel sRGB deltas, a luma delta, a chroma gain keyed by saturation, Gaussian
// band gains and a radial linear-light log gain. Records between measured slider values are interpolated linearly,
// from the identity at 0 and beyond the last measured value by scaling it. The fits describe each slider alone; the
// order in which they combine is an assumption until combined edits are measured.
internal sealed partial class HeifPhotosRenderer
{
    private const int Knots = 33;
    private const int CurveOffset = 0, LumaOffset = 99, GainOffset = 132, BandOffset = 165, RadialOffset = 173;
    private const int BandCount = 8;
    private const float LumaR = 0.2126f, LumaG = 0.7152f, LumaB = 0.0722f;

    private readonly int width, height;
    private readonly ParallelOptions parallel;
    private readonly bool identity;
    // Composed per-channel curves (R, G, B × 256), exact for 8-bit inputs.
    private readonly float[] channelLut = new float[3 * 256];
    private readonly float[] bandGains = new float[BandCount];
    private readonly bool hasBands;
    private readonly float[]? contrast, saturation, vibrance, vignette;

    public HeifPhotosRenderer(int width, int height, HeifPhotosAdjustments adjustments, CancellationToken cancellationToken)
    {
        this.width = width;
        this.height = height;
        parallel = new ParallelOptions { MaxDegreeOfParallelism = adjustments.MaxDegreeOfParallelism, CancellationToken = cancellationToken };
        identity = adjustments.IsEmpty;

        // Order of the per-channel curves; contrast, saturation, vibrance and vignette are separate stages.
        float[]?[] curves =
        [
            Record(HeifPhotosTables.WarmthValues, HeifPhotosTables.Warmth, adjustments.Warmth),
            Record(HeifPhotosTables.TintValues, HeifPhotosTables.Tint, adjustments.Tint),
            Record(HeifPhotosTables.ExposureValues, HeifPhotosTables.Exposure, adjustments.Exposure),
            Record(HeifPhotosTables.BrightnessValues, HeifPhotosTables.Brightness, adjustments.Brightness),
            Record(HeifPhotosTables.HighlightsValues, HeifPhotosTables.Highlights, adjustments.Highlights),
            Record(HeifPhotosTables.ShadowsValues, HeifPhotosTables.Shadows, adjustments.Shadows),
            Record(HeifPhotosTables.BlackPointValues, HeifPhotosTables.BlackPoint, adjustments.BlackPoint),
            Record(HeifPhotosTables.BrillianceValues, HeifPhotosTables.Brilliance, adjustments.Brilliance),
            Record(HeifPhotosTables.DefinitionValues, HeifPhotosTables.Definition, adjustments.Definition),
            Record(HeifPhotosTables.SharpnessValues, HeifPhotosTables.Sharpness, adjustments.Sharpness),
            Record(HeifPhotosTables.NoiseReductionValues, HeifPhotosTables.NoiseReduction, adjustments.NoiseReduction),
        ];
        for (int c = 0; c < 3; c++)
        {
            for (int x = 0; x < 256; x++)
            {
                float value = x;
                foreach (float[]? record in curves)
                {
                    if (record is not null)
                    {
                        value = Math.Clamp(value + Curve(record.AsSpan(CurveOffset + c * Knots, Knots), value), 0, 255);
                    }
                }

                channelLut[c * 256 + x] = value;
            }
        }

        foreach (float[]? record in curves)
        {
            if (record is not null)
            {
                for (int i = 0; i < BandCount; i++)
                {
                    bandGains[i] += record[BandOffset + i];
                }
            }
        }

        hasBands = Array.Exists(bandGains, gain => gain != 0);
        contrast = Record(HeifPhotosTables.ContrastValues, HeifPhotosTables.Contrast, adjustments.Contrast);
        saturation = Record(HeifPhotosTables.SaturationValues, HeifPhotosTables.Saturation, adjustments.Saturation);
        vibrance = Record(HeifPhotosTables.VibranceValues, HeifPhotosTables.Vibrance, adjustments.Vibrance);
        vignette = Record(HeifPhotosTables.VignetteValues, HeifPhotosTables.Vignette, adjustments.Vignette);
    }

    public void Render(ReadOnlyMemory<Rgba32> source, Memory<Rgba32> destination)
    {
        if (identity)
        {
            source.CopyTo(destination);
            return;
        }

        float[]? bands = hasBands ? Bands(source) : null;
        try
        {
            HeifParallel.ForRows(height, width, parallel, (top, bottom) =>
            {
                ReadOnlySpan<Rgba32> input = source.Span;
                Span<Rgba32> output = destination.Span;
                for (int y = top; y < bottom; y++)
                {
                    parallel.CancellationToken.ThrowIfCancellationRequested();
                    RenderRow(input.Slice(y * width, width), output.Slice(y * width, width), bands, y);
                }
            });
        }
        finally
        {
            if (bands is not null)
            {
                System.Buffers.ArrayPool<float>.Shared.Return(bands);
            }
        }
    }

    private void RenderRow(ReadOnlySpan<Rgba32> input, Span<Rgba32> output, float[]? bands, int y)
    {
        long plane = (long)width * height;
        float dy = y - (height - 1) * 0.5f;
        float diagonal = MathF.Sqrt(width * (float)width + height * (float)height) * 0.5f;
        for (int x = 0; x < width; x++)
        {
            Rgba32 p = input[x];
            float r = channelLut[p.R], g = channelLut[256 + p.G], b = channelLut[512 + p.B];
            if (bands is not null)
            {
                long i = (long)y * width + x;
                r += bands[i];
                g += bands[plane + i];
                b += bands[2 * plane + i];
            }

            if (contrast is not null)
            {
                float delta = Curve(contrast.AsSpan(LumaOffset, Knots), Luma(r, g, b));
                r += delta;
                g += delta;
                b += delta;
            }

            if (saturation is not null)
            {
                Chroma(saturation, ref r, ref g, ref b);
            }

            if (vibrance is not null)
            {
                Chroma(vibrance, ref r, ref g, ref b);
            }

            if (vignette is not null)
            {
                float dx = x - (width - 1) * 0.5f;
                float radius = MathF.Min(MathF.Sqrt(dx * dx + dy * dy) / diagonal, 1);
                float gain = MathF.Exp(Curve(vignette.AsSpan(RadialOffset, Knots), radius * 255));
                r = Encode(ToLinear(r) * gain);
                g = Encode(ToLinear(g) * gain);
                b = Encode(ToLinear(b) * gain);
            }

            output[x] = new Rgba32(ToByte(r), ToByte(g), ToByte(b), p.A);
        }
    }

    // Luma curve plus a chroma gain keyed by saturation (max − min) / max, both on sRGB values.
    private static void Chroma(float[] record, ref float r, ref float g, ref float b)
    {
        r = Math.Clamp(r, 0, 255);
        g = Math.Clamp(g, 0, 255);
        b = Math.Clamp(b, 0, 255);
        float luma = Luma(r, g, b);
        float max = MathF.Max(r, MathF.Max(g, b)), min = MathF.Min(r, MathF.Min(g, b));
        float key = max > 0 ? 255 * (max - min) / max : 0;
        float gain = 1 + Curve(record.AsSpan(GainOffset, Knots), key);
        float target = luma + Curve(record.AsSpan(LumaOffset, Knots), luma);
        r = target + gain * (r - luma);
        g = target + gain * (g - luma);
        b = target + gain * (b - luma);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Luma(float r, float g, float b) => LumaR * r + LumaG * g + LumaB * b;

    // Piecewise-linear curve on 33 knots evenly spread over 0–255.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Curve(ReadOnlySpan<float> knots, float x)
    {
        float t = Math.Clamp(x, 0, 255) * ((Knots - 1) / 255f);
        int i = Math.Min((int)t, Knots - 2);
        return knots[i] + (knots[i + 1] - knots[i]) * (t - i);
    }

    // The parameter record for slider value v: linear between the measured values, from the identity at 0, and beyond
    // the outermost measured value by scaling it. A side without measurements mirrors the other one.
    internal static float[]? Record(ReadOnlySpan<float> values, ReadOnlySpan<float> table, float v)
    {
        if (v == 0)
        {
            return null;
        }

        float sign = MathF.Sign(v);
        bool mirrored = true;
        foreach (float value in values)
        {
            mirrored &= MathF.Sign(value) != sign;
        }

        float magnitude = MathF.Abs(v), side = mirrored ? -sign : sign;
        int lower = -1, upper = -1;
        for (int i = 0; i < values.Length; i++)
        {
            if (MathF.Sign(values[i]) != side)
            {
                continue;
            }

            float a = MathF.Abs(values[i]);
            if (a <= magnitude && (lower < 0 || a > MathF.Abs(values[lower])))
            {
                lower = i;
            }

            if (a >= magnitude && (upper < 0 || a < MathF.Abs(values[upper])))
            {
                upper = i;
            }
        }

        var record = new float[HeifPhotosTables.RecordLength];
        if (upper < 0 || lower < 0 || lower == upper)
        {
            // Below the first or beyond the last measured value, or exactly on one: scale it.
            int index = upper >= 0 ? upper : lower;
            Accumulate(record, table, index, magnitude / MathF.Abs(values[index]));
        }
        else
        {
            float a = MathF.Abs(values[lower]), b = MathF.Abs(values[upper]);
            float f = (magnitude - a) / (b - a);
            Accumulate(record, table, lower, 1 - f);
            Accumulate(record, table, upper, f);
        }

        if (mirrored)
        {
            for (int i = 0; i < record.Length; i++)
            {
                record[i] = -record[i];
            }
        }

        return record;
    }

    private static void Accumulate(float[] record, ReadOnlySpan<float> table, int index, float weight)
    {
        ReadOnlySpan<float> source = table.Slice(index * HeifPhotosTables.RecordLength, HeifPhotosTables.RecordLength);
        for (int i = 0; i < record.Length; i++)
        {
            record[i] += weight * source[i];
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte ToByte(float value) => (byte)Math.Clamp((int)MathF.Round(value), 0, 255);

    private static float ToLinear(float value)
    {
        float s = Math.Clamp(value, 0, 255) / 255;
        return s <= 0.04045f ? s / 12.92f : MathF.Pow((s + 0.055f) / 1.055f, 2.4f);
    }

    private static float Encode(float linear)
    {
        float l = Math.Clamp(linear, 0, 1);
        return 255 * (l <= 0.0031308f ? 12.92f * l : 1.055f * MathF.Pow(l, 1 / 2.4f) - 0.055f);
    }
}
