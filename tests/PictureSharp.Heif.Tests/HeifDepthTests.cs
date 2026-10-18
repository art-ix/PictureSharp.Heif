using System.Buffers.Binary;
using System.Diagnostics;
using PictureSharp.Formats.Heif.Hevc;
using static PictureSharp.Formats.Heif.Tests.HeifTestFile;
using static PictureSharp.Formats.Heif.Tests.HeifDocumentFixture;

namespace PictureSharp.Formats.Heif.Tests;

// Raw depth samples: fixtures copy coded items from the corpus under a depth auxC. Expected samples come from an
// independent path: the test-side item extraction, HevcDecoder luma (or HM and libde265 output in the Oracle test),
// grid assembly and transforms written here.
public sealed class HeifDepthTests
{
    private const string DepthType = "urn:mpeg:hevc:2015:auxid:2";
    private static readonly ImageConfiguration Configuration = ImageConfiguration.Default.ToBuilder().AddFormat(new HeifFormat()).Build();

    public static IEnumerable<string> Cases() => ["mono10", "mono8-clap", "grid-rotated-mirrored", "yuv10-clap-rotated-mirrored"];

    private sealed record Plane(int Width, int Height, ushort[] Samples);

    private sealed record Case(byte[] Bytes, uint DepthId, int BitDepth, HeifAuxiliaryKind Kind, Func<Func<uint, Plane>, Plane> Expected);

    [Test]
    [MethodDataSource(nameof(Cases))]
    public async Task LoadDepth_ReturnsRawSamples_MatchingIndependentDecode(string name)
    {
        Case fixture = Build(name);
        HeifDocument doc = HeifDocument.Open(fixture.Bytes);
        HeifItemInfo item = doc.GetItem(fixture.DepthId);
        await Assert.That(doc.PrimaryImage.AuxiliaryImages).Contains(item);
        HeifDepthImage depth = doc.LoadDepth(item);
        Dictionary<uint, List<ReadOnlyMemory<byte>>> items = HeifTestItems.Extract(fixture.Bytes).ToDictionary(pair => pair.Id, pair => pair.Nals);
        Plane expected = fixture.Expected(id => Luma(HevcDecoder.Decode(items[id], null, CancellationToken.None)));

        await Assert.That((depth.Width, depth.Height)).IsEqualTo((expected.Width, expected.Height));
        await Assert.That(item.DisplaySize).IsEqualTo(new System.Drawing.Size(depth.Width, depth.Height));
        await Assert.That(depth.BitDepth).IsEqualTo(fixture.BitDepth);
        await Assert.That(depth.Samples.ToArray()).IsEquivalentTo(expected.Samples, CollectionOrdering.Matching);
        await Assert.That(depth.ItemId).IsEqualTo(fixture.DepthId);
        await Assert.That(depth.PhotoItemId).IsEqualTo(1u);
        await Assert.That(depth.Auxiliary).IsSameReferenceAs(item.Auxiliary!);
        await Assert.That(depth.Auxiliary.Kind).IsEqualTo(fixture.Kind);
        await Assert.That(depth.Auxiliary.Subtype.ToArray()).IsEquivalentTo(new byte[] { 0x5a, 0, 7 }, CollectionOrdering.Matching);
        await Assert.That(depth.Exif.IsEmpty && depth.Xmp.IsEmpty).IsTrue();
        if (fixture.BitDepth == 10)
        {
            await Assert.That(depth.Samples.ToArray().Max()).IsGreaterThan((ushort)255).Because("10-bit samples are not reduced");
        }

        HeifDepthImage sequential = doc.LoadDepth(item, new DecoderOptions { MaxDegreeOfParallelism = 1 });
        await Assert.That(sequential.Samples.ToArray()).IsEquivalentTo(expected.Samples, CollectionOrdering.Matching);
    }

