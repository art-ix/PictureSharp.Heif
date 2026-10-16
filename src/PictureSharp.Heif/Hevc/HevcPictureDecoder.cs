namespace PictureSharp.Formats.Heif.Hevc;

// Decodes the slice segments of one intra picture: CABAC parsing, reconstruction, deblocking and SAO.
// Syntax follows H.265 7.3.8; parsing and reconstruction details are ported from HM 16.20 (TDecSlice, TDecCu,
// TDecSbac, TDecEntropy), cited per method.
internal sealed partial class HevcPictureDecoder
{
    private const byte EdgeVertical = 1;
    private const byte EdgeHorizontal = 2;
    private const byte NoLoopFilter = 4;

    private readonly HevcSequenceParameterSet sps;
    private readonly HevcPictureParameterSet pps;
    private readonly HevcPictureLayout layout;
    private readonly HevcFrame frame;
    private readonly CancellationToken cancellationToken;
    private readonly HevcScalingList? scalingList;

    // Per 4x4 luma block.
    private readonly int blocksWide;
    private readonly int blocksHigh;
    private readonly byte[] ctDepth;
    private readonly byte[] intraModes;
    private readonly sbyte[] qpMap;
    private readonly byte[] blockFlags;

    // Per CTB (raster address).
    private readonly int[] ctbSliceAddress;
    private readonly int[] ctbSliceIndex;
    private readonly byte[] saoTypes;
    private readonly byte[] saoClasses;
    private readonly short[] saoOffsets;
    private readonly List<HevcSliceHeader> slices = [];

    private readonly HevcCabacDecoder cabac = new();
    private readonly byte[] contexts = new byte[HevcContext.Count];
    private readonly byte[] wppContexts = new byte[HevcContext.Count];
    private readonly byte[] dependentContexts = new byte[HevcContext.Count];
    private bool dependentContextsStored;

    private readonly int[] coefficients = new int[32 * 32];
    private readonly int[] residual = new int[32 * 32];
    private readonly int[] temporary = new int[32 * 32];
    private readonly int[] referenceTop = new int[129];
    private readonly int[] referenceLeft = new int[129];
    private readonly int[] filteredTop = new int[129];
    private readonly int[] filteredLeft = new int[129];
    private readonly int[] prediction = new int[32 * 32];

    private HevcSliceHeader slice = null!;
    private int sliceIndex = -1;
    private int decodedCtbs;
    private int lastQpY;
    private int qpYPred;
    private int qpY;
    private bool isCuQpDeltaCoded;
    private int cuQpDeltaVal;
    private bool cuTransquantBypass;
    private int log2MinCuQpDeltaSize;

    public HevcPictureDecoder(HevcSequenceParameterSet sps, HevcPictureParameterSet pps, CancellationToken cancellationToken)
    {
        this.sps = sps;
        this.pps = pps;
        this.cancellationToken = cancellationToken;
        layout = new HevcPictureLayout(sps, pps);
        frame = new HevcFrame(sps);
        scalingList = sps.ScalingListEnabled ? pps.ScalingList ?? sps.ScalingList : null;
        blocksWide = sps.Width >> 2;
        blocksHigh = sps.Height >> 2;
        int blocks = blocksWide * blocksHigh;
        ctDepth = new byte[blocks];
        intraModes = new byte[blocks];
        qpMap = new sbyte[blocks];
        blockFlags = new byte[blocks];
        ctbSliceAddress = new int[layout.CtbCount];
        ctbSliceAddress.AsSpan().Fill(-1);
        ctbSliceIndex = new int[layout.CtbCount];
        saoTypes = new byte[layout.CtbCount * 3];
        saoClasses = new byte[layout.CtbCount * 3];
        saoOffsets = new short[layout.CtbCount * 12];
        log2MinCuQpDeltaSize = sps.Log2CtbSize - pps.DiffCuQpDeltaDepth;
    }

    public HevcPictureParameterSet Pps => pps;

    // Decodes one slice segment NAL unit (RBSP with the two-byte header removed).
    public void DecodeSliceSegment(byte[] rbsp, int length, HevcNalHeader nal)
    {
        HevcSliceHeader header = HevcSliceHeader.Parse(rbsp.AsSpan(0, length), nal, sps, pps, layout,
            slices.Count > 0 ? slices[^1] : null);
        if (header.FirstSliceSegmentInPic != (decodedCtbs == 0))
        {
            throw HevcError.Invalid("slice segments are not in decoding order.");
        }

        int ctbAddrTs = layout.RsToTs[header.SegmentAddress];
        if (ctbAddrTs != decodedCtbs)
        {
            throw HevcError.Invalid("slice segment does not continue the previous one.");
        }

        slice = header;
        slices.Add(header);
        sliceIndex = slices.Count - 1;
        if (!header.Dependent)
        {
            lastQpY = header.SliceQpY;
        }

        cabac.Reset(rbsp, header.DataOffset, length - header.DataOffset);
        int ctbAddrRs = header.SegmentAddress;
        BeginCtb(ctbAddrRs);
        InitializeContexts(ctbAddrTs, ctbAddrRs, header.Dependent);
        cabac.Start();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DecodeCodingTreeUnit(ctbAddrRs);
            decodedCtbs++;
            if (pps.EntropyCodingSyncEnabled && StoresWppContexts(ctbAddrTs, ctbAddrRs))
            {
                contexts.CopyTo(wppContexts, 0);
            }

            bool endOfSliceSegment = cabac.DecodeTerminate() != 0;
            ctbAddrTs++;
            if (endOfSliceSegment)
            {
                if (pps.DependentSliceSegmentsEnabled)
                {
                    contexts.CopyTo(dependentContexts, 0);
                    dependentContextsStored = true;
                }

                break;
            }

            if (ctbAddrTs >= layout.CtbCount)
            {
                throw HevcError.Invalid("slice data continues past the last coding tree block.");
            }

            ctbAddrRs = layout.TsToRs[ctbAddrTs];
            BeginCtb(ctbAddrRs);
            bool newTile = pps.TilesEnabled && layout.TileIdTs[ctbAddrTs] != layout.TileIdTs[ctbAddrTs - 1];
            bool newRow = pps.EntropyCodingSyncEnabled && (ctbAddrRs % layout.WidthInCtbs == 0
                || layout.TileIdTs[ctbAddrTs] != layout.TileIdOfRs(ctbAddrRs - 1));
            if (newTile || newRow)
            {
                if (cabac.DecodeTerminate() == 0)
                {
                    throw HevcError.Invalid("missing end_of_subset_one_bit.");
                }

                InitializeContexts(ctbAddrTs, ctbAddrRs, false);
                cabac.Start();
            }
        }

