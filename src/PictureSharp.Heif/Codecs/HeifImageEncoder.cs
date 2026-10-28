using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using PictureSharp.Formats.Heif.Container;
using PictureSharp.Formats.Heif.Hevc;
using PictureSharp.Processing;

namespace PictureSharp.Formats.Heif.Codecs;

// Encodes an image as HEIC: BT.601 full-range 4:2:0 HEVC (the colour signalling heif-enc uses by default), with 8 or,
// for 16-bit images, 10 bits per sample (HeifEncoderOptions.BitDepth; the pixel type of the arrays selects it), a grid of
// tiles for large images, a monochrome auxiliary alpha image, ICC/nclx, EXIF and XMP, and the HDR gain map. The EXIF
// orientation becomes irot/imir, and the written EXIF says TopLeft, so readers display the image as the original was
// shown.
internal static class HeifImageEncoder
{
    // sRGB primaries and transfer with BT.601 full-range YCbCr, as YCbCrPlanes converts.
    public static readonly HeifNclx SignalledColour = new(Primaries: 1, Transfer: 13, Matrix: 6, FullRange: true);

    // The samples a coded image takes from RGBA pixels.
    public enum PlaneSource
    {
        YCbCr,
        Alpha,
        Gray,
    }

    public static byte[] Encode(Image image, HeifEncoderOptions options, CancellationToken cancellationToken)
    {
        HeifWriter.Content content = Prepare(image, options, cancellationToken);
        if (options.GainMap != HeifGainMapMode.None
            && image.GetGainMap(require: options.GainMap == HeifGainMapMode.Require) is { } gainMap)
        {
            content = content with { GainMap = HeifGainMapEncoder.Prepare(image, gainMap, options, cancellationToken) };
        }

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
        bool alpha = !image.Frames.RootFrame.IsOpaque();
        HeifCodedImage color;
        List<HeifTransform> transforms;
        HeifCodedImage? alphaImage;
        if (TenBit(image.Frames.RootFrame, options.BitDepth))
        {
            Rgba64[] pixels = Wide(image.Frames.RootFrame);
            (color, transforms) = Code(pixels, width, height, PlaneSource.YCbCr, options.Quality, options.AdaptiveQuantizationStrength, options, cancellationToken);
            alphaImage = alpha ? Code(pixels, width, height, PlaneSource.Alpha, options.AlphaQuality ?? options.Quality, 0, options, cancellationToken).Image : null;
        }
        else
        {
            Rgba32[] pixels = Narrow(image.Frames.RootFrame);
            (color, transforms) = Code(pixels, width, height, PlaneSource.YCbCr, options.Quality, options.AdaptiveQuantizationStrength, options, cancellationToken);
            alphaImage = alpha ? Code(pixels, width, height, PlaneSource.Alpha, options.AlphaQuality ?? options.Quality, 0, options, cancellationToken).Image : null;
        }

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

    // HeifBitDepth: Ten always; Auto for a 16-bit frame with a sample that is not an exact 8-bit value.
    public static bool TenBit(ImageFrame frame, HeifBitDepth depth) => depth switch
    {
        HeifBitDepth.Ten => true,
        HeifBitDepth.Auto => frame.PixelType == PixelType.Rgba64 && !IsExact8(frame.GetPixelSpan<Rgba64>()),
        _ => false,
    };

    // The frame's pixels at 16 bits (8-bit frames widened exactly).
    public static Rgba64[] Wide(ImageFrame frame)
    {
        var pixels = new Rgba64[frame.Width * frame.Height];
        if (frame.PixelType == PixelType.Rgba64)
        {
            frame.CopyPixelDataTo<Rgba64>(pixels);
            return pixels;
        }

        ReadOnlySpan<Rgba32> narrow = frame.GetPixelSpan<Rgba32>();
        for (int i = 0; i < pixels.Length; i++)
        {
            pixels[i] = Rgba64.FromRgba32(narrow[i]);
        }

        return pixels;
    }

    // The frame's pixels at 8 bits (16-bit frames rounded to the nearest value).
    public static Rgba32[] Narrow(ImageFrame frame)
    {
        var pixels = new Rgba32[frame.Width * frame.Height];
        if (frame.PixelType == PixelType.Rgba32)
        {
            frame.CopyPixelDataTo<Rgba32>(pixels);
        }
        else
        {
            frame.CopyPixelDataTo(MemoryMarshal.AsBytes(pixels.AsSpan()), PixelFormat.Rgba32);
        }

        return pixels;
    }

    // Every sample is a multiple of 257: both bytes of each 16-bit sample are equal.
    private static bool IsExact8(ReadOnlySpan<Rgba64> pixels)
    {
        ulong difference = 0;
        foreach (ulong word in MemoryMarshal.Cast<Rgba64, ulong>(pixels))
        {
            difference |= (word ^ (word >> 8)) & 0x00FF00FF00FF00FFul;
        }

        return difference == 0;
    }

    // Codes the pixels' planes as one image or a grid of TileSize tiles; a single image is coded at even dimensions
    // (4:2:0) and cropped back by the returned clean aperture, which comes first: it applies to the coded picture, before
    // the orientation transforms.
    // Rgba32 pixels are coded with 8 bits, Rgba64 pixels with 10.
    public static (HeifCodedImage Image, List<HeifTransform> Transforms) Code<TPixel>(TPixel[] pixels, int width, int height,
        PlaneSource source, int quality, float aqStrength, HeifEncoderOptions options, CancellationToken cancellationToken)
        where TPixel : unmanaged
    {
        int tile = options.TileSize;
        bool grid = tile > 0 && (width > tile || height > tile);
        int codedWidth = grid ? tile : (width + 1) & ~1;
        int codedHeight = grid ? tile : (height + 1) & ~1;
        int columns = grid ? (width + tile - 1) / tile : 1;
        int rows = grid ? (height + tile - 1) / tile : 1;
        var settings = new HevcEncoderSettings
        {
            Width = codedWidth,
            Height = codedHeight,
            Qp = QpFromQuality(quality),
            ChromaFormatIdc = source == PlaneSource.YCbCr ? 1 : 0,
            BitDepth = BitDepthOf<TPixel>(),
            AqStrength = aqStrength,
            AqReferenceLogVariance = aqStrength > 0 ? LogVariance(pixels) : 0,
        };
        HeifCodedImage image = EncodeTiles(pixels, width, height, codedWidth, codedHeight, columns, rows, settings, source,
            options, cancellationToken);
        if (grid)
        {
            return (image, []);
        }

        image = image with { Width = codedWidth, Height = codedHeight };
        return (image, codedWidth != width || codedHeight != height ? [HeifCleanAperture.TopLeft(codedWidth, codedHeight, width, height)] : []);
    }

    // ln of the summed Y, Cb and Cr variances of the whole image: the reference of variance adaptive quantization, so
    // all tiles of a grid share one quality scale.
    // The variances are in coded sample units, like the CTB variances they are compared with.
    private static double LogVariance<TPixel>(TPixel[] pixels) where TPixel : unmanaged
    {
        double sy = 0, syy = 0, sb = 0, sbb = 0, sr = 0, srr = 0;
        double half = 1 << (BitDepthOf<TPixel>() - 1);
        foreach (TPixel pixel in pixels)
        {
            (double r, double g, double b) = Colour(pixel);
            double y = (0.299 * r) + (0.587 * g) + (0.114 * b);
            double cb = half + ((b - y) / 1.772);
            double cr = half + ((r - y) / 1.402);
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

    private static HeifCodedImage EncodeTiles<TPixel>(TPixel[] pixels, int width, int height, int tileWidth, int tileHeight,
        int columns, int rows, HevcEncoderSettings settings, PlaneSource source, HeifEncoderOptions options, CancellationToken cancellationToken)
        where TPixel : unmanaged
    {
        var payloads = new byte[columns * rows][];
        byte[]? configuration = null;
        var parallel = new ParallelOptions { MaxDegreeOfParallelism = options.MaxDegreeOfParallelism, CancellationToken = cancellationToken };
        Parallel.For(0, payloads.Length, parallel, index =>
        {
            int x0 = (index % columns) * tileWidth;
            int y0 = (index / columns) * tileHeight;
            ushort[][] planes = source == PlaneSource.YCbCr
                ? YCbCrPlanes(pixels, width, height, x0, y0, settings.CodedWidth, settings.CodedHeight)
                : SinglePlane(pixels, width, height, x0, y0, settings.CodedWidth, settings.CodedHeight, source == PlaneSource.Alpha);
            List<byte[]> nals = HevcPictureEncoder.Encode(settings, planes, cancellationToken);
            payloads[index] = HeifWriter.Length(nals[3]);
            if (index == 0)
            {
                configuration = HevcConfiguration(nals[0], nals[1], nals[2], settings.ChromaFormatIdc, settings.BitDepth);
            }
        });

        return new HeifCodedImage(width, height, tileWidth, tileHeight, columns, rows, configuration!, payloads, source == PlaneSource.YCbCr ? 3 : 1)
        {
            BitDepth = settings.BitDepth,
        };
    }

    // BT.601 full range (ITU-T H.273 matrix 6), chroma as the average of each 2x2 block; samples outside the image
    // repeat the nearest edge.
    private static ushort[][] YCbCrPlanes<TPixel>(TPixel[] pixels, int width, int height, int x0, int y0, int codedWidth, int codedHeight)
        where TPixel : unmanaged
    {
        int maximum = (1 << BitDepthOf<TPixel>()) - 1;
        double half = (maximum + 1) >> 1;
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
                        (double r, double g, double b) = Colour(pixels[(Math.Min(y0 + y + dy, height - 1) * width) + Math.Min(x0 + x + dx, width - 1)]);
                        double yValue = (0.299 * r) + (0.587 * g) + (0.114 * b);
                        luma[((y + dy) * codedWidth) + x + dx] = Clip(yValue, maximum);
                        sumCb += (b - yValue) / 1.772;
                        sumCr += (r - yValue) / 1.402;
                    }
                }

                cb[((y / 2) * (codedWidth / 2)) + (x / 2)] = Clip(half + (sumCb / 4), maximum);
                cr[((y / 2) * (codedWidth / 2)) + (x / 2)] = Clip(half + (sumCr / 4), maximum);
            }
        }

