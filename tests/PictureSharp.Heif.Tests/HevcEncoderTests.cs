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
    public async Task Encode_DecoderReproducesTheReconstruction(int width, int height, int qp, bool monochrome, double aq = 0)
    {
        var settings = new HevcEncoderSettings
        {
            Width = width,
            Height = height,
            Qp = qp,
            ChromaFormatIdc = monochrome ? 0 : 1,
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

    [Test]
    [Property("Category", "Oracle")]
    public async Task Encode_HmDecodesTheSameWithLoopFilter()
    {
        string hm = Path.Combine(Environment.GetEnvironmentVariable("PICTURESHARP_HEIF_TOOLS") ?? Path.Combine(Root(), "reference", "tools"), "bin", "TAppDecoder");
        Skip.Unless(File.Exists(hm), "Build TAppDecoder with scripts/build-heif-reference.sh.");
        string work = Directory.CreateTempSubdirectory("picturesharp-heif-enc-").FullName;
        try
        {
            foreach ((int width, int height, int qp, double aq) in new[] { (96, 64, 30, 0.0), (200, 136, 40, 0.0), (64, 64, 12, 0.0), (200, 136, 32, 2.0) })
            {
                var settings = new HevcEncoderSettings { Width = width, Height = height, Qp = qp, AqStrength = aq, AqReferenceLogVariance = Math.Log(1500) };
                List<byte[]> nals = HevcPictureEncoder.Encode(settings, Picture(width, height, false, qp), CancellationToken.None);
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
                yield return (byte)plane[(y * frame.Strides[c]) + x];
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