        if (cabac.Overrun > 0)
        {
            throw HevcError.Invalid("truncated slice segment data.");
        }
    }

    public HevcFrame Finish()
    {
        if (decodedCtbs != layout.CtbCount)
        {
            throw HevcError.Invalid("the picture is missing slice segments.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        Deblock();
        cancellationToken.ThrowIfCancellationRequested();
        ApplySao();
        return frame;
    }

    // H.265 9.3.1 (port: HM TDecSlice::decompressSlice): context initialization at slice, tile and WPP row starts.
    private void InitializeContexts(int ctbAddrTs, int ctbAddrRs, bool dependent)
    {
        bool firstInTile = ctbAddrTs == 0 || layout.TileIdTs[ctbAddrTs] != layout.TileIdTs[ctbAddrTs - 1];
        if (firstInTile)
        {
            HevcContext.Initialize(contexts, slice.SliceQpY);
            lastQpY = slice.SliceQpY;
            return;
        }

        if (pps.EntropyCodingSyncEnabled && (ctbAddrRs % layout.WidthInCtbs == 0
            || layout.TileIdTs[ctbAddrTs] != layout.TileIdOfRs(ctbAddrRs - 1)))
        {
            lastQpY = slice.SliceQpY;
            int size = sps.CtbSize;
            int x = (ctbAddrRs % layout.WidthInCtbs) << sps.Log2CtbSize;
            int y = (ctbAddrRs / layout.WidthInCtbs) << sps.Log2CtbSize;
            if (IsAvailable(x, y, x + size, y - size))
            {
                wppContexts.CopyTo(contexts, 0);
            }
            else
            {
                HevcContext.Initialize(contexts, slice.SliceQpY);
            }

            return;
        }

        if (dependent)
        {
            if (!dependentContextsStored)
            {
                throw HevcError.Invalid("dependent slice segment without stored contexts.");
            }

            dependentContexts.CopyTo(contexts, 0);
            return;
        }

        HevcContext.Initialize(contexts, slice.SliceQpY);
    }

    // 9.3.2.2 storage condition: after the second CTB of a row, or after a CTB whose raster predecessor's predecessor
    // lies in another tile (the spec's literal condition; the last store before the next row wins).
    private bool StoresWppContexts(int ctbAddrTs, int ctbAddrRs) =>
        ctbAddrRs % layout.WidthInCtbs == 1
        || (ctbAddrRs > 1 && layout.TileIdTs[ctbAddrTs] != layout.TileIdOfRs(ctbAddrRs - 2));

    private void BeginCtb(int ctbAddrRs)
    {
        ctbSliceAddress[ctbAddrRs] = slice.SliceAddress;
        ctbSliceIndex[ctbAddrRs] = sliceIndex;
    }

    // H.265 6.4.1 z-scan order block availability.
    private bool IsAvailable(int xCurr, int yCurr, int xN, int yN)
    {
        if (xN < 0 || yN < 0 || xN >= sps.Width || yN >= sps.Height)
        {
            return false;
        }

        if (layout.ZscanAddress(xN, yN) > layout.ZscanAddress(xCurr, yCurr))
        {
            return false;
        }

        int ctbN = ((yN >> sps.Log2CtbSize) * layout.WidthInCtbs) + (xN >> sps.Log2CtbSize);
        int ctbCurr = ((yCurr >> sps.Log2CtbSize) * layout.WidthInCtbs) + (xCurr >> sps.Log2CtbSize);
        return ctbSliceAddress[ctbN] == ctbSliceAddress[ctbCurr] && layout.TileIdOfRs(ctbN) == layout.TileIdOfRs(ctbCurr);
    }

    private int BlockIndex(int x, int y) => ((y >> 2) * blocksWide) + (x >> 2);

    private void FillBlocks(byte[] map, int x0, int y0, int size, byte value)
    {
        int blocks = size >> 2;
        for (int j = 0; j < blocks; j++)
        {
            map.AsSpan(BlockIndex(x0, y0 + (j << 2)), blocks).Fill(value);
        }
    }
}
