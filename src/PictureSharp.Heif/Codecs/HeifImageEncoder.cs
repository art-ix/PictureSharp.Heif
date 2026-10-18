using PictureSharp.Formats.Heif.Container;
using PictureSharp.Formats.Heif.Hevc;
using PictureSharp.Processing;

namespace PictureSharp.Formats.Heif.Codecs;

// Encodes an image as HEIC: BT.601 full-range 4:2:0 HEVC (the colour signalling heif-enc uses by default), a grid of
// tiles for large images, a monochrome auxiliary alpha image, ICC/nclx, EXIF and XMP. The EXIF orientation becomes
// irot/imir, and the written EXIF says TopLeft, so readers display the image as the original was shown.
internal static class HeifImageEncoder
{
    // sRGB primaries and transfer with BT.601 full-range YCbCr, as YCbCrPlanes converts.
    private static readonly HeifNclx SignalledColour = new(Primaries: 1, Transfer: 13, Matrix: 6, FullRange: true);

    public static byte[] Encode(Image image, HeifEncoderOptions options, CancellationToken cancellationToken)
    {
        HeifWriter.Content content = Prepare(image, options, cancellationToken);
        if (options.ThumbnailMaxDimension > 0)
        {
            // An upright thumbnail has no orientation dependency on its parent, including odd-size chroma grids.
            using Image thumbnail = image.Clone(processing => processing.AutoOrient().Thumbnail(options.ThumbnailMaxDimension, options.ThumbnailMaxDimension), cancellationToken);
            content = content with
            {
                Thumbnail = Prepare(thumbnail, options with { Metadata = options.Metadata & ~(MetadataKinds.Exif | MetadataKinds.Xmp), ThumbnailMaxDimension = 0 }, cancellationToken)
            };
        }

        return HeifWriter.Write(content);
    }

    private static HeifWriter.Content Prepare(Image image, HeifEncoderOptions options, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        int width = image.Width;
        int height = image.Height;
        Rgba32[] pixels = new Rgba32[width * height];
        image.Frames.RootFrame.CopyPixelDataTo(pixels);
        int qp = QpFromQuality(options.Quality);
        bool alpha = !image.Frames.RootFrame.IsOpaque();
        int tile = options.TileSize;
        bool grid = tile > 0 && (width > tile || height > tile);

        // A single image is coded at even dimensions (4:2:0) and cropped back with a clean aperture.
        int codedWidth = grid ? tile : (width + 1) & ~1;
        int codedHeight = grid ? tile : (height + 1) & ~1;
        int columns = grid ? (width + tile - 1) / tile : 1;
        int rows = grid ? (height + tile - 1) / tile : 1;
        var colorSettings = new HevcEncoderSettings
        {
            Width = codedWidth,
            Height = codedHeight,
            Qp = qp,
            AqStrength = options.AdaptiveQuantizationStrength,
            AqReferenceLogVariance = options.AdaptiveQuantizationStrength > 0 ? LogVariance(pixels) : 0,
        };
        HeifCodedImage color = EncodeTiles(pixels, width, height, codedWidth, codedHeight, columns, rows, colorSettings,
            isAlpha: false, options, cancellationToken);
        HeifCodedImage? alphaImage = null;
        if (alpha)
        {
            var alphaSettings = colorSettings with
            {
                ChromaFormatIdc = 0,
                Qp = QpFromQuality(options.AlphaQuality ?? options.Quality),
                AqStrength = 0,
            };
            alphaImage = EncodeTiles(pixels, width, height, codedWidth, codedHeight, columns, rows, alphaSettings,
                isAlpha: true, options, cancellationToken);
        }

        if (!grid)
        {
            color = color with { Width = codedWidth, Height = codedHeight };
            alphaImage = alphaImage is null ? null : alphaImage with { Width = codedWidth, Height = codedHeight };
        }

        // The clean aperture comes first: it applies to the coded picture, before the orientation transforms.
        List<HeifTransform> transforms = !grid && (codedWidth != width || codedHeight != height)
            ? [HeifCleanAperture.TopLeft(codedWidth, codedHeight, width, height)]
            : [];
        ImageMetadata metadata = image.Metadata;
        transforms.AddRange(Transforms(metadata.Orientation));
        byte[]? icc = (options.Metadata & MetadataKinds.Icc) != 0 ? metadata.IccProfile : null;
        byte[]? exif = null;
        if ((options.Metadata & MetadataKinds.Exif) != 0 && metadata.Exif is not null)
        {
            ImageMetadata copy = metadata.Clone();
            copy.TrySetOrientation(ExifOrientation.TopLeft);
            exif = copy.Exif;
        }

        byte[]? xmp = (options.Metadata & MetadataKinds.Xmp) != 0 ? metadata.Xmp : null;
        return new HeifWriter.Content(color, alphaImage, icc, SignalledColour, transforms, exif, xmp);
    }

