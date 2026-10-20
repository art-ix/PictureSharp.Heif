using System.Buffers;

namespace PictureSharp.Formats.Heif.Codecs;

// Band filters of sharpness, definition, noise reduction, brilliance and shadows: the fitted gains g_i of the bands
// G(σ_{i−1}) − G(σ_i), σ = 1, 2, …, 128 px with G(σ_0) the photograph, applied to the photograph as Σ c_i · G(σ_i) with
// c_0 = g_1, c_i = g_{i+1} − g_i and c_8 = −g_8. Blurs up to σ = 4 run at full resolution; wider ones blur a box-
// downsampled copy with σ = 4 and are upsampled bilinearly, which approximates the fitted Gaussians closely. Borders
// reflect without repeating the edge pixel. Rows and columns are filtered independently, so the result does not depend
// on the thread count.
internal sealed partial class HeifPhotosRenderer
{
    private const int FullResolutionBands = 3;
    private const float ReducedSigma = 4;

    // Returns three planes (R, G, B) of band offsets in 8-bit sRGB units, rented from the shared pool.
    private float[] Bands(ReadOnlyMemory<Rgba32> source)
    {
        int plane = width * height;
        Span<float> coefficients = stackalloc float[BandCount + 1];
        coefficients[0] = bandGains[0];
        for (int i = 1; i < BandCount; i++)
        {
            coefficients[i] = bandGains[i] - bandGains[i - 1];
        }

        coefficients[BandCount] = -bandGains[BandCount - 1];
        float[] c = coefficients.ToArray();
        float[] result = ArrayPool<float>.Shared.Rent(3 * plane);
        float[] channel = ArrayPool<float>.Shared.Rent(plane);
        float[] temporary = ArrayPool<float>.Shared.Rent(plane);
        float[] blurred = ArrayPool<float>.Shared.Rent(plane);
        try
        {
            for (int k = 0; k < 3; k++)
            {
                int component = k;
                int offset = k * plane;
                HeifParallel.ForRows(height, width, parallel, (top, bottom) =>
                {
                    ReadOnlySpan<Rgba32> pixels = source.Span;
                    for (int i = top * width; i < bottom * width; i++)
                    {
                        Rgba32 p = pixels[i];
                        float value = component == 0 ? p.R : component == 1 ? p.G : p.B;
                        channel[i] = value;
                        result[offset + i] = c[0] * value;
                    }
                });

                for (int level = 1; level <= BandCount; level++)
                {
                    float coefficient = c[level];
                    float sigma = 1 << (level - 1);
                    if (level <= FullResolutionBands)
                    {
                        Gaussian(channel, temporary, blurred, width, height, sigma);
                        HeifParallel.ForRows(height, width, parallel, (top, bottom) =>
                        {
                            for (int i = top * width; i < bottom * width; i++)
                            {
                                result[offset + i] += coefficient * blurred[i];
                            }
                        });
                    }
                    else
                    {
                        int factor = (int)(sigma / ReducedSigma);
                        int smallWidth = (width + factor - 1) / factor, smallHeight = (height + factor - 1) / factor;
                        int smallPlane = smallWidth * smallHeight;
                        float[] small = ArrayPool<float>.Shared.Rent(smallPlane);
                        float[] smallTemporary = ArrayPool<float>.Shared.Rent(smallPlane);
                        float[] smallBlurred = ArrayPool<float>.Shared.Rent(smallPlane);
                        try
                        {
                            Downsample(channel, small, factor, smallWidth, smallHeight);
                            Gaussian(small, smallTemporary, smallBlurred, smallWidth, smallHeight, ReducedSigma);
                            UpsampleAdd(smallBlurred, smallWidth, smallHeight, factor, result, offset, coefficient);
                        }
                        finally
                        {
                            ArrayPool<float>.Shared.Return(small);
                            ArrayPool<float>.Shared.Return(smallTemporary);
                            ArrayPool<float>.Shared.Return(smallBlurred);
                        }
                    }
                }
            }
        }
        catch
        {
            ArrayPool<float>.Shared.Return(result);
            throw;
        }
        finally
        {
            ArrayPool<float>.Shared.Return(channel);
            ArrayPool<float>.Shared.Return(temporary);
            ArrayPool<float>.Shared.Return(blurred);
        }

        return result;
    }

