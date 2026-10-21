using System.Drawing;
using System.Numerics;
using PictureSharp.Formats.Heif.Codecs;

namespace PictureSharp.Formats.Heif.Tests;

// E6: vector paths equal the scalar ones bit for bit, renders with point lights and night lamps do not depend on the
// thread count, and cancellation leaves no state behind.
public sealed class PortraitEngineeringTests
{
    private const int Width = 1280, Height = 960;
    private static readonly Point Focus = new(Width / 4, Height / 2);

    [Test]
    public async Task AddDiscRow_VectorEqualsScalar()
    {
        var random = new Random(6);
        var failures = new List<string>();
        for (int trial = 0; trial < 2000; trial++)
        {
            int width = random.Next(1, 160);
            float radius = trial % 50 == 0 ? random.Next(0, 60) : (float)(random.NextDouble() * 60);
            int extent = (int)MathF.Ceiling(radius + 0.5f), dy = random.Next(-extent, extent + 1), cx = random.Next(width);
            var light = new Vector3((float)random.NextDouble(), (float)random.NextDouble() * 4, trial % 7 == 0 ? 0 : (float)random.NextDouble());
            float total = (float)(random.NextDouble() * 4000 + 0.5);
            // Columns inside the disc and the row, as the scatter passes them.
            int half = -1;
            while (half < extent && radius - MathF.Sqrt((half + 1) * (half + 1) + dy * dy) + 0.5f > 0)
            {
                half++;
            }

            int left = Math.Max(-half, -cx), right = Math.Min(half, width - 1 - cx);
            float[] start = [.. Enumerable.Range(0, width * 3).Select(_ => random.Next(4) == 0 ? 0f : (float)random.NextDouble())];
            float[] scalar = [.. start], vector = [.. start];
            HeifPortraitRenderer.AddDiscRowScalar(scalar.AsSpan(0, width), scalar.AsSpan(width, width), scalar.AsSpan(2 * width, width),
                cx, light, radius, total, dy, left, right);
            int tail = HeifPortraitRenderer.AddDiscRowVector(vector.AsSpan(0, width), vector.AsSpan(width, width), vector.AsSpan(2 * width, width),
                cx, light, radius, total, dy, left, right);
            HeifPortraitRenderer.AddDiscRowScalar(vector.AsSpan(0, width), vector.AsSpan(width, width), vector.AsSpan(2 * width, width),
                cx, light, radius, total, dy, tail, right);
            for (int i = 0; i < scalar.Length; i++)
            {
                if (BitConverter.SingleToInt32Bits(scalar[i]) != BitConverter.SingleToInt32Bits(vector[i]))
                {
                    failures.Add($"trial {trial}: index {i} expected {scalar[i]:R}, got {vector[i]:R}");
                    break;
                }
            }
        }

        await Assert.That(failures).IsEmpty();
    }

    [Test]
    public async Task Convolve_VectorEqualsScalar()
    {
        var random = new Random(7);
        var failures = new List<string>();
        for (int trial = 0; trial < 300; trial++)
        {
            int width = random.Next(16, 96), height = random.Next(16, 40);
            float radius = (float)(random.NextDouble() * 3.25);
            int extent = (int)MathF.Ceiling(radius + 0.5f);
            var texels = new Vector4[width * height];
            for (int i = 0; i < texels.Length; i++)
            {
                texels[i] = new Vector4((float)random.NextDouble(), (float)random.NextDouble() * 3, random.Next(5) == 0 ? 0 : (float)random.NextDouble(),
                    (float)random.NextDouble());
            }

            var offsets = new List<int>();
            var weights = new List<float>();
            float total = 0;
            for (int dy = -extent; dy <= extent; dy++)
            {
                for (int dx = -extent; dx <= extent; dx++)
                {
                    float weight = Math.Clamp(radius - MathF.Sqrt(dx * dx + dy * dy) + 0.5f, 0, 1);
                    if (weight > 0)
                    {
                        offsets.Add(dy * width + dx);
                        weights.Add(weight);
                        total += weight;
                    }
                }
            }

            int y = random.Next(extent, height - extent), first = extent, count = random.Next(0, width - 2 * extent + 1);
            var scalar = new Vector4[count];
            var vector = new Vector4[count];
            HeifPortraitRenderer.ConvolveScalar(texels, y * width + first, scalar, [.. offsets], [.. weights], total);
            int done = HeifPortraitRenderer.ConvolveVector(texels, y * width + first, vector, [.. offsets], [.. weights], total);
            HeifPortraitRenderer.ConvolveScalar(texels, y * width + first + done, vector.AsSpan(done), [.. offsets], [.. weights], total);
            for (int i = 0; i < count; i++)
            {
                if (!Bits(scalar[i]).SequenceEqual(Bits(vector[i])))
                {
                    failures.Add($"trial {trial}: texel {i} expected {scalar[i]}, got {vector[i]}");
                    break;
                }
            }
        }

        await Assert.That(failures).IsEmpty();
    }

    private static int[] Bits(Vector4 v) =>
        [BitConverter.SingleToInt32Bits(v.X), BitConverter.SingleToInt32Bits(v.Y), BitConverter.SingleToInt32Bits(v.Z), BitConverter.SingleToInt32Bits(v.W)];

