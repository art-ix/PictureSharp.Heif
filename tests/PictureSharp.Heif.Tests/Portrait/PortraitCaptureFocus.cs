using System.Buffers.Binary;
using System.Drawing;
using System.Text;
using System.Text.Json;

namespace PictureSharp.Formats.Heif.Tests;

// Experimental capture focus only. SubjectArea describes capture metering and may miss Photos' portrait subject.
internal static class PortraitCaptureFocus
{
    internal sealed record Area(Point Centre, Size StoredSize, ushort[] Values);
    internal sealed record Mapped(string Scene, int X, int Y, int Width, int Height, string OriginalSha256, string ExifSha256, string PropertiesSha256);

    public static Mapped[] ReadPoints(PortraitCatalog catalog)
    {
        return catalog.Scenes.Select(scene =>
        {
            byte[] bytes = File.ReadAllBytes(scene.Original);
            HeifDocument document = HeifDocument.Open(bytes);
            if (document.PrimaryImage.Type is not ("grid" or "hvc1")) { throw new NotSupportedException("Capture focus experiment supports direct/grid photographs only."); }
            byte[] exif = document.ReadMetadata(document.PrimaryImage, HeifPortraitAuditTests.Decode with { ApplyExifOrientation = false }).Exif ?? [];
            Area area = Read(exif) ?? throw new InvalidDataException(scene.Name + ": no valid capture area.");
            Size size = document.PrimaryImage.DisplaySize!.Value;
            byte[][] properties = HeifTestItems.ItemProperties(bytes, document.PrimaryImage.Id);
            Point point = Map(area, properties, size) ?? throw new InvalidDataException(scene.Name + ": cannot map capture area.");
            return new Mapped(scene.Name, point.X, point.Y, size.Width, size.Height, PortraitMeasurementRun.Hash(bytes),
                PortraitMeasurementRun.Hash(exif), PortraitMeasurementRun.Hash([.. properties.SelectMany(p => p)]));
        }).ToArray();
    }

    public static void VerifySavedPoints(string path, Mapped[] points)
    {
        using JsonDocument saved = JsonDocument.Parse(File.ReadAllText(path));
        foreach (Mapped point in points)
        {
            JsonElement expected = saved.RootElement.EnumerateArray().Single(p => p.GetProperty("Scene").GetString() == point.Scene);
            if (expected.GetProperty("X").GetInt32() != point.X || expected.GetProperty("Y").GetInt32() != point.Y
                || expected.GetProperty("Width").GetInt32() != point.Width || expected.GetProperty("Height").GetInt32() != point.Height)
            {
                throw new InvalidDataException(point.Scene + ": experiment point differs from the saved mapping.");
            }
        }
        if (saved.RootElement.GetArrayLength() != points.Length) { throw new InvalidDataException("Capture point count differs."); }
    }

    public static Area? Read(byte[]? tiff)
    {
        if (tiff is null || tiff.Length < 8) { return null; }
        bool little = tiff.AsSpan(0, 4).SequenceEqual("II*\0"u8);
        if (!little && !tiff.AsSpan(0, 4).SequenceEqual("MM\0*"u8)) { return null; }
        int pointer = Find(U32(4), 0x8769);
        if (pointer < 0 || U16(pointer + 2) != 4 || U32(pointer + 4) != 1) { return null; }
        uint directory = U32(pointer + 8);
        int area = Find(directory, 0x9214);
        if (area < 0 || U16(area + 2) != 3) { return null; }
        uint count = U32(area + 4);
        if (count is < 2 or > 4) { return null; }
        uint offset = count == 2 ? (uint)(area + 8) : U32(area + 8);
        if (offset < 8 || offset > tiff.Length || (long)offset + 2 * count > tiff.Length) { return null; }
        ushort[] values = new ushort[count];
        for (int i = 0; i < values.Length; i++) { values[i] = U16((int)offset + 2 * i); }
        int width = Dimension(0xa002), height = Dimension(0xa003);
        if (width <= 0 || height <= 0 || values[0] >= width || values[1] >= height
            || (count >= 3 && values[2] == 0) || (count == 4 && values[3] == 0)) { return null; }
        return new Area(new Point(values[0], values[1]), new Size(width, height), values);

        ushort U16(int p) => little ? BinaryPrimitives.ReadUInt16LittleEndian(tiff.AsSpan(p)) : BinaryPrimitives.ReadUInt16BigEndian(tiff.AsSpan(p));
        uint U32(int p) => little ? BinaryPrimitives.ReadUInt32LittleEndian(tiff.AsSpan(p)) : BinaryPrimitives.ReadUInt32BigEndian(tiff.AsSpan(p));
        int Find(uint start, ushort tag)
        {
            if (start < 8 || start > tiff.Length - 2) { return -1; }
            int n = U16((int)start), result = -1;
            if ((long)start + 2 + 12L * n + 4 > tiff.Length) { return -1; }
            for (int i = 0; i < n; i++)
            {
                int entry = (int)start + 2 + 12 * i;
                if (U16(entry) == tag)
                {
                    if (result >= 0) { return -1; }
                    result = entry;
                }
            }
            return result;
        }
        int Dimension(ushort tag)
        {
            int entry = Find(directory, tag);
            if (entry < 0 || U32(entry + 4) != 1) { return 0; }
            uint value = U16(entry + 2) switch { 3 => U16(entry + 8), 4 => U32(entry + 8), _ => 0 };
            return value <= int.MaxValue ? (int)value : 0;
        }
    }

