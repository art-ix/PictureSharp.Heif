namespace PictureSharp.Formats.Heif.Hevc;

// Encodes one intra picture as a single IDR slice (CTB 32, CU 32..8, TU equal to the CU except intra NxN).
// Decisions follow HM's intra search: SATD pre-selection of luma modes, rate-distortion choice with CABAC rate
// estimation, and a rate-distortion split decision per coding unit (port: HM TEncCu::xCompressCU,
// TEncSearch::estIntraPredLumaQT / estIntraPredChromaQT in a reduced form). Reconstruction uses the decoder's own
// intra predictor, dequantization and inverse transforms, so the decoder reproduces it exactly.
internal sealed partial class HevcPictureEncoder : IHevcAvailability
{
    private const int MinCbLog2 = 3;

    private readonly HevcEncoderSettings settings;
    private readonly HevcPictureLayout layout;
    private readonly HevcIntraPredictor intra = new(strongIntraSmoothing: true);
    private readonly CancellationToken cancellationToken;
    private readonly int width;
    private readonly int height;
    private readonly int chromaFormat;
    private readonly ushort[][] source;
    private readonly ushort[][] recon;
    private readonly int[][] levels;
    private readonly int[] strides;
    private readonly int blocksWide;
    private readonly byte[] ctDepth;
    private readonly byte[] intraModes;
    private readonly byte[] partNxN;
    private readonly byte[] chromaSyntax;
    private readonly byte[] contexts = new byte[HevcContext.Count];
    private double lambda;
    private double sqrtLambda;
    private int qp;
    private int chromaQp;

    // cu_qp_delta state: QpY of the previous quantization group (qPY_PREV) and whether the current one coded its delta.
    private int lastQpY;
    private bool qpDeltaCoded;

    private HevcPictureEncoder(HevcEncoderSettings settings, ushort[][] planes, CancellationToken cancellationToken)
    {
        this.settings = settings;
        this.cancellationToken = cancellationToken;
        width = settings.CodedWidth;
        height = settings.CodedHeight;
        chromaFormat = settings.ChromaFormatIdc;
        source = planes;
        int planeCount = chromaFormat == 0 ? 1 : 3;
        strides = new int[planeCount];
        recon = new ushort[planeCount][];
        levels = new int[planeCount][];
        for (int c = 0; c < planeCount; c++)
        {
            strides[c] = c == 0 ? width : width / 2;
            int samples = c == 0 ? width * height : width * height / 4;
            recon[c] = new ushort[samples];
            levels[c] = new int[samples];
        }

        // The layout is derived from the written parameter sets, so encoder and decoder agree on block addressing.
        var sps = HevcSequenceParameterSet.Parse(Rbsp(HevcParameterSetWriter.Sps(settings)));
        var pps = HevcPictureParameterSet.Parse(Rbsp(HevcParameterSetWriter.Pps(settings)));
        layout = new HevcPictureLayout(sps, pps);
        blocksWide = width >> 2;
        int blocks = blocksWide * (height >> 2);
        ctDepth = new byte[blocks];
        intraModes = new byte[blocks];
        partNxN = new byte[blocks];
        chromaSyntax = new byte[blocks];
        SetQp(settings.Qp);
    }

    private void SetQp(int value)
    {
        qp = value;
        chromaQp = HevcTransform.ChromaQp420(qp);
        // HM intra lambda: 0.57 * 2^((QP - 12) / 3).
        lambda = 0.57 * Math.Pow(2, (qp - 12) / 3.0);
        sqrtLambda = Math.Sqrt(lambda);
    }

    // port: kvazaar src/encoderstate.c (variance adaptive quantization): d * (ln var(CTB) - ln var(reference)) with
    // the CTB variance summed over luma and both chroma planes, clipped to half of the cu_qp_delta range.
    private int CtbQp(int x0, int y0, int ctbSize)
    {
        if (!settings.AdaptiveQuantization)
        {
            return settings.Qp;
        }

        double variance = Variance(source[0], strides[0], x0, y0, ctbSize, width, height);
        if (chromaFormat != 0)
        {
            variance += Variance(source[1], strides[1], x0 >> 1, y0 >> 1, ctbSize >> 1, width >> 1, height >> 1);
            variance += Variance(source[2], strides[2], x0 >> 1, y0 >> 1, ctbSize >> 1, width >> 1, height >> 1);
        }

        double offset = settings.AqStrength * (Math.Log(Math.Max(variance, 1e-3)) - settings.AqReferenceLogVariance);
        int delta = Math.Clamp((int)Math.Round(offset, MidpointRounding.AwayFromZero), -13, 12);
        return Math.Clamp(settings.Qp + delta, 0, 51);
    }

