namespace PictureSharp.Formats.Heif.Hevc;

// H.265 6.5.3–6.5.5: up-right diagonal, horizontal and vertical scan orders for square blocks of 1, 2, 4 and 8 elements
// per side. Positions are packed as x | (y << 4).
internal static class HevcScan
{
    public const int Diagonal = 0;
    public const int Horizontal = 1;
    public const int Vertical = 2;

    private static readonly byte[][] Orders = Build();

    // Scan order for a block of (1 << log2Size) elements per side, log2Size in 0..3.
    public static ReadOnlySpan<byte> Get(int log2Size, int scanIdx) => Orders[(log2Size * 3) + scanIdx];

    private static byte[][] Build()
    {
        var orders = new byte[12][];
        for (int log2 = 0; log2 < 4; log2++)
        {
            int size = 1 << log2;
            byte[] diagonal = new byte[size * size];
            int i = 0;
            int x = 0;
            int y = 0;
            while (i < size * size)
            {
                while (y >= 0)
                {
                    if (x < size && y < size)
                    {
                        diagonal[i++] = (byte)(x | (y << 4));
                    }

                    y--;
                    x++;
                }

                y = x;
                x = 0;
            }

            byte[] horizontal = new byte[size * size];
            byte[] vertical = new byte[size * size];
            for (int k = 0; k < size * size; k++)
            {
                horizontal[k] = (byte)((k % size) | ((k / size) << 4));
                vertical[k] = (byte)((k / size) | ((k % size) << 4));
            }

            orders[(log2 * 3) + Diagonal] = diagonal;
            orders[(log2 * 3) + Horizontal] = horizontal;
            orders[(log2 * 3) + Vertical] = vertical;
        }

        return orders;
    }
}
