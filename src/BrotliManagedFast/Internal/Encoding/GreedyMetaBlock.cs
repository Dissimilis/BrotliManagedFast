using System;

namespace BrotliManagedFast.Internal;

/// <summary>
/// One-pass block splitting for the fast quality tiers (the reference's BrotliBuildMetaBlockGreedy and the
/// greedy BlockSplitter of metablock_inc.h). Symbols are added in stream order; every time the current block
/// reaches its target size the splitter either starts a new block type, reuses the second last one, or merges
/// into the last one, deciding from the entropy of the combined histograms. Unlike the quality 10 and 11
/// splitter it never revisits an assignment, so it costs one pass instead of several plus clustering.
/// </summary>
internal sealed class GreedyBlockSplitter
{
    private readonly int _alphabetSize;
    private readonly int _minBlockSize;
    private readonly double _splitThreshold;
    private readonly BlockSplit _split;
    private readonly Histogram[] _histograms;
    private readonly Histogram[] _combined = new Histogram[2];
    private readonly double[] _lastEntropy = new double[2];
    private readonly int[] _lastHistogramIx = new int[2];

    private int _numBlocks;
    private int _histogramsSize;
    private int _targetBlockSize;
    private int _blockSize;
    private int _currHistogramIx;
    private int _mergeLastCount;

    // histogramSize is the width of the histograms and must cover every symbol that can be added; alphabetSize
    // is the prefix the entropy estimate looks at, which the reference keeps smaller for distances: it judges
    // on the first 64 symbols although the alphabet runs to several hundred.
    public GreedyBlockSplitter(int histogramSize, int alphabetSize, int minBlockSize, double splitThreshold, int numSymbols, BlockSplit split)
    {
        _alphabetSize = alphabetSize;
        _minBlockSize = minBlockSize;
        _splitThreshold = splitThreshold;
        _split = split;
        _targetBlockSize = minBlockSize;

        int maxNumBlocks = numSymbols / minBlockSize + 1;
        // One more histogram than the block-type limit, for the current block when the metablock is too big.
        int maxNumTypes = Math.Min(maxNumBlocks, Constants.MaxBlockTypes + 1);
        split.EnsureCapacity(maxNumBlocks);
        split.NumBlocks = maxNumBlocks;
        _histogramsSize = maxNumTypes;
        _histograms = Histogram.Create(maxNumTypes, histogramSize);
        _combined[0] = new Histogram(histogramSize);
        _combined[1] = new Histogram(histogramSize);
        _histograms[0].Clear();
    }

    public Histogram[] Histograms => _histograms;
    public int HistogramsSize => _histogramsSize;

    public void AddSymbol(int symbol)
    {
        _histograms[_currHistogramIx].Add(symbol);
        if (++_blockSize == _targetBlockSize) FinishBlock(false);
    }

