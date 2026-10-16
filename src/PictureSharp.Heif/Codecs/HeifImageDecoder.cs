using PictureSharp.Formats.Heif.Container;
using PictureSharp.Formats.Heif.Hevc;

namespace PictureSharp.Formats.Heif.Codecs;

// Renders the primary image of a validated container: HEVC items, grids and identity items, the container transforms
// in association order, auxiliary alpha and descriptive metadata.
internal sealed class HeifImageDecoder(HeifContainer container, ImageDecodeContext context)
{
    private const int MaxDepth = 32;

    private (int Primaries, int Transfer, int Matrix, bool FullRange)? colour;

    public Image Decode()
    {
        HeifItem primary = container.PrimaryItem;
        ImageInfo info = container.Info;
        context.ReservePixels(info.Width, info.Height, 1);
        Canvas canvas = Render(primary, null, alpha: false, 0);
        if (container.AlphaItem is { } alphaItem)
        {
            Canvas alpha = Render(alphaItem, null, alpha: true, 0);
            if (alpha.Width != canvas.Width || alpha.Height != canvas.Height)
            {
                throw HeifReader.Error("Alpha image dimensions do not match the primary image.");
            }

            Span<Rgba32> pixels = canvas.Pixels;
            for (int i = 0; i < pixels.Length; i++)
            {
                pixels[i].A = alpha.Pixels[i].R;
            }

            if (container.PremultipliedAlpha)
            {
                Unpremultiply(pixels);
            }
        }

        // An opaque background keeps Image.HasAlpha false for images without an alpha plane.
        Image image = container.AlphaItem is null
            ? new Image(canvas.Width, canvas.Height, new Rgba32(0, 0, 0, 255))
            : new Image(canvas.Width, canvas.Height);
        try
        {
            // CopyPixelDataFrom marks the image as having alpha; opaque pixels are written through the pixel view.
            if (container.AlphaItem is not null)
            {
                image.CopyPixelDataFrom(canvas.Pixels);
            }
            else
            {
                canvas.Pixels.CopyTo(image.GetPixelSpan());
            }

            if (!context.Options.IgnoreMetadata)
            {
                ApplyMetadata(image.Metadata, primary);
            }
        }
        catch
        {
            image.Dispose();
            throw;
        }

        return image;
    }

    private Canvas Render(HeifItem item, (int, int, int, bool)? inheritedNclx, bool alpha, int depth)
    {
        if (depth > MaxDepth)
        {
            throw HeifReader.Error("Derived image nesting exceeds the limit.");
        }

        context.ThrowIfCancellationRequested();
        (int Primaries, int Transfer, int Matrix, bool FullRange)? nclx = container.Nclx(item) ?? inheritedNclx;
        Canvas canvas;
        if (HeifContainer.IsCoded(item) || HeifContainer.IsGrid(item))
        {
            HeifPlanes planes = Assemble(item, depth);
            canvas = new Canvas(planes.Width, planes.Height);
            if (alpha)
            {
                HeifColorConverter.ConvertAlpha(planes, canvas.Pixels);
            }
            else
            {
                // Colour comes from colr nclx, else the HEVC VUI, else the HEVC defaults (unspecified matrix, limited range).
                (int Primaries, int Transfer, int Matrix, bool FullRange) description = nclx
                    ?? (planes.Colour is { } vui ? (vui.Primaries, vui.Transfer, vui.Matrix, vui.FullRange) : (2, 2, 2, false));
                colour ??= description;
                bool bilinear = planes.ChromaFormatIdc == 1 && HasOddCrop(item, planes.Width, planes.Height);
                var converter = new HeifColorConverter(planes.ChromaFormatIdc == 0 ? 6 : description.Matrix, description.FullRange);
                converter.Convert(planes, canvas.Pixels, bilinear, context.CancellationToken);
            }
        }
        else
        {
            canvas = Render(container.Source(item, 0), nclx, alpha, depth + 1);
        }

        foreach (HeifBox transform in container.Transforms(item))
        {
            canvas = transform.Type switch
            {
                0x636c6170 => canvas.Crop(transform.Data.Span),
                0x69726f74 => canvas.Rotate(transform.Data.Span[0] & 3),
                _ => canvas.Mirror(topBottom: (transform.Data.Span[0] & 1) == 0),
            };
        }

        return canvas;
    }

