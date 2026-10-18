using System.Diagnostics;

namespace PictureSharp.Formats.Heif.Tests;

// Private iPhone Portrait photographs in samples/: the Apple depth description, and how close the rendering at the
// photo's SimulatedAperture comes to the same photograph exported by Photos with the effect (IMG_xxxx_2.HEIC). The
// numbers guide tuning; they are not a compatibility claim.
public sealed class HeifPortraitLocalTests
{
    private static readonly ImageConfiguration Configuration = ImageConfiguration.Default.ToBuilder().AddFormat(new HeifFormat()).Build();

    private static string[] Samples()
    {
        string directory = Path.Combine(HevcLocalTests.FindRoot(), "samples");
        return Directory.Exists(directory)
            ? [.. Directory.GetFiles(directory, "*", SearchOption.AllDirectories)
                .Where(path => Path.GetExtension(path).Equals(".heic", StringComparison.OrdinalIgnoreCase)).Order(StringComparer.Ordinal)]
            : [];
    }

    [Test]
    [Property("Category", "Local")]
    public async Task PrivateSamples_DescribeAppleDisparity()
    {
        int described = 0;
        foreach (string path in Samples())
        {
            HeifDocument document = HeifDocument.Open(path);
            if (document.PrimaryImage.FindDepthImage() is not { } item)
            {
                continue;
            }

            HeifDepthImage depth = document.LoadDepth(item);
            HeifAppleDepthInfo info = depth.AppleDepth!;
            await Assert.That(info).IsNotNull().Because(path);
            await Assert.That(info.Kind).IsEqualTo(HeifAuxiliaryKind.Disparity);
            await Assert.That((info.StoredFormat, info.NativeFormat)).IsEqualTo(("L008", "hdis"));
            await Assert.That((info.IntMinValue, info.IntMaxValue)).IsEqualTo((0, 255));
            await Assert.That(info.FloatMaxValue).IsGreaterThan(info.FloatMinValue);
            await Assert.That(info.SimulatedAperture).IsNotNull();
            await Assert.That(info.IntrinsicMatrix.Count).IsEqualTo(9);
            await Assert.That(info.RenderingParameters.Span.StartsWith("REND"u8)).IsTrue();
            Console.WriteLine($"Local portrait: {Path.GetFileName(path)} depth {depth.Width}x{depth.Height}, disparity " +
                $"{info.FloatMinValue:G4}–{info.FloatMaxValue:G4}, f/{info.SimulatedAperture}, accuracy {info.Accuracy}, quality {info.Quality}");
            described++;
        }

        Skip.Unless(described > 0, "No private Portrait photos in samples/.");
    }