    public void FinishBlock(bool isFinal)
    {
        BlockSplit split = _split;
        if (_blockSize < _minBlockSize) _blockSize = _minBlockSize;
        if (_numBlocks == 0)
        {
            split.Lengths[0] = (uint)_blockSize;
            split.Types[0] = 0;
            _lastEntropy[0] = BitCost.BitsEntropy(_histograms[0].Data.AsSpan(0, _alphabetSize));
            _lastEntropy[1] = _lastEntropy[0];
            _numBlocks++;
            split.NumTypes++;
            _currHistogramIx++;
            if (_currHistogramIx < _histogramsSize) _histograms[_currHistogramIx].Clear();
            _blockSize = 0;
        }
        else if (_blockSize > 0)
        {
            double entropy = BitCost.BitsEntropy(_histograms[_currHistogramIx].Data.AsSpan(0, _alphabetSize));
            Span<double> combinedEntropy = stackalloc double[2];
            Span<double> diff = stackalloc double[2];
            for (int j = 0; j < 2; ++j)
            {
                _combined[j].CopyFrom(_histograms[_currHistogramIx]);
                _combined[j].AddHistogram(_histograms[_lastHistogramIx[j]]);
                combinedEntropy[j] = BitCost.BitsEntropy(_combined[j].Data.AsSpan(0, _alphabetSize));
                diff[j] = combinedEntropy[j] - entropy - _lastEntropy[j];
            }

            if (split.NumTypes < Constants.MaxBlockTypes && diff[0] > _splitThreshold && diff[1] > _splitThreshold)
            {
                // A new block type.
                split.Lengths[_numBlocks] = (uint)_blockSize;
                split.Types[_numBlocks] = (byte)split.NumTypes;
                _lastHistogramIx[1] = _lastHistogramIx[0];
                _lastHistogramIx[0] = split.NumTypes;
                _lastEntropy[1] = _lastEntropy[0];
                _lastEntropy[0] = entropy;
                _numBlocks++;
                split.NumTypes++;
                _currHistogramIx++;
                if (_currHistogramIx < _histogramsSize) _histograms[_currHistogramIx].Clear();
                _blockSize = 0;
                _mergeLastCount = 0;
                _targetBlockSize = _minBlockSize;
            }
            else if (diff[1] < diff[0] - 20.0)
            {
                // The second last block type fits better than a new one.
                split.Lengths[_numBlocks] = (uint)_blockSize;
                split.Types[_numBlocks] = split.Types[_numBlocks - 2];
                (_lastHistogramIx[0], _lastHistogramIx[1]) = (_lastHistogramIx[1], _lastHistogramIx[0]);
                _histograms[_lastHistogramIx[0]].CopyFrom(_combined[1]);
                _lastEntropy[1] = _lastEntropy[0];
                _lastEntropy[0] = combinedEntropy[1];
                _numBlocks++;
                _blockSize = 0;
                _histograms[_currHistogramIx].Clear();
                _mergeLastCount = 0;
                _targetBlockSize = _minBlockSize;
            }
            else
            {
                // Merge into the last block.
                split.Lengths[_numBlocks - 1] += (uint)_blockSize;
                _histograms[_lastHistogramIx[0]].CopyFrom(_combined[0]);
                _lastEntropy[0] = combinedEntropy[0];
                if (split.NumTypes == 1) _lastEntropy[1] = _lastEntropy[0];
                _blockSize = 0;
                _histograms[_currHistogramIx].Clear();
                if (++_mergeLastCount > 1) _targetBlockSize += _minBlockSize;
            }
        }
        if (isFinal)
        {
            _histogramsSize = split.NumTypes;
            split.NumBlocks = _numBlocks;
        }
    }
}

/// <summary>
/// The literal variant of <see cref="GreedyBlockSplitter"/>, carrying one histogram per static context inside
/// each block type (the reference's ContextBlockSplitter). The split decision sums the entropy change across
/// every context, so all contexts of a block type start and end together.
/// </summary>
internal sealed class GreedyContextBlockSplitter
{
    private readonly int _alphabetSize;
    private readonly int _numContexts;
    private readonly int _maxBlockTypes;
    private readonly int _minBlockSize;
    private readonly double _splitThreshold;
    private readonly BlockSplit _split;
    private readonly Histogram[] _histograms;
    private readonly Histogram[] _combined;
    private readonly double[] _lastEntropy;
    private readonly double[] _entropy;
    private readonly double[] _combinedEntropy;
    private readonly int[] _lastHistogramIx = new int[2];

    private int _numBlocks;
    private int _histogramsSize;
    private int _targetBlockSize;
    private int _blockSize;
    private int _currHistogramIx;
    private int _mergeLastCount;

