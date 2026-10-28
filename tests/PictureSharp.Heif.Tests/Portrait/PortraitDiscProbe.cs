namespace PictureSharp.Formats.Heif.Tests;

// Independent constant-radius disc probe for calibration, without disparity, matte protection or blur pyramids.
// Horizontal prefix sums cover the full-weight interior; only the antialiased rim needs individual taps.
internal sealed class PortraitDiscProbe
{
    private readonly int width, height;
    private readonly double[] linear, prefix;
    private static readonly double[] Decode = Enumerable.Range(0, 256).Select(v => ToLinear(v / 255d)).ToArray();
    private static readonly byte[] Encode = Enumerable.Range(0, 65536).Select(v => (byte)Math.Clamp((int)Math.Round(
        255 * (v / 65535d <= 0.0031308 ? 12.92 * v / 65535d : 1.055 * Math.Pow(v / 65535d, 1 / 2.4) - 0.055)), 0, 255)).ToArray();

    internal sealed record Row(int Dy, int Left, int Right, (int Dx, double Weight)[] Rim);
    internal sealed record Kernel(float Radius, Row[] Rows);

    public PortraitDiscProbe(Image image)
    {
        width = image.Width; height = image.Height;
        linear = new double[width * height * 3];
        prefix = new double[(width + 1) * height * 3];
        ReadOnlySpan<Rgba32> pixels = image.GetPixelSpan<Rgba32>();
        for (int y = 0; y < height; y++)
        {
            int row = y * (width + 1) * 3;
            for (int x = 0; x < width; x++)
            {
                int i = (y * width + x) * 3, p = row + x * 3;
                Rgba32 value = pixels[y * width + x];
                linear[i] = Decode[value.R]; linear[i + 1] = Decode[value.G]; linear[i + 2] = Decode[value.B];
                prefix[p + 3] = prefix[p] + linear[i];
                prefix[p + 4] = prefix[p + 1] + linear[i + 1];
                prefix[p + 5] = prefix[p + 2] + linear[i + 2];
            }
        }
    }

    public static Kernel Disc(float radius)
    {
        if (!float.IsFinite(radius) || radius is < 0 or > 32) { throw new ArgumentOutOfRangeException(nameof(radius)); }
        if (radius == 0) { return new(0, []); }
        var rows = new List<Row>();
        int extent = (int)Math.Ceiling(radius + 0.5);
        for (int y = -extent; y <= extent; y++)
        {
            int left = 1, right = 0;
            var rim = new List<(int, double)>();
            for (int x = -extent; x <= extent; x++)
            {
                double weight = Math.Clamp(radius - Math.Sqrt(x * x + y * y) + 0.5, 0, 1);
                if (weight == 1)
                {
                    if (right < left) { left = x; }
                    right = x;
                }
                else if (weight > 0) { rim.Add((x, weight)); }
            }

            if (right >= left || rim.Count > 0) { rows.Add(new(y, left, right, [.. rim])); }
        }

        return new(radius, [.. rows]);
    }

    public Rgba32 At(int x, int y, Kernel kernel)
    {
        if (x < 0 || x >= width || y < 0 || y >= height) { throw new ArgumentOutOfRangeException(nameof(x)); }
        int source = (y * width + x) * 3;
        if (kernel.Radius == 0) { return new(FromLinear(linear[source]), FromLinear(linear[source + 1]), FromLinear(linear[source + 2])); }
        double r = 0, g = 0, b = 0, mass = 0;
        foreach (Row row in kernel.Rows)
        {
            int sy = y + row.Dy;
            if (sy < 0 || sy >= height) { continue; }
            int left = Math.Max(0, x + row.Left), right = Math.Min(width - 1, x + row.Right);
            if (row.Right >= row.Left && right >= left)
            {
                int a = (sy * (width + 1) + left) * 3, z = (sy * (width + 1) + right + 1) * 3;
                r += prefix[z] - prefix[a]; g += prefix[z + 1] - prefix[a + 1]; b += prefix[z + 2] - prefix[a + 2];
                mass += right - left + 1;
            }

            foreach ((int dx, double weight) in row.Rim)
            {
                int sx = x + dx;
                if (sx < 0 || sx >= width) { continue; }
                int i = (sy * width + sx) * 3;
                r += linear[i] * weight; g += linear[i + 1] * weight; b += linear[i + 2] * weight; mass += weight;
            }
        }

        return new(FromLinear(r / mass), FromLinear(g / mass), FromLinear(b / mass));
    }

    private static byte FromLinear(double value) => Encode[Math.Clamp((int)Math.Round(value * 65535), 0, 65535)];
    private static double ToLinear(double value) => value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
}
