using System.Buffers;
using System.Buffers.Binary;
using System.Text;

namespace PictureSharp.Formats.Heif.Container;

// The Live Photo fields of a QuickTime movie (QuickTime File Format, "Metadata" and "Timed Metadata"): the
// com.apple.quicktime.content.identifier value from moov/meta (or moov/udta/meta) keys and ilst, and the presentation
// time of the first sample of the timed metadata track whose mebx keys declare com.apple.quicktime.still-image-time.
// Top-level boxes before moov are skipped by header (sought past when the stream can seek); only moov is buffered,
// within the byte limit. Reading stops at moov.
internal static class QuickTimeReader
{
    private const int MaxTopLevelBoxes = 4096;
    private const int MaxMovieBoxes = 65536;
    private const int MaxValueLength = 256;
    private const int Chunk = 81920;
    private const uint Ftyp = 0x66747970, Moov = 0x6d6f6f76, Mdat = 0x6d646174, Wide = 0x77696465, Free = 0x66726565,
        Skip = 0x736b6970, Pnot = 0x706e6f74, Mvhd = 0x6d766864, Udta = 0x75647461, Meta = 0x6d657461, Hdlr = 0x68646c72,
        Keys = 0x6b657973, Ilst = 0x696c7374, Data = 0x64617461, Mdta = 0x6d647461, Trak = 0x7472616b, Edts = 0x65647473,
        Elst = 0x656c7374, Mdia = 0x6d646961, Mdhd = 0x6d646864, Minf = 0x6d696e66, Stbl = 0x7374626c, Stsd = 0x73747364,
        Stts = 0x73747473, Stsz = 0x7374737a, Mebx = 0x6d656278, Keyd = 0x6b657964;

    private static ReadOnlySpan<byte> ContentIdentifierKey => "com.apple.quicktime.content.identifier"u8;
    private static ReadOnlySpan<byte> StillImageTimeKey => "com.apple.quicktime.still-image-time"u8;

    // Reads from the current position; the caller's stream stays open. Corrupt or truncated structure, a missing moov
    // or a moov over maxMovieBytes throws ImageFormatException.
    public static async ValueTask<QuickTimeLivePhoto> Read(Stream stream, long maxMovieBytes, bool asynchronous,
        CancellationToken cancellationToken)
    {
        byte[] header = new byte[16];
        for (int index = 0; ; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Require(index < MaxTopLevelBoxes, "Too many top-level boxes.");
            int read = await Fill(stream, header.AsMemory(0, 8), asynchronous, cancellationToken).ConfigureAwait(false);
            Require(read != 0, index == 0 ? "The movie is empty." : "The movie has no moov box.");
            Require(read == 8, "Truncated box header.");
            ulong size = BinaryPrimitives.ReadUInt32BigEndian(header);
            uint type = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(4));
            int headerLength = 8;
            if (size == 1)
            {
                Require(await Fill(stream, header.AsMemory(8, 8), asynchronous, cancellationToken).ConfigureAwait(false) == 8,
                    "Truncated box header.");
                size = BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(8));
                headerLength = 16;
            }

            Require(index > 0 || type is Ftyp or Moov or Mdat or Wide or Free or Skip or Pnot, "Not a QuickTime movie.");
            Require(size == 0 || size >= (ulong)headerLength, "Invalid box size.");
            if (type == Moov)
            {
                long? length = size == 0 ? null : (long)Math.Min(size - (ulong)headerLength, long.MaxValue);
                byte[] movie = await ReadMovie(stream, length, maxMovieBytes, asynchronous, cancellationToken).ConfigureAwait(false);
                return Parse(movie, cancellationToken);
            }

