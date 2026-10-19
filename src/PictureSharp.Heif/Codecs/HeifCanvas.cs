using PictureSharp.Formats.Heif.Container;

namespace PictureSharp.Formats.Heif.Codecs;

// A sample buffer with the container transforms (ISO/IEC 23008-12 6.5.10–6.5.12): straight-alpha RGBA for images
// (HeifRgbaCanvas), or raw luma samples for depth. Transforms move samples without changing their values.
internal sealed class HeifCanvas<T>(int width, int height) where T : unmanaged
{
    public int Width { get; } = width;
    public int Height { get; } = height;
    public T[] Pixels { get; } = GC.AllocateUninitializedArray<T>(width * height);

    public HeifCanvas<T> Apply(HeifTransform transform, ParallelOptions parallel) => transform switch
    {
        HeifCleanAperture clap => Crop(clap),
        HeifRotation rotation => Rotate(rotation.QuarterTurns, parallel),
        HeifMirror mirror => Mirror(mirror.TopBottom, parallel),
        _ => throw new ArgumentOutOfRangeException(nameof(transform)),
    };

    // clap: the integral window validated by the container.
    private HeifCanvas<T> Crop(HeifCleanAperture clap)
    {
        (int left, int top, int width, int height) = clap.Window(Width, Height);
        var result = new HeifCanvas<T>(width, height);
        for (int y = 0; y < height; y++)
        {
            Pixels.AsSpan(((top + y) * Width) + left, width).CopyTo(result.Pixels.AsSpan(y * width));
        }

        return result;
    }

    // Source rows read together by a quarter turn: each destination row gets a contiguous run of this many samples while
    // the source lines stay in cache for the next destination rows.
    private const int RotateBlock = 16;

    // irot: anti-clockwise rotation by quarterTurns * 90 degrees. A band of destination rows is a band of source
    // columns, transposed in blocks of RotateBlock source rows.
    private HeifCanvas<T> Rotate(int quarterTurns, ParallelOptions parallel)
    {
        if (quarterTurns == 0)
        {
            return this;
        }

        bool swap = (quarterTurns & 1) != 0;
        var result = new HeifCanvas<T>(swap ? Height : Width, swap ? Width : Height);
        int width = result.Width;
        HeifParallel.ForRows(result.Height, width, parallel, (start, end) =>
        {
            Span<T> destination = result.Pixels;
            switch (quarterTurns)
            {
                case 1:
                    // (x, y) moves to (y, Width - 1 - x).
                    for (int y0 = 0; y0 < Height; y0 += RotateBlock)
                    {
                        int y1 = Math.Min(y0 + RotateBlock, Height);
                        for (int ny = start; ny < end; ny++)
                        {
                            Span<T> row = destination.Slice((ny * width) + y0, y1 - y0);
                            int x = Width - 1 - ny;
                            for (int y = y0; y < y1; y++)
                            {
                                row[y - y0] = Pixels[(y * Width) + x];
                            }
                        }
                    }

                    break;
                case 2:
                    for (int ny = start; ny < end; ny++)
                    {
                        Span<T> row = destination.Slice(ny * width, width);
                        Pixels.AsSpan((Height - 1 - ny) * Width, Width).CopyTo(row);
                        row.Reverse();
                    }

                    break;
                default:
                    // (x, y) moves to (Height - 1 - y, x).
                    for (int y0 = 0; y0 < Height; y0 += RotateBlock)
                    {
                        int y1 = Math.Min(y0 + RotateBlock, Height);
                        for (int ny = start; ny < end; ny++)
                        {
                            Span<T> row = destination.Slice(ny * width, width);
                            for (int y = y0; y < y1; y++)
                            {
                                row[Height - 1 - y] = Pixels[(y * Width) + ny];
                            }
                        }
                    }

                    break;
            }
        });

        return result;
    }

    // imir (ISO/IEC 23008-12:2022 6.5.12): mode 0 exchanges top and bottom, mode 1 left and right, as libheif does.
    private HeifCanvas<T> Mirror(bool topBottom, ParallelOptions parallel)
    {
        var result = new HeifCanvas<T>(Width, Height);
        HeifParallel.ForRows(Height, Width, parallel, (start, end) =>
        {
            for (int y = start; y < end; y++)
            {
                Span<T> row = result.Pixels.AsSpan(y * Width, Width);
                Pixels.AsSpan((topBottom ? Height - 1 - y : y) * Width, Width).CopyTo(row);
                if (!topBottom)
                {
                    row.Reverse();
                }
            }
        });

        return result;
    }
}