    // Variance of a block, repeating edge samples outside the plane as kvazaar does.
    internal static double Variance(ushort[] plane, int stride, int x0, int y0, int size, int planeWidth, int planeHeight)
    {
        double sum = 0;
        double squares = 0;
        for (int y = 0; y < size; y++)
        {
            int row = Math.Min(y0 + y, planeHeight - 1) * stride;
            for (int x = 0; x < size; x++)
            {
                double value = plane[row + Math.Min(x0 + x, planeWidth - 1)];
                sum += value;
                squares += value * value;
            }
        }

        double count = (double)size * size;
        double mean = sum / count;
        return Math.Max(0, (squares / count) - (mean * mean));
    }

    // Returns VPS, SPS, PPS and the slice NAL unit. planes hold the picture at the coded size (edges replicated).
    public static List<byte[]> Encode(HevcEncoderSettings settings, ushort[][] planes, CancellationToken cancellationToken) =>
        EncodeWithReconstruction(settings, planes, cancellationToken).NalUnits;

    // The NAL units and the reconstruction before in-loop filtering (used by tests).
    internal static (List<byte[]> NalUnits, ushort[][] Reconstruction) EncodeWithReconstruction(HevcEncoderSettings settings,
        ushort[][] planes, CancellationToken cancellationToken)
    {
        if (settings.ChromaFormatIdc == 1 && ((settings.Width | settings.Height) & 1) != 0)
        {
            throw new ArgumentException("4:2:0 pictures must have even dimensions.", nameof(settings));
        }

        var encoder = new HevcPictureEncoder(settings, planes, cancellationToken);
        byte[] slice = encoder.EncodeSlice();
        return ([HevcParameterSetWriter.Vps(settings), HevcParameterSetWriter.Sps(settings), HevcParameterSetWriter.Pps(settings), slice],
            encoder.recon);
    }

    public bool IsAvailable(int xCurr, int yCurr, int xN, int yN) =>
        xN >= 0 && yN >= 0 && xN < width && yN < height && layout.ZscanAddress(xN, yN) <= layout.ZscanAddress(xCurr, yCurr);

    // Decides and writes every CTB; with SAO the slice is written a second time from the stored decisions, preceded
    // per CTB by SAO parameters estimated on the deblocked picture of the first pass (HM likewise decides SAO after
    // compressing all CTUs and entropy-codes the slice afterwards).
    private byte[] EncodeSlice()
    {
        int[] ctbQps = new int[layout.CtbCount];
        byte[] slice = WriteSlice(ctbQps, search: true, sao: null);
        if (!settings.SaoEnabled)
        {
            return slice;
        }

        HevcSaoParameters[] sao = EstimateSao(slice, ctbQps);
        return WriteSlice(ctbQps, search: false, sao);
    }

    private byte[] WriteSlice(int[] ctbQps, bool search, HevcSaoParameters[]? sao)
    {
        HevcContext.Initialize(contexts, settings.Qp);
        var cabac = new HevcCabacEncoder();
        int ctbSize = 1 << settings.Log2CtbSize;
        int components = chromaFormat == 0 ? 1 : 3;
        lastQpY = settings.Qp;
        for (int ctb = 0; ctb < layout.CtbCount; ctb++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int rx = ctb % layout.WidthInCtbs;
            int x = rx * ctbSize;
            int y = (ctb / layout.WidthInCtbs) * ctbSize;
            if (search)
            {
                ctbQps[ctb] = CtbQp(x, y, ctbSize);
            }

            SetQp(ctbQps[ctb]);
            if (search)
            {
                qpDeltaCoded = false;
                byte[] searchContexts = (byte[])contexts.Clone();
                SearchCodingQuadtree(x, y, settings.Log2CtbSize, 0, searchContexts);
            }

            if (sao is not null)
            {
                HevcSyntaxWriter.Sao(cabac, contexts, sao[ctb], rx > 0, ctb >= layout.WidthInCtbs, components);
            }

            qpDeltaCoded = false;
            WriteCodingQuadtree(cabac, contexts, x, y, settings.Log2CtbSize, 0);
            // 8.6.1: the next quantization group predicts from this one's QpY, which is the coded QP when a delta was
            // sent and the prediction itself otherwise (with one group per CTB, qPY_PRED is qPY_PREV).
            if (qpDeltaCoded)
            {
                lastQpY = qp;
            }

            cabac.EncodeTerminate(ctb == layout.CtbCount - 1 ? 1 : 0);
        }

        cabac.FinishAndAlign();
        HevcBitWriter slice = HevcParameterSetWriter.SliceHeader(settings, sao is not null);
        slice.WriteBytes(cabac.Output);
        return slice.ToNalUnit(HevcParameterSetWriter.IdrNalType);
    }

