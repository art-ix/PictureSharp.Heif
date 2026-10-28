using PictureSharp.Formats.Heif.Container;

namespace PictureSharp.Formats.Heif.Codecs;

// Image output and auxiliary alpha for a rendered straight-alpha RGBA canvas.
internal static class HeifRgbaCanvas
{
    // Without alpha, an opaque background keeps Image.HasAlpha false; CopyPixelDataFrom marks the image as having alpha.
    public static Image ToImage(this HeifCanvas<Rgba32> canvas, bool alpha)
    {
        if (alpha)
        {
            var transparent = new Image(canvas.Width, canvas.Height);
            transparent.CopyPixelDataFrom(canvas.Pixels);
            return transparent;
        }

        var image = new Image(canvas.Width, canvas.Height, new Rgba32(0, 0, 0, 255));
        canvas.Pixels.CopyTo(image.GetPixelSpan<Rgba32>());
        return image;
    }

    public static Image ToImage(this HeifCanvas<Rgba64> canvas, bool alpha)
    {
        if (alpha)
        {
            var transparent = new Image(canvas.Width, canvas.Height, PixelType.Rgba64);
            transparent.CopyPixelDataFrom<Rgba64>(canvas.Pixels);
            return transparent;
        }

        var image = new Image(canvas.Width, canvas.Height, PixelType.Rgba64, new Rgba64(0, 0, 0, ushort.MaxValue));
        canvas.Pixels.CopyTo(image.GetPixelSpan<Rgba64>());
        return image;
    }

    public static void SetAlpha(this HeifCanvas<Rgba64> canvas, HeifCanvas<Rgba64> alpha, bool premultiplied)
    {
        if (alpha.Width != canvas.Width || alpha.Height != canvas.Height)
        {
            throw HeifReader.Error("Alpha image dimensions do not match the primary image.");
        }

        Span<Rgba64> pixels = canvas.Pixels;
        for (int i = 0; i < pixels.Length; i++)
        {
            pixels[i].A = alpha.Pixels[i].R;
        }

        if (premultiplied)
        {
            for (int i = 0; i < pixels.Length; i++)
            {
                Rgba64 p = pixels[i];
                if (p.A is not (0 or ushort.MaxValue))
                {
                    pixels[i] = new Rgba64(Unpremultiply16(p.R, p.A), Unpremultiply16(p.G, p.A), Unpremultiply16(p.B, p.A), p.A);
                }
            }
        }
    }

    // PictureSharp's 16-bit premultiplied-to-straight conversion.
    private static ushort Unpremultiply16(ushort c, ushort a) =>
        (ushort)Math.Min(ushort.MaxValue, (((uint)c * 65535u) + (uint)(a >> 1)) / a);

    // Takes alpha from the red channel of a rendered auxiliary alpha image; premultiplied colour is converted back.
    public static void SetAlpha(this HeifCanvas<Rgba32> canvas, HeifCanvas<Rgba32> alpha, bool premultiplied)
    {
        if (alpha.Width != canvas.Width || alpha.Height != canvas.Height)
        {
            throw HeifReader.Error("Alpha image dimensions do not match the primary image.");
        }

        Span<Rgba32> pixels = canvas.Pixels;
        for (int i = 0; i < pixels.Length; i++)
        {
            pixels[i].A = alpha.Pixels[i].R;
        }

        if (premultiplied)
        {
            Unpremultiply(pixels);
        }
    }

    // Same rounding as PictureSharp's premultiplied-to-straight conversion.
    private static void Unpremultiply(Span<Rgba32> pixels)
    {
        for (int i = 0; i < pixels.Length; i++)
        {
            Rgba32 p = pixels[i];
            if (p.A is not (0 or 255))
            {
                pixels[i] = new Rgba32((byte)Math.Min(255, 255 * p.R / p.A), (byte)Math.Min(255, 255 * p.G / p.A),
                    (byte)Math.Min(255, 255 * p.B / p.A), p.A);
            }
        }
    }
}