    // ln of the summed Y, Cb and Cr variances of the whole image: the reference of variance adaptive quantization, so
    // all tiles of a grid share one quality scale.
    private static double LogVariance(Rgba32[] pixels)
    {
        double sy = 0, syy = 0, sb = 0, sbb = 0, sr = 0, srr = 0;
        foreach (Rgba32 p in pixels)
        {
            double y = (0.299 * p.R) + (0.587 * p.G) + (0.114 * p.B);
            double cb = 128 + ((p.B - y) / 1.772);
            double cr = 128 + ((p.R - y) / 1.402);
            sy += y;
            syy += y * y;
            sb += cb;
            sbb += cb * cb;
            sr += cr;
            srr += cr * cr;
        }

        double n = pixels.Length;
        double variance = (syy / n) - Math.Pow(sy / n, 2) + (sbb / n) - Math.Pow(sb / n, 2) + (srr / n) - Math.Pow(sr / n, 2);
        return Math.Log(Math.Max(variance, 1e-3));
    }

    // QP = round(0.51 * (100 - quality)): 0 maps to QP 51, 100 to QP 0.
    public static int QpFromQuality(int quality) => (int)Math.Round(0.51 * (100 - quality), MidpointRounding.AwayFromZero);

    private static HeifCodedImage EncodeTiles(Rgba32[] pixels, int width, int height, int tileWidth, int tileHeight,
        int columns, int rows, HevcEncoderSettings settings, bool isAlpha, HeifEncoderOptions options, CancellationToken cancellationToken)
    {
        var payloads = new byte[columns * rows][];
        byte[]? configuration = null;
        var parallel = new ParallelOptions { MaxDegreeOfParallelism = options.MaxDegreeOfParallelism, CancellationToken = cancellationToken };
        Parallel.For(0, payloads.Length, parallel, index =>
        {
            int x0 = (index % columns) * tileWidth;
            int y0 = (index / columns) * tileHeight;
            ushort[][] planes = isAlpha
                ? AlphaPlane(pixels, width, height, x0, y0, settings.CodedWidth, settings.CodedHeight)
                : YCbCrPlanes(pixels, width, height, x0, y0, settings.CodedWidth, settings.CodedHeight);
            List<byte[]> nals = HevcPictureEncoder.Encode(settings, planes, cancellationToken);
            payloads[index] = nals[3];
            if (index == 0)
            {
                configuration = HevcConfiguration(nals[0], nals[1], nals[2], settings.ChromaFormatIdc);
            }
        });

        return new HeifCodedImage(width, height, tileWidth, tileHeight, columns, rows, configuration!, payloads, isAlpha ? 1 : 3);
    }

