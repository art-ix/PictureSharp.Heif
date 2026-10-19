using System.Runtime.ExceptionServices;

namespace PictureSharp.Formats.Heif.Hevc;

// Decodes the slice segments of one intra picture: CABAC parsing, reconstruction, deblocking and SAO.
// Syntax follows H.265 7.3.8; parsing and reconstruction details are ported from HM 16.20 (TDecSlice, TDecCu,
// TDecSbac, TDecEntropy), cited per method.
internal sealed partial class HevcPictureDecoder : IHevcAvailability
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

    private HevcCabacDecoder cabac;
    private readonly byte[] contexts = new byte[HevcContext.Count];
    private readonly byte[] wppContexts = new byte[HevcContext.Count];
    private readonly byte[] dependentContexts = new byte[HevcContext.Count];
    private bool dependentContextsStored;

    private readonly int[] coefficients = new int[32 * 32];

    // Positions ParseResidualCoding wrote into coefficients; only these can be nonzero when the next block starts.
    private readonly int[] writtenCoefficients = new int[32 * 32];
    private int writtenCoefficientCount;
    private readonly int[] residual = new int[32 * 32];
    private readonly int[] temporary = new int[32 * 32];
    private readonly HevcIntraPredictor intra;

    private HevcSliceHeader slice = null!;
    private int sliceIndex = -1;
    private int decodedCtbs;
    private int currentCtbAddrTs;
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
        intra = new HevcIntraPredictor(sps.StrongIntraSmoothingEnabled);
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

    // A wavefront worker: shares the picture, its block and CTB maps and the slice list with owner, and has its own
    // entropy decoder, contexts, coefficient buffers, intra predictor and QP state.
    private HevcPictureDecoder(HevcPictureDecoder owner)
    {
        sps = owner.sps;
        pps = owner.pps;
        cancellationToken = owner.cancellationToken;
        layout = owner.layout;
        frame = owner.frame;
        scalingList = owner.scalingList;
        intra = new HevcIntraPredictor(sps.StrongIntraSmoothingEnabled);
        blocksWide = owner.blocksWide;
        blocksHigh = owner.blocksHigh;
        ctDepth = owner.ctDepth;
        intraModes = owner.intraModes;
        qpMap = owner.qpMap;
        blockFlags = owner.blockFlags;
        ctbSliceAddress = owner.ctbSliceAddress;
        ctbSliceIndex = owner.ctbSliceIndex;
        saoTypes = owner.saoTypes;
        saoClasses = owner.saoClasses;
        saoOffsets = owner.saoOffsets;
        slices = owner.slices;
        log2MinCuQpDeltaSize = owner.log2MinCuQpDeltaSize;
        slice = owner.slice;
        sliceIndex = owner.sliceIndex;
    }

    public HevcPictureParameterSet Pps => pps;

    // Decodes one slice segment NAL unit (RBSP with the two-byte header removed). removed lists the RBSP positions of
    // the emulation prevention bytes taken out of the NAL unit (HevcNalHeader.ExtractRbsp); with it and parallel, a
    // slice segment covering the whole picture with wavefront entry points decodes its CTB rows in parallel.
    public void DecodeSliceSegment(byte[] rbsp, int length, HevcNalHeader nal, List<int>? removed = null, ParallelOptions? parallel = null)
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

        if (parallel is not null && removed is not null && SubstreamStarts(header, length, removed) is { } starts
            && DecodeWavefronts(rbsp, starts, parallel))
        {
            return;
        }

        cabac.Reset(rbsp, header.DataOffset, length - header.DataOffset);
        int ctbAddrRs = header.SegmentAddress;
        BeginCtb(ctbAddrRs);
        InitializeContexts(ctbAddrTs, ctbAddrRs, header.Dependent);
        cabac.Start();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            currentCtbAddrTs = ctbAddrTs;
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

    // RBSP start of each CTB row's substream plus the end of the slice data, or null when the segment is not one
    // whole-picture wavefront slice segment or its entry points are inconsistent (then it decodes sequentially, as
    // before entry points were used). Entry points count emulation prevention bytes (7.4.7.1).
    private int[]? SubstreamStarts(HevcSliceHeader header, int length, List<int> removed)
    {
        int rows = layout.HeightInCtbs;
        uint[] offsets = header.EntryPointOffsetsMinus1;
        if (!pps.EntropyCodingSyncEnabled || pps.TilesEnabled || header.SegmentAddress != 0 || rows < 2
            || offsets.Length != rows - 1)
        {
            return null;
        }

        // The emulation prevention byte removed before RBSP position removed[i] has payload position removed[i] + i.
        int before = 0;
        while (before < removed.Count && removed[before] <= header.DataOffset)
        {
            before++;
        }

        var starts = new int[rows + 1];
        starts[0] = header.DataOffset;
        starts[rows] = length;
        long payload = header.DataOffset + before;
        int skipped = 0;
        for (int row = 1; row < rows; row++)
        {
            payload += offsets[row - 1] + 1L;
            while (skipped < removed.Count && removed[skipped] + skipped < payload)
            {
                skipped++;
            }

            long start = payload - skipped;
            if (start <= starts[row - 1] || start >= length)
            {
                return null;
            }

            starts[row] = (int)start;
        }

        return starts;
    }

    // 9.3.1 / 9.3.2.2 wavefront parallel decoding of a whole-picture slice segment: workers take CTB rows in order, and
    // CTB (x, y) starts once row y - 1 has finished CTB x + 1, so every neighbour the CTB reads (and the contexts saved
    // after the second CTB of the row above) is complete. Workers write only their own CTBs' samples and map entries.
    // Each row must end exactly where the next entry point starts, which is where sequential parsing would continue,
    // so a successful parallel decode equals the sequential one. Returns false after invalid data (wrong entry points
    // or a corrupt row) with the maps reset: the caller then decodes sequentially, which gives the same pixels or the
    // same error as without parallelism.
    private bool DecodeWavefronts(byte[] rbsp, int[] starts, ParallelOptions parallel)
    {
        int rows = layout.HeightInCtbs;
        var progress = new int[rows];
        var rowContexts = new byte[rows * HevcContext.Count];
        var errors = new ExceptionDispatchInfo?[rows];
        int nextRow = -1;
        int failed = 0;
        int workers = Math.Min(rows, parallel.MaxDegreeOfParallelism > 0 ? parallel.MaxDegreeOfParallelism : Environment.ProcessorCount);
        Parallel.For(0, workers, parallel, _ =>
        {
            var worker = new HevcPictureDecoder(this);
            int row;
            while (Volatile.Read(ref failed) == 0 && (row = Interlocked.Increment(ref nextRow)) < rows)
            {
                try
                {
                    worker.DecodeWavefrontRow(row, rbsp, starts, progress, rowContexts, ref failed);
                }
                catch (Exception exception)
                {
                    errors[row] = ExceptionDispatchInfo.Capture(exception);
                    Volatile.Write(ref failed, 1);
                }
            }
        });

        foreach (ExceptionDispatchInfo? error in errors)
        {
            if (error is null)
            {
                continue;
            }

            if (error.SourceException is not ImageFormatException)
            {
                // Cancellation, or a failure that does not depend on the data.
                error.Throw();
            }

            blockFlags.AsSpan().Clear();
            ctbSliceAddress.AsSpan().Fill(-1);
            return false;
        }

        decodedCtbs = layout.CtbCount;
        return true;
    }

    private void DecodeWavefrontRow(int row, byte[] rbsp, int[] starts, int[] progress, byte[] rowContexts, ref int failed)
    {
        int width = layout.WidthInCtbs;
        int rows = layout.HeightInCtbs;
        cabac.Reset(rbsp, starts[row], starts[row + 1] - starts[row]);
        for (int x = 0; x < width; x++)
        {
            if (row > 0)
            {
                int needed = Math.Min(x + 2, width);
                var spin = new SpinWait();
                while (Volatile.Read(ref progress[row - 1]) < needed)
                {
                    if (Volatile.Read(ref failed) != 0)
                    {
                        return;
                    }

                    spin.SpinOnce(sleep1Threshold: -1);
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            int ctbAddrRs = (row * width) + x;
            BeginCtb(ctbAddrRs);
            if (x == 0)
            {
                // InitializeContexts: the first row starts the slice; later rows take the contexts stored after the
                // second CTB of the row above when that CTB exists.
                lastQpY = slice.SliceQpY;
                if (row > 0 && width > 1)
                {
                    rowContexts.AsSpan((row - 1) * HevcContext.Count, HevcContext.Count).CopyTo(contexts);
                }
                else
                {
                    HevcContext.Initialize(contexts, slice.SliceQpY);
                }

                cabac.Start();
            }

            currentCtbAddrTs = ctbAddrRs;
            DecodeCodingTreeUnit(ctbAddrRs);
            if (x == 1)
            {
                contexts.CopyTo(rowContexts, row * HevcContext.Count);
            }

            bool endOfSliceSegment = cabac.DecodeTerminate() != 0;
            if (x < width - 1 || row < rows - 1)
            {
                if (endOfSliceSegment)
                {
                    throw HevcError.Invalid("the picture is missing slice segments.");
                }

                if (x == width - 1 && (cabac.DecodeTerminate() == 0 || cabac.Position != starts[row + 1]))
                {
                    throw HevcError.Invalid("CTB row does not end at the next entry point.");
                }
            }
            else if (!endOfSliceSegment)
            {
                throw HevcError.Invalid("slice data continues past the last coding tree block.");
            }

            Volatile.Write(ref progress[row], x + 1);
        }

        if (cabac.Overrun > 0)
        {
            throw HevcError.Invalid("truncated slice segment data.");
        }
    }

    // Deblocking and SAO; with parallel they run in bands of rows (same samples).
    public HevcFrame Finish(ParallelOptions? parallel = null)
    {
        if (decodedCtbs != layout.CtbCount)
        {
            throw HevcError.Invalid("the picture is missing slice segments.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        Deblock(parallel);
        cancellationToken.ThrowIfCancellationRequested();
        ApplySao(parallel);
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
    public bool IsAvailable(int xCurr, int yCurr, int xN, int yN)
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
