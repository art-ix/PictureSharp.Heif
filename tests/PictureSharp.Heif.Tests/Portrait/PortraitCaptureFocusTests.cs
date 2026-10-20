using System.Buffers.Binary;
using System.Drawing;
using static PictureSharp.Formats.Heif.Tests.HeifTestFile;

namespace PictureSharp.Formats.Heif.Tests;

public sealed class PortraitCaptureFocusTests
{
    [Test]
    [Arguments(true, 2)]
    [Arguments(true, 3)]
    [Arguments(true, 4)]
    [Arguments(false, 2)]
    [Arguments(false, 3)]
    [Arguments(false, 4)]
    public async Task Read_HandlesBothByteOrdersAndAllAreaShapes(bool little, int count)
    {
        PortraitCaptureFocus.Area area = PortraitCaptureFocus.Read(Tiff(little, count))!;
        await Assert.That(area.Centre).IsEqualTo(new Point(1, 2));
        await Assert.That(area.StoredSize).IsEqualTo(new Size(8, 6));
        await Assert.That(area.Values.Length).IsEqualTo(count);
    }

    [Test]
    public async Task Read_RejectsTruncationInvalidOffsetsShapesAndCoordinates()
    {
        byte[] complete = Tiff(true, 4);
        for (int length = 0; length < complete.Length; length++)
        {
            await Assert.That(PortraitCaptureFocus.Read(complete[..length])).IsNull();
        }
        await Assert.That(PortraitCaptureFocus.Read(null)).IsNull();
        foreach (int position in new[] { 4, 18, 36 })
        {
            byte[] bad = complete.ToArray();
            bad.AsSpan(position, 4).Fill(255);
            await Assert.That(PortraitCaptureFocus.Read(bad)).IsNull();
        }
        byte[] badShape = complete.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(badShape.AsSpan(32), uint.MaxValue);
        await Assert.That(PortraitCaptureFocus.Read(badShape)).IsNull();
        byte[] outside = complete.ToArray();
        BinaryPrimitives.WriteUInt16LittleEndian(outside.AsSpan(68), 8);
        await Assert.That(PortraitCaptureFocus.Read(outside)).IsNull();
        byte[] zeroSize = complete.ToArray();
        BinaryPrimitives.WriteUInt16LittleEndian(zeroSize.AsSpan(72), 0);
        await Assert.That(PortraitCaptureFocus.Read(zeroSize)).IsNull();
    }

    [Test]
    public async Task Map_UsesPixelCoordinatesForEveryRotationAndMirror()
    {
        var area = new PortraitCaptureFocus.Area(new Point(1, 2), new Size(8, 6), [1, 2]);
        Point[] expected = [new(1, 2), new(2, 6), new(6, 3), new(3, 1)];
        for (int q = 0; q < 4; q++)
        {
            Size size = (q & 1) == 0 ? new Size(8, 6) : new Size(6, 8);
            await Assert.That(PortraitCaptureFocus.Map(area, [Ispe(8, 6), Box("irot", [(byte)q])], size)).IsEqualTo((Point?)expected[q]);
            await Assert.That(PortraitCaptureFocus.Map(area, [Ispe(8, 6), Box("irot", [(byte)q]), Box("imir", [0])], size))
                .IsEqualTo((Point?)new Point(expected[q].X, size.Height - 1 - expected[q].Y));
            await Assert.That(PortraitCaptureFocus.Map(area, [Ispe(8, 6), Box("irot", [(byte)q]), Box("imir", [1])], size))
                .IsEqualTo((Point?)new Point(size.Width - 1 - expected[q].X, expected[q].Y));
        }
    }

    [Test]
    public async Task Map_PreservesAssociationOrderAndRejectsCroppedOrMismatchedPoints()
    {
        var area = new PortraitCaptureFocus.Area(new Point(2, 2), new Size(8, 6), [2, 2]);
        byte[] crop = Box("clap", Join(U32(6), U32(1), U32(4), U32(1), U32(0), U32(1), U32(0), U32(1)));
        byte[] turn = Box("irot", [3]), mirror = Box("imir", [1]);
        byte[][] props = [Ispe(8, 6), crop, turn, mirror];
        await Assert.That(PortraitCaptureFocus.Map(area, props, new Size(4, 6))).IsEqualTo((Point?)new Point(1, 1));
        await Assert.That(PortraitCaptureFocus.Map(area, props, new Size(2, 3))).IsEqualTo((Point?)new Point(0, 0));
        await Assert.That(PortraitCaptureFocus.Map(area, [Ispe(8, 6), turn, crop], new Size(6, 4))).IsEqualTo((Point?)new Point(3, 0));
        await Assert.That(PortraitCaptureFocus.Map(area with { Centre = Point.Empty }, props, new Size(4, 6))).IsNull();
        await Assert.That(PortraitCaptureFocus.Map(area with { StoredSize = new Size(6, 8) }, props, new Size(4, 6))).IsNull();
        await Assert.That(PortraitCaptureFocus.Map(area, props, new Size(4, 5))).IsNull();
    }

    internal static byte[] Tiff(bool little, int count)
    {
        byte[] tiff = new byte[count == 2 ? 68 : 68 + 2 * count];
        tiff[0] = tiff[1] = little ? (byte)'I' : (byte)'M';
        U16At(2, 42); U32At(4, 8); U16At(8, 1);
        Entry(10, 0x8769, 4, 1, 26);
        U16At(26, 3);
        Entry(28, 0x9214, 3, (uint)count, count == 2 ? 0u : 68u);
        Entry(40, 0xa002, 4, 1, 8);
        Entry(52, 0xa003, 3, 1, 0); U16At(60, 6);
        int data = count == 2 ? 36 : 68;
        U16At(data, 1); U16At(data + 2, 2);
        if (count >= 3) { U16At(data + 4, 3); }
        if (count == 4) { U16At(data + 6, 4); }
        return tiff;

        void Entry(int p, ushort tag, ushort type, uint length, uint value)
        { U16At(p, tag); U16At(p + 2, type); U32At(p + 4, length); U32At(p + 8, value); }
        void U16At(int p, ushort value)
        {
            if (little) { BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(p), value); }
            else { BinaryPrimitives.WriteUInt16BigEndian(tiff.AsSpan(p), value); }
        }
        void U32At(int p, uint value)
        {
            if (little) { BinaryPrimitives.WriteUInt32LittleEndian(tiff.AsSpan(p), value); }
            else { BinaryPrimitives.WriteUInt32BigEndian(tiff.AsSpan(p), value); }
        }
    }
}
