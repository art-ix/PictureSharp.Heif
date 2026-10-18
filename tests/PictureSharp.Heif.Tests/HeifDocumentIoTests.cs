using System.Drawing;
using static PictureSharp.Formats.Heif.Tests.HeifDocumentFixture;
using static PictureSharp.Formats.Heif.Tests.HeifTestFile;

namespace PictureSharp.Formats.Heif.Tests;

public sealed class HeifDocumentIoTests
{
    private static readonly ImageConfiguration Configuration = ImageConfiguration.Default.ToBuilder().AddFormat(new HeifFormat()).Build();
    private static DecoderOptions Options => new() { Configuration = Configuration };

    [Test]
    public async Task DocumentOptions_ValidateOnlyInputBuffering()
    {
        await Assert.That(new HeifDocumentOptions().MaxInputBytes).IsEqualTo((long)Array.MaxLength);
        await Assert.That(() => new HeifDocumentOptions { MaxInputBytes = 0 }).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => new HeifDocumentOptions { MaxInputBytes = -1 }).Throws<ArgumentOutOfRangeException>();
        byte[] bytes = Build(new Item(1, Hidden: true));
        HeifDocument doc = HeifDocument.Open(bytes, new HeifDocumentOptions { MaxInputBytes = bytes.Length });
        await Assert.That(doc.PrimaryImage).IsSameReferenceAs(doc.GetItem(doc.PrimaryItemId));
        await Assert.That(doc.PrimaryImage.IsHidden).IsTrue();
        await Assert.That(doc.TopLevelImages.Count).IsEqualTo(0);
    }

    [Test]
    public async Task AsyncOpen_UsesAsyncOnlyIo_FromCurrentPosition_AndLeavesTheStreamOpen()
    {
        byte[] bytes = Build(Encoded(1, 80, 60));
        using var stream = new AsyncOnlyStream([1, 2, 3, .. bytes], initialPosition: 3);
        HeifDocument doc = await HeifDocument.OpenAsync(stream, new HeifDocumentOptions { MaxInputBytes = bytes.Length });
        await Assert.That(stream.AsyncReads).IsGreaterThan(1);
        await Assert.That(stream.CanRead).IsTrue();
        await Assert.That(doc.PrimaryImage.DisplaySize).IsEqualTo(new Size(80, 60));
        stream.Dispose();
        using Image image = doc.LoadImage(doc.PrimaryItemId, Options);
        using Image direct = Image.Load(bytes, Options);
        await Assert.That(HeifDecodeTests.RgbaHash(image)).IsEqualTo(HeifDecodeTests.RgbaHash(direct));
    }

    [Test]
    public async Task AsyncOpen_PathAndSyncOpenProduceTheSameCatalogue_AfterDeletingTheFile()
    {
        byte[] bytes = Build(Encoded(1, 80, 60));
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".heic");
        try
        {
            await System.IO.File.WriteAllBytesAsync(path, bytes);
            HeifDocument asyncDoc = await HeifDocument.OpenAsync(path), syncDoc = HeifDocument.Open(path);
            System.IO.File.Delete(path);
            await Assert.That(asyncDoc.Items.Select(item => (item.Id, item.Type, item.DisplaySize)))
                .IsEquivalentTo(syncDoc.Items.Select(item => (item.Id, item.Type, item.DisplaySize)), CollectionOrdering.Matching);
            using Image image = asyncDoc.LoadImage(asyncDoc.PrimaryItemId, Options);
            await Assert.That(image.Size).IsEqualTo(new Size(80, 60));
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Test]
    public async Task AsyncOpen_LimitsAndMalformedDataLeaveCallerStreamOpen()
    {
        byte[] bytes = Build(new Item(1));
        using var limited = new AsyncOnlyStream(bytes);
        await Assert.That(async () => await HeifDocument.OpenAsync(limited, new HeifDocumentOptions { MaxInputBytes = bytes.Length - 1 }))
            .Throws<ImageFormatException>();
        await Assert.That(limited.CanRead).IsTrue();
        using var broken = new AsyncOnlyStream(bytes[..^1]);
        await Assert.That(async () => await HeifDocument.OpenAsync(broken)).Throws<ImageFormatException>();
        await Assert.That(broken.CanRead).IsTrue();
    }

    [Test]
    public async Task AsyncOpen_CancellationBeforeAndDuringIo_AndBeforeParsing_LeavesStreamOpen()
    {
        byte[] bytes = Build(new Item(1));
        using var cts = new CancellationTokenSource();
        using var stream = new AsyncOnlyStream(bytes, expectedToken: cts.Token) { BeforeRead = () => cts.Cancel() };
        await Assert.That(async () => await HeifDocument.OpenAsync(stream, cancellationToken: cts.Token)).Throws<OperationCanceledException>();
        await Assert.That(stream.CanRead).IsTrue();
        await Assert.That(async () => await HeifDocument.OpenAsync("this-file-does-not-exist.heic", cancellationToken: cts.Token))
            .Throws<OperationCanceledException>();
        using var beforeParse = new CancellationTokenSource();
        using var endStream = new AsyncOnlyStream(bytes, expectedToken: beforeParse.Token) { AtEnd = () => beforeParse.Cancel() };
        await Assert.That(async () => await HeifDocument.OpenAsync(endStream, cancellationToken: beforeParse.Token))
            .Throws<OperationCanceledException>();
        await Assert.That(endStream.CanRead).IsTrue();
    }

    [Test]
    public async Task LoadImage_ReadOptionsAndBudgetsAreIndependent_AndExplicitIdWins()
    {
        Item photo = Encoded(1, 80, 60);
        byte[] xmp = "<photo/>"u8.ToArray();
        byte[] bytes = Build(photo, new Item(2, "mime", [], xmp, References: [("cdsc", [1])]));
        HeifDocument doc = HeifDocument.Open(bytes, new HeifDocumentOptions { MaxInputBytes = bytes.Length });
        var options = Options with { FormatOptions = [new HeifDecoderOptions { ItemId = 999 }], TargetSize = new Size(20, 15) };
        using Image reduced = doc.LoadImage(1, options);
        await Assert.That(reduced.Size).IsEqualTo(new Size(20, 15));
        await Assert.That(reduced.Metadata.Xmp!).IsEquivalentTo(xmp, CollectionOrdering.Matching);
        await Assert.That(options.GetFormatOptions<HeifDecoderOptions>()!.ItemId).IsEqualTo(999u);
        await Assert.That(options.TargetSize).IsEqualTo(new Size(20, 15));
        await Assert.That(() => doc.LoadImage(1, Options with { MaxInputBytes = bytes.Length - 1 })).Throws<ImageFormatException>();
        await Assert.That(() => doc.LoadImage(1, Options with { MaxPixels = 4799 })).Throws<ImageFormatException>();
        await Assert.That(() => doc.LoadImage(1, Options with { MaxMetadataBytes = xmp.Length - 1 })).Throws<ImageFormatException>();
        using Image ignored = doc.LoadImage(1, Options with { IgnoreMetadata = true, MaxMetadataBytes = 0 });
        await Assert.That(ignored.Metadata.Xmp).IsNull();
        for (int i = 0; i < 2; i++)
        {
            using Image full = doc.LoadImage(1, Options with { MaxPixels = 4800, MaxMetadataBytes = xmp.Length });
            await Assert.That(full.Size).IsEqualTo(new Size(80, 60));
        }

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.That(() => doc.LoadImage(1, Options, cts.Token)).Throws<OperationCanceledException>();
        await Assert.That(() => doc.LoadImage(2, Options)).Throws<ImageFormatException>();
    }

    [Test]
    public async Task PrimaryPreview_DelegatesWithTheSameOptionsLimitsAndErrors()
    {
        HeifDocument doc = HeifDocument.Open(Build(Encoded(1, 80, 60)));
        var options = Options with { TargetSize = new Size(1, 1), FormatOptions = [new HeifDecoderOptions { ItemId = 999 }] };
        using Image primary = doc.LoadPreview(new Size(20, 15), options);
        using Image explicitId = doc.LoadPreview(doc.PrimaryItemId, new Size(20, 15), options);
        await Assert.That(primary.Size).IsEqualTo(new Size(20, 15));
        await Assert.That(HeifDecodeTests.RgbaHash(primary)).IsEqualTo(HeifDecodeTests.RgbaHash(explicitId));
        await Assert.That(options.TargetSize).IsEqualTo(new Size(1, 1));
        await Assert.That(() => doc.LoadPreview(new Size(20, 15), Options with { MaxPixels = 1 })).Throws<ImageFormatException>();
        await Assert.That(() => doc.LoadPreview(new Size(0, 15), options)).Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    [Arguments(40, 40, false)]
    [Arguments(41, 24, true)]
    [Arguments(41, 25, false)]
    [Arguments(410, 232, true)]
    [Arguments(410, 233, false)]
    [Arguments(410, 235, false)]
    public async Task Preview_RequiresProportionsWithinOnePixel_NotAPercentage(int width, int height, bool accepted)
    {
        // 101:57 -> 41:23.139 or 410:231.386; tolerance is a pixel even for the larger thumbnail.
        Item thumbnail = Encoded(2, width, height, [("thmb", [1])]);
        HeifDocument doc = HeifDocument.Open(Build(new Item(1, Properties: [Ispe(101, 57), Hvcc()]), thumbnail));
        if (accepted)
        {
            using Image preview = doc.LoadPreview(new Size(20, 20), Options);
            await Assert.That(preview.Width).IsEqualTo(20);
        }
        else
        {
            // The full photograph has no HEVC payload, so rejecting the thumbnail must fail on fallback.
            await Assert.That(() => doc.LoadPreview(new Size(20, 20), Options)).Throws<ImageFormatException>();
        }
    }

    [Test]
    public async Task Preview_DoesNotSelectThumbnailWhenPhotoDimensionsAreUnknown()
    {
        Item thumbnail = Encoded(2, 40, 30, [("thmb", [1])]);
        HeifDocument doc = HeifDocument.Open(Build(new Item(1, "jpeg", []), thumbnail));
        await Assert.That(doc.PrimaryImage.DisplaySize).IsNull();
        await Assert.That(() => doc.LoadPreview(new Size(20, 15), Options)).Throws<NotSupportedException>();
    }

    private sealed class AsyncOnlyStream(byte[] bytes, int initialPosition = 0, CancellationToken expectedToken = default) : Stream
    {
        private int position = initialPosition;
        private bool disposed;
        public int AsyncReads { get; private set; }
        public Action? BeforeRead { get; init; }
        public Action? AtEnd { get; init; }
        public override bool CanRead => !disposed;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new InvalidOperationException("Async-only stream used for synchronous I/O.");
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (expectedToken != cancellationToken)
            {
                throw new InvalidOperationException("The caller's cancellation token was not forwarded.");
            }

            AsyncReads++;
            await Task.Yield();
            BeforeRead?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            int count = Math.Min(Math.Min(buffer.Length, 127), bytes.Length - position);
            bytes.AsMemory(position, count).CopyTo(buffer);
            position += count;
            if (count == 0)
            {
                AtEnd?.Invoke();
            }

            return count;
        }

        protected override void Dispose(bool disposing) { disposed = true; base.Dispose(disposing); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