            // A size of 0 extends the box to the end of the file, so no moov follows.
            Require(size != 0, "The movie has no moov box.");
            await SkipBytes(stream, size - (ulong)headerLength, asynchronous, cancellationToken).ConfigureAwait(false);
        }
    }

    // Grows with the data actually read, so a declared size never allocates past what the stream delivers.
    private static async ValueTask<byte[]> ReadMovie(Stream stream, long? length, long limit, bool asynchronous,
        CancellationToken cancellationToken)
    {
        limit = Math.Min(limit, Array.MaxLength);
        Require(length is null || length <= limit, "The moov box exceeds MaxMetadataBytes.");
        long wanted = length ?? limit + 1;
        using var buffer = new MemoryStream();
        byte[] chunk = ArrayPool<byte>.Shared.Rent(Chunk);
        try
        {
            while (buffer.Length < wanted)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int count = (int)Math.Min(chunk.Length, wanted - buffer.Length);
                int read = await Fill(stream, chunk.AsMemory(0, count), asynchronous, cancellationToken).ConfigureAwait(false);
                buffer.Write(chunk, 0, read);
                if (read < count)
                {
                    break;
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(chunk);
        }

        Require(length is null || buffer.Length == length, "Truncated moov box.");
        Require(buffer.Length <= limit, "The moov box exceeds MaxMetadataBytes.");
        return buffer.ToArray();
    }

    private static async ValueTask SkipBytes(Stream stream, ulong count, bool asynchronous, CancellationToken cancellationToken)
    {
        if (stream.CanSeek)
        {
            Require(count <= (ulong)Math.Max(0, stream.Length - stream.Position), "Truncated box.");
            stream.Seek((long)count, SeekOrigin.Current);
            return;
        }

        byte[] chunk = ArrayPool<byte>.Shared.Rent(Chunk);
        try
        {
            while (count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int wanted = (int)Math.Min((ulong)chunk.Length, count);
                int read = await Fill(stream, chunk.AsMemory(0, wanted), asynchronous, cancellationToken).ConfigureAwait(false);
                Require(read == wanted, "Truncated box.");
                count -= (ulong)read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(chunk);
        }
    }

    private static async ValueTask<int> Fill(Stream stream, Memory<byte> buffer, bool asynchronous, CancellationToken cancellationToken) =>
        asynchronous
            ? await stream.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false)
            : stream.ReadAtLeast(buffer.Span, buffer.Length, throwOnEndOfStream: false);

    private static QuickTimeLivePhoto Parse(ReadOnlyMemory<byte> movie, CancellationToken cancellationToken)
    {
        try
        {
            var parser = new Parser(cancellationToken);
            return parser.Movie(movie);
        }
        catch (ImageFormatException error) when (error.Format is not null)
        {
            // The box reader reports HEIF; the structure is a QuickTime movie.
            throw new ImageFormatException("Invalid QuickTime movie: " + error.Message, error);
        }
    }

    private sealed class Parser(CancellationToken cancellationToken)
    {
        private int boxes;

        public QuickTimeLivePhoto Movie(ReadOnlyMemory<byte> moov)
        {
            uint movieTimescale = 0;
            string? identifier = null;
            ReadOnlyMemory<byte>? userData = null;
            var tracks = new List<ReadOnlyMemory<byte>>();
            foreach (HeifBox box in Children(moov))
            {
                switch (box.Type)
                {
                    case Mvhd:
                        movieTimescale = Timescale(box.Data);
                        break;
                    case Meta:
                        identifier ??= ContentIdentifier(box.Data);
                        break;
                    case Udta:
                        userData ??= box.Data;
                        break;
                    case Trak:
                        tracks.Add(box.Data);
                        break;
                }
            }

            if (identifier is null && userData is { } udta)
            {
                identifier = Children(udta).Where(box => box.Type == Meta).Select(box => ContentIdentifier(box.Data))
                    .FirstOrDefault(value => value is not null);
            }

            TimeSpan? still = null;
            foreach (ReadOnlyMemory<byte> track in tracks)
            {
                still ??= StillImageTime(track, movieTimescale);
            }

            return new(identifier, still);
        }

        // QuickTime meta has no version and flags before its hdlr; the ISO form (written by some tools) has them.
        private string? ContentIdentifier(ReadOnlyMemory<byte> meta)
        {
            if (meta.Length >= 8 && BinaryPrimitives.ReadUInt32BigEndian(meta.Span[4..]) != Hdlr)
            {
                meta = meta[4..];
            }

            uint handler = 0;
            int keyIndex = 0;
            ReadOnlyMemory<byte>? items = null;
            foreach (HeifBox box in Children(meta))
            {
                switch (box.Type)
                {
                    case Hdlr:
                        handler = Handler(box.Data);
                        break;
                    case Keys:
                        keyIndex = KeyIndex(box.Data);
                        break;
                    case Ilst:
                        items = box.Data;
                        break;
                }
            }

            if (handler != Mdta || keyIndex == 0 || items is not { } list)
            {
                return null;
            }

            // Each ilst child is named by the 1-based key index and holds data boxes: type indicator, locale, value.
            foreach (HeifBox item in Children(list).Where(item => item.Type == (uint)keyIndex))
            {
                foreach (HeifBox data in Children(item.Data).Where(box => box.Type == Data))
                {
                    var reader = new HeifReader(data.Data);
                    uint indicator = reader.UInt32();
                    reader.UInt32();
                    ReadOnlySpan<byte> value = reader.Read(reader.Remaining).Span;
                    return indicator == 1 ? Text(value) : null;
                }
            }

            return null;
        }

        // The 1-based index of the mdta content identifier key, or 0.
        private int KeyIndex(ReadOnlyMemory<byte> keys)
        {
            var reader = new HeifReader(keys);
            reader.FullBox(out _);
            uint count = reader.UInt32();
            Require(count <= (uint)reader.Remaining / 8, "Invalid key count.");
            int found = 0;
            for (int index = 1; index <= count; index++)
            {
                uint size = reader.UInt32();
                Require(size >= 8, "Invalid key size.");
                uint space = reader.UInt32();
                ReadOnlySpan<byte> name = reader.Read((int)Math.Min(size - 8, int.MaxValue)).Span;
                if (found == 0 && space == Mdta && name.SequenceEqual(ContentIdentifierKey))
                {
                    found = index;
                }
            }

            return found;
        }

        private TimeSpan? StillImageTime(ReadOnlyMemory<byte> track, uint movieTimescale)
        {
            ReadOnlyMemory<byte>? edits = null, media = null;
            foreach (HeifBox box in Children(track))
            {
                if (box.Type == Edts)
                {
                    edits ??= Children(box.Data).Where(child => child.Type == Elst).Select(child => (ReadOnlyMemory<byte>?)child.Data).FirstOrDefault();
                }
                else if (box.Type == Mdia)
                {
                    media ??= box.Data;
                }
            }

            if (media is not { } mdia)
            {
                return null;
            }

            uint mediaTimescale = 0, handler = 0;
            ReadOnlyMemory<byte>? information = null;
            foreach (HeifBox box in Children(mdia))
            {
                switch (box.Type)
                {
                    case Mdhd: mediaTimescale = Timescale(box.Data); break;
                    case Hdlr: handler = Handler(box.Data); break;
                    case Minf: information ??= box.Data; break;
                }
            }

            if (handler != Meta || information is not { } minf
                || Children(minf).Where(box => box.Type == Stbl).Select(box => (ReadOnlyMemory<byte>?)box.Data).FirstOrDefault() is not { } stbl)
            {
                return null;
            }

            ReadOnlyMemory<byte>? descriptions = null, times = null, sizes = null;
            foreach (HeifBox box in Children(stbl))
            {
                switch (box.Type)
                {
                    case Stsd: descriptions ??= box.Data; break;
                    case Stts: times ??= box.Data; break;
                    case Stsz: sizes ??= box.Data; break;
                }
            }

            if (descriptions is not { } stsd || !DeclaresStillImageTime(stsd) || times is not { } stts || sizes is not { } stsz
                || FirstNonEmptySample(stsz) is not { } sample || DecodeTime(stts, sample) is not { } decodeTime || mediaTimescale == 0)
            {
                return null;
            }

            return edits is { } elst ? Present(elst, decodeTime, movieTimescale, mediaTimescale) : Ticks(decodeTime, mediaTimescale);
        }

        // stsd mebx entries: 6 reserved bytes and a data reference index, then keys, whose children (named by local key
        // identifiers) hold keyd boxes: a namespace and the key value.
        private bool DeclaresStillImageTime(ReadOnlyMemory<byte> stsd)
        {
            var reader = new HeifReader(stsd);
            reader.FullBox(out _);
            reader.UInt32();
            foreach (HeifBox entry in Children(reader.Read(reader.Remaining)).Where(entry => entry.Type == Mebx && entry.Data.Length >= 8))
            {
                foreach (HeifBox keys in Children(entry.Data[8..]).Where(box => box.Type == Keys))
                {
                    foreach (HeifBox key in Children(keys.Data))
                    {
                        foreach (HeifBox definition in Children(key.Data).Where(box => box.Type == Keyd && box.Data.Length >= 4))
                        {
                            ReadOnlySpan<byte> value = definition.Data.Span;
                            if (BinaryPrimitives.ReadUInt32BigEndian(value) == Mdta && value[4..].SequenceEqual(StillImageTimeKey))
                            {
                                return true;
                            }
                        }
                    }
                }
            }

            return false;
        }

        // The first sample with data; writers may place empty samples before the timed value.
        private static uint? FirstNonEmptySample(ReadOnlyMemory<byte> stsz)
        {
            var reader = new HeifReader(stsz);
            reader.FullBox(out _);
            uint size = reader.UInt32(), count = reader.UInt32();
            if (size != 0)
            {
                return count > 0 ? 0 : null;
            }

            Require(count <= (uint)reader.Remaining / 4, "Invalid sample size table.");
            for (uint sample = 0; sample < count; sample++)
            {
                if (reader.UInt32() != 0)
                {
                    return sample;
                }
            }

            return null;
        }

        private static ulong? DecodeTime(ReadOnlyMemory<byte> stts, uint sample)
        {
            var reader = new HeifReader(stts);
            reader.FullBox(out _);
            uint entries = reader.UInt32();
            Require(entries <= (uint)reader.Remaining / 8, "Invalid time-to-sample table.");
            UInt128 time = 0;
            for (uint i = 0; i < entries; i++)
            {
                uint count = reader.UInt32(), delta = reader.UInt32();
                if (sample < count)
                {
                    time += (UInt128)sample * delta;
                    return time <= ulong.MaxValue ? (ulong)time : null;
                }

                time += (UInt128)count * delta;
                sample -= count;
            }

            return null;
        }

        // Maps a media time through the edit list: empty edits (media_time -1) delay the track, a normal edit at rate 1
        // shows [media_time, media_time + duration) from the current movie time. Null when no edit shows the sample.
        private static TimeSpan? Present(ReadOnlyMemory<byte> elst, ulong mediaTime, uint movieTimescale, uint mediaTimescale)
        {
            var reader = new HeifReader(elst);
            byte version = reader.FullBox(out _);
            uint entries = reader.UInt32();
            Require(entries <= (uint)reader.Remaining / (version == 1 ? 20u : 12u), "Invalid edit list.");
            if (movieTimescale == 0)
            {
                return null;
            }

            Int128 start = 0;
            for (uint i = 0; i < entries; i++)
            {
                ulong duration = version == 1 ? reader.UInt64() : reader.UInt32();
                long first = version == 1 ? (long)reader.UInt64() : (int)reader.UInt32();
                short rate = (short)reader.UInt16();
                reader.UInt16();
                if (first >= 0 && rate == 1 && (Int128)mediaTime >= first
                    && ((Int128)mediaTime - first) * movieTimescale < (Int128)duration * mediaTimescale)
                {
                    return FromTicks((start * TimeSpan.TicksPerSecond / movieTimescale)
                        + (((Int128)mediaTime - first) * TimeSpan.TicksPerSecond / mediaTimescale));
                }

                start += duration;
            }

            return null;
        }

        private static TimeSpan? Ticks(ulong time, uint timescale) => FromTicks((Int128)time * TimeSpan.TicksPerSecond / timescale);

        private static TimeSpan? FromTicks(Int128 ticks) => ticks <= TimeSpan.MaxValue.Ticks ? TimeSpan.FromTicks((long)ticks) : null;

        // mvhd and mdhd: version 1 has 64-bit creation and modification times before the 32-bit timescale.
        private static uint Timescale(ReadOnlyMemory<byte> header)
        {
            var reader = new HeifReader(header);
            reader.Read(reader.FullBox(out _) == 1 ? 16 : 8);
            return reader.UInt32();
        }

        private static uint Handler(ReadOnlyMemory<byte> hdlr)
        {
            var reader = new HeifReader(hdlr);
            reader.FullBox(out _);
            reader.UInt32();
            return reader.UInt32();
        }

        private static string? Text(ReadOnlySpan<byte> value)
        {
            int end = value.IndexOf((byte)0);
            value = end < 0 ? value : value[..end];
            if (value.IsEmpty || value.Length > MaxValueLength || !System.Text.Unicode.Utf8.IsValid(value))
            {
                return null;
            }

            return Encoding.UTF8.GetString(value);
        }

        private List<HeifBox> Children(ReadOnlyMemory<byte> data)
        {
            var reader = new HeifReader(data);
            var children = new List<HeifBox>();
            while (reader.Remaining > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Require(++boxes <= MaxMovieBoxes, "Too many boxes in moov.");
                children.Add(reader.Box());
            }

            return children;
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new ImageFormatException("Invalid QuickTime movie: " + message);
        }
    }
}

internal readonly record struct QuickTimeLivePhoto(string? ContentIdentifier, TimeSpan? StillImageTime);
