using System.IO.Compression;
using System.Text;

namespace PictureSharp.Formats.Heif.Tests;

// XMP items with content_encoding (ISO/IEC 23008-12 A.2.2): deflate is decompressed and charged by its decompressed
// size; other codings and corrupt streams leave the image readable without the block.
public sealed class HeifCompressedXmpTests
{
    private static readonly ImageConfiguration Configuration = ImageConfiguration.Default.ToBuilder().AddFormat(new HeifFormat()).Build();
    private static readonly byte[] Xmp = Encoding.UTF8.GetBytes(
        "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">"
        + string.Concat(Enumerable.Range(0, 200).Select(i => $"<rdf:Description rdf:about=\"{i}\"/>")) + "</rdf:RDF></x:xmpmeta>");

    private static readonly Lazy<byte[]> Plain = new(() =>
    {
        using Image image = HeifEncodeTests.Sample(64, 48);
        image.Metadata.SetBlock(MetadataKinds.Xmp, "<x/>"u8);
        return image.ToHeic(new HeifEncoderOptions { Configuration = Configuration });
    });

    [Test]
    [Arguments("deflate", true)]
    [Arguments("deflate", false)]
    [Arguments("Deflate", true)]
    public async Task LoadAndReadMetadata_ReturnTheSourceXmp(string encoding, bool zlib)
    {
        byte[] file = HeifTestFile.WithEncodedXmp(Plain.Value, Compress(Xmp, zlib), encoding);
        using Image image = Image.Load(file.AsSpan(), Options());
        await Assert.That(Block(image.Metadata)).IsEquivalentTo(Xmp, CollectionOrdering.Matching);
        await Assert.That(Block(Image.ReadMetadata(file.AsMemory(), Options()))).IsEquivalentTo(Xmp, CollectionOrdering.Matching);
        await Assert.That(image.Width).IsEqualTo(64);
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task LargeStreamSpanningManyInputReads_IsComplete(bool zlib)
    {
        // Random text barely compresses, so the input is far larger than DeflateStream's internal buffer.
        var random = new Random(7);
        byte[] text = new byte[300_000];
        random.NextBytes(text);
        byte[] xmp = Encoding.ASCII.GetBytes($"<x:xmpmeta xmlns:x=\"adobe:ns:meta/\">{Convert.ToBase64String(text)}</x:xmpmeta>");
        byte[] compressed = Compress(xmp, zlib);
        await Assert.That(compressed.Length).IsGreaterThan(200_000);
        byte[] file = HeifTestFile.WithEncodedXmp(Plain.Value, compressed, "deflate");
        await Assert.That(Block(Image.ReadMetadata(file.AsMemory(), Options()))).IsEquivalentTo(xmp, CollectionOrdering.Matching);
    }

    [Test]
    public async Task DecompressedSizeIsChargedAgainstMaxMetadataBytes()
    {
        byte[] file = HeifTestFile.WithEncodedXmp(Plain.Value, Compress(Xmp, zlib: true), "deflate");
        using Image image = Image.Load(file.AsSpan(), Options(Xmp.Length));
        await Assert.That(Block(image.Metadata).Length).IsEqualTo(Xmp.Length);
        await Assert.That(() => Image.Load(file.AsSpan(), Options(Xmp.Length - 1)).Dispose()).Throws<ImageFormatException>();
        await Assert.That(() => Image.ReadMetadata(file.AsMemory(), Options(Xmp.Length - 1))).Throws<ImageFormatException>();
    }

    [Test]
    public async Task DecompressionBomb_FailsWithoutAllocatingPastTheLimit()
    {
        const int limit = 1 << 20;
        byte[] file = HeifTestFile.WithEncodedXmp(Plain.Value, Compress(new byte[256 << 20], zlib: true), "deflate");
        await Assert.That(file.Length).IsLessThan(1 << 20);
        long before = GC.GetAllocatedBytesForCurrentThread();
        await Assert.That(() => Image.ReadMetadata(file.AsMemory(), Options(limit))).Throws<ImageFormatException>();
        await Assert.That(GC.GetAllocatedBytesForCurrentThread() - before).IsLessThan(4L * limit);
        await Assert.That(() => Image.Load(file.AsSpan(), Options(limit)).Dispose()).Throws<ImageFormatException>();
    }

    [Test]
    public async Task IgnoreMetadata_SkipsDecompression()
    {
        byte[] file = HeifTestFile.WithEncodedXmp(Plain.Value, Compress(new byte[64 << 20], zlib: true), "deflate");
        using Image image = Image.Load(file.AsSpan(), Options(1024) with { IgnoreMetadata = true });
        await Assert.That(image.Metadata.GetBlockLength(MetadataKinds.Xmp)).IsEqualTo(0);
    }

    [Test]
    [Arguments("br")]
    [Arguments("gzip")]
    public async Task UnknownEncoding_SkipsTheBlock(string encoding)
    {
        byte[] file = HeifTestFile.WithEncodedXmp(Plain.Value, Compress(Xmp, zlib: true), encoding);
        await AssertSkipped(file);
    }

    [Test]
    public async Task CorruptStream_SkipsTheBlock()
    {
        byte[] compressed = Compress(Xmp, zlib: true);
        // A reserved block type (BTYPE 11) right after the zlib header.
        compressed[2] = 0xff;
        await AssertSkipped(HeifTestFile.WithEncodedXmp(Plain.Value, compressed, "deflate"));
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task TruncatedStream_SkipsTheBlock(bool zlib)
    {
        byte[] compressed = Compress(Xmp, zlib);
        await AssertSkipped(HeifTestFile.WithEncodedXmp(Plain.Value, compressed[..(compressed.Length / 2)], "deflate"));
    }

    [Test]
    [Property("Category", "Slow")]
    public async Task MutatedStreamsNeverLeakUncheckedExceptions()
    {
        var random = new Random(0x584d50);
        byte[][] seeds = [Compress(Xmp, zlib: true), Compress(Xmp, zlib: false)];
        for (int iteration = 0; iteration < 3000; iteration++)
        {
            byte[] compressed = seeds[iteration % seeds.Length].ToArray();
            if (iteration % 5 == 0)
            {
                compressed = compressed[..random.Next(1, compressed.Length)];
            }

            for (int j = 0; j < 1 + (iteration % 6); j++)
            {
                if (compressed.Length > 0)
                {
                    compressed[random.Next(compressed.Length)] ^= (byte)(1 << random.Next(8));
                }
            }

            byte[] file = HeifTestFile.WithEncodedXmp(Plain.Value, compressed, "deflate");
            try
            {
                Image.ReadMetadata(file.AsMemory(), Options(4096));
            }
            catch (ImageFormatException)
            {
                // Only an XMP past the limit may fail the read.
            }
        }

        using Image image = Image.Load(Plain.Value.AsSpan(), Options());
        await Assert.That(image.Width).IsEqualTo(64);
    }

    private static async Task AssertSkipped(byte[] file)
    {
        using Image image = Image.Load(file.AsSpan(), Options());
        await Assert.That(image.Width).IsEqualTo(64);
        await Assert.That(image.Metadata.GetBlockLength(MetadataKinds.Xmp)).IsEqualTo(0);
        await Assert.That(Image.ReadMetadata(file.AsMemory(), Options()).GetBlockLength(MetadataKinds.Xmp)).IsEqualTo(0);
    }

    private static DecoderOptions Options(long maxMetadataBytes = 1 << 20) =>
        new() { Configuration = Configuration, MaxMetadataBytes = maxMetadataBytes };

    private static byte[] Compress(byte[] data, bool zlib)
    {
        using var output = new MemoryStream();
        using (Stream compressor = zlib
            ? new ZLibStream(output, CompressionLevel.SmallestSize)
            : new DeflateStream(output, CompressionLevel.SmallestSize))
        {
            compressor.Write(data);
        }

        return output.ToArray();
    }

    private static byte[] Block(ImageMetadata metadata)
    {
        var block = new byte[metadata.GetBlockLength(MetadataKinds.Xmp)];
        metadata.CopyBlockTo(MetadataKinds.Xmp, block);
        return block;
    }
}