    // A decoded HEVC picture, or a grid of them assembled in YCbCr (libheif converts the assembled image).
    private HeifPlanes Assemble(HeifItem item, int depth)
    {
        if (HeifContainer.IsCoded(item))
        {
            HevcFrame frame = HevcDecoder.Decode(container.HevcNalUnits(item), null, context.CancellationToken);
            (int width, int height) = container.StoredSize(item);
            if (frame.CroppedWidth != width || frame.CroppedHeight != height)
            {
                throw HeifReader.Error("HEVC picture size does not match the image item's spatial extents.");
            }

            return HeifPlanes.FromFrame(frame);
        }

        if (!HeifContainer.IsGrid(item) || depth > MaxDepth)
        {
            throw new NotSupportedException("HEIF grids must consist of HEVC or grid image items.");
        }

        (int rows, int columns, int gridWidth, int gridHeight) = container.GridLayout(item);
        HeifPlanes? grid = null;
        int index = 0;
        int y = 0;
        for (int row = 0; row < rows; row++)
        {
            int x = 0;
            int tileHeight = 0;
            for (int column = 0; column < columns; column++)
            {
                HeifItem tileItem = container.Source(item, index++);
                if (container.Transforms(tileItem).Any())
                {
                    throw new NotSupportedException("Transformed HEIF grid tiles are not supported.");
                }

                HeifPlanes tile = Assemble(tileItem, depth + 1);
                grid ??= HeifPlanes.Create(gridWidth, gridHeight, tile);
                if (!grid.HasSameFormat(tile) || (grid.ChromaFormatIdc == 1 && ((tile.Width | tile.Height) & 1) != 0))
                {
                    throw HeifReader.Error("Grid tiles have inconsistent formats.");
                }

                grid.Paste(tile, x, y);
                x += tile.Width;
                tileHeight = tile.Height;
            }

            y += tileHeight;
        }

        return grid!;
    }

    // libheif crops 4:2:0 images at an odd column or row by upsampling chroma to 4:4:4 (bilinear) first.
    private bool HasOddCrop(HeifItem item, int width, int height)
    {
        foreach (HeifBox transform in container.Transforms(item))
        {
            ReadOnlySpan<byte> data = transform.Data.Span;
            if (transform.Type == 0x69726f74 && (data[0] & 1) != 0)
            {
                (width, height) = (height, width);
            }
            else if (transform.Type == 0x636c6170)
            {
                (int left, int top, int croppedWidth, int croppedHeight) = Canvas.CleanAperture(data, width, height);
                if (((left | top) & 1) != 0)
                {
                    return true;
                }

                (width, height) = (croppedWidth, croppedHeight);
            }
        }

        return false;
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

    private void ApplyMetadata(ImageMetadata metadata, HeifItem primary)
    {
        ReadOnlyMemory<byte>? icc = container.Icc(primary);
        if (icc is null && !HeifContainer.IsCoded(primary) && container.SourceCount(primary) > 0)
        {
            icc = container.Icc(container.Source(primary, 0));
        }

        if (icc is { Length: > 0 } profile)
        {
            metadata.SetBlock(MetadataKinds.Icc, profile.Span);
        }
        else if (colour is { } description)
        {
            HeifColorDescription.Apply(metadata, description.Primaries, description.Transfer, context.Options);
        }

        foreach ((bool exif, ReadOnlyMemory<byte> data) in container.DescriptiveMetadata(primary))
        {
            ReadOnlySpan<byte> span = data.Span;
            if (exif)
            {
                // ISO/IEC 23008-12 A.2.1: exif_tiff_header_offset precedes the TIFF header.
                if (span.Length < 4)
                {
                    continue;
                }

                uint offset = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(span);
                if (offset > (uint)(span.Length - 4))
                {
                    continue;
                }

                ReadOnlySpan<byte> tiff = span[(4 + (int)offset)..];
                if (tiff.StartsWith("II*\0"u8) || tiff.StartsWith("MM\0*"u8))
                {
                    metadata.SetBlock(MetadataKinds.Exif, tiff);
                    // irot/imir already oriented the pixels; the EXIF orientation is informative only.
                    metadata.TrySetOrientation(ExifOrientation.TopLeft);
                }
            }
            else
            {
                metadata.SetBlock(MetadataKinds.Xmp, span);
            }
        }
    }

    // A straight-alpha RGBA buffer with the container transforms (ISO/IEC 23008-12 6.5.10–6.5.12).
    private sealed class Canvas(int width, int height)
    {
        public int Width { get; } = width;
        public int Height { get; } = height;
        public Rgba32[] Pixels { get; } = GC.AllocateUninitializedArray<Rgba32>(width * height);

        // Tiles extending past the grid's output size are cropped.
        public void Paste(Canvas tile, int x0, int y0)
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
                v[i] = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(clap[(i * 4)..]);
            }

            int croppedWidth = (int)(v[0] / v[1]);
            int croppedHeight = (int)(v[2] / v[3]);
            long left = (((long)(width - croppedWidth) * v[5]) + (2L * (int)v[4])) / (2L * v[5]);
            long top = (((long)(height - croppedHeight) * v[7]) + (2L * (int)v[6])) / (2L * v[7]);
            return ((int)left, (int)top, croppedWidth, croppedHeight);
        }

        public Canvas Crop(ReadOnlySpan<byte> clap)
        {
            (int left, int top, int width, int height) = CleanAperture(clap, Width, Height);
            var result = new Canvas(width, height);
            for (int y = 0; y < height; y++)
            {
                Pixels.AsSpan((int)(((top + y) * Width) + left), width).CopyTo(result.Pixels.AsSpan(y * width));
            }

            return result;
        }

        // irot: anti-clockwise rotation by quarterTurns * 90 degrees.
        public Canvas Rotate(int quarterTurns)
        {
            if (quarterTurns == 0)
            {
                return this;
            }

            bool swap = (quarterTurns & 1) != 0;
            var result = new Canvas(swap ? Height : Width, swap ? Width : Height);
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
        public Canvas Mirror(bool topBottom)
        {
            var result = new Canvas(Width, Height);
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
}
