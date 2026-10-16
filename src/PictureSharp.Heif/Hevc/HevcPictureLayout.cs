namespace PictureSharp.Formats.Heif.Hevc;

// H.265 6.5.1–6.5.2: CTB raster/tile scan conversion, tile identifiers and the z-scan order of minimum transform blocks.
internal sealed class HevcPictureLayout
{
    public HevcPictureLayout(HevcSequenceParameterSet sps, HevcPictureParameterSet pps)
    {
        WidthInCtbs = sps.PicWidthInCtbs;
        HeightInCtbs = sps.PicHeightInCtbs;
        CtbCount = WidthInCtbs * HeightInCtbs;
        if (pps.NumTileColumns > WidthInCtbs || pps.NumTileRows > HeightInCtbs)
        {
            throw HevcError.Invalid("more tiles than coding tree blocks.");
        }

        ColumnBoundaries = Boundaries(pps.NumTileColumns, WidthInCtbs, pps.UniformSpacing, pps.ColumnWidths);
        RowBoundaries = Boundaries(pps.NumTileRows, HeightInCtbs, pps.UniformSpacing, pps.RowHeights);
        RsToTs = new int[CtbCount];
        TsToRs = new int[CtbCount];
        TileIdTs = new int[CtbCount];
        for (int rs = 0; rs < CtbCount; rs++)
        {
            int x = rs % WidthInCtbs;
            int y = rs / WidthInCtbs;
            int tileX = 0;
            int tileY = 0;
            while (x >= ColumnBoundaries[tileX + 1])
            {
                tileX++;
            }

            while (y >= RowBoundaries[tileY + 1])
            {
                tileY++;
            }

            int ts = 0;
            for (int i = 0; i < tileX; i++)
            {
                ts += RowHeight(tileY) * ColumnWidth(i);
            }

            for (int j = 0; j < tileY; j++)
            {
                ts += WidthInCtbs * RowHeight(j);
            }

            ts += ((y - RowBoundaries[tileY]) * ColumnWidth(tileX)) + x - ColumnBoundaries[tileX];
            RsToTs[rs] = ts;
            TsToRs[ts] = rs;
            TileIdTs[ts] = (tileY * pps.NumTileColumns) + tileX;
        }

        Log2CtbSize = sps.Log2CtbSize;
        Log2MinTbSize = sps.Log2MinTbSize;
        int shift = sps.Log2CtbSize - sps.Log2MinTbSize;
        MinTbStride = WidthInCtbs << shift;
        int heightInMinTbs = HeightInCtbs << shift;
        MinTbAddrZs = new int[MinTbStride * heightInMinTbs];
        for (int y = 0; y < heightInMinTbs; y++)
        {
            for (int x = 0; x < MinTbStride; x++)
            {
                int ctbRs = (WidthInCtbs * (y >> shift)) + (x >> shift);
                int address = RsToTs[ctbRs] << (shift * 2);
                for (int i = 0; i < shift; i++)
                {
                    int m = 1 << i;
                    address += ((m & x) != 0 ? m * m : 0) + ((m & y) != 0 ? 2 * m * m : 0);
                }

                MinTbAddrZs[(y * MinTbStride) + x] = address;
            }
        }
    }

    public int WidthInCtbs { get; }
    public int HeightInCtbs { get; }
    public int CtbCount { get; }
    public int[] ColumnBoundaries { get; }
    public int[] RowBoundaries { get; }
    public int[] RsToTs { get; }
    public int[] TsToRs { get; }
    public int[] TileIdTs { get; }
    public int Log2CtbSize { get; }
    public int Log2MinTbSize { get; }
    public int MinTbStride { get; }
    public int[] MinTbAddrZs { get; }

    public int TileIdOfRs(int ctbRs) => TileIdTs[RsToTs[ctbRs]];

    public int ZscanAddress(int x, int y) => MinTbAddrZs[((y >> Log2MinTbSize) * MinTbStride) + (x >> Log2MinTbSize)];

    private int ColumnWidth(int i) => ColumnBoundaries[i + 1] - ColumnBoundaries[i];

    private int RowHeight(int j) => RowBoundaries[j + 1] - RowBoundaries[j];

    // 6-3/6-4: uniform or explicit sizes, the last one taking the remainder.
    private static int[] Boundaries(int count, int total, bool uniform, int[] sizes)
    {
        int[] boundaries = new int[count + 1];
        for (int i = 0; i < count; i++)
        {
            int size;
            if (uniform)
            {
                size = (((i + 1) * total) / count) - ((i * total) / count);
            }
            else
            {
                size = i < count - 1 ? sizes[i] : total - boundaries[i];
            }

            if (size <= 0 || boundaries[i] + size > total)
            {
                throw HevcError.Invalid("tile sizes do not fit the picture.");
            }

            boundaries[i + 1] = boundaries[i] + size;
        }

        return boundaries;
    }
}
