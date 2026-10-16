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
    private readonly double lambda;
    private readonly double sqrtLambda;
    private readonly int qp;
    private readonly int chromaQp;

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
        qp = settings.Qp;
        chromaQp = HevcTransform.ChromaQp420(qp);
        // HM intra lambda: 0.57 * 2^((QP - 12) / 3).
        lambda = 0.57 * Math.Pow(2, (qp - 12) / 3.0);
        sqrtLambda = Math.Sqrt(lambda);
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

    private byte[] EncodeSlice()
    {
        HevcContext.Initialize(contexts, qp);
        var cabac = new HevcCabacEncoder();
        int ctbSize = 1 << settings.Log2CtbSize;
        for (int ctb = 0; ctb < layout.CtbCount; ctb++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int x = (ctb % layout.WidthInCtbs) * ctbSize;
            int y = (ctb / layout.WidthInCtbs) * ctbSize;
            byte[] searchContexts = (byte[])contexts.Clone();
            SearchCodingQuadtree(x, y, settings.Log2CtbSize, 0, searchContexts);
            WriteCodingQuadtree(cabac, contexts, x, y, settings.Log2CtbSize, 0);
            cabac.EncodeTerminate(ctb == layout.CtbCount - 1 ? 1 : 0);
        }

        cabac.FinishAndAlign();
        HevcBitWriter slice = HevcParameterSetWriter.SliceHeader(settings);
        slice.WriteBytes(cabac.Output);
        return slice.ToNalUnit(HevcParameterSetWriter.IdrNalType);
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
