using PictureSharp.Formats.Heif.Container;

namespace PictureSharp.Formats.Heif.Codecs;

// A straight-alpha RGBA buffer with the container transforms (ISO/IEC 23008-12 6.5.10–6.5.12).
internal sealed class HeifCanvas(int width, int height)
{
    public int Width { get; } = width;
    public int Height { get; } = height;
    public Rgba32[] Pixels { get; } = GC.AllocateUninitializedArray<Rgba32>(width * height);

    // Without alpha, an opaque background keeps Image.HasAlpha false; CopyPixelDataFrom marks the image as having alpha.
    public Image ToImage(bool alpha)
    {
        if (alpha)
        {
            var transparent = new Image(Width, Height);
            transparent.CopyPixelDataFrom(Pixels);
            return transparent;
        }

        var image = new Image(Width, Height, new Rgba32(0, 0, 0, 255));
        Pixels.CopyTo(image.GetPixelSpan());
        return image;
    }

    public HeifCanvas Apply(HeifTransform transform) => transform switch
    {
        HeifCleanAperture clap => Crop(clap),
        HeifRotation rotation => Rotate(rotation.QuarterTurns),
        HeifMirror mirror => Mirror(mirror.TopBottom),
        _ => throw new ArgumentOutOfRangeException(nameof(transform)),
    };

    // Takes alpha from the red channel of a rendered auxiliary alpha image; premultiplied colour is converted back.
    public void SetAlpha(HeifCanvas alpha, bool premultiplied)
    {
        if (alpha.Width != Width || alpha.Height != Height)
        {
            throw HeifReader.Error("Alpha image dimensions do not match the primary image.");
        }

        Span<Rgba32> pixels = Pixels;
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

    // clap: the integral window validated by the container.
    private HeifCanvas Crop(HeifCleanAperture clap)
    {
        (int left, int top, int width, int height) = clap.Window(Width, Height);
        var result = new HeifCanvas(width, height);
        for (int y = 0; y < height; y++)
        {
            Pixels.AsSpan(((top + y) * Width) + left, width).CopyTo(result.Pixels.AsSpan(y * width));
        }

        return result;
    }

    // irot: anti-clockwise rotation by quarterTurns * 90 degrees.
    private HeifCanvas Rotate(int quarterTurns)
    {
        if (quarterTurns == 0)
        {
            return this;
        }

        bool swap = (quarterTurns & 1) != 0;
        var result = new HeifCanvas(swap ? Height : Width, swap ? Width : Height);
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                (int nx, int ny) = quarterTurns switch
                {
                    1 => (y, Width - 1 - x),
                    2 => (Width - 1 - x, Height - 1 - y),
                    _ => (Height - 1 - y, x),
                };
                result.Pixels[(ny * result.Width) + nx] = Pixels[(y * Width) + x];
            }
        }

        return result;
    }

    // imir (ISO/IEC 23008-12:2022 6.5.12): mode 0 exchanges top and bottom, mode 1 left and right, as libheif does.
    private HeifCanvas Mirror(bool topBottom)
    {
        var result = new HeifCanvas(Width, Height);
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                result.Pixels[(y * Width) + x] = topBottom
                    ? Pixels[((Height - 1 - y) * Width) + x]
                    : Pixels[(y * Width) + Width - 1 - x];
            }
        }

        return result;
    }
}