    // Properties come from an already validated image, in ipma association order, never ipco storage order.
    public static Point? Map(Area area, byte[][] properties, Size target)
    {
        byte[]? ispe = properties.SingleOrDefault(p => Encoding.ASCII.GetString(p, 4, 4) == "ispe");
        if (ispe is null || ispe.Length < 20 || target.Width <= 0 || target.Height <= 0) { return null; }
        int width = (int)BinaryPrimitives.ReadUInt32BigEndian(ispe.AsSpan(12));
        int height = (int)BinaryPrimitives.ReadUInt32BigEndian(ispe.AsSpan(16));
        if (area.StoredSize != new Size(width, height)) { return null; }
        int x = area.Centre.X, y = area.Centre.Y;
        foreach (byte[] property in properties)
        {
            ReadOnlySpan<byte> p = property.AsSpan(8);
            switch (Encoding.ASCII.GetString(property, 4, 4))
            {
                case "irot":
                    int q = p[0] & 3;
                    (x, y) = q switch { 1 => (y, width - 1 - x), 2 => (width - 1 - x, height - 1 - y), 3 => (height - 1 - y, x), _ => (x, y) };
                    if ((q & 1) != 0) { (width, height) = (height, width); }
                    break;
                case "imir":
                    if ((p[0] & 1) == 0) { y = height - 1 - y; } else { x = width - 1 - x; }
                    break;
                case "clap":
                    uint wd = BinaryPrimitives.ReadUInt32BigEndian(p[4..]), hd = BinaryPrimitives.ReadUInt32BigEndian(p[12..]);
                    uint od = BinaryPrimitives.ReadUInt32BigEndian(p[20..]), yd = BinaryPrimitives.ReadUInt32BigEndian(p[28..]);
                    if (wd == 0 || hd == 0 || od == 0 || yd == 0) { return null; }
                    double w = BinaryPrimitives.ReadUInt32BigEndian(p) / (double)wd;
                    double h = BinaryPrimitives.ReadUInt32BigEndian(p[8..]) / (double)hd;
                    double left = (width - w) / 2 + BinaryPrimitives.ReadInt32BigEndian(p[16..]) / (double)od;
                    double top = (height - h) / 2 + BinaryPrimitives.ReadInt32BigEndian(p[24..]) / (double)yd;
                    if (w != Math.Truncate(w) || h != Math.Truncate(h) || left != Math.Truncate(left) || top != Math.Truncate(top)
                        || w <= 0 || h <= 0 || left < 0 || top < 0 || left + w > width || top + h > height) { return null; }
                    x -= (int)left; y -= (int)top; width = (int)w; height = (int)h;
                    break;
            }
            if (x < 0 || y < 0 || x >= width || y >= height) { return null; }
        }
        if ((long)target.Width * height != (long)target.Height * width) { return null; }
        return new Point(Math.Min(target.Width - 1, (int)((x + .5) * target.Width / width)),
            Math.Min(target.Height - 1, (int)((y + .5) * target.Height / height)));
    }
}
