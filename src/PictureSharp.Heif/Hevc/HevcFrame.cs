namespace PictureSharp.Formats.Heif.Hevc;

// A decoded picture: full coded planes plus the conformance cropping window (H.265 7.4.3.2.1).
internal sealed class HevcFrame
{
    public HevcFrame(HevcSequenceParameterSet sps)
    {
        Width = sps.Width;
        Height = sps.Height;
        ChromaFormatIdc = sps.ChromaFormatIdc;
        BitDepthLuma = sps.BitDepthLuma;
        BitDepthChroma = sps.BitDepthChroma;
        CropLeft = sps.ConformanceLeft * sps.SubWidthC;
        CropTop = sps.ConformanceTop * sps.SubHeightC;
        CroppedWidth = Width - (sps.SubWidthC * (sps.ConformanceLeft + sps.ConformanceRight));
        CroppedHeight = Height - (sps.SubHeightC * (sps.ConformanceTop + sps.ConformanceBottom));
        Colour = sps.Colour;
        int planes = ChromaFormatIdc == 0 ? 1 : 3;
        Planes = new ushort[planes][];
        Strides = new int[planes];
        Planes[0] = GC.AllocateUninitializedArray<ushort>(Width * Height);
        Strides[0] = Width;
        for (int c = 1; c < planes; c++)
        {
            Strides[c] = Width / sps.SubWidthC;
            Planes[c] = GC.AllocateUninitializedArray<ushort>(Strides[c] * (Height / sps.SubHeightC));
        }
    }

    public int Width { get; }
    public int Height { get; }
    public int ChromaFormatIdc { get; }
    public int BitDepthLuma { get; }
    public int BitDepthChroma { get; }
    public int CropLeft { get; }
    public int CropTop { get; }
    public int CroppedWidth { get; }
    public int CroppedHeight { get; }
    public HevcColourDescription? Colour { get; }
    public ushort[][] Planes { get; }
    public int[] Strides { get; }
}
