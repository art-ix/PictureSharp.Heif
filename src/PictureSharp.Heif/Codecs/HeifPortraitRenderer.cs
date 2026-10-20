using System.Buffers;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;

namespace PictureSharp.Formats.Heif.Codecs;

// Depth-of-field rendering for Apple Portrait photographs. Each pixel gets a blur radius from its disparity difference
// to the focus; the blurred colour interpolates between disc-blurred layers of growing radius. Radii up to 3 px are
// gathered from the photograph itself, larger ones from layers computed on a 2×2 box pyramid and sampled bilinearly.
// Layers are normalized convolutions in linear light that leave out the protected subject (matte), so subject colours
// do not bleed into the background. Every output row depends only on its inputs, so the result does not depend on the
// thread count.
//
// The radius model was fitted to Photos renderings of four iPhone 17 Pro photographs (1x/2x and 4x lenses at f/4.5):
// r = max(0, Scale · L · φ^1.5 · ((4.5 / N) · Δ − DepthOfField)), with L the photograph's longer side in pixels, φ the
// focal length in units of the sensor's longer side (from the depth intrinsics), N the f-number, D disparity in 1/m
// and Δ its distance from the focus range [Df, Dn]: Df − D behind it, max(0, D − Dn) in front of it. An explicit focus
// point gives Dn = Df; the default subject focus extends Dn to the subject's nearer parts (E3 joint calibration). The
// portrait matte protects only pixels whose radius stays small, so people far behind the focus are blurred like their
// surroundings. Foreground people receive less blur at wide apertures; the geometric radius still determines
// protection so this adjustment is not compounded by a second protection change. Along the matte's outline the depth
// is split into subject and background estimates, strands are unmixed from the background behind them and the solid
// outline is softened at wide apertures (E4).
//
// E5 (HeifPortraitRenderer.Bokeh.cs): blurred areas get fine grain scaled by ISO; point lights are scattered as hard
// discs instead of the layers' soft ones; scenery in front of the focus outside a person's matte is blurred less at wide
// apertures; on the 2× lens large radii grow more slowly; at night clipped lamps are scattered brighter as cat's-eye
// discs. Rendering parameters and the measurements behind them: docs/measurements/portrait-0.6.7-bokeh.md.
internal sealed partial class HeifPortraitRenderer
{
    public const float ReferenceAperture = 4.5f;
    private const float Scale = 0.0055f;
    private const float DepthOfField = 0.02f;
    // E2 probes on held-out person/refocus scenes support less foreground blur than the symmetric radius law.
    private const float ForegroundMatteScale = 0.5f;
    // About a 50 mm full-frame equivalent, used when the depth item has no intrinsics.
    public const float DefaultFocalRatio = 1.39f;
    // Half-width, in depth samples, of the window that estimates subject and background disparity under the matte.
    private const int SplitRadius = 3;
    private const int DirectRadius = 3;
    // Unmixing applies to strands, whose 3 px disc holds little subject, and fades out on the solid outline.
    private const float StrandShare = 0.25f, ContourShare = 0.5f;
    // On the outline at wide apertures the subject side is softened with this subject-only disc.
    private const int ContourSoftRadius = 2;
    // Photos leaves fine grain in blurred areas (E5, native high-pass std ≈ 0.5–0.7 codes at ISO 50–80, 1.35 at
    // ISO 1250): GrainLevel · (ISO / 64)^GrainIsoExponent codes, fading in over the first GrainRadius pixels of blur.
    private const float GrainLevel = 0.6f, GrainIsoExponent = 0.28f, GrainRadius = 2f;
    // Point lights (E5): the light of a level-1 texel above the mean of the (2·PointWindow + 1)² texels around it is
    // spread as a hard disc of the texel's own radius instead of the gathered layers' soft one, as in Photos' bokeh of
    // lamps and water drops. It counts from PointContrast times the surrounding mean, fully from PointFull times, and
    // from PointLuma (linear) upwards; the window is wide enough that a lamp's glow does not raise the mean much.
    private const float PointContrast = 1.25f, PointFull = 2.0f, PointLuma = 0.25f;
    private const int PointWindow = 16;
    // Clipped lamps and drops stand for more light than code 255 shows: light moved from texels whose brightest
    // channel nears clipping (linear 0.8 to 1) and whose disc is wide (8 to 24 px) is scaled up towards PointGain;
    // other points keep their light.
    private const float PointGain = 1.5f;
    // Night lamps (E5, after Apple's US10992845 highlight recovery): in night shots near-white background pixels behind
    // the focus (brightest channel 0.95–0.99, darkest 0.5–0.65 of full scale, radius 4–12 px) that form a dense clipped
    // core are moved out of the layers and scattered with HighlightGain times their light. When near-white background
    // covers more than HighlightGateLow of the picture the boost fades out, fully at HighlightGateHigh.
    private const float HighlightGain = 6f, HighlightGateLow = 0.04f, HighlightGateHigh = 0.08f;
    private const int BandRows = 32;
    private const float MinimumWeight = 1e-3f;

