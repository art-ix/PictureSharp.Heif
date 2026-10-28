using System.Diagnostics;
using PictureSharp.Formats.Heif.Hevc;

namespace PictureSharp.Formats.Heif.Tests;

public sealed class HevcEncoderTests
{
    // Synthetic content with gradients, edges, flat areas, checkerboards and noise, at the coded size.
    internal static ushort[][] Picture(int width, int height, bool monochrome, int seed)
    {
        int codedWidth = (width + 7) & ~7;
        int codedHeight = (height + 7) & ~7;
        var random = new Random(seed);
        ushort[] Plane(int w, int h, int cw, int ch, int phase)
        {
            var plane = new ushort[cw * ch];
            for (int y = 0; y < ch; y++)
            {
                for (int x = 0; x < cw; x++)
                {
                    int sx = Math.Min(x, w - 1);
                    int sy = Math.Min(y, h - 1);
                    int region = ((sx * 4 / w) + (sy * 3 / h * 4) + phase) % 6;
                    int value = region switch
                    {
                        0 => sx * 255 / Math.Max(1, w - 1),
                        1 => (sx + (2 * sy)) % 23 < 11 ? 230 : 40,
                        2 => 150,
                        3 => ((sx >> 2) ^ (sy >> 2)) % 2 == 0 ? 255 : 0,
                        4 => random.Next(256),
                        _ => ((sy * 255 / Math.Max(1, h - 1)) + random.Next(16)) % 256,
                    };
                    plane[(y * cw) + x] = (ushort)value;
                }
            }

            return plane;
        }

        return monochrome
            ? [Plane(width, height, codedWidth, codedHeight, 0)]
            : [Plane(width, height, codedWidth, codedHeight, 0), Plane(width / 2, height / 2, codedWidth / 2, codedHeight / 2, 2),
                Plane(width / 2, height / 2, codedWidth / 2, codedHeight / 2, 4)];
    }

    [Test]
    [Arguments(64, 48, 30, false)]
    [Arguments(96, 64, 4, false)]
    [Arguments(100, 58, 22, false)]
    [Arguments(8, 8, 51, false)]
    [Arguments(200, 136, 37, false)]
    [Arguments(72, 40, 27, true)]
    [Arguments(200, 136, 30, false, 1.5)]
    [Arguments(96, 64, 8, false, 3.0)]
    [Arguments(160, 96, 45, true, 1.0)]
    [Arguments(96, 64, 22, false, 0, false)]
    [Arguments(72, 40, 27, true, 0, false)]
    public async Task Encode_DecoderReproducesTheReconstruction(int width, int height, int qp, bool monochrome, double aq = 0, bool rdoq = true)
    {
        var settings = new HevcEncoderSettings
        {
            Width = width,
            Height = height,
            Qp = qp,
            ChromaFormatIdc = monochrome ? 0 : 1,
            Rdoq = rdoq,
            DeblockingDisabled = true,
            SaoEnabled = false,
            AqStrength = aq,
            AqReferenceLogVariance = Math.Log(1500),
        };
        ushort[][] planes = Picture(width, height, monochrome, width + height + qp);
        (List<byte[]> nals, ushort[][] reconstruction) = HevcPictureEncoder.EncodeWithReconstruction(settings, planes, CancellationToken.None);
        HevcFrame frame = HevcDecoder.Decode([.. nals.Select(n => (ReadOnlyMemory<byte>)n)], null, CancellationToken.None);
        await Assert.That((frame.CroppedWidth, frame.CroppedHeight)).IsEqualTo((width, height));
        for (int c = 0; c < frame.Planes.Length; c++)
        {
            await Assert.That(frame.Planes[c]).IsEquivalentTo(reconstruction[c], CollectionOrdering.Matching).Because($"plane {c}");
        }
    }

    // The butterfly transforms must equal the plain matrix products (H.265 8.6.4.2) for any input.
    [Test]
    [Arguments(2)]
    [Arguments(3)]
    [Arguments(4)]
    [Arguments(5)]
    public async Task Transforms_EqualMatrixProducts(int log2Size)
    {
        int size = 1 << log2Size;
        int step = 32 >> log2Size;
        var random = new Random(log2Size);
        int[] input = new int[size * size];
        int[] actual = new int[size * size];
        int[] expected = new int[size * size];
        int[] temporary = new int[size * size];
        for (int trial = 0; trial < 50; trial++)
        {
            bool sparse = trial % 2 == 1;
            for (int i = 0; i < input.Length; i++)
            {
                input[i] = sparse && random.Next(4) != 0 ? 0 : random.Next(-255, 256);
            }

            HevcForwardTransform.Forward(input, actual, temporary, log2Size, false, 8);
            for (int k2 = 0; k2 < size; k2++)
            {
                for (int k1 = 0; k1 < size; k1++)
                {
                    long sum = 0;
                    for (int y = 0; y < size; y++)
                    {
                        long row = 0;
                        for (int x = 0; x < size; x++)
                        {
                            row += HevcTransform.DctCoefficient(k1 * step, x) * input[(y * size) + x];
                        }

                        int shift1 = log2Size - 1;
                        sum += HevcTransform.DctCoefficient(k2 * step, y) * ((row + (1L << (shift1 - 1))) >> shift1);
                    }

                    expected[(k2 * size) + k1] = (int)((sum + (1L << (log2Size + 5))) >> (log2Size + 6));
                }
            }

            await Assert.That(actual).IsEquivalentTo(expected, CollectionOrdering.Matching).Because($"forward {size}x{size} trial {trial}");

            int[] coefficients = [.. input.Select(v => v * 64)];
            HevcTransform.Inverse(coefficients, actual, temporary, log2Size, false, 8);
            await Assert.That(actual).IsEquivalentTo(InverseByMatrix(coefficients, log2Size), CollectionOrdering.Matching)
                .Because($"inverse {size}x{size} trial {trial}");
        }
    }

