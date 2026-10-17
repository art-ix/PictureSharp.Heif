using PictureSharp.Formats.Heif.Hevc;

namespace PictureSharp.Formats.Heif.Tests;

// Every vector path against its scalar reference on random and extreme data. The vector code runs with software
// fallbacks where the hardware lacks it, so both widths are checked on any machine.
public sealed class HevcSimdTests
{
    private const int Trials = 400;

    // Records the first difference of each failing comparison.
    private static void Equal(List<string> failures, ReadOnlySpan<int> expected, ReadOnlySpan<int> actual, string context)
    {
        for (int i = 0; i < expected.Length; i++)
        {
            if (expected[i] != actual[i])
            {
                failures.Add($"{context}: index {i} expected {expected[i]}, got {actual[i]}.");
                return;
            }
        }
    }

    // Coefficient blocks with a random nonzero extent; some values at the 16-bit limits.
    private static int[] Coefficients(Random random, int size)
    {
        int[] block = new int[size * size];
        int rows = random.Next(1, size + 1);
        int columns = random.Next(1, size + 1);
        int range = random.Next(4) switch { 0 => 4, 1 => 64, 2 => 2048, _ => 32768 };
        for (int y = 0; y < rows; y++)
        {
            for (int x = 0; x < columns; x++)
            {
                block[(y * size) + x] = random.Next(8) switch
                {
                    0 => 0,
                    1 => short.MinValue,
                    2 => short.MaxValue,
                    _ => random.Next(-range, range),
                };
            }
        }

        return block;
    }

    [Test]
    public async Task InverseDct_VectorEqualsScalar()
    {
        var random = new Random(1);
        var failures = new List<string>();
        for (int trial = 0; trial < Trials; trial++)
        {
            int log2Size = 2 + (trial % 4);
            int size = 1 << log2Size;
            int shift2 = 20 - (trial % 3 == 0 ? 10 : 8);
            int[] coefficients = Coefficients(random, size);
            if (trial % 17 == 0)
            {
                Array.Clear(coefficients);
            }

            int[] expected = new int[size * size];
            HevcTransform.InverseDctScalar(coefficients, expected, new int[size * size], log2Size, shift2);
            foreach (bool wide in new[] { false, true })
            {
                int[] actual = new int[size * size];
                HevcTransform.InverseDctVector(coefficients, actual, new int[size * size], log2Size, shift2, wide);
                Equal(failures, expected, actual, $"trial {trial}, size {size}, wide {wide}");
            }
        }

        await Assert.That(failures).IsEmpty();
    }

    [Test]
    public async Task Dequantize_VectorEqualsScalar()
    {
        var random = new Random(2);
        var failures = new List<string>();
        for (int trial = 0; trial < Trials; trial++)
        {
            int log2Size = 2 + (trial % 4);
            int count = 1 << (2 * log2Size);
            int bitDepth = trial % 3 == 0 ? 10 : 8;
            int qp = random.Next(0, 52 + ((bitDepth - 8) * 6));
            int[] levels = new int[count];
            for (int i = 0; i < count; i++)
            {
                levels[i] = random.Next(6) switch
                {
                    0 => 0,
                    1 => random.Next(-3, 4),
                    2 => int.MinValue + random.Next(4),
                    3 => int.MaxValue - random.Next(4),
                    _ => random.Next(-40000, 40000),
                };
            }

            int[] factors = [];
            if (trial % 2 == 1)
            {
                factors = new int[count];
                for (int i = 0; i < count; i++)
                {
                    factors[i] = 16 * random.Next(1, 256);
                }
            }

            int[] expected = (int[])levels.Clone();
            int[] actual = (int[])levels.Clone();
            HevcTransform.Dequantize(expected, log2Size, qp, bitDepth, factors, vector: false);
            HevcTransform.Dequantize(actual, log2Size, qp, bitDepth, factors, vector: true);
            Equal(failures, expected, actual, $"trial {trial}, log2Size {log2Size}, qp {qp}, scaling {factors.Length > 0}");
        }

        await Assert.That(failures).IsEmpty();
    }

