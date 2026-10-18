using System.Buffers;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace PictureSharp.Formats.Heif.Codecs;

// Depth-of-field rendering for Apple Portrait photographs. Each pixel gets a blur radius from its disparity difference
// to the focus; the blurred colour interpolates between disc-blurred layers of growing radius. Radii up to 3 px are
// gathered from the photograph itself, larger ones from layers computed on a 2×2 box pyramid and sampled bilinearly.
// Layers are normalized convolutions in linear light that leave out the protected subject (matte), so subject colours
// do not bleed into the background. Every output row depends only on its inputs, so the result does not depend on the
// thread count.
//
// The radius model was fitted to Photos renderings of four iPhone 17 Pro photographs (1x/2x and 4x lenses at f/4.5):
// r = max(0, Scale · L · φ^1.5 · ((4.5 / N) · |D − Df| − DepthOfField)), with L the photograph's longer side in
// pixels, φ the focal length in units of the sensor's longer side (from the depth intrinsics), N the f-number and D
// disparity in 1/m. The portrait matte protects only pixels whose radius stays small, so people far behind the focus
// are blurred like their surroundings.
internal sealed class HeifPortraitRenderer
{
    public const float ReferenceAperture = 4.5f;
    private const float Scale = 0.0055f;
    private const float DepthOfField = 0.02f;
    // About a 50 mm full-frame equivalent, used when the depth item has no intrinsics.
    public const float DefaultFocalRatio = 1.39f;
    private const int DirectRadius = 3;
    private const int BandRows = 32;
    private const float MinimumWeight = 1e-3f;

    // Layer radii in pixels of the photograph; up to DirectRadius they are gathered directly, the rest come from
    // pyramid levels where the radius spans 2.25–3.25 texels.
    private static ReadOnlySpan<float> LayerRadii => [0f, 1f, 2f, 3f, 4.5f, 6.5f, 9f, 13f, 18f, 26f, 36f, 52f, 72f, 104f, 144f, 208f];

    private static readonly float[] LinearLut = BuildLinearLut();
    private static readonly float[] EncodeThresholds = BuildEncodeThresholds();

    private readonly int width, height, depthWidth, depthHeight, matteWidth, matteHeight;
    private readonly float[] disparity;
    private readonly byte[]? matte;
    private readonly float focus, slope, offset, maxRadius, matteLow, matteHigh;
    private readonly ParallelOptions parallel;
    private readonly Axis depthX, depthY;
    private readonly Axis? matteX, matteY;

    public HeifPortraitRenderer(int width, int height, float[] disparity, int depthWidth, int depthHeight, byte[]? matte,
        int matteWidth, int matteHeight, float focus, float aperture, float focalRatio, int maxDegreeOfParallelism,
        CancellationToken cancellationToken)
    {
        this.width = width;
        this.height = height;
        this.disparity = disparity;
        this.depthWidth = depthWidth;
        this.depthHeight = depthHeight;
        this.matte = matte;
        this.matteWidth = matteWidth;
        this.matteHeight = matteHeight;
        this.focus = focus;
        float longer = Math.Max(width, height);
        float strength = Scale * longer * MathF.Pow(focalRatio, 1.5f);
        slope = strength * ReferenceAperture / aperture;
        offset = strength * DepthOfField;
        maxRadius = Math.Min(LayerRadii[^1], longer / 40f);
        matteLow = 4f * longer / 4032f;
        matteHigh = 8f * longer / 4032f;
        depthX = new Axis(width, depthWidth);
        depthY = new Axis(height, depthHeight);
        matteX = matte is null ? null : new Axis(width, matteWidth);
        matteY = matte is null ? null : new Axis(height, matteHeight);
        parallel = new ParallelOptions { MaxDegreeOfParallelism = maxDegreeOfParallelism, CancellationToken = cancellationToken };
    }

    // The number of layers built from the pyramid for the largest radius in the map; 0 when no pixel needs one.
    private int StoredLayers()
    {
        float largest = 0;
        foreach (float d in disparity)
        {
            largest = Math.Max(largest, Radius(d));
        }

        int count = 0;
        for (int k = DirectRadius + 1; k < LayerRadii.Length && LayerRadii[k - 1] < largest; k++)
        {
            count++;
        }

        return count;
    }

    private static int LevelOf(float radius) => BitOperations.Log2((uint)(radius / 2.25f));

