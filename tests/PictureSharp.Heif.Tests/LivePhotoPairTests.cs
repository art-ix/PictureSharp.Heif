using System.Diagnostics;
using System.Text;
using PictureSharp.Formats.Jpeg;
using static PictureSharp.Formats.Heif.Tests.HeifTestFile;

namespace PictureSharp.Formats.Heif.Tests;

// Live Photo pairing: photographs carry an Apple MakerNote built here, movies are synthetic QuickTime files modelled on
// an iPhone 17 Pro MOV (moov/meta keys and ilst; a mebx still-image-time track with an empty edit), plus ffmpeg output.
public sealed class LivePhotoPairTests
{
    private const string Id = "D7A56E28-F0B3-4738-AB89-66B70E9A7251";
    private static readonly ImageConfiguration Configuration = ImageConfiguration.Default.ToBuilder().AddFormat(new HeifFormat()).Build();

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task HeicAndMov_WithTheSameIdentifier_Match_AndReportTheStillImageTime(bool asynchronous)
    {
        byte[] photo = Heic(Exif(Id)), movie = Movie(Id, stillImageTime: 820);
        LivePhotoPair pair = asynchronous
            ? await LivePhotoPair.ReadAsync(new MemoryStream(photo), new MemoryStream(movie))
            : LivePhotoPair.Read(new MemoryStream(photo), new MemoryStream(movie));
        await Assert.That(pair.Status).IsEqualTo(LivePhotoPairStatus.Matched);
        await Assert.That(pair.PhotoContentIdentifier).IsEqualTo(Id);
        await Assert.That(pair.VideoContentIdentifier).IsEqualTo(Id);
        await Assert.That(pair.StillImageTime).IsEqualTo(TimeSpan.FromTicks(820L * TimeSpan.TicksPerSecond / 600));
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task JpegAndMov_FromFiles_Match_AndLeaveNoHandlesOpen(bool asynchronous)
    {
        using Image image = HeifEncodeTests.Sample(32, 24);
        image.Metadata.Exif = Exif(Id, littleEndian: true);
        string directory = Directory.CreateTempSubdirectory("live-photo-").FullName;
        try
        {
            // The names do not match each other: only the identifiers confirm the pair.
            string photo = Path.Combine(directory, "IMG_0001.JPG"), movie = Path.Combine(directory, "other.mov");
            System.IO.File.WriteAllBytes(photo, image.ToJpeg(new JpegEncoderOptions()));
            System.IO.File.WriteAllBytes(movie, Movie(Id, stillImageTime: null));
            LivePhotoPair pair = asynchronous ? await LivePhotoPair.ReadAsync(photo, movie) : LivePhotoPair.Read(photo, movie);
            await Assert.That(pair.Status).IsEqualTo(LivePhotoPairStatus.Matched);
            await Assert.That(pair.StillImageTime).IsNull();
            System.IO.File.Delete(photo);
            System.IO.File.Delete(movie);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Test]
    public async Task MissingOrDifferentIdentifiers_AreReported()
    {
        byte[] withId = Heic(Exif(Id));
        LivePhotoPair conflicting = LivePhotoPair.Read(new MemoryStream(withId), new MemoryStream(Movie("2A1B4C3D-0000-4000-8000-000000000001", 820)));
        await Assert.That(conflicting.Status).IsEqualTo(LivePhotoPairStatus.ConflictingIdentifiers);
        await Assert.That(conflicting.VideoContentIdentifier).IsEqualTo("2A1B4C3D-0000-4000-8000-000000000001");

        LivePhotoPair caseOnly = LivePhotoPair.Read(new MemoryStream(withId), new MemoryStream(Movie(Id.ToLowerInvariant(), null)));
        await Assert.That(caseOnly.Status).IsEqualTo(LivePhotoPairStatus.ConflictingIdentifiers);

        LivePhotoPair noVideoId = LivePhotoPair.Read(new MemoryStream(withId), new MemoryStream(Movie(null, 820)));
        await Assert.That(noVideoId.Status).IsEqualTo(LivePhotoPairStatus.MissingIdentifier);
        await Assert.That(noVideoId.PhotoContentIdentifier).IsEqualTo(Id);
        await Assert.That(noVideoId.StillImageTime).IsNotNull();

        foreach (byte[]? exif in new[] { null, Exif(null), Exif(Id, appleHeader: false), Exif("bad\u0001id") })
        {
            LivePhotoPair noPhotoId = LivePhotoPair.Read(new MemoryStream(Heic(exif)), new MemoryStream(Movie(Id, 820)));
            await Assert.That(noPhotoId.Status).IsEqualTo(LivePhotoPairStatus.MissingIdentifier);
            await Assert.That(noPhotoId.PhotoContentIdentifier).IsNull();
            await Assert.That(noPhotoId.VideoContentIdentifier).IsEqualTo(Id);
        }
    }

    [Test]
    public async Task MovieLayouts_AreRead_FromSeekableAndForwardOnlyStreams()
    {
        byte[] photo = Heic(Exif(Id));
        byte[][] movies =
        [
            Movie(Id, 820),
            Movie(Id, 820, moovFirst: true),
            Movie(Id, 820, largeMdat: true),
            Movie(Id, 820, isoMeta: true),
            Movie(Id, 820, inUserData: true),
            Movie(Id, 820, moovFirst: true, trailingMdatToEnd: true),
        ];
        foreach (byte[] movie in movies)
        {
            foreach (Stream stream in new Stream[] { new MemoryStream(movie), new ForwardOnlyStream(movie) })
            {
                using var photoStream = new MemoryStream(photo);
                LivePhotoPair pair = await LivePhotoPair.ReadAsync(photoStream, stream);
                await Assert.That(pair.Status).IsEqualTo(LivePhotoPairStatus.Matched);
                await Assert.That(pair.StillImageTime).IsEqualTo(TimeSpan.FromTicks(820L * TimeSpan.TicksPerSecond / 600));
                await Assert.That(photoStream.CanRead && stream.CanRead).IsTrue();
            }
        }
    }

    [Test]
    public async Task StillImageTime_FollowsTheSampleTableAndEditList()
    {
        byte[] photo = Heic(Exif(Id));
        // Without an edit list: the second sample (the first has no data) starts at 300/1000 s.
        LivePhotoPair noEdits = LivePhotoPair.Read(new MemoryStream(photo), new MemoryStream(Movie(Id, 820, emptyFirstSample: true, edits: false)));
        await Assert.That(noEdits.StillImageTime).IsEqualTo(TimeSpan.FromMilliseconds(300));
        // An edit that starts after the sample hides it.
        LivePhotoPair hidden = LivePhotoPair.Read(new MemoryStream(photo), new MemoryStream(Movie(Id, 820, mediaStart: 5)));
        await Assert.That(hidden.StillImageTime).IsNull();
        await Assert.That(hidden.Status).IsEqualTo(LivePhotoPairStatus.Matched);
    }

    [Test]
    public async Task CorruptTruncatedAndOversizedMovies_ThrowImageFormatException()
    {
        byte[] photo = Heic(Exif(Id)), movie = Movie(Id, 820);
        byte[][] invalid =
        [
            [],
            "not a movie at all"u8.ToArray(),
            System.IO.File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "corpus", "x265-rgb8.heic"))[8..],
            Join(Box("ftyp", "qt  "u8.ToArray(), U32(0)), Box("mdat", new byte[16])),
            Join(Box("ftyp", "qt  "u8.ToArray(), U32(0)), U32(4), "moov"u8.ToArray()),
            Join(Box("ftyp", "qt  "u8.ToArray(), U32(0)), Box("moov", Box("meta", U32(100), "hdlr"u8.ToArray()))),
        ];
        foreach (byte[] bytes in invalid)
        {
            await Assert.That(() => LivePhotoPair.Read(new MemoryStream(photo), new MemoryStream(bytes))).Throws<ImageFormatException>();
        }

        // moov follows mdat, so every truncation loses it.
        for (int length = 0; length < movie.Length; length += 7)
        {
            await Assert.That(() => LivePhotoPair.Read(new MemoryStream(photo), new ForwardOnlyStream(movie[..length]))).Throws<ImageFormatException>();
            await Assert.That(() => LivePhotoPair.Read(new MemoryStream(photo), new MemoryStream(movie[..length]))).Throws<ImageFormatException>();
        }

        int moovSize = MoovSize(movie);
        await Assert.That(() => LivePhotoPair.Read(new MemoryStream(photo), new MemoryStream(movie),
            new DecoderOptions { MaxMetadataBytes = moovSize - 9 })).Throws<ImageFormatException>();
        await Assert.That(LivePhotoPair.Read(new MemoryStream(photo), new MemoryStream(movie),
            new DecoderOptions { MaxMetadataBytes = moovSize - 8 }).Status).IsEqualTo(LivePhotoPairStatus.Matched);
        await Assert.That(() => LivePhotoPair.Read(new MemoryStream(photo[..100]), new MemoryStream(movie))).Throws<ImageFormatException>();
    }