    // Layer radii in pixels of the photograph; up to DirectRadius they are gathered directly, the rest come from
    // pyramid levels where the radius spans 2.25–3.25 texels.
    private static ReadOnlySpan<float> LayerRadii => [0f, 1f, 2f, 3f, 4.5f, 6.5f, 9f, 13f, 18f, 26f, 36f, 52f, 72f, 104f, 144f, 208f];

    private static readonly float[] LinearLut = BuildLinearLut();
    // Every Half widened to float (exact), indexed by its bits.
    private static readonly float[] HalfToSingle = BuildHalfToSingle();

    private static float[] BuildHalfToSingle()
    {
        var table = new float[65536];
        for (int i = 0; i < table.Length; i++)
        {
            table[i] = (float)BitConverter.UInt16BitsToHalf((ushort)i);
        }

        return table;
    }
    private static readonly float[] EncodeThresholds = BuildEncodeThresholds();

    private readonly int width, height, depthWidth, depthHeight, matteWidth, matteHeight;
    private readonly float[] disparity;
    private readonly float[]? subjectDisparity, backgroundDisparity;
    private readonly byte[]? matte;
    private readonly float focus, nearFocus, slope, offset, maxRadius, matteLow, matteHigh, foregroundStrength, sceneForeground, kneeStart, frontZone;
    private readonly ParallelOptions parallel;
    private readonly float grain;
    private float highlightStrength;
    // Lamp light is moved out of the layers and scattered as cat's-eye discs (mechanical vignetting, as in Photos): the
    // disc is cut by a second disc shifted towards the picture centre by CatEye · radius at the picture corner. Pixels
    // on a person's outline take less of it, so lamps behind a head do not light up its edge.
    private Vector3[]? highlightTexels;
    private const float CatEye = 0.8f, ContourPlain = 20f;
    // Summed-area table of near-white pixels per level-1 texel (0–4 each), for the size test of a highlight.
    private int[]? whiteSums;
    // A lamp is a dense clipped core (most of a (2·CoreWindow + 1)² texel window) that covers little of the wide
    // (2·WhiteWindow + 1)² window; lit leaves are scattered specks, a lit umbrella or wall fills the wide window.
    private const int CoreWindow = 4, WhiteWindow = 40;
    private readonly float highlightExposure;
    // Point-light light scattered as hard discs, premultiplied linear RGB per level-1 texel; null without points.
    private float[]? points;
    private int pointsWidth, pointsHeight;
    private readonly Axis depthX, depthY;
    private readonly Axis? matteX, matteY;