    // Working pixels for MaxPixels: the result plus every pyramid level and stored layer.
    public long WorkingPixels()
    {
        long total = (long)width * height;
        int layers = StoredLayers();
        int maxLevel = layers == 0 ? 0 : LevelOf(LayerRadii[DirectRadius + layers]);
        for (int level = 1; level <= maxLevel; level++)
        {
            total += (long)LevelSize(width, level) * LevelSize(height, level);
        }

        for (int k = DirectRadius + 1; k <= DirectRadius + layers; k++)
        {
            int level = LevelOf(LayerRadii[k]);
            total += (long)LevelSize(width, level) * LevelSize(height, level);
        }

        return total;
    }

    private static int LevelSize(int size, int level) => ((size - 1) >> level) + 1;

    public void Render(ReadOnlyMemory<Rgba32> source, Memory<Rgba32> destination)
    {
        int layerCount = StoredLayers();
        var layers = new Layer[layerCount];
        var levels = new List<Level>();
        try
        {
            if (layerCount > 0)
            {
                int maxLevel = LevelOf(LayerRadii[DirectRadius + layerCount]);
                levels.Add(BuildFirstLevel(source));
                for (int level = 2; level <= maxLevel; level++)
                {
                    levels.Add(Downsample(levels[^1]));
                }

                for (int i = 0; i < layerCount; i++)
                {
                    float radius = LayerRadii[DirectRadius + 1 + i];
                    int level = LevelOf(radius);
                    layers[i] = BuildLayer(levels[level - 1], radius / (1 << level), level);
                }
            }

            foreach (Level level in levels)
            {
                level.Dispose();
            }

            levels.Clear();
            parallel.CancellationToken.ThrowIfCancellationRequested();
            int bands = (height + BandRows - 1) / BandRows;
            if (parallel.MaxDegreeOfParallelism == 1)
            {
                for (int band = 0; band < bands; band++)
                {
                    RenderBand(source, destination, layers, band * BandRows, Math.Min(height, (band + 1) * BandRows));
                }
            }
            else
            {
                Parallel.For(0, bands, parallel, band =>
                    RenderBand(source, destination, layers, band * BandRows, Math.Min(height, (band + 1) * BandRows)));
            }
        }
        finally
        {
            foreach (Level level in levels)
            {
                level.Dispose();
            }

            foreach (Layer? layer in layers)
            {
                layer?.Dispose();
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private float Radius(float d) => Math.Clamp(slope * Math.Abs(d - focus) - offset, 0, maxRadius);

    // The share of the protected subject (0–1) at a pixel with the given radius: the matte, faded out as the radius
    // grows from matteLow to matteHigh.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private float Protection(float matteValue, float radius) =>
        matteValue * Math.Clamp((matteHigh - radius) / (matteHigh - matteLow), 0, 1);

    // Bilinear sampling of a low-resolution map at the centre of a photograph pixel, with edge clamping.
    private sealed class Axis
    {
        public readonly int[] Low;
        public readonly float[] Fraction;

        public Axis(int size, int mapSize)
        {
            Low = new int[size];
            Fraction = new float[size];
            float scale = (float)mapSize / size;
            for (int i = 0; i < size; i++)
            {
                float position = Math.Clamp((i + 0.5f) * scale - 0.5f, 0, mapSize - 1);
                int low = Math.Min((int)position, mapSize - 2 < 0 ? 0 : mapSize - 2);
                Low[i] = low;
                Fraction[i] = mapSize == 1 ? 0 : position - low;
            }
        }
    }

    // Radius and protection for one row of the photograph.
    private void RowParameters(int y, Span<float> radius, Span<float> protection)
    {
        int dy = depthY.Low[y];
        float fy = depthY.Fraction[y];
        int nextRow = Math.Min(dy + 1, depthHeight - 1);
        ReadOnlySpan<float> top = disparity.AsSpan(dy * depthWidth, depthWidth), bottom = disparity.AsSpan(nextRow * depthWidth, depthWidth);
        for (int x = 0; x < width; x++)
        {
            int dx = depthX.Low[x], next = Math.Min(dx + 1, depthWidth - 1);
            float fx = depthX.Fraction[x];
            float upper = top[dx] + (top[next] - top[dx]) * fx, lower = bottom[dx] + (bottom[next] - bottom[dx]) * fx;
            radius[x] = Radius(upper + (lower - upper) * fy);
        }

        if (matte is null || matteX is null || matteY is null)
        {
            protection.Clear();
            return;
        }

        int my = matteY.Low[y];
        float mfy = matteY.Fraction[y];
        int matteNext = Math.Min(my + 1, matteHeight - 1);
        ReadOnlySpan<byte> mTop = matte.AsSpan(my * matteWidth, matteWidth), mBottom = matte.AsSpan(matteNext * matteWidth, matteWidth);
        for (int x = 0; x < width; x++)
        {
            int mx = matteX.Low[x], next = Math.Min(mx + 1, matteWidth - 1);
            float fx = matteX.Fraction[x];
            float upper = mTop[mx] + (mTop[next] - mTop[mx]) * fx, lower = mBottom[mx] + (mBottom[next] - mBottom[mx]) * fx;
            protection[x] = Protection((upper + (lower - upper) * mfy) / 255f, radius[x]);
        }
    }

    // A row of premultiplied linear colour with the background weight (1 − protection) in W.
    private void WeightedRow(ReadOnlySpan<Rgba32> row, ReadOnlySpan<float> protection, Span<Vector4> output)
    {
        float[] lut = LinearLut;
        for (int x = 0; x < width; x++)
        {
            Rgba32 p = row[x];
            float w = 1 - protection[x];
            output[x] = new Vector4(lut[p.R] * w, lut[p.G] * w, lut[p.B] * w, w);
        }
    }

    private sealed class Level(int width, int height) : IDisposable
    {
        public readonly int Width = width, Height = height;
        public readonly Vector4[] Texels = ArrayPool<Vector4>.Shared.Rent(width * height);

        public void Dispose() => ArrayPool<Vector4>.Shared.Return(Texels);
    }

    private sealed class Layer(int width, int height, int level) : IDisposable
    {
        public readonly int Width = width, Height = height, Shift = level;
        public readonly Half[] Texels = ArrayPool<Half>.Shared.Rent(width * height * 4);

        public void Dispose() => ArrayPool<Half>.Shared.Return(Texels);

        // Bilinear sample at the centre of photograph pixel (x, y).
        public Vector4 Sample(int x, int y)
        {
            float scale = 1f / (1 << Shift);
            float px = Math.Clamp((x + 0.5f) * scale - 0.5f, 0, Width - 1), py = Math.Clamp((y + 0.5f) * scale - 0.5f, 0, Height - 1);
            int x0 = (int)px, y0 = (int)py, x1 = Math.Min(x0 + 1, Width - 1), y1 = Math.Min(y0 + 1, Height - 1);
            float fx = px - x0, fy = py - y0;
            Vector4 top = Vector4.Lerp(Load(x0, y0), Load(x1, y0), fx), bottom = Vector4.Lerp(Load(x0, y1), Load(x1, y1), fx);
            return Vector4.Lerp(top, bottom, fy);
        }

        private Vector4 Load(int x, int y)
        {
            int i = (y * Width + x) * 4;
            return new Vector4((float)Texels[i], (float)Texels[i + 1], (float)Texels[i + 2], (float)Texels[i + 3]);
        }
    }

    // Level 1 averages 2×2 photograph pixels of premultiplied linear colour and background weight.
    private Level BuildFirstLevel(ReadOnlyMemory<Rgba32> source)
    {
        var level = new Level(LevelSize(width, 1), LevelSize(height, 1));
        HeifParallel.ForRows(level.Height, level.Width, parallel, (first, end) =>
        {
            float[] radius = ArrayPool<float>.Shared.Rent(width), protection = ArrayPool<float>.Shared.Rent(width);
            Vector4[] upper = ArrayPool<Vector4>.Shared.Rent(width), lower = ArrayPool<Vector4>.Shared.Rent(width);
            try
            {
                for (int ly = first; ly < end; ly++)
                {
                    parallel.CancellationToken.ThrowIfCancellationRequested();
                    int y0 = ly * 2, y1 = Math.Min(y0 + 1, height - 1);
                    RowParameters(y0, radius, protection);
                    WeightedRow(source.Span.Slice(y0 * width, width), protection, upper);
                    RowParameters(y1, radius, protection);
                    WeightedRow(source.Span.Slice(y1 * width, width), protection, lower);
                    float rows = y1 == y0 ? 1 : 2;
                    Span<Vector4> output = level.Texels.AsSpan(ly * level.Width, level.Width);
                    for (int lx = 0; lx < level.Width; lx++)
                    {
                        int x0 = lx * 2, x1 = x0 + 1;
                        Vector4 sum = upper[x0] + (y1 == y0 ? Vector4.Zero : lower[x0]);
                        float count = rows;
                        if (x1 < width)
                        {
                            sum += upper[x1] + (y1 == y0 ? Vector4.Zero : lower[x1]);
                            count += rows;
                        }

                        output[lx] = sum / count;
                    }
                }
            }
            finally
            {
                ArrayPool<float>.Shared.Return(radius);
                ArrayPool<float>.Shared.Return(protection);
                ArrayPool<Vector4>.Shared.Return(upper);
                ArrayPool<Vector4>.Shared.Return(lower);
            }
        });
        return level;
    }

    private Level Downsample(Level source)
    {
        var level = new Level((source.Width + 1) / 2, (source.Height + 1) / 2);
        HeifParallel.ForRows(level.Height, level.Width, parallel, (first, end) =>
        {
            for (int ly = first; ly < end; ly++)
            {
                parallel.CancellationToken.ThrowIfCancellationRequested();
                int y0 = ly * 2, y1 = Math.Min(y0 + 1, source.Height - 1);
                for (int lx = 0; lx < level.Width; lx++)
                {
                    int x0 = lx * 2, x1 = Math.Min(x0 + 1, source.Width - 1);
                    Vector4 sum = source.Texels[y0 * source.Width + x0];
                    float count = 1;
                    if (x1 != x0)
                    {
                        sum += source.Texels[y0 * source.Width + x1];
                        count++;
                    }

                    if (y1 != y0)
                    {
                        sum += source.Texels[y1 * source.Width + x0];
                        count++;
                        if (x1 != x0)
                        {
                            sum += source.Texels[y1 * source.Width + x1];
                            count++;
                        }
                    }

                    level.Texels[ly * level.Width + lx] = sum / count;
                }
            }
        });
        return level;
    }

    // An antialiased disc: weight = clamp(radius − distance + 0.5, 0, 1).
    private static (int Dx, int Dy, float Weight)[] Disc(float radius)
    {
        int extent = (int)MathF.Ceiling(radius + 0.5f);
        var taps = new List<(int, int, float)>();
        for (int dy = -extent; dy <= extent; dy++)
        {
            for (int dx = -extent; dx <= extent; dx++)
            {
                float weight = Math.Clamp(radius - MathF.Sqrt(dx * dx + dy * dy) + 0.5f, 0, 1);
                if (weight > 0)
                {
                    taps.Add((dx, dy, weight));
                }
            }
        }

        return [.. taps];
    }

    // Disc convolution of a level; texels outside the level do not contribute and the sum is divided by the kernel
    // weight that fell inside, so W stays the mean background weight.
    private Layer BuildLayer(Level source, float radius, int shift)
    {
        (int Dx, int Dy, float Weight)[] taps = Disc(radius);
        var layer = new Layer(source.Width, source.Height, shift);
        HeifParallel.ForRows(source.Height, source.Width, parallel, (first, end) =>
        {
            for (int y = first; y < end; y++)
            {
                parallel.CancellationToken.ThrowIfCancellationRequested();
                for (int x = 0; x < source.Width; x++)
                {
                    Vector4 sum = Vector4.Zero;
                    float total = 0;
                    foreach ((int dx, int dy, float weight) in taps)
                    {
                        int sx = x + dx, sy = y + dy;
                        if ((uint)sx < (uint)source.Width && (uint)sy < (uint)source.Height)
                        {
                            sum += source.Texels[sy * source.Width + sx] * weight;
                            total += weight;
                        }
                    }

                    sum /= total;
                    int i = (y * source.Width + x) * 4;
                    layer.Texels[i] = (Half)sum.X;
                    layer.Texels[i + 1] = (Half)sum.Y;
                    layer.Texels[i + 2] = (Half)sum.Z;
                    layer.Texels[i + 3] = (Half)sum.W;
                }
            }
        });
        return layer;
    }

    private void RenderBand(ReadOnlyMemory<Rgba32> sourceMemory, Memory<Rgba32> destinationMemory, Layer[] layers, int first, int end)
    {
        parallel.CancellationToken.ThrowIfCancellationRequested();
        // Rows first − DirectRadius … end + DirectRadius of weighted colour for the direct layers.
        int top = Math.Max(0, first - DirectRadius), bottom = Math.Min(height, end + DirectRadius);
        int rows = bottom - top;
        Vector4[] weighted = ArrayPool<Vector4>.Shared.Rent(rows * width);
        float[] radii = ArrayPool<float>.Shared.Rent(rows * width), protections = ArrayPool<float>.Shared.Rent(rows * width);
        try
        {
            ReadOnlySpan<Rgba32> source = sourceMemory.Span;
            for (int y = top; y < bottom; y++)
            {
                int offset = (y - top) * width;
                Span<float> radius = radii.AsSpan(offset, width), protection = protections.AsSpan(offset, width);
                RowParameters(y, radius, protection);
                WeightedRow(source.Slice(y * width, width), protection, weighted.AsSpan(offset, width));
            }

            Span<Rgba32> destination = destinationMemory.Span;
            float[] lut = LinearLut;
            for (int y = first; y < end; y++)
            {
                int offset = (y - top) * width;
                ReadOnlySpan<Rgba32> sourceRow = source.Slice(y * width, width);
                Span<Rgba32> output = destination.Slice(y * width, width);
                for (int x = 0; x < width; x++)
                {
                    Rgba32 original = sourceRow[x];
                    float radius = radii[offset + x], protection = protections[offset + x];
                    if (radius <= 0 || protection >= 1)
                    {
                        output[x] = original;
                        continue;
                    }

                    var sharp = new Vector3(lut[original.R], lut[original.G], lut[original.B]);
                    int k = 1;
                    while (k < LayerRadii.Length - 1 && LayerRadii[k] < radius)
                    {
                        k++;
                    }

                    float t = (radius - LayerRadii[k - 1]) / (LayerRadii[k] - LayerRadii[k - 1]);
                    Vector4 a = k == 1 ? new Vector4(sharp, 1) : LayerValue(k - 1, x, y, top, weighted, layers);
                    Vector4 b = LayerValue(k, x, y, top, weighted, layers);
                    Vector3 blurred;
                    if (a.W > MinimumWeight && b.W > MinimumWeight)
                    {
                        blurred = Vector3.Lerp(Color(a), Color(b), t);
                    }
                    else if (a.W > MinimumWeight || b.W > MinimumWeight)
                    {
                        blurred = Color(a.W > MinimumWeight ? a : b);
                    }
                    else
                    {
                        blurred = sharp;
                    }

                    Vector3 linear = sharp * protection + blurred * (1 - protection);
                    output[x] = new Rgba32(Encode(linear.X), Encode(linear.Y), Encode(linear.Z), original.A);
                }
            }
        }
        finally
        {
            ArrayPool<Vector4>.Shared.Return(weighted);
            ArrayPool<float>.Shared.Return(radii);
            ArrayPool<float>.Shared.Return(protections);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector3 Color(Vector4 value) => new Vector3(value.X, value.Y, value.Z) / value.W;

    // The premultiplied value of blurred layer k ≥ 1 at pixel (x, y): a direct disc gather from the band for radii up
    // to DirectRadius, otherwise a bilinear sample of the stored layer (always built for the largest radius in use).
    private Vector4 LayerValue(int k, int x, int y, int top, Vector4[] weighted, Layer[] layers)
    {
        if (k > DirectRadius)
        {
            return layers[k - DirectRadius - 1].Sample(x, y);
        }

        (int Dx, int Dy, float Weight)[] taps = DirectDiscs[k];
        int rows = Math.Min(height, y + DirectRadius + 1) - top;
        Vector4 sum = Vector4.Zero;
        float total = 0;
        foreach ((int dx, int dy, float weight) in taps)
        {
            int sx = x + dx, sy = y + dy - top;
            if ((uint)sx < (uint)width && sy >= 0 && sy < rows && y + dy >= 0 && y + dy < height)
            {
                sum += weighted[sy * width + sx] * weight;
                total += weight;
            }
        }

        return sum / total;
    }

    private static readonly (int Dx, int Dy, float Weight)[][] DirectDiscs = [[], Disc(1), Disc(2), Disc(3)];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte Encode(float linear)
    {
        // The index of the first threshold above the value: thresholds are the linear values halfway between codes.
        float[] thresholds = EncodeThresholds;
        int low = 0, high = 255;
        while (low < high)
        {
            int middle = (low + high) >> 1;
            if (thresholds[middle] <= linear)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return (byte)low;
    }

    // sRGB transfer functions evaluated in double once; the tables make the per-pixel path platform independent.
    private static float[] BuildLinearLut()
    {
        var lut = new float[256];
        for (int i = 0; i < 256; i++)
        {
            lut[i] = (float)ToLinear(i / 255.0);
        }

        return lut;
    }

    private static float[] BuildEncodeThresholds()
    {
        var thresholds = new float[255];
        for (int i = 0; i < 255; i++)
        {
            thresholds[i] = (float)ToLinear((i + 0.5) / 255.0);
        }

        return thresholds;
    }

    private static double ToLinear(double v) => v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
}
