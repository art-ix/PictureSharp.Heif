using System.Drawing;
using System.Text.Json;

namespace PictureSharp.Formats.Heif.Tests;

internal sealed record PortraitFocus(double[]? Point, double[] Sharp, string Confidence);
internal sealed record PortraitRoi(string Scene, double[][]? Background, double[][]? Subject, Dictionary<string, PortraitFocus> Focus);

internal static class PortraitMeasurementInputs
{
    public const int MeasurementLongSide = 1536;
    public static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static Dictionary<string, PortraitRoi> Read(string path) =>
        (JsonSerializer.Deserialize<PortraitRoi[]>(File.ReadAllText(path), Json) ?? throw new InvalidDataException("Missing ROI definitions."))
        .ToDictionary(r => r.Scene, StringComparer.Ordinal);

    public static Rectangle Rectangle(double[] area, int width, int height)
    {
        if (area.Length != 4 || area.Any(v => !double.IsFinite(v)) || area[0] < 0 || area[1] < 0 || area[2] <= 0 || area[3] <= 0
            || area[0] + area[2] > 1.0000001 || area[1] + area[3] > 1.0000001)
        {
            throw new InvalidDataException("ROI must be a positive rectangle inside the normalized photograph.");
        }

        int left = (int)Math.Round(area[0] * width), top = (int)Math.Round(area[1] * height);
        int right = Math.Min(width, (int)Math.Round((area[0] + area[2]) * width)), bottom = Math.Min(height, (int)Math.Round((area[1] + area[3]) * height));
        return new(left, top, right - left, bottom - top);
    }

    public static Point? Point(double[]? point, int width, int height)
    {
        if (point is null)
        {
            return null;
        }

        if (point.Length != 2 || point.Any(v => !double.IsFinite(v) || v is < 0 or >= 1))
        {
            throw new InvalidDataException("Focus point must lie in the normalized photograph.");
        }

        return new((int)(point[0] * width), (int)(point[1] * height));
    }

    public static Image Reduce(Image image)
    {
        int longer = Math.Max(image.Width, image.Height);
        return longer <= MeasurementLongSide ? image.Clone() : image.Clone(x => x.Resize(
            (int)Math.Round((double)image.Width * MeasurementLongSide / longer),
            (int)Math.Round((double)image.Height * MeasurementLongSide / longer)));
    }

    public static byte[] Regions(PortraitRoi roi, PortraitFocus focus, Image photo, Image? matte, Image? hair)
    {
        int w = photo.Width, h = photo.Height;
        var values = new byte[w * h];
        var mask = new byte[values.Length];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                byte m = Sample(matte, x, y, w, h);
                int i = y * w + x;
                mask[i] = m;
                if (matte is not null)
                {
                    values[i] |= m < 26 ? (byte)PortraitRegion.Background : m > 230 ? (byte)PortraitRegion.Subject : (byte)0;
                }

                if (hair is not null && Sample(hair, x, y, w, h) > 0 && m < 230)
                {
                    values[i] |= (byte)PortraitRegion.Hair;
                }
            }
        }

        if (roi.Background is not null || roi.Subject is not null)
        {
            for (int i = 0; i < values.Length; i++)
            {
                values[i] &= unchecked((byte)~(PortraitRegion.Background | PortraitRegion.Subject));
            }

            Fill(roi.Background, PortraitRegion.Background);
            Fill(roi.Subject, PortraitRegion.Subject);
        }

        Fill([focus.Sharp], PortraitRegion.Focus);
        if (matte is not null && roi.Subject is null)
        {
            var distance = new int[values.Length];
            Array.Fill(distance, w + h);
            for (int y = 1; y < h - 1; y++)
            {
                for (int x = 1; x < w - 1; x++)
                {
                    int i = y * w + x;
                    bool inside = mask[i] >= 128;
                    if ((mask[i - 1] >= 128) != inside || (mask[i + 1] >= 128) != inside
                        || (mask[i - w] >= 128) != inside || (mask[i + w] >= 128) != inside)
                    {
                        distance[i] = 0;
                    }
                }
            }

            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x;
                    if (x > 0) { distance[i] = Math.Min(distance[i], distance[i - 1] + 1); }
                    if (y > 0) { distance[i] = Math.Min(distance[i], distance[i - w] + 1); }
                }
            }

            for (int y = h - 1; y >= 0; y--)
            {
                for (int x = w - 1; x >= 0; x--)
                {
                    int i = y * w + x;
                    if (x + 1 < w) { distance[i] = Math.Min(distance[i], distance[i + 1] + 1); }
                    if (y + 1 < h) { distance[i] = Math.Min(distance[i], distance[i + w] + 1); }
                }
            }

            int low = Math.Max(1, (int)Math.Round(2.0 * Math.Max(w, h) / 4032)), high = Math.Max(low, (int)Math.Round(24.0 * Math.Max(w, h) / 4032));
            for (int i = 0; i < values.Length; i++)
            {
                if (distance[i] >= low && distance[i] <= high)
                {
                    values[i] |= (byte)PortraitRegion.Edge;
                }
            }
        }

        return values;

        void Fill(double[][]? areas, PortraitRegion region)
        {
            foreach (double[] area in areas ?? [])
            {
                Rectangle rect = Rectangle(area, w, h);
                for (int y = rect.Top; y < rect.Bottom; y++)
                {
                    for (int x = rect.Left; x < rect.Right; x++)
                    {
                        values[y * w + x] |= (byte)region;
                    }
                }
            }
        }
    }

    private static byte Sample(Image? map, int x, int y, int width, int height)
    {
        if (map is null) { return 0; }
        double mx = Math.Clamp((x + 0.5) * map.Width / width - 0.5, 0, map.Width - 1), my = Math.Clamp((y + 0.5) * map.Height / height - 0.5, 0, map.Height - 1);
        int left = (int)mx, top = (int)my, right = Math.Min(left + 1, map.Width - 1), bottom = Math.Min(top + 1, map.Height - 1);
        double upper = map[left, top].R + (map[right, top].R - map[left, top].R) * (mx - left);
        double lower = map[left, bottom].R + (map[right, bottom].R - map[left, bottom].R) * (mx - left);
        return (byte)Math.Round(upper + (lower - upper) * (my - top));
    }
}