    public HeifPortraitRenderer(int width, int height, float[] disparity, int depthWidth, int depthHeight, byte[]? matte,
        int matteWidth, int matteHeight, float focus, float nearFocus, float aperture, float focalRatio, int maxDegreeOfParallelism,
        CancellationToken cancellationToken, float iso = 64, float exposure = 0)
    {
        // Night shots only (ISO × seconds from 5, fully from 20): daylight whites are not light sources.
        highlightExposure = exposure > 0 ? Math.Clamp(MathF.Log(exposure / 5f) / MathF.Log(4f), 0, 1) : 0;
        grain = GrainLevel * MathF.Pow(Math.Max(iso, 1) / 64f, GrainIsoExponent);
        this.width = width;
        this.height = height;
        this.disparity = disparity;
        this.depthWidth = depthWidth;
        this.depthHeight = depthHeight;
        this.matte = matte;
        this.matteWidth = matteWidth;
        this.matteHeight = matteHeight;
        this.focus = focus;
        this.nearFocus = nearFocus;
        float longer = Math.Max(width, height);
        // φ^1.5 as φ·√φ: square root and product are correctly rounded everywhere, unlike MathF.Pow.
        float strength = Scale * longer * (focalRatio * MathF.Sqrt(focalRatio));
        // Object portraits only (no person matte): with people Photos blurs the scenery in front as usual.
        frontZone = matte is null ? FrontZoneRadius * longer / 4032f / strength : 0;
        slope = strength * ReferenceAperture / aperture;
        offset = strength * DepthOfField;
        maxRadius = Math.Min(LayerRadii[^1], longer / 40f);
        // Only depth items with intrinsics identify the lens; the default ratio is not treated as the 2× crop.
        kneeStart = focalRatio is >= 1.2f and <= 1.6f && focalRatio != DefaultFocalRatio ? longer * CropKneeRatio : float.MaxValue;
        matteLow = 4f * longer / 4032f;
        matteHigh = 8f * longer / 4032f;
        // At narrow apertures the existing protection already leaves only a small foreground blur.
        foregroundStrength = (1 - ForegroundMatteScale) * Math.Clamp((8 - aperture) / (8 - 2.8f), 0, 1);
        sceneForeground = 1 - (1 - ForegroundScale) * Math.Clamp((8 - aperture) / (8 - 2.8f), 0, 1);
        depthX = new Axis(width, depthWidth);
        depthY = new Axis(height, depthHeight);
        matteX = matte is null ? null : new Axis(width, matteWidth);
        matteY = matte is null ? null : new Axis(height, matteHeight);
        if (matte is not null)
        {
            (subjectDisparity, backgroundDisparity) = SplitDisparity(disparity, depthWidth, depthHeight, matte, matteWidth, matteHeight);
        }
        parallel = new ParallelOptions { MaxDegreeOfParallelism = maxDegreeOfParallelism, CancellationToken = cancellationToken };
        directTaps = [.. DirectDiscs.Select(taps => Taps(taps, width))];
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

        if (layers > 0)
        {
            // Point-light discs and the level-1 radius map.
            total += (long)LevelSize(width, 1) * LevelSize(height, 1);
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
                float[] texelRadius = ArrayPool<float>.Shared.Rent(LevelSize(width, 1) * LevelSize(height, 1));
                try
                {
                    highlightStrength = highlightExposure > 0 ? highlightExposure * HighlightGate(source) : 0;
                    levels.Add(BuildFirstLevel(source, texelRadius, boost: highlightStrength > 0));
                    if (highlightStrength > 0)
                    {
                        // At night lamp scatter takes the place of point scatter.
                        ScatterHighlights(texelRadius);
                    }
                    else
                    {
                        ScatterPoints(levels[0], texelRadius);
                    }

                    ReturnBuffers(withPoints: false);
                }
                finally
                {
                    ArrayPool<float>.Shared.Return(texelRadius);
                }

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

            ReturnBuffers(withPoints: true);

        }
    }