    [Test]
    [Property("Category", "Oracle")]
    [MethodDataSource(nameof(Cases))]
    public async Task LoadDepth_MatchesHmAndLibde265(string name)
    {
        string tools = Path.Combine(Environment.GetEnvironmentVariable("PICTURESHARP_HEIF_TOOLS") ?? Path.Combine(Root(), "reference", "tools"), "bin");
        string hm = Path.Combine(tools, "TAppDecoder"), dec265 = Path.Combine(tools, "dec265");
        Skip.Unless(System.IO.File.Exists(hm) && System.IO.File.Exists(dec265), "Build TAppDecoder and dec265 with scripts/build-heif-reference.sh.");
        Case fixture = Build(name);
        Dictionary<uint, List<ReadOnlyMemory<byte>>> items = HeifTestItems.Extract(fixture.Bytes).ToDictionary(pair => pair.Id, pair => pair.Nals);
        string work = Directory.CreateTempSubdirectory("heif-depth-oracle-").FullName;
        try
        {
            var decoded = new Dictionary<(string Tool, uint Id), Plane>();
            foreach ((uint id, List<ReadOnlyMemory<byte>> nals) in items)
            {
                string stream = Path.Combine(work, id + ".hevc");
                System.IO.File.WriteAllBytes(stream, HeifTestItems.ToAnnexB(nals));
                // Our decoder supplies only the cropped size and bit depth needed to read the reference YUV files.
                HevcFrame frame = HevcDecoder.Decode(nals, null, CancellationToken.None);
                string hmOutput = Path.Combine(work, id + ".hm.yuv"), libde265Output = Path.Combine(work, id + ".de265.yuv");
                await Run(hm, "-b", stream, "-o", hmOutput, "-d", "0");
                await Run(dec265, "-q", "-t", "0", "-0", "-o", libde265Output, stream);
                decoded[("hm", id)] = YuvLuma(System.IO.File.ReadAllBytes(hmOutput), frame);
                decoded[("de265", id)] = YuvLuma(System.IO.File.ReadAllBytes(libde265Output), frame);
            }

            HeifDocument doc = HeifDocument.Open(fixture.Bytes);
            ushort[] actual = doc.LoadDepth(doc.GetItem(fixture.DepthId)).Samples.ToArray();
            foreach (string tool in new[] { "hm", "de265" })
            {
                Plane expected = fixture.Expected(id => decoded[(tool, id)]);
                await Assert.That(actual).IsEquivalentTo(expected.Samples, CollectionOrdering.Matching).Because(tool);
            }
        }
        finally
        {
            Directory.Delete(work, true);
        }
    }

    [Test]
    public async Task LoadDepth_ReturnsMetadataOfTheDepthItem_UnlessIgnored()
    {
        byte[] xmp = "<x:xmpmeta><depth/></x:xmpmeta>"u8.ToArray(), photoXmp = "<photo/>"u8.ToArray();
        Item depth = Depth(2, "x265-gray8.heic", 1, HeifAuxiliaryKind.Depth);
        byte[] bytes = HeifDocumentFixture.Build(Encoded(1, 80, 60), depth,
            new Item(3, "mime", [], xmp, References: [("cdsc", [2])]), new Item(4, "mime", [], photoXmp, References: [("cdsc", [1])]));
        HeifDocument doc = HeifDocument.Open(bytes);
        HeifDepthImage withMetadata = doc.LoadDepth(doc.GetItem(2));
        await Assert.That(withMetadata.Xmp.ToArray()).IsEquivalentTo(xmp, CollectionOrdering.Matching);
        await Assert.That(withMetadata.Exif.IsEmpty).IsTrue();
        await Assert.That(doc.LoadDepth(doc.GetItem(2), new DecoderOptions { IgnoreMetadata = true }).Xmp.IsEmpty).IsTrue();
        await Assert.That(() => doc.LoadDepth(doc.GetItem(2), new DecoderOptions { MaxMetadataBytes = xmp.Length - 1 })).Throws<ImageFormatException>();
        HeifDepthImage limited = doc.LoadDepth(doc.GetItem(2), new DecoderOptions { MaxMetadataBytes = xmp.Length });
        await Assert.That(limited.Xmp.Length).IsEqualTo(xmp.Length);
    }