    [Test]
    public async Task ForwardTransform_VectorEqualsScalar()
    {
        var random = new Random(3);
        var failures = new List<string>();
        for (int trial = 0; trial < Trials; trial++)
        {
            int log2Size = 2 + (trial % 4);
            int size = 1 << log2Size;
            bool dst = log2Size == 2 && trial % 8 == 0;
            int bitDepth = trial % 5 == 0 ? 10 : 8;
            int maximum = (1 << bitDepth) - 1;
            int[] residual = new int[size * size];
            for (int i = 0; i < residual.Length; i++)
            {
                residual[i] = random.Next(4) == 0 ? (random.Next(2) == 0 ? -maximum : maximum) : random.Next(-maximum, maximum + 1);
            }

            int[] expected = new int[size * size];
            HevcForwardTransform.ForwardScalar(residual, expected, new int[size * size], log2Size, dst, bitDepth);
            foreach (bool wide in new[] { false, true })
            {
                int[] actual = new int[size * size];
                HevcForwardTransform.ForwardVector(residual, actual, new int[size * size], log2Size, dst, bitDepth, wide);
                Equal(failures, expected, actual, $"trial {trial}, size {size}, dst {dst}, wide {wide}");
            }
        }

        await Assert.That(failures).IsEmpty();
    }

    [Test]
    public async Task SampleRows_VectorEqualsScalar()
    {
        var random = new Random(4);
        var failures = new List<string>();
        for (int trial = 0; trial < Trials; trial++)
        {
            int length = 4 << (trial % 4);
            int maximum = trial % 2 == 0 ? 255 : 1023;
            int[] prediction = new int[length];
            int[] residual = new int[length];
            ushort[] original = new ushort[length];
            for (int x = 0; x < length; x++)
            {
                prediction[x] = random.Next(maximum + 1);
                residual[x] = random.Next(-2 * maximum, (2 * maximum) + 1);
                original[x] = (ushort)random.Next(maximum + 1);
            }

            ushort[] expectedRow = new ushort[length];
            ushort[] actualRow = new ushort[length];
            HevcSamples.StoreClippedScalar(expectedRow, prediction, residual, maximum);
            HevcSamples.StoreClippedVector(actualRow, prediction, residual, maximum);
            Equal(failures, [.. expectedRow.Select(v => (int)v)], [.. actualRow.Select(v => (int)v)], $"StoreClipped trial {trial}");

            HevcSamples.StoreScalar(expectedRow, prediction);
            HevcSamples.StoreVector(actualRow, prediction);
            Equal(failures, [.. expectedRow.Select(v => (int)v)], [.. actualRow.Select(v => (int)v)], $"Store trial {trial}");

            int expectedError = HevcSamples.StoreClippedErrorScalar(expectedRow, prediction, residual, maximum, original);
            int actualError = HevcSamples.StoreClippedErrorVector(actualRow, prediction, residual, maximum, original);
            Equal(failures, [expectedError, .. expectedRow.Select(v => (int)v)], [actualError, .. actualRow.Select(v => (int)v)],
                $"StoreClippedError trial {trial}");

            int[] expectedResidual = new int[length];
            int[] actualResidual = new int[length];
            HevcSamples.SubtractScalar(original, prediction, expectedResidual);
            HevcSamples.SubtractVector(original, prediction, actualResidual);
            Equal(failures, expectedResidual, actualResidual, $"Subtract trial {trial}");
        }

        await Assert.That(failures).IsEmpty();
    }

    [Test]
    public async Task Satd_VectorEqualsScalar()
    {
        var random = new Random(5);
        var failures = new List<string>();
        const int stride = 80;
        for (int trial = 0; trial < Trials; trial++)
        {
            int size = 4 << (trial % 4);
            int x0 = random.Next(0, stride - size + 1);
            int y0 = random.Next(0, 8);
            ushort[] original = new ushort[stride * (y0 + size)];
            int[] prediction = new int[size * size];
            bool extreme = trial % 3 == 0;
            for (int i = 0; i < original.Length; i++)
            {
                original[i] = (ushort)(extreme ? (random.Next(2) * 255) : random.Next(256));
            }

            for (int i = 0; i < prediction.Length; i++)
            {
                prediction[i] = extreme ? random.Next(2) * 255 : random.Next(256);
            }

            double expected = HevcPictureEncoder.SatdScalar(original, stride, x0, y0, prediction, size);
            double actual = HevcPictureEncoder.SatdVector(original, stride, x0, y0, prediction, size);
            if (expected != actual)
            {
                failures.Add($"trial {trial}, size {size}: expected {expected}, got {actual}.");
            }
        }

        await Assert.That(failures).IsEmpty();
    }
}