    [Test]
    public async Task Cancellation_IsObserved()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.That(() => LivePhotoPair.Read(new MemoryStream(Heic(Exif(Id))), new MemoryStream(Movie(Id, 820)), null, cancellation.Token))
            .Throws<OperationCanceledException>();
        await Assert.That(async () => await LivePhotoPair.ReadAsync(new MemoryStream(Heic(Exif(Id))), new MemoryStream(Movie(Id, 820)), null, cancellation.Token))
            .Throws<OperationCanceledException>();
    }

    [Test]
    public async Task MutatedMovies_NeverLeakUncheckedExceptions()
    {
        var random = new Random(0x4d4f56);
        byte[] photo = Heic(Exif(Id));
        byte[][] seeds = [Movie(Id, 820), Movie(Id, 820, moovFirst: true, isoMeta: true), Movie(Id, 820, inUserData: true, emptyFirstSample: true, edits: false)];
        int results = 0;
        for (int iteration = 0; iteration < 4000; iteration++)
        {
            byte[] bytes = seeds[iteration % seeds.Length].ToArray();
            for (int j = 0; j < 1 + iteration % 6; j++)
            {
                bytes[random.Next(bytes.Length)] = (byte)random.Next(256);
            }

            if (iteration % 5 == 0)
            {
                bytes = bytes[..random.Next(bytes.Length)];
            }

            try
            {
                LivePhotoPair.Read(new MemoryStream(photo), iteration % 2 == 0 ? new MemoryStream(bytes) : new ForwardOnlyStream(bytes),
                    new DecoderOptions { MaxMetadataBytes = 4096 });
                results++;
            }
            catch (ImageFormatException)
            {
            }
        }

        await Assert.That(results).IsGreaterThan(0);
    }

    [Test]
    [Property("Category", "Oracle")]
    public async Task FfmpegMetadata_IsRead()
    {
        Skip.Unless(System.IO.File.Exists("/usr/bin/ffmpeg"), "ffmpeg writes QuickTime mdta metadata independently of these fixtures.");
        string directory = Directory.CreateTempSubdirectory("live-photo-ffmpeg-").FullName;
        try
        {
            string movie = Path.Combine(directory, "clip.mov");
            var start = new ProcessStartInfo("/usr/bin/ffmpeg") { RedirectStandardError = true, UseShellExecute = false };
            foreach (string argument in new[] { "-v", "error", "-y", "-f", "lavfi", "-i", "color=c=red:s=16x16:d=0.2", "-c:v", "mjpeg",
                "-metadata", "com.apple.quicktime.content.identifier=" + Id, "-movflags", "use_metadata_tags", movie })
            {
                start.ArgumentList.Add(argument);
            }

            using (Process process = Process.Start(start)!)
            {
                string error = await process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync();
                await Assert.That(process.ExitCode).IsEqualTo(0).Because(error);
            }

            LivePhotoPair pair = LivePhotoPair.Read(new MemoryStream(Heic(Exif(Id))), System.IO.File.OpenRead(movie));
            await Assert.That(pair.Status).IsEqualTo(LivePhotoPairStatus.Matched);
            await Assert.That(pair.StillImageTime).IsNull();
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    // Private iPhone pairs in samples/ (never committed): IMG_xxxx.HEIC with IMG_xxxx.MOV. The names only find the
    // candidates; the identifiers must confirm them.
    [Test]
    [Property("Category", "Local")]
    public async Task PrivateSamples_LivePhotoPairsMatch()
    {
        string samples = Path.Combine(Root(), "samples");
        (string Photo, string Movie)[] pairs = Directory.Exists(samples)
            ? [.. Directory.GetFiles(samples).Where(path => Path.GetExtension(path).Equals(".mov", StringComparison.OrdinalIgnoreCase))
                .Select(movie => (Photo: Directory.GetFiles(samples, Path.GetFileNameWithoutExtension(movie) + ".*")
                    .FirstOrDefault(path => Path.GetExtension(path).ToLowerInvariant() is ".heic" or ".jpg" or ".jpeg"), Movie: movie))
                .Where(pair => pair.Photo is not null).Select(pair => (pair.Photo!, pair.Movie)).Order()]
            : [];
        Skip.Unless(pairs.Length > 0, "No private Live Photo pairs in samples/.");
        foreach ((string photo, string movie) in pairs)
        {
            LivePhotoPair pair = await LivePhotoPair.ReadAsync(photo, movie);
            await Assert.That(pair.Status).IsEqualTo(LivePhotoPairStatus.Matched).Because(Path.GetFileName(movie));
            await Assert.That(Guid.TryParse(pair.PhotoContentIdentifier, out _)).IsTrue();
            await Assert.That(pair.StillImageTime).IsNotNull().Because(Path.GetFileName(movie));
            await Assert.That(pair.StillImageTime!.Value).IsGreaterThan(TimeSpan.Zero).And.IsLessThan(TimeSpan.FromSeconds(5));
            Console.WriteLine($"{Path.GetFileName(movie)}: still image at {pair.StillImageTime.Value.TotalSeconds:0.000} s");
        }
    }

    private static byte[] Heic(byte[]? exif)
    {
        using Image image = HeifEncodeTests.Sample(32, 24);
        image.Metadata.Exif = exif;
        return image.ToHeic(new HeifEncoderOptions { Configuration = Configuration, TileSize = 0 });
    }

    // TIFF: IFD0 → Exif IFD → MakerNote "Apple iOS\0", version 1, "MM", then a big-endian IFD with tag 0x0011 (ASCII,
    // offsets relative to the MakerNote). The outer TIFF may be little-endian.
    private static byte[] Exif(string? contentIdentifier, bool littleEndian = false, bool appleHeader = true)
    {
        byte[] value = contentIdentifier is null ? [] : Encoding.ASCII.GetBytes(contentIdentifier + '\0');
        byte[] note = Join(appleHeader ? "Apple iOS\0"u8.ToArray() : "Other iOS\0"u8.ToArray(), [0, 1], "MM"u8.ToArray(),
            U16(contentIdentifier is null ? 0 : 1),
            contentIdentifier is null ? [] : Join(U16(0x0011), U16(2), U32((uint)value.Length), U32(14 + 2 + 12 + 4)),
            U32(0), value);
        byte[] Number16(int number) => littleEndian ? [(byte)number, (byte)(number >> 8)] : U16(number);
        byte[] Number32(uint number) => littleEndian ? [(byte)number, (byte)(number >> 8), (byte)(number >> 16), (byte)(number >> 24)] : U32(number);
        // Header (8), IFD0 with one entry (18) at 8, Exif IFD with one entry (18) at 26, MakerNote at 44.
        return Join(littleEndian ? "II*\0"u8.ToArray() : "MM\0*"u8.ToArray(), Number32(8),
            Number16(1), Number16(0x8769), Number16(4), Number32(1), Number32(26), Number32(0),
            Number16(1), Number16(0x927c), Number16(7), Number32((uint)note.Length), Number32(44), Number32(0), note);
    }

    // A QuickTime movie: ftyp, wide, mdat and moov (or moov first). moov holds mvhd (timescale 600), the metadata in
    // moov/meta (QuickTime form), moov/meta with version and flags, or moov/udta/meta, a video track, and a mebx
    // still-image-time track (mdhd timescale 1000) whose edit list delays it by stillImageTime/600 s.
    private static byte[] Movie(string? contentIdentifier, int? stillImageTime, bool moovFirst = false, bool largeMdat = false,
        bool isoMeta = false, bool inUserData = false, bool trailingMdatToEnd = false, bool emptyFirstSample = false,
        bool edits = true, int mediaStart = 0)
    {
        byte[] Full(string type, params byte[][] parts) => HeifTestFile.Full(type, 0, 0, parts);
        byte[] Key(string name) => Join(U32((uint)(8 + name.Length)), "mdta"u8.ToArray(), Encoding.ASCII.GetBytes(name));
        byte[] Data(int type, string value) => Box("data", U32((uint)type), U32(0), Encoding.UTF8.GetBytes(value));
        byte[] Handler(string type) => Full("hdlr", U32(0), Encoding.ASCII.GetBytes(type), new byte[12], [0]);
        byte[] metadata = [];
        if (contentIdentifier is not null)
        {
            byte[] keys = Full("keys", U32(3), Key("com.apple.quicktime.location.ISO6709"), Key("com.apple.quicktime.make"),
                Key("com.apple.quicktime.content.identifier"));
            byte[] ilst = Box("ilst", Box("\0\0\0\u0001", Data(1, "+52.0000+021.0000/")),
                Box("\0\0\0\u0002", Data(1, "Apple")), Box("\0\0\0\u0003", Data(1, contentIdentifier)));
            byte[] content = Join(Handler("mdta"), keys, ilst, Box("free", new byte[12]));
            metadata = Box("meta", isoMeta ? [0, 0, 0, 0] : [], content);
            if (inUserData)
            {
                metadata = Box("udta", Box("meta", Handler("mdir"), Box("ilst")), metadata);
            }
        }

        byte[] mvhd = Full("mvhd", U32(0), U32(0), U32(600), U32(1800), new byte[80]);
        byte[] video = Box("trak", Full("tkhd", new byte[80]), Box("mdia", Full("mdhd", U32(0), U32(0), U32(600), U32(1800), U32(0)),
            Handler("vide"), Box("minf", Box("stbl", Full("stsd", U32(1), Box("hvc1", new byte[78])), Full("stts", U32(1), U32(60), U32(30)),
                Full("stsz", U32(0), U32(60), Join([.. Enumerable.Repeat(U32(100), 60)]))))));
        byte[] keyDefinition = Box("\0\0\0\u0001", Box("keyd", "mdta"u8.ToArray(), "com.apple.quicktime.still-image-time"u8.ToArray()),
            Full("dtyp", U32(65)));
        byte[] still = stillImageTime is not { } offset ? [] : Box("trak", Full("tkhd", new byte[80]),
            edits ? Box("edts", Full("elst", U32(2), U32((uint)offset), U32(uint.MaxValue), U16(1), U16(0), U32(1), U32((uint)mediaStart), U16(1), U16(0))) : [],
            Box("mdia", Full("mdhd", U32(0), U32(0), U32(1000), U32(1), U32(0)), Handler("meta"),
                Box("minf", Box("stbl", Full("stsd", U32(1), Box("mebx", new byte[6], U16(1), Box("keys", keyDefinition))),
                    emptyFirstSample ? Full("stts", U32(2), U32(1), U32(300), U32(1), U32(1)) : Full("stts", U32(1), U32(1), U32(1)),
                    emptyFirstSample ? Full("stsz", U32(0), U32(2), U32(0), U32(9)) : Full("stsz", U32(9), U32(1))))));
        byte[] moov = Box("moov", mvhd, video, still, metadata);
        byte[] ftyp = Box("ftyp", "qt  "u8.ToArray(), U32(0), "qt  "u8.ToArray());
        byte[] payload = [.. Enumerable.Range(0, 3000).Select(i => (byte)(i * 7))];
        byte[] mdat = largeMdat ? Join(U32(1), "mdat"u8.ToArray(), U64((ulong)payload.Length + 16), payload)
            : trailingMdatToEnd ? Join(U32(0), "mdat"u8.ToArray(), payload) : Box("mdat", payload);
        return moovFirst ? Join(ftyp, Box("wide"), moov, mdat) : Join(ftyp, Box("wide"), mdat, moov);
    }

    private static int MoovSize(byte[] movie)
    {
        int offset = 0;
        while (Encoding.ASCII.GetString(movie, offset + 4, 4) != "moov")
        {
            offset += (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(movie.AsSpan(offset));
        }

        return (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(movie.AsSpan(offset));
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

    // A stream that can only be read forward, as a network body is.
    private sealed class ForwardOnlyStream(byte[] data) : Stream
    {
        private readonly MemoryStream inner = new(data);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, Math.Min(count, 1000));
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
