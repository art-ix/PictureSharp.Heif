using System.Buffers.Binary;

namespace PictureSharp.Formats.Heif.Container;

// A transformative item property (ISO/IEC 23008-12 6.5.9–6.5.12). Images apply them in association order; the
// container validated their fields before any is read.
internal abstract record HeifTransform
{
    public abstract uint Type { get; }

    public static HeifTransform Read(HeifBox property) => property.Type switch
    {
        HeifFourCc.Irot => new HeifRotation(property.Data.Span[0] & 3),
        HeifFourCc.Imir => new HeifMirror(TopBottom: (property.Data.Span[0] & 1) == 0),
        HeifFourCc.Clap => HeifCleanAperture.Read(property.Data.Span),
        _ => throw new ArgumentOutOfRangeException(nameof(property), "Not a transformative property."),
    };

    public static bool IsTransform(uint type) => type is HeifFourCc.Irot or HeifFourCc.Imir or HeifFourCc.Clap;

    // The property payload as written.
    public abstract byte[] Payload();
}

// irot: anti-clockwise rotation by QuarterTurns * 90 degrees.
internal sealed record HeifRotation(int QuarterTurns) : HeifTransform
{
    public override uint Type => HeifFourCc.Irot;

    public bool SwapsDimensions => (QuarterTurns & 1) != 0;

    public override byte[] Payload() => [(byte)QuarterTurns];
}

// imir (ISO/IEC 23008-12:2022 6.5.12): axis 0 exchanges top and bottom, axis 1 left and right, as libheif does.
internal sealed record HeifMirror(bool TopBottom) : HeifTransform
{
    public override uint Type => HeifFourCc.Imir;

    public override byte[] Payload() => [TopBottom ? (byte)0 : (byte)1];
}

// clap (ISO/IEC 14496-12 12.1.4): a window of WidthN/WidthD by HeightN/HeightD whose centre is offset from the image
// centre by HorizontalOffsetN/HorizontalOffsetD and VerticalOffsetN/VerticalOffsetD.
internal sealed record HeifCleanAperture(uint WidthN, uint WidthD, uint HeightN, uint HeightD,
    int HorizontalOffsetN, uint HorizontalOffsetD, int VerticalOffsetN, uint VerticalOffsetD) : HeifTransform
{
    public override uint Type => HeifFourCc.Clap;

    public static HeifCleanAperture Read(ReadOnlySpan<byte> data) => new(
        BinaryPrimitives.ReadUInt32BigEndian(data), BinaryPrimitives.ReadUInt32BigEndian(data[4..]),
        BinaryPrimitives.ReadUInt32BigEndian(data[8..]), BinaryPrimitives.ReadUInt32BigEndian(data[12..]),
        BinaryPrimitives.ReadInt32BigEndian(data[16..]), BinaryPrimitives.ReadUInt32BigEndian(data[20..]),
        BinaryPrimitives.ReadInt32BigEndian(data[24..]), BinaryPrimitives.ReadUInt32BigEndian(data[28..]));

    // A width x height window at the top-left corner of a coded picture: offsets relative to the centre,
    // (width - codedWidth) / 2, as fractions with denominator 2.
    public static HeifCleanAperture TopLeft(int codedWidth, int codedHeight, int width, int height) =>
        new((uint)width, 1, (uint)height, 1, width - codedWidth, 2, height - codedHeight, 2);

    // The integral window inside an image of the given size; the container checked that it is integral and inside.
    public (int Left, int Top, int Width, int Height) Window(int width, int height)
    {
        int croppedWidth = (int)(WidthN / WidthD);
        int croppedHeight = (int)(HeightN / HeightD);
        long left = (((long)(width - croppedWidth) * HorizontalOffsetD) + (2L * HorizontalOffsetN)) / (2L * HorizontalOffsetD);
        long top = (((long)(height - croppedHeight) * VerticalOffsetD) + (2L * VerticalOffsetN)) / (2L * VerticalOffsetD);
        return ((int)left, (int)top, croppedWidth, croppedHeight);
    }

    public override byte[] Payload()
    {
        byte[] payload = new byte[32];
        Span<byte> span = payload;
        BinaryPrimitives.WriteUInt32BigEndian(span, WidthN);
        BinaryPrimitives.WriteUInt32BigEndian(span[4..], WidthD);
        BinaryPrimitives.WriteUInt32BigEndian(span[8..], HeightN);
        BinaryPrimitives.WriteUInt32BigEndian(span[12..], HeightD);
        BinaryPrimitives.WriteInt32BigEndian(span[16..], HorizontalOffsetN);
        BinaryPrimitives.WriteUInt32BigEndian(span[20..], HorizontalOffsetD);
        BinaryPrimitives.WriteInt32BigEndian(span[24..], VerticalOffsetN);
        BinaryPrimitives.WriteUInt32BigEndian(span[28..], VerticalOffsetD);
        return payload;
    }
}