    public GreedyContextBlockSplitter(int alphabetSize, int numContexts, int minBlockSize, double splitThreshold,
        int numSymbols, BlockSplit split)
    {
        _alphabetSize = alphabetSize;
        _numContexts = numContexts;
        _maxBlockTypes = Constants.MaxBlockTypes / numContexts;
        _minBlockSize = minBlockSize;
        _splitThreshold = splitThreshold;
        _split = split;
        _targetBlockSize = minBlockSize;

        int maxNumBlocks = numSymbols / minBlockSize + 1;
        int maxNumTypes = Math.Min(maxNumBlocks, _maxBlockTypes + 1);
        split.EnsureCapacity(maxNumBlocks);
        split.NumBlocks = maxNumBlocks;
        _histogramsSize = maxNumTypes * numContexts;
        _histograms = Histogram.Create(_histogramsSize, alphabetSize);
        _combined = Histogram.Create(2 * numContexts, alphabetSize);
        _lastEntropy = new double[2 * numContexts];
        _entropy = new double[numContexts];
        _combinedEntropy = new double[2 * numContexts];
        for (int i = 0; i < numContexts; ++i) _histograms[i].Clear();
    }

    public Histogram[] Histograms => _histograms;
    public int HistogramsSize => _histogramsSize;

    public void AddSymbol(int symbol, int context)
    {
        _histograms[_currHistogramIx + context].Add(symbol);
        if (++_blockSize == _targetBlockSize) FinishBlock(false);
    }

    public void FinishBlock(bool isFinal)
    {
        BlockSplit split = _split;
        int n = _numContexts;
        if (_blockSize < _minBlockSize) _blockSize = _minBlockSize;
        if (_numBlocks == 0)
        {
            split.Lengths[0] = (uint)_blockSize;
            split.Types[0] = 0;
            for (int i = 0; i < n; ++i)
            {
                _lastEntropy[i] = BitCost.BitsEntropy(_histograms[i].Data.AsSpan(0, _alphabetSize));
                _lastEntropy[n + i] = _lastEntropy[i];
            }
            _numBlocks++;
            split.NumTypes++;
            _currHistogramIx += n;
            ClearCurrent();
            _blockSize = 0;
        }
        else if (_blockSize > 0)
        {
            double diff0 = 0.0, diff1 = 0.0;
            for (int i = 0; i < n; ++i)
            {
                int curr = _currHistogramIx + i;
                _entropy[i] = BitCost.BitsEntropy(_histograms[curr].Data.AsSpan(0, _alphabetSize));
                for (int j = 0; j < 2; ++j)
                {
                    int jx = j * n + i;
                    _combined[jx].CopyFrom(_histograms[curr]);
                    _combined[jx].AddHistogram(_histograms[_lastHistogramIx[j] + i]);
                    _combinedEntropy[jx] = BitCost.BitsEntropy(_combined[jx].Data.AsSpan(0, _alphabetSize));
                    double d = _combinedEntropy[jx] - _entropy[i] - _lastEntropy[jx];
                    if (j == 0) diff0 += d; else diff1 += d;
                }
            }

            if (split.NumTypes < _maxBlockTypes && diff0 > _splitThreshold && diff1 > _splitThreshold)
            {
                split.Lengths[_numBlocks] = (uint)_blockSize;
                split.Types[_numBlocks] = (byte)split.NumTypes;
                _lastHistogramIx[1] = _lastHistogramIx[0];
                _lastHistogramIx[0] = split.NumTypes * n;
                for (int i = 0; i < n; ++i)
                {
                    _lastEntropy[n + i] = _lastEntropy[i];
                    _lastEntropy[i] = _entropy[i];
                }
                _numBlocks++;
                split.NumTypes++;
                _currHistogramIx += n;
                ClearCurrent();
                _blockSize = 0;
                _mergeLastCount = 0;
                _targetBlockSize = _minBlockSize;
            }
            else if (diff1 < diff0 - 20.0)
            {
                split.Lengths[_numBlocks] = (uint)_blockSize;
                split.Types[_numBlocks] = split.Types[_numBlocks - 2];
                (_lastHistogramIx[0], _lastHistogramIx[1]) = (_lastHistogramIx[1], _lastHistogramIx[0]);
                for (int i = 0; i < n; ++i)
                {
                    _histograms[_lastHistogramIx[0] + i].CopyFrom(_combined[n + i]);
                    _lastEntropy[n + i] = _lastEntropy[i];
                    _lastEntropy[i] = _combinedEntropy[n + i];
                    _histograms[_currHistogramIx + i].Clear();
                }
                _numBlocks++;
                _blockSize = 0;
                _mergeLastCount = 0;
                _targetBlockSize = _minBlockSize;
            }
            else
            {
                split.Lengths[_numBlocks - 1] += (uint)_blockSize;
                for (int i = 0; i < n; ++i)
                {
                    _histograms[_lastHistogramIx[0] + i].CopyFrom(_combined[i]);
                    _lastEntropy[i] = _combinedEntropy[i];
                    if (split.NumTypes == 1) _lastEntropy[n + i] = _lastEntropy[i];
                    _histograms[_currHistogramIx + i].Clear();
                }
                _blockSize = 0;
                if (++_mergeLastCount > 1) _targetBlockSize += _minBlockSize;
            }
        }
        if (isFinal)
        {
            _histogramsSize = split.NumTypes * n;
            split.NumBlocks = _numBlocks;
        }
    }