    // BT.601 full range (ITU-T H.273 matrix 6), chroma as the average of each 2x2 block; samples outside the image
    // repeat the nearest edge.
    private static ushort[][] YCbCrPlanes(Rgba32[] pixels, int width, int height, int x0, int y0, int codedWidth, int codedHeight)
    {
        var luma = new ushort[codedWidth * codedHeight];
        var cb = new ushort[codedWidth * codedHeight / 4];
        var cr = new ushort[codedWidth * codedHeight / 4];
        for (int y = 0; y < codedHeight; y += 2)
        {
            for (int x = 0; x < codedWidth; x += 2)
            {
                double sumCb = 0;
                double sumCr = 0;
                for (int dy = 0; dy < 2; dy++)
                {
                    for (int dx = 0; dx < 2; dx++)
                    {
                        Rgba32 p = pixels[(Math.Min(y0 + y + dy, height - 1) * width) + Math.Min(x0 + x + dx, width - 1)];
                        double yValue = (0.299 * p.R) + (0.587 * p.G) + (0.114 * p.B);
                        luma[((y + dy) * codedWidth) + x + dx] = Clip(yValue);
                        sumCb += (p.B - yValue) / 1.772;
                        sumCr += (p.R - yValue) / 1.402;
                    }
                }

                cb[((y / 2) * (codedWidth / 2)) + (x / 2)] = Clip(128 + (sumCb / 4));
                cr[((y / 2) * (codedWidth / 2)) + (x / 2)] = Clip(128 + (sumCr / 4));
            }
        }

        return [luma, cb, cr];
    }

    private static ushort[][] AlphaPlane(Rgba32[] pixels, int width, int height, int x0, int y0, int codedWidth, int codedHeight)
    {
        var plane = new ushort[codedWidth * codedHeight];
        for (int y = 0; y < codedHeight; y++)
        {
            for (int x = 0; x < codedWidth; x++)
            {
                plane[(y * codedWidth) + x] = pixels[(Math.Min(y0 + y, height - 1) * width) + Math.Min(x0 + x, width - 1)].A;
            }
        }

        return [plane];
    }

    private static ushort Clip(double value) => (ushort)Math.Clamp((int)Math.Round(value, MidpointRounding.AwayFromZero), 0, 255);

    // HEVCDecoderConfigurationRecord (ISO/IEC 14496-15 8.3.3.1) with the parameter sets; profile fields come from the SPS.
    private static byte[] HevcConfiguration(byte[] vps, byte[] sps, byte[] pps, int chromaFormat)
    {
        byte[] rbsp = new byte[sps.Length];
        int length = HevcNalHeader.ExtractRbsp(sps, rbsp);
        if (length < 13)
        {
            throw new InvalidOperationException("Unexpected SPS.");
        }

        var record = new List<byte> { 1 };
        record.AddRange(rbsp.AsSpan(1, 12).ToArray()); // general profile, compatibility, constraint flags, level
        record.AddRange([0xf0, 0x00, 0xfc, (byte)(0xfc | chromaFormat), 0xf8, 0xf8, 0x00, 0x00, 0x0f, 3]);
        foreach (byte[] nal in new[] { vps, sps, pps })
        {
            record.Add((byte)(0x80 | ((nal[0] >> 1) & 0x3f)));
            record.AddRange([0, 1, (byte)(nal.Length >> 8), (byte)nal.Length]);
            record.AddRange(nal);
        }

        return [.. record];
    }

    // EXIF orientation as HEIF transforms in association order (irot rotates anti-clockwise; imir mode 1 is left-right).
    private static IEnumerable<HeifTransform> Transforms(ExifOrientation orientation) => orientation switch
    {
        ExifOrientation.TopRight => [new HeifMirror(TopBottom: false)],
        ExifOrientation.BottomRight => [new HeifRotation(2)],
        ExifOrientation.BottomLeft => [new HeifMirror(TopBottom: true)],
        ExifOrientation.LeftTop => [new HeifRotation(3), new HeifMirror(TopBottom: false)],
        ExifOrientation.RightTop => [new HeifRotation(3)],
        ExifOrientation.RightBottom => [new HeifRotation(1), new HeifMirror(TopBottom: false)],
        ExifOrientation.LeftBottom => [new HeifRotation(1)],
        _ => [],
    };
}