    [Test]
    [Property("Category", "Local")]
    public async Task PrivateSamples_RenderingApproachesPhotosExport()
    {
        var pairs = Samples().Where(path => !Path.GetFileNameWithoutExtension(path).EndsWith("_2", StringComparison.Ordinal))
            .Select(path => (Original: path, Reference: Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path) + "_2" + Path.GetExtension(path))))
            .Where(pair => File.Exists(pair.Reference)).ToArray();
        Skip.Unless(pairs.Length > 0, "No Portrait pairs IMG_xxxx.HEIC + IMG_xxxx_2.HEIC in samples/.");
        var options = new DecoderOptions { Configuration = Configuration, LoadGainMap = true };
        foreach ((string original, string reference) in pairs)
        {
            HeifDocument document = HeifDocument.Open(original);
            HeifItemInfo? matteItem = document.PrimaryImage.FindAuxiliaryImage(HeifAppleAuxiliaryTypes.PortraitEffectsMatte);
            if (document.PrimaryImage.FindDepthImage() is null || matteItem is null)
            {
                continue;
            }

            long allocated = GC.GetTotalAllocatedBytes(true);
            var clock = Stopwatch.StartNew();
            using Image ours = document.RenderPortrait(document.PrimaryImage, null, options);
            long elapsed = clock.ElapsedMilliseconds;
            allocated = GC.GetTotalAllocatedBytes(true) - allocated;
            using Image photo = document.LoadImage(document.PrimaryImage, options);
            HeifDepthImage depth = document.LoadDepth(document.PrimaryImage.FindDepthImage()!);
            using Image matte = document.LoadImage(matteItem, options);
            clock.Restart();
            using Image again = HeifPortrait.Render(photo, depth, matte);
            long renderOnly = clock.ElapsedMilliseconds;
            HeifDocument exported = HeifDocument.Open(reference);
            using Image apple = exported.LoadImage(exported.PrimaryImage, options);
            await Assert.That((apple.Width, apple.Height)).IsEqualTo((photo.Width, photo.Height));
            await Assert.That(photo.HasHdr && ours.HasHdr).IsFalse().Because("the gain map is not attached to the result");

            matte.Mutate(x => x.Resize(photo.Width, photo.Height));
            Func<byte, bool> background = m => m < 26, subject = m => m > 230;
            (double before, double after) = (Psnr(photo, apple, matte, background), Psnr(ours, apple, matte, background));
            (double subjectBefore, double subjectAfter) = (Psnr(photo, apple, matte, subject), Psnr(ours, apple, matte, subject));
            (double ssimBefore, double ssimAfter) = (Ssim(photo, apple, matte, background), Ssim(ours, apple, matte, background));
            Console.WriteLine($"Local portrait: {Path.GetFileName(original)} {photo.Width}x{photo.Height}: background PSNR {before:F2} → " +
                $"{after:F2} dB, SSIM {ssimBefore:F3} → {ssimAfter:F3}; subject PSNR {subjectBefore:F2} → {subjectAfter:F2} dB; " +
                $"RenderPortrait {elapsed} ms ({allocated / 1048576} MiB allocated), Render {renderOnly} ms");
            await Assert.That(after).IsGreaterThan(before + 5).Because("the background should approach Apple's blur");
            await Assert.That(ssimAfter).IsGreaterThan(ssimBefore);
            await Assert.That(subjectAfter).IsGreaterThan(subjectBefore - 1).Because("the subject should stay as sharp as Apple's");
        }
    }

    // RGB PSNR over pixels whose matte value passes the selector.
    private static double Psnr(Image a, Image b, Image matte, Func<byte, bool> select)
    {
        ReadOnlySpan<Rgba32> pa = a.GetPixelSpan(), pb = b.GetPixelSpan(), pm = matte.GetPixelSpan();
        double error = 0;
        long count = 0;
        for (int i = 0; i < pa.Length; i++)
        {
            if (select(pm[i].R))
            {
                int dr = pa[i].R - pb[i].R, dg = pa[i].G - pb[i].G, db = pa[i].B - pb[i].B;
                error += dr * dr + dg * dg + db * db;
                count += 3;
            }
        }

        return 10 * Math.Log10(255.0 * 255.0 / (error / count));
    }

    // Mean luma SSIM of 8×8 blocks whose centre passes the selector.
    private static double Ssim(Image a, Image b, Image matte, Func<byte, bool> select)
    {
        const double c1 = 6.5025, c2 = 58.5225;
        double total = 0;
        int blocks = 0;
        for (int by = 0; by + 8 <= a.Height; by += 8)
        {
            for (int bx = 0; bx + 8 <= a.Width; bx += 8)
            {
                if (!select(matte[bx + 4, by + 4].R))
                {
                    continue;
                }

                double sa = 0, sb = 0, saa = 0, sbb = 0, sab = 0;
                for (int y = by; y < by + 8; y++)
                {
                    for (int x = bx; x < bx + 8; x++)
                    {
                        double la = HeifPortraitTests.Luma(a[x, y]), lb = HeifPortraitTests.Luma(b[x, y]);
                        sa += la;
                        sb += lb;
                        saa += la * la;
                        sbb += lb * lb;
                        sab += la * lb;
                    }
                }

                double ma = sa / 64, mb = sb / 64, va = saa / 64 - ma * ma, vb = sbb / 64 - mb * mb, cov = sab / 64 - ma * mb;
                total += (2 * ma * mb + c1) * (2 * cov + c2) / ((ma * ma + mb * mb + c1) * (va + vb + c2));
                blocks++;
            }
        }

        return total / blocks;
    }
}