    // Separable Gaussian with radius ⌈4σ⌉: rows into temporary, then columns into output.
    private void Gaussian(float[] input, float[] temporary, float[] output, int w, int h, float sigma)
    {
        int radius = (int)MathF.Ceiling(4 * sigma);
        float[] kernel = new float[2 * radius + 1];
        float sum = 0;
        for (int i = -radius; i <= radius; i++)
        {
            sum += kernel[i + radius] = MathF.Exp(-i * i / (2 * sigma * sigma));
        }

        for (int i = 0; i < kernel.Length; i++)
        {
            kernel[i] /= sum;
        }

        HeifParallel.ForRows(h, w, parallel, (top, bottom) =>
        {
            for (int y = top; y < bottom; y++)
            {
                parallel.CancellationToken.ThrowIfCancellationRequested();
                int row = y * w;
                for (int x = 0; x < w; x++)
                {
                    float value = 0;
                    if (x >= radius && x + radius < w)
                    {
                        for (int k = 0; k < kernel.Length; k++)
                        {
                            value += kernel[k] * input[row + x - radius + k];
                        }
                    }
                    else
                    {
                        for (int k = 0; k < kernel.Length; k++)
                        {
                            value += kernel[k] * input[row + Reflect(x - radius + k, w)];
                        }
                    }

                    temporary[row + x] = value;
                }
            }
        });
        HeifParallel.ForRows(h, w, parallel, (top, bottom) =>
        {
            for (int y = top; y < bottom; y++)
            {
                parallel.CancellationToken.ThrowIfCancellationRequested();
                int row = y * w;
                output.AsSpan(row, w).Clear();
                for (int k = 0; k < kernel.Length; k++)
                {
                    int source = Reflect(y - radius + k, h) * w;
                    float weight = kernel[k];
                    for (int x = 0; x < w; x++)
                    {
                        output[row + x] += weight * temporary[source + x];
                    }
                }
            }
        });
    }

    // Reflection about the edge pixel (OpenCV's BORDER_REFLECT_101).
    private static int Reflect(int i, int n)
    {
        if (n == 1)
        {
            return 0;
        }

        while (i < 0 || i >= n)
        {
            i = i < 0 ? -i : 2 * n - 2 - i;
        }

        return i;
    }

    // Box average over factor × factor blocks; blocks at the right and bottom edges average the pixels they hold.
    private void Downsample(float[] input, float[] output, int factor, int smallWidth, int smallHeight)
    {
        HeifParallel.ForRows(smallHeight, smallWidth * factor * factor, parallel, (top, bottom) =>
        {
            for (int sy = top; sy < bottom; sy++)
            {
                int y0 = sy * factor, y1 = Math.Min(height, y0 + factor);
                for (int sx = 0; sx < smallWidth; sx++)
                {
                    int x0 = sx * factor, x1 = Math.Min(width, x0 + factor);
                    float sum = 0;
                    for (int y = y0; y < y1; y++)
                    {
                        for (int x = x0; x < x1; x++)
                        {
                            sum += input[y * width + x];
                        }
                    }

                    output[sy * smallWidth + sx] = sum / ((y1 - y0) * (x1 - x0));
                }
            }
        });
    }

    // Bilinear upsampling with pixel centres aligned, added with a coefficient.
    private void UpsampleAdd(float[] small, int smallWidth, int smallHeight, int factor, float[] result, int offset, float coefficient)
    {
        var x0 = new int[width];
        var x1 = new int[width];
        var fx = new float[width];
        for (int x = 0; x < width; x++)
        {
            (x0[x], x1[x], fx[x]) = Sample(x, factor, smallWidth);
        }

        HeifParallel.ForRows(height, width, parallel, (top, bottom) =>
        {
            for (int y = top; y < bottom; y++)
            {
                (int y0, int y1, float fy) = Sample(y, factor, smallHeight);
                int row0 = y0 * smallWidth, row1 = y1 * smallWidth, row = offset + y * width;
                for (int x = 0; x < width; x++)
                {
                    float a = small[row0 + x0[x]] + (small[row0 + x1[x]] - small[row0 + x0[x]]) * fx[x];
                    float b = small[row1 + x0[x]] + (small[row1 + x1[x]] - small[row1 + x0[x]]) * fx[x];
                    result[row + x] += coefficient * (a + (b - a) * fy);
                }
            }
        });
    }

    private static (int Low, int High, float Fraction) Sample(int x, int factor, int size)
    {
        float position = Math.Clamp((x + 0.5f) / factor - 0.5f, 0, size - 1);
        int low = Math.Min((int)position, size - 1);
        return (low, Math.Min(low + 1, size - 1), position - low);
    }
}