    [Test]
    public async Task LoadDepth_ChecksPixelLimitBeforeDecoding_AndObservesCancellation()
    {
        Case fixture = Build("grid-rotated-mirrored");
        HeifDocument doc = HeifDocument.Open(fixture.Bytes);
        HeifItemInfo item = doc.GetItem(fixture.DepthId);
        // The grid and both tiles are charged: the tiles alone cover more than the displayed size.
        long displayed = (long)item.DisplaySize!.Value.Width * item.DisplaySize.Value.Height;
        await Assert.That(() => doc.LoadDepth(item, new DecoderOptions { MaxPixels = displayed })).Throws<ImageFormatException>();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.That(() => doc.LoadDepth(item, null, cancellation.Token)).Throws<OperationCanceledException>();
    }

    [Test]
    public async Task LoadDepth_RejectsOtherItems_AndReportsCorruptOrUnsupportedDepth()
    {
        byte[] alpha = System.IO.File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "corpus", "x265-rgba8.heic"));
        HeifDocument withAlpha = HeifDocument.Open(alpha);
        await Assert.That(() => withAlpha.LoadDepth(withAlpha.PrimaryImage)).Throws<ArgumentException>();
        await Assert.That(() => withAlpha.LoadDepth(withAlpha.GetItem(2))).Throws<ArgumentException>();
        Case fixture = Build("mono10");
        HeifDocument other = HeifDocument.Open(fixture.Bytes);
        await Assert.That(() => HeifDocument.Open(fixture.Bytes).LoadDepth(other.GetItem(fixture.DepthId))).Throws<ArgumentException>();
        await Assert.That(() => other.LoadDepth(null!)).Throws<ArgumentNullException>();

        Item depth = Depth(2, "x265-gray8.heic", 1, HeifAuxiliaryKind.Depth);
        byte[] truncated = HeifDocumentFixture.Build(Encoded(1, 80, 60), depth with { Payload = depth.Payload![..(depth.Payload!.Length / 2)] });
        HeifDocument corrupt = HeifDocument.Open(truncated);
        await Assert.That(() => corrupt.LoadDepth(corrupt.GetItem(2))).Throws<ImageFormatException>();

        byte[] av1 = HeifDocumentFixture.Build(Encoded(1, 80, 60), new Item(2, "av01", [Ispe(8, 8), Auxiliary(DepthType)], References: [("auxl", [1])]));
        HeifDocument unsupported = HeifDocument.Open(av1);
        await Assert.That(unsupported.GetItem(2).Auxiliary!.Kind).IsEqualTo(HeifAuxiliaryKind.Depth);
        await Assert.That(() => unsupported.LoadDepth(unsupported.GetItem(2))).Throws<NotSupportedException>();
    }

    [Test]
    public async Task LoadDepth_WithoutAuxlReference_HasNoPhoto_AndLoadImageStillWorks()
    {
        Item depth = Depth(2, "x265-gray8.heic", 1, HeifAuxiliaryKind.Disparity) with { References = [] };
        HeifDocument doc = HeifDocument.Open(HeifDocumentFixture.Build(Encoded(1, 80, 60), depth));
        HeifDepthImage disparity = doc.LoadDepth(doc.GetItem(2));
        await Assert.That(disparity.PhotoItemId).IsNull();
        await Assert.That(disparity.Auxiliary.Kind).IsEqualTo(HeifAuxiliaryKind.Disparity);
        using Image image = doc.LoadImage(doc.GetItem(2), new DecoderOptions { Configuration = Configuration });
        await Assert.That((image.Width, image.Height)).IsEqualTo((disparity.Width, disparity.Height));
    }

    // Private iPhone Portrait photos (samples/, never committed): every depth or disparity item must equal the luma HM and
    // libde265 decode for its coded items, assembled and transformed by the test-side code from the item's own boxes.
    [Test]
    [Property("Category", "Local")]
    public async Task PrivateSamples_DepthMatchesHmAndLibde265()
    {
        string root = Root(), samples = Path.Combine(root, "samples");
        string tools = Path.Combine(Environment.GetEnvironmentVariable("PICTURESHARP_HEIF_TOOLS") ?? Path.Combine(root, "reference", "tools"), "bin");
        string hm = Path.Combine(tools, "TAppDecoder"), dec265 = Path.Combine(tools, "dec265");
        string[] photos = Directory.Exists(samples)
            ? [.. Directory.GetFiles(samples, "*", SearchOption.AllDirectories)
                .Where(path => Path.GetExtension(path).ToLowerInvariant() is ".heic" or ".heif").Order(StringComparer.Ordinal)]
            : [];
        var withDepth = new List<(string Path, byte[] Bytes, HeifDocument Document, HeifItemInfo[] Items)>();
        foreach (string path in photos)
        {
            byte[] bytes = System.IO.File.ReadAllBytes(path);
            HeifDocument document = HeifDocument.Open(bytes);
            HeifItemInfo[] items = [.. document.Items.Where(item => item.Auxiliary?.Kind is HeifAuxiliaryKind.Depth or HeifAuxiliaryKind.Disparity)];
            if (items.Length > 0)
            {
                withDepth.Add((path, bytes, document, items));
            }
        }

        Skip.Unless(withDepth.Count > 0, "No private HEIC photos with depth in samples/.");
        Skip.Unless(System.IO.File.Exists(hm) && System.IO.File.Exists(dec265), "Build TAppDecoder and dec265 with scripts/build-heif-reference.sh.");
        string work = Directory.CreateTempSubdirectory("heif-depth-local-").FullName;
        try
        {
            foreach ((string path, byte[] bytes, HeifDocument document, HeifItemInfo[] items) in withDepth)
            {
                Dictionary<uint, List<ReadOnlyMemory<byte>>> coded = HeifTestItems.Extract(bytes).ToDictionary(pair => pair.Id, pair => pair.Nals);
                foreach (HeifItemInfo item in items)
                {
                    string label = $"{Path.GetFileName(path)} item {item.Id}";
                    HeifDepthImage depth = document.LoadDepth(item);
                    var decoded = new Dictionary<(string Tool, uint Id), Plane>();
                    foreach (uint id in CodedItems(document, item))
                    {
                        string stream = Path.Combine(work, "item.hevc"), hmOutput = Path.Combine(work, "hm.yuv"), libde265Output = Path.Combine(work, "de265.yuv");
                        System.IO.File.WriteAllBytes(stream, HeifTestItems.ToAnnexB(coded[id]));
                        HevcFrame frame = HevcDecoder.Decode(coded[id], null, CancellationToken.None);
                        await Run(hm, "-b", stream, "-o", hmOutput, "-d", "0");
                        await Run(dec265, "-q", "-t", "0", "-0", "-o", libde265Output, stream);
                        decoded[("hm", id)] = YuvLuma(System.IO.File.ReadAllBytes(hmOutput), frame);
                        decoded[("de265", id)] = YuvLuma(System.IO.File.ReadAllBytes(libde265Output), frame);
                    }

                    foreach (string tool in new[] { "hm", "de265" })
                    {
                        Plane expected = Render(bytes, document, item, id => decoded[(tool, id)]);
                        await Assert.That((depth.Width, depth.Height)).IsEqualTo((expected.Width, expected.Height)).Because(label);
                        await Assert.That(depth.Samples.ToArray()).IsEquivalentTo(expected.Samples, CollectionOrdering.Matching).Because($"{label} vs {tool}");
                    }

                    await Assert.That(item.DisplaySize).IsEqualTo(new System.Drawing.Size(depth.Width, depth.Height)).Because(label);
                    await Assert.That(depth.BitDepth).IsEqualTo(item.BitsPerChannel!.Value).Because(label);
                    await Assert.That(depth.PhotoItemId).IsEqualTo(item.References.First(reference => reference.Type == HeifReferenceTypes.Auxiliary).TargetItemIds[0]);
                    await Assert.That(depth.Auxiliary.Subtype.ToArray()).IsEquivalentTo(item.Auxiliary!.Subtype.ToArray(), CollectionOrdering.Matching);
                    await Assert.That(depth.Xmp.IsEmpty).IsEqualTo(!item.MetadataItems.Any(metadata => metadata.Type == HeifItemTypes.Mime)).Because(label);
                    Console.WriteLine($"{label}: {depth.Auxiliary.Type} {depth.Width}x{depth.Height} {depth.BitDepth}-bit, XMP {depth.Xmp.Length} B, " +
                        $"samples {depth.Samples.ToArray().Min()}–{depth.Samples.ToArray().Max()}");
                }
            }
        }
        finally
        {
            Directory.Delete(work, true);
        }
    }

    // The hvc1 items a derived item (grid, iden) decodes from, through the document's dimg relationships.
    private static IEnumerable<uint> CodedItems(HeifDocument document, HeifItemInfo item) =>
        item.Type == HeifItemTypes.Hevc ? [item.Id] : item.SourceImages.SelectMany(source => CodedItems(document, source));

    // Test-side rendering of raw samples from the item's own boxes: grid header, dimg sources, then clap, irot and imir in
    // association order.
    private static Plane Render(byte[] file, HeifDocument document, HeifItemInfo item, Func<uint, Plane> luma)
    {
        Plane plane;
        switch (item.Type)
        {
            case "hvc1":
                plane = luma(item.Id);
                break;
            case "iden":
                plane = Render(file, document, item.SourceImages[0], luma);
                break;
            case "grid":
            {
                byte[] header = HeifTestItems.Payloads(file).Single(payload => payload.Id == item.Id).Data;
                bool wide = (header[1] & 1) != 0;
                int width = wide ? (int)BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(4)) : BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(4));
                int height = wide ? (int)BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(8)) : BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(6));
                plane = Assemble([.. item.SourceImages.Select(tile => Render(file, document, tile, luma))], header[3] + 1, header[2] + 1, width, height);
                break;
            }
            default:
                throw new NotSupportedException(item.Type);
        }

        foreach (byte[] box in HeifTestItems.ItemProperties(file, item.Id))
        {
            ReadOnlySpan<byte> data = box.AsSpan(8);
            switch (System.Text.Encoding.ASCII.GetString(box, 4, 4))
            {
                case "irot":
                    plane = Rotate(plane, data[0] & 3);
                    break;
                case "imir":
                    plane = (data[0] & 1) == 0 ? MirrorTopBottom(plane) : MirrorLeftRight(plane);
                    break;
                case "clap":
                {
                    uint widthN = BinaryPrimitives.ReadUInt32BigEndian(data), widthD = BinaryPrimitives.ReadUInt32BigEndian(data[4..]);
                    uint heightN = BinaryPrimitives.ReadUInt32BigEndian(data[8..]), heightD = BinaryPrimitives.ReadUInt32BigEndian(data[12..]);
                    int horizontalN = BinaryPrimitives.ReadInt32BigEndian(data[16..]), verticalN = BinaryPrimitives.ReadInt32BigEndian(data[24..]);
                    uint horizontalD = BinaryPrimitives.ReadUInt32BigEndian(data[20..]), verticalD = BinaryPrimitives.ReadUInt32BigEndian(data[28..]);
                    int width = (int)(widthN / widthD), height = (int)(heightN / heightD);
                    // Centre offset: left = (W - w) / 2 + horizontalN / horizontalD.
                    int left = (int)((((long)(plane.Width - width) * horizontalD) + (2L * horizontalN)) / (2L * horizontalD));
                    int top = (int)((((long)(plane.Height - height) * verticalD) + (2L * verticalN)) / (2L * verticalD));
                    plane = Crop(plane, left, top, width, height);
                    break;
                }
            }
        }

        return plane;
    }

    private static Case Build(string name)
    {
        Item photo = Encoded(1, 80, 60);
        switch (name)
        {
            case "mono10":
                return new(HeifDocumentFixture.Build(photo, Depth(2, "x265-rgba10.heic", 2, HeifAuxiliaryKind.Depth)), 2, 10,
                    HeifAuxiliaryKind.Depth, luma => luma(2));
            case "mono8-clap":
                // The corpus item's own clap crops the coded 80x64 picture to its top 80x60.
                return new(HeifDocumentFixture.Build(photo, Depth(2, "x265-gray8.heic", 1, HeifAuxiliaryKind.Depth)), 2, 8,
                    HeifAuxiliaryKind.Depth, luma => Crop(luma(2), 0, 0, 80, 60));
            case "grid-rotated-mirrored":
            {
                byte[] grid = Corpus("grid-2x1.heic");
                List<(uint Id, string Type, byte[] Data)> payloads = HeifTestItems.Payloads(grid);
                byte[] header = payloads.Single(item => item.Id == 1).Data;
                int columns = header[3] + 1, rows = header[2] + 1;
                int width = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(4)), height = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(6));
                Item depth = new(2, "grid", [.. HeifTestItems.ItemProperties(grid, 1), Auxiliary(DepthType, 0x5a, 0, 7), Box("irot", [1]), Box("imir", [1])],
                    header, References: [("auxl", [1]), ("dimg", [3, 4])]);
                Item Tile(int id, uint source) => new(id, Properties: HeifTestItems.ItemProperties(grid, source),
                    Payload: payloads.Single(item => item.Id == source).Data, Hidden: true);
                return new(HeifDocumentFixture.Build(photo, depth, Tile(3, 2), Tile(4, 3)), 2, 8, HeifAuxiliaryKind.Depth,
                    luma => MirrorLeftRight(Rotate(Assemble([luma(3), luma(4)], columns, rows, width, height), 1)));
            }
            case "yuv10-clap-rotated-mirrored":
            {
                // An odd left/top clap window on 4:2:0: chroma never constrains the luma crop.
                Item depth = Depth(2, "x265-bt709-full10.heic", 1, HeifAuxiliaryKind.Disparity);
                (int codedWidth, int codedHeight) = (96, 64);
                (int left, int top, int width, int height) = (23, 11, 51, 37);
                byte[] clap = Box("clap", U32((uint)width), U32(1), U32((uint)height), U32(1),
                    U32((uint)(2 * left - (codedWidth - width))), U32(2), U32((uint)(2 * top - (codedHeight - height))), U32(2));
                depth = depth with { Properties = [.. depth.Properties!, clap, Box("irot", [3]), Box("imir", [0])] };
                return new(HeifDocumentFixture.Build(photo, depth), 2, 10, HeifAuxiliaryKind.Disparity,
                    luma => MirrorTopBottom(Rotate(Crop(luma(2), left, top, width, height), 3)));
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(name));
        }
    }

    // A coded corpus item under a depth or disparity auxC (subtype 5a 00 07), referring to item 1.
    private static Item Depth(int id, string file, uint source, HeifAuxiliaryKind kind)
    {
        byte[] bytes = Corpus(file);
        string type = kind == HeifAuxiliaryKind.Disparity ? "urn:com:apple:photo:2018:aux:disparity" : DepthType;
        byte[][] properties = [.. HeifTestItems.ItemProperties(bytes, source).Where(box => !IsAuxC(box)), Auxiliary(type, 0x5a, 0, 7)];
        return new Item(id, Properties: properties, Payload: HeifTestItems.Payloads(bytes).Single(item => item.Id == source).Data,
            References: [("auxl", [1])]);
    }

    private static bool IsAuxC(byte[] box) => box.AsSpan(4, 4).SequenceEqual("auxC"u8);

    private static byte[] Corpus(string file) => System.IO.File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "corpus", file));

    private static Plane Luma(HevcFrame frame)
    {
        var samples = new ushort[frame.CroppedWidth * frame.CroppedHeight];
        for (int y = 0; y < frame.CroppedHeight; y++)
        {
            frame.Planes[0].AsSpan(((frame.CropTop + y) * frame.Strides[0]) + frame.CropLeft, frame.CroppedWidth)
                .CopyTo(samples.AsSpan(y * frame.CroppedWidth));
        }

        return new(frame.CroppedWidth, frame.CroppedHeight, samples);
    }

    // The luma plane at the start of a cropped reference YUV file: bytes for 8-bit, little-endian 16-bit otherwise.
    private static Plane YuvLuma(byte[] yuv, HevcFrame frame)
    {
        var samples = new ushort[frame.CroppedWidth * frame.CroppedHeight];
        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] = frame.BitDepthLuma == 8 ? yuv[i] : BinaryPrimitives.ReadUInt16LittleEndian(yuv.AsSpan(2 * i));
        }

        return new(frame.CroppedWidth, frame.CroppedHeight, samples);
    }

    private static Plane Assemble(Plane[] tiles, int columns, int rows, int width, int height)
    {
        var samples = new ushort[width * height];
        for (int index = 0; index < columns * rows; index++)
        {
            Plane tile = tiles[index];
            int x0 = index % columns * tile.Width, y0 = index / columns * tile.Height;
            for (int y = 0; y < tile.Height && y0 + y < height; y++)
            {
                for (int x = 0; x < tile.Width && x0 + x < width; x++)
                {
                    samples[((y0 + y) * width) + x0 + x] = tile.Samples[(y * tile.Width) + x];
                }
            }
        }

        return new(width, height, samples);
    }

    private static Plane Crop(Plane plane, int left, int top, int width, int height)
    {
        var samples = new ushort[width * height];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                samples[(y * width) + x] = plane.Samples[((top + y) * plane.Width) + left + x];
            }
        }

        return new(width, height, samples);
    }

    // Anti-clockwise quarter turns, as irot defines them.
    private static Plane Rotate(Plane plane, int quarterTurns)
    {
        for (int turn = 0; turn < quarterTurns; turn++)
        {
            var samples = new ushort[plane.Samples.Length];
            for (int y = 0; y < plane.Height; y++)
            {
                for (int x = 0; x < plane.Width; x++)
                {
                    // One anti-clockwise turn moves (x, y) to (y, width - 1 - x) in a height-wide image.
                    samples[((plane.Width - 1 - x) * plane.Height) + y] = plane.Samples[(y * plane.Width) + x];
                }
            }

            plane = new(plane.Height, plane.Width, samples);
        }

        return plane;
    }

    private static Plane MirrorLeftRight(Plane plane) => new(plane.Width, plane.Height,
        [.. Enumerable.Range(0, plane.Samples.Length).Select(i => plane.Samples[(i / plane.Width * plane.Width) + plane.Width - 1 - (i % plane.Width)])]);

    private static Plane MirrorTopBottom(Plane plane) => new(plane.Width, plane.Height,
        [.. Enumerable.Range(0, plane.Samples.Length).Select(i => plane.Samples[((plane.Height - 1 - (i / plane.Width)) * plane.Width) + (i % plane.Width)])]);

    private static async Task Run(string executable, params string[] arguments)
    {
        var start = new ProcessStartInfo(executable) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(start)!;
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(), stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        await Assert.That(process.ExitCode).IsEqualTo(0).Because(await stdout + await stderr);
    }

    private static string Root()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !System.IO.File.Exists(Path.Combine(directory.FullName, "PictureSharp.Heif.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? AppContext.BaseDirectory;
    }
}