    // The pooled highlight buffers of one rendering, and the point-light map once the bands are done.
    private void ReturnBuffers(bool withPoints)
    {
        if (highlightTexels is not null)
        {
            ArrayPool<Vector3>.Shared.Return(highlightTexels);
            highlightTexels = null;
        }

        if (whiteSums is not null)
        {
            ArrayPool<int>.Shared.Return(whiteSums);
            whiteSums = null;
        }

        if (withPoints && points is not null)
        {
            ArrayPool<float>.Shared.Return(points);
            points = null;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private float Radius(float d) => Shape(slope * (d > focus ? Math.Max(0, d - nearFocus) : focus - d) - offset);

    // E2/E5: on the 2× lens (a crop of the main camera, focal ratio about 1.4) Photos' radius grows only about half as
    // fast beyond L/126 (rynek and swiatla f/1.4: 52–63 px where the law gives 62–73; lezak f/4.5: 31.5 against 43).
    private const float CropKneeRatio = 1f / 126, CropKneeSlope = 0.5f;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private float Shape(float radius) =>
        Math.Clamp(radius > kneeStart ? kneeStart + (radius - kneeStart) * CropKneeSlope : radius, 0, maxRadius);

    // E2/E5: outside a person's matte Photos blurs what lies in front of the focus far less than the symmetric law
    // (table, ground, posts: none to half) at wide apertures, while people in front are blurred as before. The scale
    // fades back to 1 by f/8 like the matte's foreground correction (rynek refocus: full blur in front at f/8).
    private const float ForegroundScale = 0.5f;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private float SceneRadius(float d) =>
        Shape((d > focus ? slope * sceneForeground * Math.Max(0, d - nearFocus - frontZone) : slope * (focus - d)) - offset);

    // E2/DoD: in object portraits (no person matte) Photos leaves what lies just in front of the focus range sharp at
    // every aperture while its f/4.5 radius would stay below about FrontZoneRadius px at 4032 px (slupek: no blur up
    // to Δ ≈ 0.3), and blurs as usual beyond it.
    private const float FrontZoneRadius = 5f;

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

    // Matte-weighted mean disparities of the subject (weight m²) and of the background (weight (1 − m)²) within
    // SplitRadius depth samples. The depth map is several times coarser than the matte, so near a subject's outline a
    // depth sample mixes both; these estimates give the matte's fine edge (a hood, single hairs) the subject's depth
    // and the background beside it the background's depth. The background estimate takes over in proportion to the
    // subject weight in the window (fully from one subject sample), and a side without weight keeps the sample.
    private static (float[] Subject, float[] Background) SplitDisparity(float[] disparity, int depthWidth, int depthHeight,
        byte[] matte, int matteWidth, int matteHeight)
    {
        int count = depthWidth * depthHeight;
        var mx = new Axis(depthWidth, matteWidth);
        var my = new Axis(depthHeight, matteHeight);
        float[] sums = ArrayPool<float>.Shared.Rent(count * 4), rows = ArrayPool<float>.Shared.Rent(count * 4);
        try
        {
            for (int y = 0; y < depthHeight; y++)
            {
                int top = my.Low[y] * matteWidth, bottom = Math.Min(my.Low[y] + 1, matteHeight - 1) * matteWidth;
                float fy = my.Fraction[y];
                for (int x = 0; x < depthWidth; x++)
                {
                    int x0 = mx.Low[x], x1 = Math.Min(x0 + 1, matteWidth - 1);
                    float fx = mx.Fraction[x];
                    float upper = matte[top + x0] + (matte[top + x1] - matte[top + x0]) * fx;
                    float lower = matte[bottom + x0] + (matte[bottom + x1] - matte[bottom + x0]) * fx;
                    float m = (upper + (lower - upper) * fy) / 255f, d = disparity[y * depthWidth + x];
                    float subject = m * m, background = (1 - m) * (1 - m);
                    int i = (y * depthWidth + x) * 4;
                    sums[i] = subject;
                    sums[i + 1] = subject * d;
                    sums[i + 2] = background;
                    sums[i + 3] = background * d;
                }
            }

            // Separable box sums, clipped at the map's edges.
            for (int y = 0; y < depthHeight; y++)
            {
                for (int x = 0; x < depthWidth; x++)
                {
                    var total = Vector4.Zero;
                    for (int sx = Math.Max(0, x - SplitRadius); sx <= Math.Min(depthWidth - 1, x + SplitRadius); sx++)
                    {
                        int k = (y * depthWidth + sx) * 4;
                        total += new Vector4(sums[k], sums[k + 1], sums[k + 2], sums[k + 3]);
                    }

                    int i = (y * depthWidth + x) * 4;
                    (rows[i], rows[i + 1], rows[i + 2], rows[i + 3]) = (total.X, total.Y, total.Z, total.W);
                }
            }

            float[] subjectDisparity = new float[count], backgroundDisparity = new float[count];
            for (int y = 0; y < depthHeight; y++)
            {
                for (int x = 0; x < depthWidth; x++)
                {
                    var total = Vector4.Zero;
                    for (int sy = Math.Max(0, y - SplitRadius); sy <= Math.Min(depthHeight - 1, y + SplitRadius); sy++)
                    {
                        int k = (sy * depthWidth + x) * 4;
                        total += new Vector4(rows[k], rows[k + 1], rows[k + 2], rows[k + 3]);
                    }

                    int i = y * depthWidth + x;
                    float d = disparity[i];
                    // Inside the subject its own samples stay (arms or hair may lie at other depths); the estimate
                    // takes over towards the outline, fully where at most half of the window is subject.
                    int window = (Math.Min(depthWidth - 1, x + SplitRadius) - Math.Max(0, x - SplitRadius) + 1)
                        * (Math.Min(depthHeight - 1, y + SplitRadius) - Math.Max(0, y - SplitRadius) + 1);
                    float edge = Math.Clamp(2 * (1 - total.X / window), 0, 1);
                    subjectDisparity[i] = total.X > MinimumWeight ? d + (total.Y / total.X - d) * edge : d;
                    // Away from the subject the background keeps its own samples, so thin objects stay as they are.
                    float near = Math.Min(1, total.X);
                    backgroundDisparity[i] = total.Z > MinimumWeight ? d + (total.W / total.Z - d) * near : d;
                }
            }

            return (subjectDisparity, backgroundDisparity);
        }
        finally
        {
            ArrayPool<float>.Shared.Return(sums);
            ArrayPool<float>.Shared.Return(rows);
        }
    }

    // One row of a depth-resolution map sampled bilinearly at the photograph's pixel centres.
    private void SampleDepth(float[] map, int y, Span<float> output)
    {
        int dy = depthY.Low[y];
        float fy = depthY.Fraction[y];
        int nextRow = Math.Min(dy + 1, depthHeight - 1);
        ReadOnlySpan<float> top = map.AsSpan(dy * depthWidth, depthWidth), bottom = map.AsSpan(nextRow * depthWidth, depthWidth);
        for (int x = 0; x < width; x++)
        {
            int dx = depthX.Low[x], next = Math.Min(dx + 1, depthWidth - 1);
            float fx = depthX.Fraction[x];
            float upper = top[dx] + (top[next] - top[dx]) * fx, lower = bottom[dx] + (bottom[next] - bottom[dx]) * fx;
            output[x] = upper + (lower - upper) * fy;
        }
    }

    // Radius and protection for one row of the photograph. Under the matte the radius follows the subject's
    // disparity and elsewhere the background's, mixed by the matte value; the matte protects the subject only while
    // the subject's own radius stays small.
    private void RowParameters(int y, Span<float> radius, Span<float> protection)
    {
        if (matte is null || matteX is null || matteY is null || subjectDisparity is null || backgroundDisparity is null)
        {
            SampleDepth(disparity, y, radius);
            for (int x = 0; x < width; x++)
            {
                radius[x] = SceneRadius(radius[x]);
            }

            protection.Clear();
            return;
        }

        // Disparities first: the background's in radius, the subject's in protection.
        SampleDepth(backgroundDisparity, y, radius);
        SampleDepth(subjectDisparity, y, protection);
        int my = matteY.Low[y];
        float mfy = matteY.Fraction[y];
        int matteNext = Math.Min(my + 1, matteHeight - 1);
        ReadOnlySpan<byte> mTop = matte.AsSpan(my * matteWidth, matteWidth), mBottom = matte.AsSpan(matteNext * matteWidth, matteWidth);
        for (int x = 0; x < width; x++)
        {
            int mx = matteX.Low[x], next = Math.Min(mx + 1, matteWidth - 1);
            float fx = matteX.Fraction[x];
            float upper = mTop[mx] + (mTop[next] - mTop[mx]) * fx, lower = mBottom[mx] + (mBottom[next] - mBottom[mx]) * fx;
            float matteValue = (upper + (lower - upper) * mfy) / 255f;
            float subjectValue = protection[x], background = SceneRadius(radius[x]), subject = Radius(subjectValue);
            // The geometric radius sets protection; the foreground correction only changes the blur that remains.
            protection[x] = Protection(matteValue, subject);
            if (subjectValue > focus)
            {
                subject *= 1 - foregroundStrength;
            }

            radius[x] = background + (subject - background) * matteValue;
        }
    }

    // A row of premultiplied linear colour with the subject weight (protection) in W.
    private static void SubjectRow(ReadOnlySpan<Rgba32> row, ReadOnlySpan<float> protection, Span<Vector4> output)
    {
        float[] lut = LinearLut;
        for (int x = 0; x < row.Length; x++)
        {
            Rgba32 p = row[x];
            float w = protection[x];
            output[x] = new Vector4(lut[p.R] * w, lut[p.G] * w, lut[p.B] * w, w);
        }
    }

    // A partly protected pixel (a hair over the background, the soft edge of the matte) mixes the subject with the
    // sharp background behind it. Keeping it as protection would keep that background sharp, so for strands its
    // background share is replaced by the subject's colour nearby: I + (1 − p)(F − B), with F and B the subject- and
    // background-weighted means in the largest direct disc. Where the subject is blurred too both means cover the same
    // pixels and cancel. On a solid outline the same replacement would draw a crisp line that Photos does not show, so
    // there the subject side is instead softened with a small subject-only disc once the radius exceeds matteLow.
    private Vector3 Unmix(Vector3 sharp, float protection, float radius, int x, int y, int top, Vector4[] weighted, Vector4[] subjectWeighted, Layer[] layers)
    {
        Vector4 background = LayerValue(DirectRadius, x, y, top, weighted, layers);
        Vector4 subject = LayerValue(DirectRadius, x, y, top, subjectWeighted, layers);
        if (background.W <= MinimumWeight || subject.W <= MinimumWeight)
        {
            return sharp;
        }

        // subject.W is the mean protection in the disc: about one half on a solid outline, little around a strand.
        float strand = Math.Clamp((ContourShare - subject.W) / (ContourShare - StrandShare), 0, 1);
        Vector3 unmixed = sharp + (Color(subject) - Color(background)) * ((1 - protection) * strand);
        Vector4 soft = LayerValue(ContourSoftRadius, x, y, top, subjectWeighted, layers);
        float softness = (1 - strand) * Math.Clamp((radius - matteLow) / (matteHigh - matteLow), 0, 1);
        return soft.W > MinimumWeight ? Lerp(unmixed, Color(soft), softness) : unmixed;
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
            Vector4 top = Lerp(Load(x0, y0), Load(x1, y0), fx), bottom = Lerp(Load(x0, y1), Load(x1, y1), fx);
            return Lerp(top, bottom, fy);
        }

        private Vector4 Load(int x, int y)
        {
            int i = (y * Width + x) * 4;
            float[] single = HalfToSingle;
            return new Vector4(single[BitConverter.HalfToUInt16Bits(Texels[i])], single[BitConverter.HalfToUInt16Bits(Texels[i + 1])],
                single[BitConverter.HalfToUInt16Bits(Texels[i + 2])], single[BitConverter.HalfToUInt16Bits(Texels[i + 3])]);
        }
    }

    // Level 1 averages 2×2 photograph pixels of premultiplied linear colour and background weight.
    private Level BuildFirstLevel(ReadOnlyMemory<Rgba32> source, float[] texelRadius, bool boost)
    {
        var level = new Level(LevelSize(width, 1), LevelSize(height, 1));
        if (boost)
        {
            highlightTexels = ArrayPool<Vector3>.Shared.Rent(level.Width * level.Height);
        }

        HeifParallel.ForRows(level.Height, level.Width, parallel, (first, end) =>
        {
            float[] radius = ArrayPool<float>.Shared.Rent(width), protection = ArrayPool<float>.Shared.Rent(width);
            float[] upperRadius = ArrayPool<float>.Shared.Rent(width);
            Vector3[] upperLight = ArrayPool<Vector3>.Shared.Rent(width), lowerLight = ArrayPool<Vector3>.Shared.Rent(width);
            Vector4[] upper = ArrayPool<Vector4>.Shared.Rent(width), lower = ArrayPool<Vector4>.Shared.Rent(width);
            try
            {
                for (int ly = first; ly < end; ly++)
                {
                    parallel.CancellationToken.ThrowIfCancellationRequested();
                    int y0 = ly * 2, y1 = Math.Min(y0 + 1, height - 1);
                    RowParameters(y0, upperRadius, protection);
                    HighlightRow(y0, source.Span.Slice(y0 * width, width), protection, upperRadius, upper, upperLight, boost);
                    RowParameters(y1, radius, protection);
                    HighlightRow(y1, source.Span.Slice(y1 * width, width), protection, radius, lower, lowerLight, boost);
                    float rows = y1 == y0 ? 1 : 2;
                    Span<Vector4> output = level.Texels.AsSpan(ly * level.Width, level.Width);
                    for (int lx = 0; lx < level.Width; lx++)
                    {
                        int x0 = lx * 2, x1 = x0 + 1;
                        Vector4 sum = upper[x0] + (y1 == y0 ? Vector4.Zero : lower[x0]);
                        Vector3 light = upperLight[x0] + (y1 == y0 ? Vector3.Zero : lowerLight[x0]);
                        float radii = upperRadius[x0] + (y1 == y0 ? 0 : radius[x0]);
                        float count = rows;
                        if (x1 < width)
                        {
                            sum += upper[x1] + (y1 == y0 ? Vector4.Zero : lower[x1]);
                            light += upperLight[x1] + (y1 == y0 ? Vector3.Zero : lowerLight[x1]);
                            radii += upperRadius[x1] + (y1 == y0 ? 0 : radius[x1]);
                            count += rows;
                        }

                        output[lx] = sum / count;
                        texelRadius[ly * level.Width + lx] = radii / count;
                        if (boost)
                        {
                            highlightTexels![ly * level.Width + lx] = light * (HighlightGain / count);
                        }
                    }
                }
            }
            finally
            {
                ArrayPool<float>.Shared.Return(radius);
                ArrayPool<float>.Shared.Return(protection);
                ArrayPool<float>.Shared.Return(upperRadius);
                ArrayPool<Vector3>.Shared.Return(upperLight);
                ArrayPool<Vector3>.Shared.Return(lowerLight);
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
        // Away from the edges every tap lies inside, in the same order: index offsets and the same weight total.
        int extent = (int)MathF.Ceiling(radius + 0.5f);
        (int[] offsets, float[] weights, float inside) = Taps(taps, source.Width);
        HeifParallel.ForRows(source.Height, source.Width, parallel, (first, end) =>
        {
            Vector4[] texels = source.Texels;
            Vector4[] row = ArrayPool<Vector4>.Shared.Rent(source.Width);
            try
            {
                for (int y = first; y < end; y++)
                {
                    parallel.CancellationToken.ThrowIfCancellationRequested();
                    // Columns insideFirst … insideEnd − 1 of rows away from the edges have every tap inside.
                    int insideFirst = extent, insideEnd = source.Width - extent;
                    if (y < extent || y + extent >= source.Height || insideFirst >= insideEnd)
                    {
                        insideFirst = insideEnd = 0;
                    }

                    Convolve(texels, y * source.Width + insideFirst, row.AsSpan(insideFirst, insideEnd - insideFirst), offsets, weights, inside);
                    for (int x = 0; x < source.Width; x++)
                    {
                        Vector4 sum;
                        if (x >= insideFirst && x < insideEnd)
                        {
                            sum = row[x];
                        }
                        else
                        {
                            sum = Vector4.Zero;
                            float total = 0;
                            foreach ((int dx, int dy, float weight) in taps)
                            {
                                int sx = x + dx, sy = y + dy;
                                if ((uint)sx < (uint)source.Width && (uint)sy < (uint)source.Height)
                                {
                                    sum += texels[sy * source.Width + sx] * weight;
                                    total += weight;
                                }
                            }

                            sum /= total;
                        }

                        int i = (y * source.Width + x) * 4;
                        layer.Texels[i] = (Half)sum.X;
                        layer.Texels[i + 1] = (Half)sum.Y;
                        layer.Texels[i + 2] = (Half)sum.Z;
                        layer.Texels[i + 3] = (Half)sum.W;
                    }
                }
            }
            finally
            {
                ArrayPool<Vector4>.Shared.Return(row);
            }
        });
        return layer;
    }

    // Normalized disc sums of consecutive texels whose taps all lie inside the level, starting at texel index centre.
    internal static void Convolve(ReadOnlySpan<Vector4> texels, int centre, Span<Vector4> output, int[] offsets, float[] weights, float total)
    {
        int done = Vector256.IsHardwareAccelerated ? ConvolveVector(texels, centre, output, offsets, weights, total) : 0;
        ConvolveScalar(texels, centre + done, output[done..], offsets, weights, total);
    }

    internal static void ConvolveScalar(ReadOnlySpan<Vector4> texels, int centre, Span<Vector4> output, int[] offsets, float[] weights, float total)
    {
        for (int j = 0; j < output.Length; j++)
        {
            Vector4 sum = Vector4.Zero;
            for (int t = 0; t < offsets.Length; t++)
            {
                sum += texels[centre + j + offsets[t]] * weights[t];
            }

            output[j] = sum / total;
        }
    }

    // Index offsets of disc taps in rows of the given stride, their weights and the weights' sum in tap order.
    private static (int[] Offsets, float[] Weights, float Total) Taps((int Dx, int Dy, float Weight)[] taps, int stride)
    {
        var offsets = new int[taps.Length];
        var weights = new float[taps.Length];
        float total = 0;
        for (int t = 0; t < taps.Length; t++)
        {
            offsets[t] = taps[t].Dy * stride + taps[t].Dx;
            weights[t] = taps[t].Weight;
            total += taps[t].Weight;
        }

        return (offsets, weights, total);
    }

    private void RenderBand(ReadOnlyMemory<Rgba32> sourceMemory, Memory<Rgba32> destinationMemory, Layer[] layers, int first, int end)
    {
        parallel.CancellationToken.ThrowIfCancellationRequested();
        // Rows first − DirectRadius … end + DirectRadius of weighted colour for the direct layers.
        int top = Math.Max(0, first - DirectRadius), bottom = Math.Min(height, end + DirectRadius);
        int rows = bottom - top;
        Vector4[] weighted = ArrayPool<Vector4>.Shared.Rent(rows * width);
        Vector4[]? subjectWeighted = matte is null ? null : ArrayPool<Vector4>.Shared.Rent(rows * width);
        float[] radii = ArrayPool<float>.Shared.Rent(rows * width), protections = ArrayPool<float>.Shared.Rent(rows * width);
        Vector3[]? pointRow = points is null ? null : ArrayPool<Vector3>.Shared.Rent(width);
        try
        {
            ReadOnlySpan<Rgba32> source = sourceMemory.Span;
            for (int y = top; y < bottom; y++)
            {
                int offset = (y - top) * width;
                Span<float> radius = radii.AsSpan(offset, width), protection = protections.AsSpan(offset, width);
                RowParameters(y, radius, protection);
                WeightedRow(source.Slice(y * width, width), protection, weighted.AsSpan(offset, width));
                if (subjectWeighted is not null)
                {
                    SubjectRow(source.Slice(y * width, width), protection, subjectWeighted.AsSpan(offset, width));
                }
            }

            Span<Rgba32> destination = destinationMemory.Span;
            float[] lut = LinearLut;
            for (int y = first; y < end; y++)
            {
                int offset = (y - top) * width;
                ReadOnlySpan<Rgba32> sourceRow = source.Slice(y * width, width);
                Span<Rgba32> output = destination.Slice(y * width, width);
                if (pointRow is not null)
                {
                    PointRow(y, pointRow.AsSpan(0, width));
                }

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
                    Vector3 blurred = Blurred(layers, sharp, k, t, x, y, top, weighted);

                    if (points is not null && radius > LayerRadii[DirectRadius])
                    {
                        // Points were taken out of the stored layers only, so pixels gathering directly keep them.
                        float share = Math.Min(1, (radius - LayerRadii[DirectRadius]) / (LayerRadii[DirectRadius + 1] - LayerRadii[DirectRadius]));
                        // Lamps at night spare a person's outline; daytime points keep their light everywhere.
                        float outline = highlightStrength > 0 ? 1 - Math.Min(1, protection * ContourPlain) : 1;
                        blurred += pointRow![x] * (share * outline);
                    }

                    if (subjectWeighted is not null && protection > 0)
                    {
                        sharp = Unmix(sharp, protection, radius, x, y, top, weighted, subjectWeighted, layers);
                    }

                    Vector3 linear = sharp * protection + blurred * (1 - protection);
                    float amount = grain * (1 - protection) * Math.Min(1, radius / GrainRadius);
                    if (amount > 0)
                    {
                        float n = amount * Noise(x, y);
                        linear = new Vector3(Grain(linear.X, n), Grain(linear.Y, n), Grain(linear.Z, n));
                    }

                    output[x] = new Rgba32(Encode(linear.X), Encode(linear.Y), Encode(linear.Z), original.A);
                }
            }
        }
        finally
        {
            ArrayPool<Vector4>.Shared.Return(weighted);
            if (subjectWeighted is not null)
            {
                ArrayPool<Vector4>.Shared.Return(subjectWeighted);
            }
            ArrayPool<float>.Shared.Return(radii);
            ArrayPool<float>.Shared.Return(protections);
            if (pointRow is not null)
            {
                ArrayPool<Vector3>.Shared.Return(pointRow);
            }
        }
    }

    // The blurred colour between layers k − 1 and k of one layer set.
    private Vector3 Blurred(Layer[] set, Vector3 sharp, int k, float t, int x, int y, int top, Vector4[] weighted)
    {
        Vector4 a = k == 1 ? new Vector4(sharp, 1) : LayerValue(k - 1, x, y, top, weighted, set);
        Vector4 b = LayerValue(k, x, y, top, weighted, set);
        if (a.W > MinimumWeight && b.W > MinimumWeight)
        {
            return Lerp(Color(a), Color(b), t);
        }

        return a.W > MinimumWeight || b.W > MinimumWeight ? Color(a.W > MinimumWeight ? a : b) : sharp;
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

        if (x >= DirectRadius && x + DirectRadius < width && y >= DirectRadius && y + DirectRadius < height)
        {
            // Every tap lies inside the band's rows and the photograph, in the same order as below.
            (int[] offsets, float[] weights, float inside) = directTaps[k];
            int centre = (y - top) * width + x;
            Vector4 total4 = Vector4.Zero;
            for (int t = 0; t < offsets.Length; t++)
            {
                total4 += weighted[centre + offsets[t]] * weights[t];
            }

            return total4 / inside;
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
    private readonly (int[] Offsets, float[] Weights, float Total)[] directTaps;

    // The index of the first threshold above the value: thresholds are the linear values halfway between codes. The
    // search starts from the code of the value's 1/EncodeBuckets bucket (the count of thresholds below the bucket's
    // start), which is at most a few codes short; values below 0 and NaN give 0.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    // Vector3/Vector4.Lerp fuse to a multiply-add where the CPU has FMA, so their result depends on the machine (x64
    // with or without FMA, ARM64, Native AOT); the separate products and sum are rounded the same everywhere.
    private static Vector3 Lerp(Vector3 a, Vector3 b, float t) => (a * (1 - t)) + (b * t);

    private static Vector4 Lerp(Vector4 a, Vector4 b, float t) => (a * (1 - t)) + (b * t);

    private static byte Encode(float linear)
    {
        float[] thresholds = EncodeThresholds;
        int code = linear >= 0 ? EncodeStarts[(int)Math.Min(linear * EncodeBuckets, EncodeBuckets)] : 0;
        while (code < thresholds.Length && thresholds[code] <= linear)
        {
            code++;
        }

        return (byte)code;
    }

    private const int EncodeBuckets = 4096;
    private static readonly byte[] EncodeStarts = BuildEncodeStarts();

    private static byte[] BuildEncodeStarts()
    {
        // Bucket b holds values from b / EncodeBuckets (as computed by Encode), so its start counts thresholds below that.
        var starts = new byte[EncodeBuckets + 1];
        int code = 0;
        for (int b = 0; b <= EncodeBuckets; b++)
        {
            while (code < EncodeThresholds.Length && EncodeThresholds[code] < (float)b / EncodeBuckets)
            {
                code++;
            }

            starts[b] = (byte)code;
        }

        return starts;
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
