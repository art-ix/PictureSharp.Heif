namespace PictureSharp.Formats.Heif.Container;

// Read-only input of a deflate stream over the item data. DeflateStream and ZLibStream return the data decoded so far
// when their input ends early, without an error; they ask for more input only while the stream is unfinished, so a read
// past the end marks a truncated stream.
internal sealed class HeifInflateInput(ReadOnlyMemory<byte> data) : Stream
{
    private int position;

    public bool ReadPastEnd { get; private set; }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (buffer.IsEmpty)
        {
            return 0;
        }

        int count = Math.Min(buffer.Length, data.Length - position);
        ReadPastEnd |= count == 0;
        data.Span.Slice(position, count).CopyTo(buffer);
        position += count;
        return count;
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
