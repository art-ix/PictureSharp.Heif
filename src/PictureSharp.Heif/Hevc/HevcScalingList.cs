namespace PictureSharp.Formats.Heif.Hevc;

// H.265 7.3.4 scaling_list_data and 7.4.5 ScalingFactor derivation.
// port: HM source/Lib/TLibDecoder/TDecCAVLC.cpp parseScalingList, xDecodeScalingList;
//       HM source/Lib/TLibCommon/TComTrQuant.cpp processScalingListDec
internal sealed class HevcScalingList
{
    // Lists are stored in raster order: 4x4 for sizeId 0, 8x8 (upsampled later) for sizeId 1..3.
    private readonly int[][][] lists = new int[4][][];
    private readonly int[,] dc = new int[4, 6];
    private int[][][]? factors;

    private HevcScalingList()
    {
        for (int sizeId = 0; sizeId < 4; sizeId++)
        {
            lists[sizeId] = new int[6][];
            for (int matrixId = 0; matrixId < 6; matrixId++)
            {
                lists[sizeId][matrixId] = new int[sizeId == 0 ? 16 : 64];
            }
        }
    }

    public static HevcScalingList CreateDefault()
    {
        var list = new HevcScalingList();
        for (int sizeId = 0; sizeId < 4; sizeId++)
        {
            for (int matrixId = 0; matrixId < 6; matrixId++)
            {
                list.SetDefault(sizeId, matrixId);
            }
        }

        return list;
    }

    public static HevcScalingList Parse(ref HevcBitReader reader)
    {
        var list = new HevcScalingList();
        for (int sizeId = 0; sizeId < 4; sizeId++)
        {
            for (int matrixId = 0; matrixId < 6; matrixId++)
            {
                int[] destination = list.lists[sizeId][matrixId];
                if (sizeId == 3 && matrixId % 3 != 0)
                {
                    // 32x32 chroma lists are only coded for ChromaArrayType 3; derive them from 16x16 as HM does.
                    list.lists[2][matrixId].CopyTo(destination, 0);
                    list.dc[3, matrixId] = list.dc[2, matrixId];
                    continue;
                }

                if (!reader.ReadFlag())
                {
                    int delta = reader.ReadUe(sizeId == 3 ? 1 : 5, "scaling_list_pred_matrix_id_delta");
                    if (sizeId == 3)
                    {
                        delta *= 3;
                    }

                    if (delta > matrixId)
                    {
                        throw HevcError.Invalid("scaling_list_pred_matrix_id_delta out of range.");
                    }

                    if (delta == 0)
                    {
                        list.SetDefault(sizeId, matrixId);
                    }
                    else
                    {
                        int reference = matrixId - delta;
                        list.lists[sizeId][reference].CopyTo(destination, 0);
                        list.dc[sizeId, matrixId] = list.dc[sizeId, reference];
                    }

                    continue;
                }

                int next = 8;
                int count = sizeId == 0 ? 16 : 64;
                if (sizeId > 1)
                {
                    next = reader.ReadSe(-7, 247, "scaling_list_dc_coef_minus8") + 8;
                    list.dc[sizeId, matrixId] = next;
                }

                ReadOnlySpan<byte> scan = HevcScan.Get(sizeId == 0 ? 2 : 3, HevcScan.Diagonal);
                int stride = sizeId == 0 ? 4 : 8;
                for (int i = 0; i < count; i++)
                {
                    next = (next + reader.ReadSe(-128, 127, "scaling_list_delta_coef") + 256) % 256;
                    destination[(scan[i] & 15) + ((scan[i] >> 4) * stride)] = next;
                }

                if (sizeId <= 1)
                {
                    list.dc[sizeId, matrixId] = destination[0];
                }
            }
        }

        return list;
    }

    // ScalingFactor m[x][y] for a transform block of (1 << log2Size) samples, in raster order.
    public ReadOnlySpan<int> GetFactors(int log2Size, int matrixId)
    {
        factors ??= BuildFactors();
        return factors[log2Size - 2][matrixId];
    }

    private int[][][] BuildFactors()
    {
        var result = new int[4][][];
        for (int sizeId = 0; sizeId < 4; sizeId++)
        {
            int size = 4 << sizeId;
            int listSize = sizeId == 0 ? 4 : 8;
            int ratio = size / listSize;
            result[sizeId] = new int[6][];
            for (int matrixId = 0; matrixId < 6; matrixId++)
            {
                int[] factor = new int[size * size];
                int[] source = lists[sizeId][matrixId];
                for (int y = 0; y < size; y++)
                {
                    for (int x = 0; x < size; x++)
                    {
                        factor[(y * size) + x] = source[(listSize * (y / ratio)) + (x / ratio)];
                    }
                }

                if (ratio > 1)
                {
                    factor[0] = dc[sizeId, matrixId];
                }

                result[sizeId][matrixId] = factor;
            }
        }

        return result;
    }

    private void SetDefault(int sizeId, int matrixId)
    {
        int[] destination = lists[sizeId][matrixId];
        if (sizeId == 0)
        {
            destination.AsSpan().Fill(16);
        }
        else
        {
            (matrixId < 3 ? IntraDefault8x8 : InterDefault8x8).CopyTo(destination);
        }

        dc[sizeId, matrixId] = 16;
    }

    // Table 7-6 in raster order (port: HM source/Lib/TLibCommon/TComRom.cpp g_quantIntraDefault8x8).
    private static ReadOnlySpan<int> IntraDefault8x8 =>
    [
        16, 16, 16, 16, 17, 18, 21, 24,
        16, 16, 16, 16, 17, 19, 22, 25,
        16, 16, 17, 18, 20, 22, 25, 29,
        16, 16, 18, 21, 24, 27, 31, 36,
        17, 17, 20, 24, 30, 35, 41, 47,
        18, 19, 22, 27, 35, 44, 54, 65,
        21, 22, 25, 31, 41, 54, 70, 88,
        24, 25, 29, 36, 47, 65, 88, 115,
    ];

    // port: HM source/Lib/TLibCommon/TComRom.cpp g_quantInterDefault8x8
    private static ReadOnlySpan<int> InterDefault8x8 =>
    [
        16, 16, 16, 16, 17, 18, 20, 24,
        16, 16, 16, 17, 18, 20, 24, 25,
        16, 16, 17, 18, 20, 24, 25, 28,
        16, 17, 18, 20, 24, 25, 28, 33,
        17, 18, 20, 24, 25, 28, 33, 41,
        18, 20, 24, 25, 28, 33, 41, 54,
        20, 24, 25, 28, 33, 41, 54, 71,
        24, 25, 28, 33, 41, 54, 71, 91,
    ];
}