    private void ClearCurrent()
    {
        if (_currHistogramIx >= _histogramsSize) return;
        for (int i = 0; i < _numContexts; ++i) _histograms[_currHistogramIx + i].Clear();
    }
}

/// <summary>
/// Chooses a static literal context map and builds a block-split metablock in one pass over the commands, for
/// qualities below the shortest-path tier (the reference's DecideOverLiteralContextModeling and
/// BrotliBuildMetaBlockGreedy).
/// </summary>
internal static class GreedyMetaBlock
{
    public const int MaxStaticContexts = 13;

    /// <summary>Two contexts: the byte after a UTF-8 lead byte is modelled apart from everything else.</summary>
    private static readonly uint[] SimpleUtf8 = BuildMap(new byte[] { 0, 0, 1, 1 });

    /// <summary>Three contexts: continuation bytes split further by whether the previous byte was a lead.</summary>
    private static readonly uint[] Continuation = BuildMap(new byte[] { 1, 1, 2, 2 });

    /// <summary>Thirteen contexts keyed on punctuation, digits and letter case; only for long UTF-8 text.</summary>
    private static readonly uint[] ComplexUtf8 =
    {
        11, 11, 12, 12,
        0, 0, 0, 0,
        1, 1, 9, 9,
        2, 2, 2, 2,
        1, 1, 1, 1,
        8, 3, 3, 3,
        1, 1, 1, 1,
        2, 2, 2, 2,
        8, 4, 4, 4,
        8, 7, 4, 4,
        8, 0, 0, 0,
        3, 3, 3, 3,
        5, 5, 10, 5,
        5, 5, 10, 5,
        6, 6, 6, 6,
        6, 6, 6, 6,
    };

    private static uint[] BuildMap(byte[] firstFour)
    {
        var map = new uint[64];
        for (int i = 0; i < firstFour.Length; i++) map[i] = firstFour[i];
        return map;
    }

