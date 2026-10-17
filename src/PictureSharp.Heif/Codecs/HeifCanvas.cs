using System.Buffers.Binary;

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

    // Tiles extending past the grid's output size are cropped.
    public void Paste(HeifCanvas tile, int x0, int y0)
    {
        int width = Math.Min(tile.Width, Width - x0);
        int height = Math.Min(tile.Height, Height - y0);
        for (int y = 0; y < height; y++)
        {
            tile.Pixels.AsSpan(y * tile.Width, width).CopyTo(Pixels.AsSpan(((y0 + y) * Width) + x0));
        }
    }

    // clap: the integral window validated by the container (ISO/IEC 23008-12 6.5.9).
    public static (int Left, int Top, int Width, int Height) CleanAperture(ReadOnlySpan<byte> clap, int width, int height)
    {
        Span<uint> v = stackalloc uint[8];
        for (int i = 0; i < 8; i++)
        {
            v[i] = BinaryPrimitives.ReadUInt32BigEndian(clap[(i * 4)..]);
        }

        int croppedWidth = (int)(v[0] / v[1]);
        int croppedHeight = (int)(v[2] / v[3]);
        long left = (((long)(width - croppedWidth) * v[5]) + (2L * (int)v[4])) / (2L * v[5]);
        long top = (((long)(height - croppedHeight) * v[7]) + (2L * (int)v[6])) / (2L * v[7]);
        return ((int)left, (int)top, croppedWidth, croppedHeight);
    }

    public HeifCanvas Crop(ReadOnlySpan<byte> clap)
    {
        (int left, int top, int width, int height) = CleanAperture(clap, Width, Height);
        var result = new HeifCanvas(width, height);
        for (int y = 0; y < height; y++)
        {
            Pixels.AsSpan((int)(((top + y) * Width) + left), width).CopyTo(result.Pixels.AsSpan(y * width));
        }

        return result;
    }

    // irot: anti-clockwise rotation by quarterTurns * 90 degrees.
    public HeifCanvas Rotate(int quarterTurns)
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
    public HeifCanvas Mirror(bool topBottom)
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
