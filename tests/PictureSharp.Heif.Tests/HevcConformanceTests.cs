using System.Security.Cryptography;
using System.Text.Json;
using PictureSharp.Formats.Heif.Hevc;

namespace PictureSharp.Formats.Heif.Tests;

// Decoded YCbCr must equal the HM reconstruction (also matched by libde265 or the encoder's picture hash) bit for bit.
public sealed class HevcConformanceTests
{
    private static string Directory => Path.Combine(AppContext.BaseDirectory, "hevc");

    private static readonly Lazy<Manifest> Data = new(() => JsonSerializer.Deserialize<Manifest>(
        File.ReadAllText(Path.Combine(Directory, "manifest.json")), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!);

    public static IEnumerable<string> Streams() => Data.Value.Streams.Select(entry => entry.File);

    public static IEnumerable<string> HeifItems() => Data.Value.HeifItems.Select(entry => $"{entry.File}#{entry.ItemId}");

    [Test]
    [MethodDataSource(nameof(HeifItems))]
    public async Task HeifItem_MatchesReferenceReconstruction(string key)
    {
        HeifItemEntry entry = Data.Value.HeifItems.Single(e => $"{e.File}#{e.ItemId}" == key);
        byte[] file = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "corpus", entry.File));
        List<ReadOnlyMemory<byte>> nals = HeifTestItems.Extract(file).Single(item => item.Id == entry.ItemId).Nals;
        HevcFrame frame = HevcDecoder.Decode(nals, null, CancellationToken.None);
        await Assert.That((frame.CroppedWidth, frame.CroppedHeight, frame.BitDepthLuma, frame.ChromaFormatIdc))
            .IsEqualTo((entry.Width, entry.Height, entry.BitDepth, entry.ChromaFormat));
        await Assert.That(PlaneHashes(frame)).IsEquivalentTo(entry.Planes, CollectionOrdering.Matching);
    }

    [Test]
    [MethodDataSource(nameof(Streams))]
    public async Task Stream_MatchesReferenceReconstruction(string file)
    {
        StreamEntry entry = Data.Value.Streams.Single(e => e.File == file);
        byte[] bytes = File.ReadAllBytes(Path.Combine(Directory, file));
        await Assert.That(Convert.ToHexStringLower(SHA256.HashData(bytes))).IsEqualTo(entry.Sha256);
        if (entry.NotSupported)
        {
            await Assert.That(() => HevcDecoder.Decode(SplitAnnexB(bytes), null, CancellationToken.None)).Throws<NotSupportedException>();
            return;
        }

        HevcFrame frame = HevcDecoder.Decode(SplitAnnexB(bytes), null, CancellationToken.None);
        await Assert.That((frame.CroppedWidth, frame.CroppedHeight, frame.BitDepthLuma, frame.ChromaFormatIdc))
            .IsEqualTo((entry.Width, entry.Height, entry.BitDepth, entry.ChromaFormat));
        string[] hashes = PlaneHashes(frame);
        await Assert.That(entry.Planes.Length).IsEqualTo(entry.ChromaFormat == 0 ? 1 : 3);
        await Assert.That(hashes.Length).IsEqualTo(entry.Planes.Length);
        for (int i = 0; i < entry.Planes.Length; i++)
        {
            await Assert.That(hashes[i]).IsEqualTo(entry.Planes[i]).Because($"plane {i} of {file}");
        }
    }

    // SHA-256 of each cropped plane in the reference YUV layout: bytes for 8-bit, little-endian 16-bit otherwise.
    internal static string[] PlaneHashes(HevcFrame frame)
    {
        string[] result = new string[frame.Planes.Length];
        for (int c = 0; c < frame.Planes.Length; c++)
        {
            int shift = c == 0 ? 0 : 1;
            int bitDepth = c == 0 ? frame.BitDepthLuma : frame.BitDepthChroma;
            int width = frame.CroppedWidth >> shift;
            int height = frame.CroppedHeight >> shift;
            int sampleSize = bitDepth > 8 ? 2 : 1;
            byte[] bytes = new byte[width * height * sampleSize];
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    ushort value = frame.Planes[c][(((frame.CropTop >> shift) + y) * frame.Strides[c]) + (frame.CropLeft >> shift) + x];
                    int index = ((y * width) + x) * sampleSize;
                    bytes[index] = (byte)value;
                    if (sampleSize == 2)
                    {
                        bytes[index + 1] = (byte)(value >> 8);
                    }
                }
            }

            result[c] = Convert.ToHexStringLower(SHA256.HashData(bytes));
        }

        return result;
    }

    // Annex B byte stream: NAL units separated by 0x000001 start codes; trailing zero bytes belong to the next start code.
    internal static List<ReadOnlyMemory<byte>> SplitAnnexB(byte[] stream)
    {
        var nals = new List<ReadOnlyMemory<byte>>();
        int start = -1;
        int i = 0;
        while (i + 2 < stream.Length)
        {
            if (stream[i] == 0 && stream[i + 1] == 0 && stream[i + 2] == 1)
            {
                if (start >= 0)
                {
                    nals.Add(Trim(stream, start, i));
                }

                i += 3;
                start = i;
                continue;
            }

            i++;
        }

        if (start >= 0)
        {
            nals.Add(Trim(stream, start, stream.Length));
        }

        return nals;
    }

    private static ReadOnlyMemory<byte> Trim(byte[] stream, int start, int end)
    {
        while (end > start && stream[end - 1] == 0)
        {
            end--;
        }

        return stream.AsMemory(start, end - start);
    }

    private sealed record Manifest(StreamEntry[] Streams, HeifItemEntry[] HeifItems);

    private sealed record HeifItemEntry(string File, uint ItemId, int Width, int Height, int BitDepth, int ChromaFormat, string[] Planes);

    private sealed record StreamEntry(string File, string Sha256, int Width, int Height, int BitDepth, int ChromaFormat, string[] Planes, bool NotSupported);
}