    /// <summary>
    /// Decides how many literal contexts to model and which static map to use. Returns 1 for no modeling, in
    /// which case the map is null. Samples 64-byte strides every 4 KB, as the reference does, so the decision
    /// costs the same regardless of metablock size.
    /// </summary>
    public static int ChooseContexts(byte[] buf, int start, int length, int quality, long sizeHint, out uint[]? map)
    {
        map = null;
        if (quality < 5 || length < 64) return 1;
        if (sizeHint >= 1 << 20 && ShouldUseComplexMap(buf, start, length))
        {
            map = ComplexUtf8;
            return MaxStaticContexts;
        }

        // Bigram histogram over the two top bits of each byte, which is what the UTF-8 context mode keys on.
        Span<uint> bigram = stackalloc uint[9];
        ReadOnlySpan<int> lut = stackalloc int[4] { 0, 0, 1, 2 };
        int end = start + length;
        for (int pos = start; pos + 64 <= end; pos += 4096)
        {
            int strideEnd = pos + 64;
            int prev = lut[buf[pos] >> 6] * 3;
            for (int i = pos + 1; i < strideEnd; ++i)
            {
                byte literal = buf[i];
                bigram[prev + lut[literal >> 6]]++;
                prev = lut[literal >> 6] * 3;
            }
        }

        Span<uint> monogram = stackalloc uint[3];
        Span<uint> twoPrefix = stackalloc uint[6];
        for (int i = 0; i < 9; ++i)
        {
            monogram[i % 3] += bigram[i];
            twoPrefix[i % 6] += bigram[i];
        }
        double e1 = EstimateEntropy(monogram);
        double e2 = EstimateEntropy(twoPrefix.Slice(0, 3)) + EstimateEntropy(twoPrefix.Slice(3, 3));
        double e3 = 0;
        for (int i = 0; i < 3; ++i) e3 += EstimateEntropy(bigram.Slice(3 * i, 3));

        long total = monogram[0] + monogram[1] + monogram[2];
        if (total == 0) return 1;
        double scale = 1.0 / total;
        e1 *= scale;
        e2 *= scale;
        e3 *= scale;
        // Three models cost the decoder an extra tree; the reference only pays for them from quality 7.
        if (quality < 7) e3 = e1 * 10;

        // Below 0.2 bits per symbol the saving does not pay for the slower decode.
        if (e1 - e2 < 0.2 && e1 - e3 < 0.2) return 1;
        if (e2 - e3 < 0.02) { map = SimpleUtf8; return 2; }
        map = Continuation;
        return 3;
    }

    private static bool ShouldUseComplexMap(byte[] buf, int start, int length)
    {
        // Histograms over the top five bits of each literal: one without context, thirteen with.
        var combined = new uint[32];
        var contextHisto = new uint[32 * MaxStaticContexts];
        long total = 0;
        int end = start + length;
        ReadOnlySpan<byte> lut = ContextLookup.Table.Slice(2 << 9, 512);   // CONTEXT_UTF8
        for (int pos = start; pos + 64 <= end; pos += 4096)
        {
            int strideEnd = pos + 64;
            byte prev2 = buf[pos];
            byte prev1 = buf[pos + 1];
            for (int i = pos + 2; i < strideEnd; ++i)
            {
                byte literal = buf[i];
                int ctx = (int)ComplexUtf8[lut[prev1] | lut[256 + prev2]];
                total++;
                combined[literal >> 3]++;
                contextHisto[(ctx << 5) + (literal >> 3)]++;
                prev2 = prev1;
                prev1 = literal;
            }
        }
        if (total == 0) return false;
        double e1 = EstimateEntropy(combined);
        double e2 = 0;
        for (int i = 0; i < MaxStaticContexts; ++i) e2 += EstimateEntropy(contextHisto.AsSpan(i << 5, 32));
        double scale = 1.0 / total;
        e1 *= scale;
        e2 *= scale;
        // Tuned by the reference on the Silesia corpus: skip poorly compressible input and small savings.
        return !(e2 > 3.0 || e1 - e2 < 0.2);
    }

    private static double EstimateEntropy(ReadOnlySpan<uint> histogram)
    {
        long sum = 0;
        double retval = 0;
        for (int i = 0; i < histogram.Length; ++i)
        {
            sum += histogram[i];
            retval -= histogram[i] * BitCost.FastLog2(histogram[i]);
        }
        if (sum != 0) retval += sum * BitCost.FastLog2((uint)sum);
        return retval;
    }

