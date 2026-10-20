using System.Buffers;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace PictureSharp.Formats.Heif.Codecs;

// E5 bokeh and texture: grain, daytime point lights and night lamps (cat's-eye discs).
internal sealed partial class HeifPortraitRenderer
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Highlight(Rgba32 p, float radius)
    {
        int high = Math.Max(p.R, Math.Max(p.G, p.B)), low = Math.Min(p.R, Math.Min(p.G, p.B));
        return Math.Clamp((high / 255f - 0.95f) / 0.04f, 0, 1) * Math.Clamp((low / 255f - 0.5f) / 0.15f, 0, 1)
            * Math.Clamp((radius - 4) / 8, 0, 1);
    }

    // The share of the picture that is near-white background sets the boost strength; rows are counted in order, so the
    // result does not depend on the thread count.
    private float HighlightGate(ReadOnlyMemory<Rgba32> source)
    {
        double[] rows = new double[height];
        HeifParallel.ForRows(height, width, parallel, (first, end) =>
        {
            float[] radius = ArrayPool<float>.Shared.Rent(width), protection = ArrayPool<float>.Shared.Rent(width);
            try
            {
                for (int y = first; y < end; y++)
                {
                    RowParameters(y, radius, protection);
                    ReadOnlySpan<Rgba32> row = source.Span.Slice(y * width, width);
                    double sum = 0;
                    for (int x = 0; x < width; x++)
                    {
                        sum += Highlight(row[x], radius[x]) * (1 - protection[x]);  // the gate counts all near-white background
                    }

                    rows[y] = sum;
                }
            }
            finally
            {
                ArrayPool<float>.Shared.Return(radius);
                ArrayPool<float>.Shared.Return(protection);
            }
        });
        double total = 0;
        foreach (double value in rows)
        {
            total += value;
        }

        float share = (float)(total / ((double)width * height));
        whiteSums = BuildWhiteSums(source);
        return Math.Clamp((HighlightGateHigh - share) / (HighlightGateHigh - HighlightGateLow), 0, 1);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool NearWhite(Rgba32 p) => Math.Max(p.R, Math.Max(p.G, p.B)) >= 245 && Math.Min(p.R, Math.Min(p.G, p.B)) >= 128;

    private int[] BuildWhiteSums(ReadOnlyMemory<Rgba32> source)
    {
        int w = LevelSize(width, 1), h = LevelSize(height, 1), stride = w + 1;
        int[] sums = ArrayPool<int>.Shared.Rent(stride * (h + 1));
        Array.Clear(sums, 0, stride * (h + 1));
        ReadOnlySpan<Rgba32> pixels = source.Span;
        for (int ty = 0; ty < h; ty++)
        {
            int row = 0;
            for (int tx = 0; tx < w; tx++)
            {
                int count = 0;
                for (int y = ty * 2; y < Math.Min(height, ty * 2 + 2); y++)
                {
                    for (int x = tx * 2; x < Math.Min(width, tx * 2 + 2); x++)
                    {
                        count += NearWhite(pixels[y * width + x]) ? 1 : 0;
                    }
                }

                row += count;
                sums[(ty + 1) * stride + tx + 1] = sums[ty * stride + tx + 1] + row;
            }
        }

        return sums;
    }

    // The near-white share of the (2·WhiteWindow + 1)² texels around a pixel: lamps and drops are small, a lit wall or
    // umbrella is not and is left as it is.
    private float WhiteShare(int x, int y, int window)
    {
        int w = LevelSize(width, 1), h = LevelSize(height, 1), stride = w + 1, tx = x / 2, ty = y / 2;
        int left = Math.Max(0, tx - window), right = Math.Min(w, tx + window + 1);
        int top = Math.Max(0, ty - window), bottom = Math.Min(h, ty + window + 1);
        int[] s = whiteSums!;
        int count = s[bottom * stride + right] - s[top * stride + right] - s[bottom * stride + left] + s[top * stride + left];
        return count / (4f * (right - left) * (bottom - top));
    }

    // WeightedRow for level 1. At night the light of lamp cores behind the focus (the patent's background test) goes to
    // moved instead of the layers, to be scattered as cat's-eye discs.
    private void HighlightRow(int y, ReadOnlySpan<Rgba32> row, ReadOnlySpan<float> protection, ReadOnlySpan<float> radius, Span<Vector4> output,
        Span<Vector3> moved, bool boost)
    {
        float[] lut = LinearLut;
        float[] depth = ArrayPool<float>.Shared.Rent(width);
        float strength = boost ? highlightStrength : 0;
        if (strength > 0)
        {
            SampleDepth(disparity, y, depth);
        }

        for (int x = 0; x < width; x++)
        {
            Rgba32 p = row[x];
            float w = 1 - protection[x];
            float h = strength > 0 ? strength * Highlight(p, radius[x]) * Math.Clamp((focus - depth[x]) / DepthOfField, 0, 1) : 0;
            if (h > 0)
            {
                // swiatla: lamp cores 1.0 with 0.08–0.19 around; the lit umbrella 0.7–0.85 with 0.24–0.40.
                h *= Math.Clamp((WhiteShare(x, y, CoreWindow) - 0.8f) / 0.17f, 0, 1) * Math.Clamp((0.25f - WhiteShare(x, y, WhiteWindow)) / 0.1f, 0, 1);
            }
            var color = new Vector3(lut[p.R], lut[p.G], lut[p.B]);
            output[x] = new Vector4(color * (w * (1 - h)), w);
            moved[x] = color * (w * h);
        }

        ArrayPool<float>.Shared.Return(depth);
    }

    // Moves the light of point lights out of level 1 (so the gathered layers no longer spread it) and scatters it as
    // antialiased hard discs of the point's own radius, which keeps their total light. Each texel decides on its own,
    // and the discs are added to every texel in the points' raster order, so the sums do not depend on the thread count.
    private void ScatterPoints(Level level, float[] texelRadius)
    {
        int w = level.Width, h = level.Height, stride = w + 1;
        double[] integral = ArrayPool<double>.Shared.Rent(stride * (h + 1));
        try
        {
            Array.Clear(integral, 0, stride * (h + 1));
            for (int y = 0; y < h; y++)
            {
                double row = 0;
                for (int x = 0; x < w; x++)
                {
                    row += Luma(level.Texels[y * w + x]);
                    integral[(y + 1) * stride + x + 1] = integral[y * stride + x + 1] + row;
                }
            }

            float minimumRadius = LayerRadii[DirectRadius];
            var found = new Splat[]?[h];
            HeifParallel.ForRows(h, w, parallel, (first, end) =>
            {
                var discs = new List<Splat>();
                for (int y = first; y < end; y++)
                {
                    parallel.CancellationToken.ThrowIfCancellationRequested();
                    int top = Math.Max(0, y - PointWindow), bottom = Math.Min(h, y + PointWindow + 1);
                    discs.Clear();
                    for (int x = 0; x < w; x++)
                    {
                        float radius = texelRadius[y * w + x];
                        Vector4 texel = level.Texels[y * w + x];
                        float luma = Luma(texel);
                        if (radius <= minimumRadius || luma < PointLuma)
                        {
                            continue;
                        }

                        int left = Math.Max(0, x - PointWindow), right = Math.Min(w, x + PointWindow + 1);
                        double area = (bottom - top) * (right - left);
                        float mean = (float)((integral[bottom * stride + right] - integral[top * stride + right]
                            - integral[bottom * stride + left] + integral[top * stride + left]) / area);
                        float contrast = luma / Math.Max(mean, 1e-6f);
                        float share = Math.Clamp((contrast - PointContrast) / (PointFull - PointContrast), 0, 1)
                            * Math.Clamp((luma - PointLuma) / PointLuma, 0, 1)
                            * Math.Clamp((radius - minimumRadius) / (LayerRadii[DirectRadius + 1] - minimumRadius), 0, 1);
                        if (share <= 0)
                        {
                            continue;
                        }

                        // Only the light above the surroundings moves; the rest stays in the gathered layers.
                        var light = new Vector3(texel.X, texel.Y, texel.Z) * (share * (1 - 1 / contrast));
                        level.Texels[y * w + x] = texel - new Vector4(light, 0);
                        float brightest = Math.Max(texel.X, Math.Max(texel.Y, texel.Z)) / Math.Max(texel.W, MinimumWeight);
                        // A small disc keeps a clipped core clipped anyway; the extra light shows once it is spread widely.
                        light *= 1 + (PointGain - 1) * Math.Clamp((brightest - 0.8f) / 0.2f, 0, 1) * Math.Clamp((radius - 8) / 16, 0, 1);
                        var shape = new DiscShape(radius / 2, 0, 0, eye: false);
                        discs.Add(new Splat(x, light, shape.Radius, shape.Total()));
                    }

                    found[y] = discs.Count == 0 ? null : [.. discs];
                }
            });
            Scatter(found, w, h, eye: false);
        }
        finally
        {
            ArrayPool<double>.Shared.Return(integral);
        }
    }

    // Scatters each lamp texel's light (already scaled by HighlightGain) as a cat's-eye disc of the texel's radius.
    private void ScatterHighlights(float[] texelRadius)
    {
        int w = LevelSize(width, 1), h = LevelSize(height, 1);
        Vector3[] lights = highlightTexels!;
        var found = new Splat[]?[h];
        HeifParallel.ForRows(h, w, parallel, (first, end) =>
        {
            var discs = new List<Splat>();
            for (int y = first; y < end; y++)
            {
                parallel.CancellationToken.ThrowIfCancellationRequested();
                discs.Clear();
                for (int x = 0; x < w; x++)
                {
                    Vector3 light = lights[y * w + x];
                    if (light.X + light.Y + light.Z <= 1e-6f)
                    {
                        continue;
                    }

                    DiscShape shape = EyeShape(x, y, texelRadius[y * w + x] / 2, w, h);
                    discs.Add(new Splat(x, light, shape.Radius, shape.Total()));
                }

                found[y] = discs.Count == 0 ? null : [.. discs];
            }
        });
        Scatter(found, w, h, eye: true);
    }

    // The light of one point and the disc it is spread over, centred at column X of its row (in level-1 texels).
    private readonly struct Splat(int x, Vector3 light, float radius, float total)
    {
        public readonly int X = x;
        public readonly Vector3 Light = light;
        public readonly float Radius = radius, Total = total;
    }

    // A disc cut by the same disc shifted towards the picture centre by CatEye · radius at the corner.
    private static DiscShape EyeShape(int x, int y, float radius, int w, int h)
    {
        float centreX = (w - 1) / 2f, centreY = (h - 1) / 2f, corner = MathF.Sqrt(centreX * centreX + centreY * centreY);
        float shift = CatEye * radius / corner;
        return new DiscShape(radius, (centreX - x) * shift, (centreY - y) * shift, eye: true);
    }

    // An antialiased disc, or a cat's-eye disc (cut by the same disc shifted by Shift).
    private readonly struct DiscShape(float radius, float shiftX, float shiftY, bool eye)
    {
        public readonly float Radius = radius;
        public readonly int Extent = (int)MathF.Ceiling(radius + 0.5f);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public float Weight(int dx, int dy)
        {
            float disc = Math.Clamp(Radius - Root(dx * dx + dy * dy) + 0.5f, 0, 1);
            if (!eye)
            {
                return disc;
            }

            float ex = dx - shiftX, ey = dy - shiftY;
            return Math.Min(disc, Math.Clamp(Radius - MathF.Sqrt(ex * ex + ey * ey) + 0.5f, 0, 1));
        }

        // The largest |dx| of a tap with weight in row dy (−1 for none); the whole extent for cat's-eye discs.
        public int HalfWidth(int dy)
        {
            if (eye)
            {
                return Extent;
            }

            int half = -1;
            while (half < Extent && Radius - Root((half + 1) * (half + 1) + dy * dy) + 0.5f > 0)
            {
                half++;
            }

            return half;
        }

        // The sum of all tap weights in raster order; taps outside the disc weigh 0 and leave it unchanged.
        public float Total()
        {
            float total = 0;
            for (int dy = -Extent; dy <= Extent; dy++)
            {
                int half = HalfWidth(dy);
                for (int dx = -half; dx <= half; dx++)
                {
                    total += Weight(dx, dy);
                }
            }

            return total;
        }
    }

    // MathF.Sqrt of the small integer squared distances of disc taps, looked up (the same values, computed once).
    private static readonly float[] Roots = BuildRoots();

    private static float[] BuildRoots()
    {
        // Disc extents reach Ceiling(LayerRadii[^1] / 4 + 0.5) texels: the largest radius over 2 at level 1, over 2 again.
        int extent = (int)MathF.Ceiling(LayerRadii[^1] / 2 + 0.5f) + 1;
        var roots = new float[2 * extent * extent + 1];
        for (int i = 0; i < roots.Length; i++)
        {
            roots[i] = MathF.Sqrt(i);
        }

        return roots;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Root(int squared) => (uint)squared < (uint)Roots.Length ? Roots[squared] : MathF.Sqrt(squared);

    // Adds the discs to the point map in row bands; within a band every texel receives the discs in raster order, as on
    // one thread. Light falling outside the picture is lost (the disc's total includes it).
    private void Scatter(Splat[]?[] found, int w, int h, bool eye)
    {
        int extent = -1;
        foreach (Splat[]? row in found)
        {
            foreach (Splat disc in row ?? [])
            {
                extent = Math.Max(extent, (int)MathF.Ceiling(disc.Radius + 0.5f));
            }
        }

        if (extent < 0)
        {
            return;
        }

        // Planar map: red, green and blue planes of w × h texels.
        int plane = w * h;
        points = ArrayPool<float>.Shared.Rent(plane * 3);
        Array.Clear(points, 0, plane * 3);
        (pointsWidth, pointsHeight) = (w, h);
        float[] target = points;
        HeifParallel.ForRows(h, w, parallel, (first, end) =>
        {
            for (int cy = Math.Max(0, first - extent); cy < Math.Min(h, end + extent); cy++)
            {
                parallel.CancellationToken.ThrowIfCancellationRequested();
                foreach (Splat disc in found[cy] ?? [])
                {
                    if (disc.Total <= 0)
                    {
                        continue;
                    }

                    DiscShape shape = eye ? EyeShape(disc.X, cy, disc.Radius, w, h) : new DiscShape(disc.Radius, 0, 0, eye: false);
                    for (int dy = Math.Max(-shape.Extent, first - cy); dy <= Math.Min(shape.Extent, end - 1 - cy); dy++)
                    {
                        int y = cy + dy, half = shape.HalfWidth(dy);
                        int left = Math.Max(-half, -disc.X), right = Math.Min(half, w - 1 - disc.X);
                        if (left > right)
                        {
                            continue;
                        }

                        Span<float> red = target.AsSpan(y * w, w), green = target.AsSpan(plane + y * w, w), blue = target.AsSpan(2 * plane + y * w, w);
                        if (eye)
                        {
                            AddEyeRow(red, green, blue, disc, shape, dy, left, right);
                        }
                        else
                        {
                            AddDiscRow(red, green, blue, disc.X, disc.Light, disc.Radius, disc.Total, dy, left, right);
                        }
                    }
                }
            }
        });
    }

    // Light of a cat's-eye disc in one row; taps of the cut-off part weigh 0 and are skipped.
    private static void AddEyeRow(Span<float> red, Span<float> green, Span<float> blue, in Splat disc, in DiscShape shape, int dy, int left, int right)
    {
        for (int dx = left; dx <= right; dx++)
        {
            float weight = shape.Weight(dx, dy);
            if (weight <= 0)
            {
                continue;
            }

            int x = disc.X + dx;
            Vector3 add = disc.Light * (weight / disc.Total);
            red[x] += add.X;
            green[x] += add.Y;
            blue[x] += add.Z;
        }
    }

    // Light of a round disc centred at column cx in row offset dy, for dx = left … right, all inside the disc.
    internal static void AddDiscRow(Span<float> red, Span<float> green, Span<float> blue, int cx, Vector3 light, float radius, float total,
        int dy, int left, int right)
    {
        if (Vector.IsHardwareAccelerated)
        {
            left = AddDiscRowVector(red, green, blue, cx, light, radius, total, dy, left, right);
        }

        AddDiscRowScalar(red, green, blue, cx, light, radius, total, dy, left, right);
    }

    internal static void AddDiscRowScalar(Span<float> red, Span<float> green, Span<float> blue, int cx, Vector3 light, float radius, float total,
        int dy, int left, int right)
    {
        for (int dx = left; dx <= right; dx++)
        {
            float weight = Math.Clamp(radius - Root(dx * dx + dy * dy) + 0.5f, 0, 1);
            int x = cx + dx;
            Vector3 add = light * (weight / total);
            red[x] += add.X;
            green[x] += add.Y;
            blue[x] += add.Z;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Luma(Vector4 premultiplied) => 0.2126f * premultiplied.X + 0.7152f * premultiplied.Y + 0.0722f * premultiplied.Z;

    // Bilinear samples of the scattered point light at the centres of the pixels of photograph row y.
    private void PointRow(int y, Span<Vector3> output)
    {
        float[] map = points!;
        int plane = pointsWidth * pointsHeight;
        float py = Math.Clamp((y + 0.5f) * 0.5f - 0.5f, 0, pointsHeight - 1);
        int y0 = (int)py, y1 = Math.Min(y0 + 1, pointsHeight - 1);
        float fy = py - y0;
        int upper = y0 * pointsWidth, lower = y1 * pointsWidth;
        for (int x = 0; x < output.Length; x++)
        {
            float px = Math.Clamp((x + 0.5f) * 0.5f - 0.5f, 0, pointsWidth - 1);
            int x0 = (int)px, x1 = Math.Min(x0 + 1, pointsWidth - 1);
            float fx = px - x0;
            var a = new Vector3(map[upper + x0], map[plane + upper + x0], map[2 * plane + upper + x0]);
            var b = new Vector3(map[upper + x1], map[plane + upper + x1], map[2 * plane + upper + x1]);
            var c = new Vector3(map[lower + x0], map[plane + lower + x0], map[2 * plane + lower + x0]);
            var d = new Vector3(map[lower + x1], map[plane + lower + x1], map[2 * plane + lower + x1]);
            output[x] = Lerp(Lerp(a, b, fx), Lerp(c, d, fx), fy);
        }
    }

    // Shifts a linear value by n sRGB code values, using the local slope of the transfer curve.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Grain(float linear, float n)
    {
        float[] lut = LinearLut;
        int code = Encode(linear), low = Math.Max(code - 1, 0), high = Math.Min(code + 1, 255);
        return Math.Max(0, linear + n * (lut[high] - lut[low]) / (high - low));
    }

    // Deterministic noise with unit standard deviation from the pixel position: a 2×2 mean of approximately Gaussian
    // values (sum of four hashed bytes), so neighbours correlate by about one half as in Photos' grain.
    private static float Noise(int x, int y) =>
        (Gauss(x, y) + Gauss(x + 1, y) + Gauss(x, y + 1) + Gauss(x + 1, y + 1)) * 0.5f;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Gauss(int x, int y)
    {
        uint h = (uint)x * 0x8da6b343u ^ (uint)y * 0xd8163841u;
        h ^= h >> 15;
        h *= 0x2c1b3c6du;
        h ^= h >> 12;
        h *= 0x297a2d39u;
        h ^= h >> 15;
        int sum = (int)(h & 0xff) + (int)((h >> 8) & 0xff) + (int)((h >> 16) & 0xff) + (int)(h >> 24);
        return (sum - 510) / 147.8f;
    }
}
