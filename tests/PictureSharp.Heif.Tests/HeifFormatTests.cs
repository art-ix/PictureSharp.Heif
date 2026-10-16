using System.Buffers.Binary;
using System.Text;

namespace PictureSharp.Formats.Heif.Tests;

public sealed class HeifFormatTests
{
    [Test]
    [Arguments("heic", new[] { "mif1", "heic" })]                                          // iPhone
    [Arguments("heic", new[] { "mif1", "MiHE", "MiPr", "miaf", "MiHB", "heic" })]
    [Arguments("mif1", new[] { "heic" })]
    [Arguments("mif1", new[] { "miaf" })]                                                  // generic HEIF, codec decided by the container
    [Arguments("heix", new string[0])]
    [Arguments("msf1", new[] { "hevc" })]
    public async Task IsSupported_HevcHeif_ReturnsTrue(string major, string[] compatible)
    {
        await Assert.That(new HeifFormat().IsSupported(Ftyp(major, compatible))).IsTrue();
    }

    [Test]
    [Arguments("avif", new[] { "mif1", "miaf" })]
    [Arguments("mif1", new[] { "avif" })]
    [Arguments("isom", new[] { "mp41" })]                                                  // MP4 video
    [Arguments("qt  ", new string[0])]
    public async Task IsSupported_OtherIsoBmffBrands_ReturnsFalse(string major, string[] compatible)
    {
        await Assert.That(new HeifFormat().IsSupported(Ftyp(major, compatible))).IsFalse();
    }

    [Test]
    public async Task IsSupported_MalformedOrShortHeaders_ReturnsFalse()
    {
        var format = new HeifFormat();
        byte[] valid = Ftyp("heic", ["mif1"]);
        await Assert.That(format.IsSupported([])).IsFalse();
        await Assert.That(format.IsSupported(valid.AsSpan(0, 15))).IsFalse();
        await Assert.That(format.IsSupported("\x89PNG\r\n\x1a\n\0\0\0\0\0\0\0\0"u8)).IsFalse();

        foreach (uint size in (uint[])[1, 8, 15, 17, uint.MaxValue])
        {
            byte[] broken = [.. valid];
            BinaryPrimitives.WriteUInt32BigEndian(broken, size);
            await Assert.That(format.IsSupported(broken)).IsFalse();
        }
    }

    [Test]
    public async Task IsSupported_BrandsBeyondTheBoxSize_AreIgnored()
    {
        // The declared box ends after the minor version; the "heic" that follows belongs to the next box.
        byte[] data = Ftyp("mif1", ["heic"]);
        BinaryPrimitives.WriteUInt32BigEndian(data, 16);
        await Assert.That(new HeifFormat().IsSupported(data)).IsTrue();   // mif1 alone is still generic HEIF
        "avif"u8.CopyTo(data.AsSpan(8));
        await Assert.That(new HeifFormat().IsSupported(data)).IsFalse();
    }

    [Test]
    public async Task Format_DescribesHeif_AndIsSharedByAllModules()
    {
        var format = new HeifFormat();
        await Assert.That(format.Format).IsSameReferenceAs(new HeifFormat().Format);
        await Assert.That(format.Format).IsSameReferenceAs(ImageFormat.Heif);
        await Assert.That(format.Format.MimeType).IsEqualTo("image/heic");
        await Assert.That(format.Format.FileExtensions).IsEquivalentTo([".heic", ".heif", ".hif"], CollectionOrdering.Matching);
        await Assert.That(format.HeaderSize).IsBetween(16, 4096);
    }

    [Test]
    public async Task Registration_AddsTheFormat_ForDecodingAndEncoding()
    {
        ImageConfiguration configuration = ImageConfiguration.Default.ToBuilder().AddFormat(new HeifFormat()).Build();
        await Assert.That(configuration.Formats).Contains(ImageFormat.Heif);
        await Assert.That(configuration.FromExtension("photo.HEIC")).IsSameReferenceAs(ImageFormat.Heif);
        await Assert.That(configuration.FromMimeType("image/heic")).IsSameReferenceAs(ImageFormat.Heif);
        await Assert.That(configuration.CanDecode(ImageFormat.Heif)).IsTrue();
        await Assert.That(configuration.CanEncode(ImageFormat.Heif)).IsTrue();
    }

    [Test]
    [NotInParallel]
    public async Task SaveAsHeic_UsesTheDefaultConfiguration()
    {
        // PictureSharp saves through ImageConfiguration.Default, so the plugin must be registered there.
        ImageConfiguration previous = ImageConfiguration.Default;
        ImageConfiguration.Default = previous.ToBuilder().AddFormat(new HeifFormat()).Build();
        try
        {
            using var image = new Image(32, 16, new Rgba32(200, 30, 30, 255));
            byte[] heic = image.ToHeic(new HeifEncoderOptions { Quality = 80 });
            await Assert.That(new HeifFormat().IsSupported(heic)).IsTrue();
            using Image decoded = Image.Load(heic.AsSpan());
            await Assert.That((decoded.Width, decoded.Height)).IsEqualTo((32, 16));
        }
        finally
        {
            ImageConfiguration.Default = previous;
        }
    }

    [Test]
    public async Task LoadAndIdentify_SignatureWithoutContainer_ThrowFormatException()
    {
        var options = new DecoderOptions { Configuration = ImageConfiguration.Default.ToBuilder().AddFormat(new HeifFormat()).Build() };
        byte[] data = Ftyp("heic", ["mif1"]);
        await Assert.That(() => Image.Load(data.AsSpan(), options)).Throws<ImageFormatException>();
        await Assert.That(() => Image.Identify(data.AsMemory(), options)).Throws<ImageFormatException>();
    }

    private static byte[] Ftyp(string major, string[] compatible)
    {
        var data = new byte[16 + 4 * compatible.Length];
        BinaryPrimitives.WriteUInt32BigEndian(data, (uint)data.Length);
        Encoding.ASCII.GetBytes("ftyp").CopyTo(data, 4);
        Encoding.ASCII.GetBytes(major).CopyTo(data, 8);
        for (int i = 0; i < compatible.Length; i++)
        {
            Encoding.ASCII.GetBytes(compatible[i]).CopyTo(data, 16 + 4 * i);
        }

        return data;
    }
}