    /// <summary>
    /// Fills <paramref name="mb"/> from the commands in one pass. With more than one context the literal split
    /// carries a histogram per context and the context map repeats the static map for every block type.
    /// </summary>
    public static void Build(byte[] buf, int pos, byte prev1, byte prev2, int contextMode, int numContexts,
        uint[]? staticContextMap, int distAlphabetSize, Command[] cmds, int numCommands, MetaBlockSplit mb)
    {
        int numLiterals = 0;
        for (int i = 0; i < numCommands; ++i) numLiterals += cmds[i].InsertLength;

        GreedyBlockSplitter? litPlain = null;
        GreedyContextBlockSplitter? litCtx = null;
        if (numContexts == 1)
        {
            litPlain = new GreedyBlockSplitter(Constants.NumLiteralSymbols, Constants.NumLiteralSymbols, 512, 400.0, numLiterals, mb.LiteralSplit);
        }
        else
        {
            litCtx = new GreedyContextBlockSplitter(Constants.NumLiteralSymbols, numContexts, 512, 400.0, numLiterals, mb.LiteralSplit);
        }
        var cmdSplitter = new GreedyBlockSplitter(Constants.NumCommandSymbols, Constants.NumCommandSymbols, 1024, 500.0, numCommands, mb.CommandSplit);
        var distSplitter = new GreedyBlockSplitter(distAlphabetSize, 64, 512, 100.0, numCommands, mb.DistanceSplit);

        ReadOnlySpan<byte> lut = ContextLookup.Table.Slice(contextMode << 9, 512);
        for (int i = 0; i < numCommands; ++i)
        {
            ref Command cmd = ref cmds[i];
            cmdSplitter.AddSymbol(cmd.CmdPrefix);
            for (int j = cmd.InsertLength; j != 0; --j)
            {
                byte literal = buf[pos];
                if (numContexts == 1)
                {
                    litPlain!.AddSymbol(literal);
                }
                else
                {
                    int context = lut[prev1] | lut[256 + prev2];
                    litCtx!.AddSymbol(literal, (int)staticContextMap![context]);
                }
                prev2 = prev1;
                prev1 = literal;
                ++pos;
            }
            int copyLen = cmd.CopyLen;
            pos += copyLen;
            if (copyLen != 0)
            {
                prev2 = buf[pos - 2];
                prev1 = buf[pos - 1];
                if (cmd.CmdPrefix >= 128) distSplitter.AddSymbol(cmd.DistPrefix & 0x3FF);
            }
        }

        if (numContexts == 1) litPlain!.FinishBlock(true); else litCtx!.FinishBlock(true);
        cmdSplitter.FinishBlock(true);
        distSplitter.FinishBlock(true);

        mb.LiteralHistograms = numContexts == 1 ? litPlain!.Histograms : litCtx!.Histograms;
        mb.LiteralHistogramsSize = numContexts == 1 ? litPlain!.HistogramsSize : litCtx!.HistogramsSize;
        mb.CommandHistograms = cmdSplitter.Histograms;
        mb.CommandHistogramsSize = cmdSplitter.HistogramsSize;
        mb.DistanceHistograms = distSplitter.Histograms;
        mb.DistanceHistogramsSize = distSplitter.HistogramsSize;
        // Both maps are always written out: the writer codes every symbol through a map, and a map whose
        // entries repeat costs almost nothing once run-length coded.
        int litMapSize = mb.LiteralSplit.NumTypes << Constants.LiteralContextBits;
        if (mb.LiteralContextMap.Length < litMapSize) mb.LiteralContextMap = new uint[litMapSize];
        mb.LiteralContextMapSize = litMapSize;
        for (int i = 0; i < mb.LiteralSplit.NumTypes; ++i)
        {
            uint offset = (uint)(i * numContexts);
            for (int j = 0; j < 1 << Constants.LiteralContextBits; ++j)
            {
                mb.LiteralContextMap[(i << Constants.LiteralContextBits) + j] =
                    numContexts == 1 ? (uint)i : offset + staticContextMap![j];
            }
        }

        int distMapSize = mb.DistanceSplit.NumTypes << Constants.DistanceContextBits;
        if (mb.DistanceContextMap.Length < distMapSize) mb.DistanceContextMap = new uint[distMapSize];
        mb.DistanceContextMapSize = distMapSize;
        for (int i = 0; i < mb.DistanceSplit.NumTypes; ++i)
        {
            for (int j = 0; j < 1 << Constants.DistanceContextBits; ++j)
            {
                mb.DistanceContextMap[(i << Constants.DistanceContextBits) + j] = (uint)i;
            }
        }
    }
}