    // Deblocks the first-pass picture with the decoder itself, then chooses SAO parameters per CTB in raster order.
    private HevcSaoParameters[] EstimateSao(byte[] slice, int[] ctbQps)
    {
        HevcFrame deblocked = HevcDecoder.Decode(
            [HevcParameterSetWriter.Vps(settings), HevcParameterSetWriter.Sps(settings), HevcParameterSetWriter.Pps(settings), slice],
            null, cancellationToken);
        var estimator = new HevcSaoEstimator(source, deblocked.Planes, strides, width, height, settings.Log2CtbSize);
        var sao = new HevcSaoParameters[layout.CtbCount];
        int components = chromaFormat == 0 ? 1 : 3;
        for (int ctb = 0; ctb < layout.CtbCount; ctb++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int rx = ctb % layout.WidthInCtbs;
            int ry = ctb / layout.WidthInCtbs;
            SetQp(ctbQps[ctb]);
            sao[ctb] = estimator.Estimate(rx, ry, components, lambda, rx > 0 ? sao[ctb - 1] : null,
                ry > 0 ? sao[ctb - layout.WidthInCtbs] : null);
        }

        return sao;
    }

    private static byte[] Rbsp(byte[] nal)
    {
        byte[] rbsp = new byte[nal.Length];
        int length = HevcNalHeader.ExtractRbsp(nal, rbsp);
        return rbsp[..length];
    }

    private int BlockIndex(int x, int y) => ((y >> 2) * blocksWide) + (x >> 2);

    private void Fill(byte[] map, int x0, int y0, int size, byte value)
    {
        for (int j = 0; j < size >> 2; j++)
        {
            map.AsSpan(BlockIndex(x0, y0 + (j << 2)), size >> 2).Fill(value);
        }
    }

    // 8.4.2 most probable mode candidates for the prediction block at (xPb, yPb), as the decoder derives them.
    private void MpmCandidates(int xPb, int yPb, Span<int> candidates)
    {
        int candA = IsAvailable(xPb, yPb, xPb - 1, yPb) ? intraModes[BlockIndex(xPb - 1, yPb)] : 1;
        int candB = 1;
        if (IsAvailable(xPb, yPb, xPb, yPb - 1) && yPb - 1 >= ((yPb >> settings.Log2CtbSize) << settings.Log2CtbSize))
        {
            candB = intraModes[BlockIndex(xPb, yPb - 1)];
        }

        if (candA == candB)
        {
            if (candA < 2)
            {
                candidates[0] = 0;
                candidates[1] = 1;
                candidates[2] = 26;
            }
            else
            {
                candidates[0] = candA;
                candidates[1] = 2 + ((candA + 29) % 32);
                candidates[2] = 2 + ((candA - 2 + 1) % 32);
            }
        }
        else
        {
            candidates[0] = candA;
            candidates[1] = candB;
            candidates[2] = candA != 0 && candB != 0 ? 0 : candA != 1 && candB != 1 ? 1 : 26;
        }
    }

    private static int ChromaModeFromSyntax(int syntax, int lumaMode)
    {
        if (syntax == 4)
        {
            return lumaMode;
        }

        int mode = syntax switch { 0 => 0, 1 => 26, 2 => 10, _ => 1 };
        return mode == lumaMode ? 34 : mode;
    }

    // 7.4.9.11 scanIdx for intra blocks of 4x4 and luma 8x8.
    private static int ScanIndex(int mode, int log2Size, bool luma)
    {
        if (log2Size == 2 || (log2Size == 3 && luma))
        {
            if (mode is >= 6 and <= 14)
            {
                return HevcScan.Vertical;
            }

            if (mode is >= 22 and <= 30)
            {
                return HevcScan.Horizontal;
            }
        }

        return HevcScan.Diagonal;
    }
}