    [Test]
    [Property("Category", "Slow")]
    public async Task Render_WithPointLightsAndNightLamps_DoesNotDependOnTheThreadCount()
    {
        (HeifDepthImage depth, Image matte) = Load();
        foreach (bool night in new[] { false, true })
        {
            using Image photo = Specks(night);
            using Image single = Render(photo, depth, matte, threads: 1);
            foreach (int threads in new[] { 2, 3, -1 })
            {
                using Image parallel = Render(photo, depth, matte, threads);
                await Assert.That(single.GetPixelSpan().SequenceEqual(parallel.GetPixelSpan())).IsTrue()
                    .Because($"{(night ? "night" : "day")}, {threads} threads");
            }
        }
    }

    [Test]
    public async Task Render_AfterCancellation_GivesTheSameResult()
    {
        (HeifDepthImage depth, Image matte) = Load();
        using Image photo = Specks(night: false);
        using Image expected = Render(photo, depth, matte, threads: 2);
        for (int delay = 1; delay <= 64; delay *= 4)
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(delay));
            try
            {
                TestRender.Portrait(photo, depth, matte, Options, 2, cancellation.Token).Dispose();
            }
            catch (OperationCanceledException)
            {
            }
        }

        using Image again = Render(photo, depth, matte, threads: 2);
        await Assert.That(again.GetPixelSpan().SequenceEqual(expected.GetPixelSpan())).IsTrue();
    }

    // These APIs round differently on different CPUs (Lerp and the *Estimate methods fuse to a multiply-add where FMA
    // exists, MathF.Pow is not correctly rounded), so a render would differ between x64, Native AOT and ARM64. The
    // grain level is a single per-render value from the ISO, measured identical on x64 and ARM64.
    [Test]
    public async Task Renderer_AvoidsMachineDependentRounding()
    {
        string folder = Path.Combine(HevcLocalTests.FindRoot(), "src", "PictureSharp.Heif", "Codecs");
        string[] forbidden = ["Vector2.Lerp(", "Vector3.Lerp(", "Vector4.Lerp(", "Estimate(", "FusedMultiplyAdd(", "MultiplyAdd("];
        var found = new List<string>();
        foreach (string file in Directory.GetFiles(folder, "HeifPortraitRenderer*.cs"))
        {
            string[] lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                found.AddRange(forbidden.Where(lines[i].Contains).Select(api => $"{Path.GetFileName(file)}:{i + 1} {api}"));
                if (lines[i].Contains("MathF.Pow(") && !lines[i].Contains("grain ="))
                {
                    found.Add($"{Path.GetFileName(file)}:{i + 1} MathF.Pow(");
                }
            }
        }

        await Assert.That(found).IsEmpty();
    }

    // A dark textured background with bright specks of several sizes; at night with near-white clipped lamps and a
    // long exposure at high ISO.
    private static Image Specks(bool night)
    {
        var image = new Image(Width, Height);
        var random = new Random(night ? 2 : 1);
        for (int y = 0; y < Height; y++)
        {
            Span<Rgba32> row = image.GetRowSpan(y);
            for (int x = 0; x < Width; x++)
            {
                byte v = (byte)(20 + ((x * 7 + y * 13) % 23));
                row[x] = new Rgba32(v, (byte)(v + 5), (byte)(v + 10));
            }
        }

        // Night lamps must be large enough for a dense clipped core (several level-1 texels).
        for (int i = 0; i < (night ? 60 : 400); i++)
        {
            int size = night ? random.Next(12, 25) : random.Next(1, 4);
            int cx = random.Next(Width / 2, Width - size), cy = random.Next(Height - size);
            byte value = night ? (byte)random.Next(250, 256) : (byte)random.Next(150, 256);
            for (int y = cy; y < cy + size; y++)
            {
                for (int x = cx; x < cx + size; x++)
                {
                    image.GetRowSpan(y)[x] = new Rgba32(value, value, (byte)(night ? 200 : value));
                }
            }
        }

        if (night)
        {
            image.Metadata.ExifProfile.SetValue(ExifTag.IsoSpeed, (ushort)1600);
            image.Metadata.ExifProfile.SetValue(ExifTag.ExposureTime, new ExifRational(1, 15));
        }

        return image;
    }

    private static (HeifDepthImage Depth, Image Matte) Load()
    {
        HeifDocument doc = HeifDocument.Open(HeifPortraitTests.Scene.Build(Width, Height).Bytes);
        HeifDepthImage depth = doc.LoadDepth(doc.PrimaryImage.FindDepthImage()!, HeifPortraitAuditTests.Decode);
        Image matte = doc.LoadImage(doc.PrimaryImage.FindAuxiliaryImage(HeifAppleAuxiliaryTypes.PortraitEffectsMatte)!, HeifPortraitAuditTests.Decode);
        return (depth, matte);
    }

    private static readonly HeifPortraitOptions Options = new() { Aperture = 1.4f, FocusPoint = Focus };

    private static Image Render(Image photo, HeifDepthImage depth, Image matte, int threads) =>
        TestRender.Portrait(photo, depth, matte, Options, threads);
}