    private static int[] InverseByMatrix(int[] coefficients, int log2Size)
    {
        int size = 1 << log2Size;
        int step = 32 >> log2Size;
        int[] middle = new int[size * size];
        int[] output = new int[size * size];
        for (int x = 0; x < size; x++)
        {
            for (int y = 0; y < size; y++)
            {
                long sum = 0;
                for (int k = 0; k < size; k++)
                {
                    sum += HevcTransform.DctCoefficient(k * step, y) * coefficients[(k * size) + x];
                }

                middle[(y * size) + x] = (int)Math.Clamp((sum + 64) >> 7, short.MinValue, short.MaxValue);
            }
        }

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                long sum = 0;
                for (int k = 0; k < size; k++)
                {
                    sum += HevcTransform.DctCoefficient(k * step, x) * middle[(y * size) + k];
                }

                output[(y * size) + x] = (int)Math.Clamp((sum + 2048) >> 12, short.MinValue, short.MaxValue);
            }
        }

        return output;
    }

    [Test]
    [Property("Category", "Oracle")]
    [Arguments(8)]
    [Arguments(10)]
    public async Task Encode_HmDecodesTheSameWithLoopFilter(int bitDepth)
    {
        string hm = Path.Combine(Environment.GetEnvironmentVariable("PICTURESHARP_HEIF_TOOLS") ?? Path.Combine(Root(), "reference", "tools"), "bin", "TAppDecoder");
        Skip.Unless(File.Exists(hm), "Build TAppDecoder with scripts/build-heif-reference.sh.");
        string work = Directory.CreateTempSubdirectory("picturesharp-heif-enc-").FullName;
        try
        {
            foreach ((int width, int height, int qp, double aq) in new[] { (96, 64, 30, 0.0), (200, 136, 40, 0.0), (64, 64, 12, 0.0), (200, 136, 32, 2.0) })
            {
                var settings = new HevcEncoderSettings { Width = width, Height = height, Qp = qp, BitDepth = bitDepth, AqStrength = aq, AqReferenceLogVariance = Math.Log(1500) };
                ushort[][] source = Picture(width, height, false, qp);
                if (bitDepth == 10)
                {
                    foreach (ushort[] plane in source)
                        for (int i = 0; i < plane.Length; i++) plane[i] = (ushort)((plane[i] * 4) + (i & 3));
                }

                List<byte[]> nals = HevcPictureEncoder.Encode(settings, source, CancellationToken.None);
                string stream = Path.Combine(work, "s.hevc");
                File.WriteAllBytes(stream, HeifTestItems.ToAnnexB(nals.Select(n => (ReadOnlyMemory<byte>)n)));
                string output = Path.Combine(work, "s.yuv");
                var start = new ProcessStartInfo(hm) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
                foreach (string argument in new[] { "-b", stream, "-o", output, "-d", "0" })
                {
                    start.ArgumentList.Add(argument);
                }

                using (Process process = Process.Start(start)!)
                {
                    string log = await process.StandardOutput.ReadToEndAsync();
                    await process.WaitForExitAsync();
                    await Assert.That(process.ExitCode).IsEqualTo(0).Because(log);
                }

                HevcFrame frame = HevcDecoder.Decode([.. nals.Select(n => (ReadOnlyMemory<byte>)n)], null, CancellationToken.None);
                byte[] yuv = File.ReadAllBytes(output);
                byte[] ours = [.. frame.Planes.SelectMany((plane, c) => Crop(frame, plane, c))];
                await Assert.That(ours).IsEquivalentTo(yuv, CollectionOrdering.Matching).Because($"{width}x{height} QP {qp}");
            }
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    private static IEnumerable<byte> Crop(HevcFrame frame, ushort[] plane, int c)
    {
        int shift = c == 0 ? 0 : 1;
        for (int y = 0; y < frame.CroppedHeight >> shift; y++)
        {
            for (int x = 0; x < frame.CroppedWidth >> shift; x++)
            {
                ushort sample = plane[(y * frame.Strides[c]) + x];
                yield return (byte)sample;
                if (frame.BitDepthLuma > 8) yield return (byte)(sample >> 8);
            }
        }
    }

    private static string Root()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PictureSharp.Heif.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? AppContext.BaseDirectory;
    }
}