        return [luma, cb, cr];
    }

    // Alpha, or the red component of gray pixels.
    private static ushort[][] SinglePlane<TPixel>(TPixel[] pixels, int width, int height, int x0, int y0, int codedWidth, int codedHeight, bool alpha)
        where TPixel : unmanaged
    {
        var plane = new ushort[codedWidth * codedHeight];
        for (int y = 0; y < codedHeight; y++)
        {
            for (int x = 0; x < codedWidth; x++)
            {
                TPixel pixel = pixels[(Math.Min(y0 + y, height - 1) * width) + Math.Min(x0 + x, width - 1)];
                if (typeof(TPixel) == typeof(Rgba32))
                {
                    Rgba32 p = Unsafe.As<TPixel, Rgba32>(ref pixel);
                    plane[(y * codedWidth) + x] = alpha ? p.A : p.R;
                }
                else
                {
                    Rgba64 p = Unsafe.As<TPixel, Rgba64>(ref pixel);
                    plane[(y * codedWidth) + x] = To10(alpha ? p.A : p.R);
                }
            }
        }

        return [plane];
    }

    private static ushort Clip(double value, int maximum) => (ushort)Math.Clamp((int)Math.Round(value, MidpointRounding.AwayFromZero), 0, maximum);

    private static int BitDepthOf<TPixel>() where TPixel : unmanaged => typeof(TPixel) == typeof(Rgba64) ? 10 : 8;

    // R, G and B in coded sample units: 8-bit values as they are, 16-bit values scaled to 10 bits.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static (double R, double G, double B) Colour<TPixel>(TPixel pixel) where TPixel : unmanaged
    {
        if (typeof(TPixel) == typeof(Rgba32))
        {
            Rgba32 p = Unsafe.As<TPixel, Rgba32>(ref pixel);
            return (p.R, p.G, p.B);
        }

        const double scale = 1023.0 / 65535;
        Rgba64 wide = Unsafe.As<TPixel, Rgba64>(ref pixel);
        return (wide.R * scale, wide.G * scale, wide.B * scale);
    }

    private static ushort To10(ushort value) => (ushort)((((uint)value * 1023) + 32767) / 65535);

    // HEVCDecoderConfigurationRecord (ISO/IEC 14496-15 8.3.3.1) with the parameter sets; profile fields come from the SPS.
    private static byte[] HevcConfiguration(byte[] vps, byte[] sps, byte[] pps, int chromaFormat, int bitDepth)
    {
        byte[] rbsp = new byte[sps.Length];
        int length = HevcNalHeader.ExtractRbsp(sps, rbsp);
        if (length < 13)
        {
            throw new InvalidOperationException("Unexpected SPS.");
        }

        var record = new List<byte> { 1 };
        record.AddRange(rbsp.AsSpan(1, 12).ToArray()); // general profile, compatibility, constraint flags, level
        record.AddRange([0xf0, 0x00, 0xfc, (byte)(0xfc | chromaFormat), (byte)(0xf8 | (bitDepth - 8)), (byte)(0xf8 | (bitDepth - 8)), 0x00, 0x00, 0x0f, 3]);
        foreach (byte[] nal in new[] { vps, sps, pps })
        {
            record.Add((byte)(0x80 | ((nal[0] >> 1) & 0x3f)));
            record.AddRange([0, 1, (byte)(nal.Length >> 8), (byte)nal.Length]);
            record.AddRange(nal);
        }

        return [.. record];
    }

    // EXIF orientation as HEIF transforms in association order (irot rotates anti-clockwise; imir mode 1 is left-right).
    public static IEnumerable<HeifTransform> Transforms(ExifOrientation orientation) => orientation switch
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
