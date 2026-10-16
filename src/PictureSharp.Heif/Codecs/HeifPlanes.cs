using PictureSharp.Formats.Heif.Hevc;

namespace PictureSharp.Formats.Heif.Codecs;

// A YCbCr (4:2:0) or monochrome image: one decoded HEVC picture, or grid tiles assembled before colour conversion.
internal sealed class HeifPlanes
{
    private HeifPlanes(int width, int height, int chromaFormatIdc, int bitDepth, ushort[][] planes, int[] strides,
        int left, int top, HevcColourDescription? colour)
    {
        Width = width;
        Height = height;
        ChromaFormatIdc = chromaFormatIdc;
        BitDepth = bitDepth;
        Planes = planes;
        Strides = strides;
        Left = left;
        Top = top;
        Colour = colour;
    }

    public int Width { get; }
    public int Height { get; }
    public int ChromaFormatIdc { get; }
    public int BitDepth { get; }
    public ushort[][] Planes { get; }
    public int[] Strides { get; }

    // Origin of the visible image inside the luma plane (even for 4:2:0).
    public int Left { get; }
    public int Top { get; }
    public HevcColourDescription? Colour { get; }

    public static HeifPlanes FromFrame(HevcFrame frame)
    {
        if (frame.BitDepthChroma != frame.BitDepthLuma && frame.ChromaFormatIdc != 0)
        {
            throw new NotSupportedException("HEIF images with different luma and chroma bit depths are not supported.");
        }

        return new(frame.CroppedWidth, frame.CroppedHeight, frame.ChromaFormatIdc, frame.BitDepthLuma, frame.Planes,
            frame.Strides, frame.CropLeft, frame.CropTop, frame.Colour);
    }

    public static HeifPlanes Create(int width, int height, HeifPlanes format)
    {
        int planeCount = format.ChromaFormatIdc == 0 ? 1 : 3;
        var planes = new ushort[planeCount][];
        var strides = new int[planeCount];
        strides[0] = width;
        planes[0] = GC.AllocateUninitializedArray<ushort>(width * height);
        for (int c = 1; c < planeCount; c++)
        {
            strides[c] = (width + 1) >> 1;
            planes[c] = GC.AllocateUninitializedArray<ushort>(strides[c] * ((height + 1) >> 1));
        }

        return new(width, height, format.ChromaFormatIdc, format.BitDepth, planes, strides, 0, 0, format.Colour);
    }

    public bool HasSameFormat(HeifPlanes other) => ChromaFormatIdc == other.ChromaFormatIdc && BitDepth == other.BitDepth;

    // Copies a tile at an even position, cropped to this image.
    public void Paste(HeifPlanes tile, int x0, int y0)
    {
        int width = Math.Min(tile.Width, Width - x0);
        int height = Math.Min(tile.Height, Height - y0);
        for (int y = 0; y < height; y++)
        {
            tile.Planes[0].AsSpan(((tile.Top + y) * tile.Strides[0]) + tile.Left, width)
                .CopyTo(Planes[0].AsSpan(((y0 + y) * Strides[0]) + x0));
        }

        for (int c = 1; c < Planes.Length; c++)
        {
            int chromaWidth = (width + 1) >> 1;
            for (int y = 0; y < (height + 1) >> 1; y++)
            {
                tile.Planes[c].AsSpan((((tile.Top >> 1) + y) * tile.Strides[c]) + (tile.Left >> 1), chromaWidth)
                    .CopyTo(Planes[c].AsSpan((((y0 >> 1) + y) * Strides[c]) + (x0 >> 1)));
            }
        }
    }

    public ushort Luma(int x, int y) => Planes[0][((Top + y) * Strides[0]) + Left + x];

    public ushort Chroma(int c, int x, int y) => Planes[c][(((Top >> 1) + y) * Strides[c]) + (Left >> 1) + x];
}
