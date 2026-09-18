using System;
using System.Numerics;

namespace BrotliManagedFast.Internal;

/// <summary>NPOSTFIX / NDIRECT and the derived alphabet sizes (the reference's BrotliDistanceParams).</summary>
internal struct DistanceParams
{
    public int PostfixBits;
    public int NumDirectCodes;
    public int AlphabetSizeMax;
    public int AlphabetSizeLimit;
    public long MaxDistance;

    public static DistanceParams Create(int npostfix, int ndirect, bool largeWindow)
    {
        DistanceParams p;
        p.PostfixBits = npostfix;
        p.NumDirectCodes = ndirect;
        p.AlphabetSizeMax = Constants.DistanceAlphabetSize(npostfix, ndirect, Constants.MaxDistanceBits);
        p.AlphabetSizeLimit = p.AlphabetSizeMax;
        p.MaxDistance = ndirect + (1L << (Constants.MaxDistanceBits + npostfix + 2)) - (1L << (npostfix + 2));
        if (largeWindow)
        {
            p.AlphabetSizeMax = Constants.DistanceAlphabetSize(npostfix, ndirect, Constants.LargeMaxDistanceBits);
            CalculateDistanceCodeLimit(Constants.MaxAllowedDistance, npostfix, ndirect, out int alphabetSizeLimit, out long maxDistance);
            p.AlphabetSizeLimit = alphabetSizeLimit;
            p.MaxDistance = maxDistance;
        }
        return p;
    }

    /// <summary>
    /// Largest distance alphabet that cannot express a distance above <paramref name="maxDistance"/>, restricted
    /// to complete code groups (RFC 7932 section 4 as the reference implements it for 32-bit decoders).
    /// </summary>
    private static void CalculateDistanceCodeLimit(long maxDistance, int npostfix, int ndirect, out int alphabetSizeLimit, out long limitDistance)
    {
        if (maxDistance <= ndirect)
        {
            alphabetSizeLimit = (int)maxDistance + Constants.NumDistanceShortCodes;
            limitDistance = maxDistance;
            return;
        }
        long forbidden = maxDistance + 1;
        long offset = forbidden - ndirect - 1;
        int postfix = (1 << npostfix) - 1;
        offset = (offset >> npostfix) + 4;
        int ndistbits = BitOperations.Log2((ulong)(offset / 2));
        long half = (offset >> ndistbits) & 1;
        long group = ((ndistbits - 1) << 1) + half;   // half is the low bit
        if (group == 0)
        {
            alphabetSizeLimit = ndirect + Constants.NumDistanceShortCodes;
            limitDistance = ndirect;
            return;
        }
        group--;
        ndistbits = (int)(group >> 1) + 1;
        long extra = (1L << ndistbits) - 1;
        long start = (1L << (ndistbits + 1)) - 4;
        start += (group & 1) << ndistbits;
        alphabetSizeLimit = (int)(((group << npostfix) + postfix) + ndirect + Constants.NumDistanceShortCodes + 1);   // postfix fills the low npostfix bits
        limitDistance = ((start + extra) << npostfix) + postfix + ndirect + 1;
    }

    /// <summary>Distance symbol, extra-bit count and extra bits for an intermediate distance code (short codes 0-15, else distance + 15).</summary>
    public static void PrefixEncodeCopyDistance(long distanceCode, int numDirectCodes, int postfixBits, out int code, out int nExtra, out uint extra)
    {
        if (distanceCode < Constants.NumDistanceShortCodes + numDirectCodes)
        {
            code = (int)distanceCode;
            nExtra = 0;
            extra = 0;
            return;
        }
        long dist = (1L << (postfixBits + 2)) + (distanceCode - Constants.NumDistanceShortCodes - numDirectCodes);
        int bucket = BitOperations.Log2((ulong)dist) - 1;
        long postfixMask = (1L << postfixBits) - 1;
        long postfix = dist & postfixMask;
        long prefix = (dist >> bucket) & 1;
        long offset = (2 + prefix) << bucket;
        int nbits = bucket - postfixBits;
        code = (int)(Constants.NumDistanceShortCodes + numDirectCodes + ((2 * (nbits - 1) + prefix) << postfixBits) + postfix);
        nExtra = nbits;
        extra = (uint)((dist - offset) >> postfixBits);
    }

    /// <summary>The intermediate distance code of a command's stored prefix and extra bits under these parameters.</summary>
    public long RestoreDistanceCode(ushort distPrefix, uint distExtra)
    {
        int dcode = distPrefix & 0x3FF;
        if (dcode < Constants.NumDistanceShortCodes + NumDirectCodes) return dcode;
        int nbits = distPrefix >> 10;
        long postfixMask = (1L << PostfixBits) - 1;
        long hcode = (dcode - NumDirectCodes - Constants.NumDistanceShortCodes) >> PostfixBits;
        long lcode = (dcode - NumDirectCodes - Constants.NumDistanceShortCodes) & postfixMask;
        long offset = ((2 + (hcode & 1)) << nbits) - 4;
        return ((offset + distExtra) << PostfixBits) + lcode + NumDirectCodes + Constants.NumDistanceShortCodes;
    }
}

/// <summary>Block splits, context maps and clustered histograms of one metablock (the reference's MetaBlockSplit).</summary>
internal sealed class MetaBlockSplit
{
    public readonly BlockSplit LiteralSplit = new();
    public readonly BlockSplit CommandSplit = new();
    public readonly BlockSplit DistanceSplit = new();
    public uint[] LiteralContextMap = Array.Empty<uint>();
    public int LiteralContextMapSize;
    public uint[] DistanceContextMap = Array.Empty<uint>();
    public int DistanceContextMapSize;
    public Histogram[] LiteralHistograms = Array.Empty<Histogram>();
    public int LiteralHistogramsSize;
    public Histogram[] CommandHistograms = Array.Empty<Histogram>();
    public int CommandHistogramsSize;
    public Histogram[] DistanceHistograms = Array.Empty<Histogram>();
    public int DistanceHistogramsSize;

    public void Reset()
    {
        LiteralSplit.Reset();
        CommandSplit.Reset();
        DistanceSplit.Reset();
        LiteralContextMapSize = 0;
        DistanceContextMapSize = 0;
        LiteralHistogramsSize = 0;
        CommandHistogramsSize = 0;
        DistanceHistogramsSize = 0;
    }
}

/// <summary>Builds and stores block-split metablocks (metablock.c and the block-split part of brotli_bit_stream.c).</summary>
internal static class MetaBlockBuilder
{
    private const int MaxNumberOfHistograms = 256;

    public static int CommandDistanceContext(ushort cmdPrefix)
    {
        int r = cmdPrefix >> 6;
        int c = cmdPrefix & 7;
        if ((r == 0 || r == 2 || r == 4 || r == 7) && c <= 2) return c;
        return 3;
    }

    /// <summary>Context mode for the metablock: UTF8 unless the data is mostly not UTF-8, then Signed.</summary>
    public static int ChooseContextMode(byte[] buf, int start, int length)
    {
        return ZopfliParser.IsMostlyUtf8(buf, start, length) ? 2 : 3;   // CONTEXT_UTF8 : CONTEXT_SIGNED
    }

    private static bool ComputeDistanceCost(Command[] cmds, int numCommands, in DistanceParams origParams, in DistanceParams newParams, out double cost, Histogram tmp)
    {
        bool equalParams = origParams.PostfixBits == newParams.PostfixBits && origParams.NumDirectCodes == newParams.NumDirectCodes;
        double extraBits = 0.0;
        tmp.Clear();
        cost = 0;
        for (int i = 0; i < numCommands; i++)
        {
            ref Command cmd = ref cmds[i];
            if (cmd.CopyLen != 0 && cmd.CmdPrefix >= 128)
            {
                ushort distPrefix;
                if (equalParams)
                {
                    distPrefix = cmd.DistPrefix;
                }
                else
                {
                    long distance = origParams.RestoreDistanceCode(cmd.DistPrefix, cmd.DistExtra);
                    if (distance > newParams.MaxDistance) return false;
                    // The symbol must fit the histogram the caller sized from AlphabetSizeLimit.
                    DistanceParams.PrefixEncodeCopyDistance(distance, newParams.NumDirectCodes, newParams.PostfixBits, out int code, out int nExtra, out _);
                    distPrefix = (ushort)(code | (nExtra << 10));
                }
                tmp.Add(distPrefix & 0x3FF);
                extraBits += distPrefix >> 10;
            }
        }
        cost = BitCost.PopulationCost(tmp) + extraBits;
        return true;
    }

    private static void RecomputeDistancePrefixes(Command[] cmds, int numCommands, in DistanceParams origParams, in DistanceParams newParams)
    {
        if (origParams.PostfixBits == newParams.PostfixBits && origParams.NumDirectCodes == newParams.NumDirectCodes) return;
        for (int i = 0; i < numCommands; ++i)
        {
            ref Command cmd = ref cmds[i];
            if (cmd.CopyLen != 0 && cmd.CmdPrefix >= 128)
            {
                long distance = origParams.RestoreDistanceCode(cmd.DistPrefix, cmd.DistExtra);
                DistanceParams.PrefixEncodeCopyDistance(distance, newParams.NumDirectCodes, newParams.PostfixBits, out int code, out int nExtra, out uint extra);
                cmd.DistPrefix = (ushort)(code | (nExtra << 10));
                cmd.DistExtra = extra;
            }
        }
    }

    /// <summary>Chooses the distance parameters with the smallest estimated cost and recodes the commands for them.</summary>
    public static DistanceParams ChooseDistanceParams(Command[] cmds, int numCommands, in DistanceParams orig, bool largeWindow)
    {
        var tmp = new Histogram(544);
        DistanceParams best = orig;
        bool checkOrig = true;
        double bestDistCost = 1e99;
        // All 64 legal pairs, rather than the reference's walk that stops at the first worsening cost and
        // carries the direct-code count between postfix values. That walk misses its own objective's minimum:
        // on the map tile it settles for 8 direct codes where 120 cost about 2400 bits less.
        for (int npostfix = 0; npostfix <= Constants.MaxNPostfix; npostfix++)
        {
            for (int ndirectMsb = 0; ndirectMsb < 16; ndirectMsb++)
            {
                int ndirect = ndirectMsb << npostfix;
                DistanceParams candidate = DistanceParams.Create(npostfix, ndirect, largeWindow);
                if (npostfix == orig.PostfixBits && ndirect == orig.NumDirectCodes) checkOrig = false;
                if (!ComputeDistanceCost(cmds, numCommands, orig, candidate, out double distCost, tmp)) continue;
                if (distCost >= bestDistCost) continue;
                bestDistCost = distCost;
                best = candidate;
            }
        }
        if (checkOrig)
        {
            ComputeDistanceCost(cmds, numCommands, orig, orig, out double distCost, tmp);
            if (distCost < bestDistCost) best = orig;
        }
        RecomputeDistancePrefixes(cmds, numCommands, orig, best);
        return best;
    }

    private static void SplitBlocks(Command[] cmds, int numCommands, byte[] buf, int pos, BlockSplit literalSplit, BlockSplit commandSplit, BlockSplit distSplit)
    {
        {
            int literalsCount = 0;
            for (int i = 0; i < numCommands; ++i) literalsCount += cmds[i].InsertLength;
            var literals = new ushort[literalsCount];
            int p = 0;
            int from = pos;
            for (int i = 0; i < numCommands; ++i)
            {
                int n = cmds[i].InsertLength;
                for (int j = 0; j < n; j++) literals[p + j] = buf[from + j];
                p += n;
                from += n + cmds[i].CopyLen;
            }
            BlockSplitter.SplitByteVector(literals, literalsCount, Constants.NumLiteralSymbols, BlockSplitter.SymbolsPerLiteralHistogram,
                BlockSplitter.MaxLiteralHistograms, BlockSplitter.LiteralStrideLength, BlockSplitter.LiteralBlockSwitchCost, literalSplit);
        }
        {
            var codes = new ushort[numCommands];
            for (int i = 0; i < numCommands; ++i) codes[i] = cmds[i].CmdPrefix;
            BlockSplitter.SplitByteVector(codes, numCommands, Constants.NumCommandSymbols, BlockSplitter.SymbolsPerCommandHistogram,
                BlockSplitter.MaxCommandHistograms, BlockSplitter.CommandStrideLength, BlockSplitter.CommandBlockSwitchCost, commandSplit);
        }
        {
            var prefixes = new ushort[numCommands];
            int j = 0;
            for (int i = 0; i < numCommands; ++i)
            {
                ref Command cmd = ref cmds[i];
                if (cmd.CopyLen != 0 && cmd.CmdPrefix >= 128) prefixes[j++] = (ushort)(cmd.DistPrefix & 0x3FF);
            }
            BlockSplitter.SplitByteVector(prefixes, j, 544, BlockSplitter.SymbolsPerDistanceHistogram,
                BlockSplitter.MaxCommandHistograms, BlockSplitter.DistanceStrideLength, BlockSplitter.DistanceBlockSwitchCost, distSplit);
        }
    }

    private struct SplitIterator
    {
        private readonly BlockSplit _split;
        private int _idx;
        public int Type;
        private uint _length;

        public SplitIterator(BlockSplit split)
        {
            _split = split;
            _idx = 0;
            Type = 0;
            _length = split.NumBlocks > 0 ? split.Lengths[0] : 0;
        }

        public void Next()
        {
            if (_length == 0)
            {
                ++_idx;
                Type = _split.Types[_idx];
                _length = _split.Lengths[_idx];
            }
            --_length;
        }
    }

    private static void BuildHistogramsWithContext(Command[] cmds, int numCommands, BlockSplit literalSplit, BlockSplit commandSplit, BlockSplit distSplit,
        byte[] buf, int pos, byte prevByte, byte prevByte2, int contextMode, Histogram[] literalHistograms, Histogram[] commandHistograms, Histogram[] distHistograms)
    {
        var litIt = new SplitIterator(literalSplit);
        var cmdIt = new SplitIterator(commandSplit);
        var distIt = new SplitIterator(distSplit);
        // contextMode -1: no literal context modeling, one histogram per literal block type.
        ReadOnlySpan<byte> lut = contextMode < 0 ? ReadOnlySpan<byte>.Empty : ContextLookup.Table.Slice(contextMode << 9, 512);
        for (int i = 0; i < numCommands; ++i)
        {
            ref Command cmd = ref cmds[i];
            cmdIt.Next();
            commandHistograms[cmdIt.Type].Add(cmd.CmdPrefix);
            for (int j = cmd.InsertLength; j != 0; --j)
            {
                litIt.Next();
                int context = contextMode < 0 ? litIt.Type : (litIt.Type << Constants.LiteralContextBits) + (lut[prevByte] | lut[256 + prevByte2]);
                byte literal = buf[pos];
                literalHistograms[context].Add(literal);
                prevByte2 = prevByte;
                prevByte = literal;
                ++pos;
            }
            pos += cmd.CopyLen;
            if (cmd.CopyLen != 0)
            {
                prevByte2 = buf[pos - 2];
                prevByte = buf[pos - 1];
                if (cmd.CmdPrefix >= 128)
                {
                    distIt.Next();
                    int context = (distIt.Type << Constants.DistanceContextBits) + CommandDistanceContext(cmd.CmdPrefix);
                    distHistograms[context].Add(cmd.DistPrefix & 0x3FF);
                }
            }
        }
    }

    /// <summary>
    /// Splits the commands and literals into blocks, builds context histograms, clusters them and fills
    /// <paramref name="mb"/>. The commands' distance prefixes must already match <paramref name="dist"/>.
    /// </summary>
    public static void Build(byte[] buf, int pos, byte prevByte, byte prevByte2, Command[] cmds, int numCommands, int contextMode, bool contextModeling, in DistanceParams dist, MetaBlockSplit mb)
    {
        mb.Reset();
        SplitBlocks(cmds, numCommands, buf, pos, mb.LiteralSplit, mb.CommandSplit, mb.DistanceSplit);

        int numLiteralTypes = mb.LiteralSplit.NumTypes;
        int literalHistogramsSize = contextModeling ? numLiteralTypes << Constants.LiteralContextBits : numLiteralTypes;
        Histogram[] literalHistograms = Histogram.Create(literalHistogramsSize, Constants.NumLiteralSymbols);
        int distanceHistogramsSize = mb.DistanceSplit.NumTypes << Constants.DistanceContextBits;
        Histogram[] distanceHistograms = Histogram.Create(distanceHistogramsSize, dist.AlphabetSizeLimit);
        mb.CommandHistogramsSize = mb.CommandSplit.NumTypes;
        mb.CommandHistograms = Histogram.Create(mb.CommandHistogramsSize, Constants.NumCommandSymbols);
        BuildHistogramsWithContext(cmds, numCommands, mb.LiteralSplit, mb.CommandSplit, mb.DistanceSplit, buf, pos, prevByte, prevByte2, contextModeling ? contextMode : -1,
            literalHistograms, mb.CommandHistograms, distanceHistograms);

        mb.LiteralContextMapSize = numLiteralTypes << Constants.LiteralContextBits;
        mb.LiteralContextMap = new uint[mb.LiteralContextMapSize];
        mb.LiteralHistograms = Histogram.Create(literalHistogramsSize, Constants.NumLiteralSymbols);
        mb.LiteralHistogramsSize = HistogramCluster.ClusterHistograms(literalHistograms, literalHistogramsSize, MaxNumberOfHistograms, mb.LiteralHistograms, mb.LiteralContextMap);
        if (!contextModeling)
        {
            // One histogram per block type: every context of a type maps to the type's cluster.
            for (int t = numLiteralTypes; t != 0;)
            {
                t--;
                uint cluster = mb.LiteralContextMap[t];
                for (int j = 0; j < (1 << Constants.LiteralContextBits); j++) mb.LiteralContextMap[(t << Constants.LiteralContextBits) + j] = cluster;
            }
        }

        mb.DistanceContextMapSize = distanceHistogramsSize;
        mb.DistanceContextMap = new uint[distanceHistogramsSize];
        mb.DistanceHistograms = Histogram.Create(distanceHistogramsSize, dist.AlphabetSizeLimit);
        mb.DistanceHistogramsSize = HistogramCluster.ClusterHistograms(distanceHistograms, distanceHistogramsSize, MaxNumberOfHistograms, mb.DistanceHistograms, mb.DistanceContextMap);
    }

    /// <summary>Makes every histogram friendlier to the run-length coded tree description (entropy_encode.c).</summary>
    public static void OptimizeHistograms(int numDistanceCodes, MetaBlockSplit mb)
    {
        var goodForRle = new byte[Constants.NumCommandSymbols];
        for (int i = 0; i < mb.LiteralHistogramsSize; ++i) OptimizeHuffmanCountsForRle(256, mb.LiteralHistograms[i].Data, goodForRle);
        for (int i = 0; i < mb.CommandHistogramsSize; ++i) OptimizeHuffmanCountsForRle(Constants.NumCommandSymbols, mb.CommandHistograms[i].Data, goodForRle);
        for (int i = 0; i < mb.DistanceHistogramsSize; ++i) OptimizeHuffmanCountsForRle(numDistanceCodes, mb.DistanceHistograms[i].Data, goodForRle);
    }

    public static void OptimizeHuffmanCountsForRle(int length, uint[] counts, byte[] goodForRle)
    {
        int nonzeroCount = 0;
        const int streakLimit = 1240;
        for (int i = 0; i < length; i++) if (counts[i] != 0) ++nonzeroCount;
        if (nonzeroCount < 16) return;
        while (length != 0 && counts[length - 1] == 0) --length;
        if (length == 0) return;
        {
            int nonzeros = 0;
            uint smallestNonzero = 1 << 30;
            for (int i = 0; i < length; ++i)
            {
                if (counts[i] != 0)
                {
                    ++nonzeros;
                    if (smallestNonzero > counts[i]) smallestNonzero = counts[i];
                }
            }
            if (nonzeros < 5) return;
            if (smallestNonzero < 4)
            {
                int zeros = length - nonzeros;
                if (zeros < 6)
                {
                    for (int i = 1; i < length - 1; ++i)
                    {
                        if (counts[i - 1] != 0 && counts[i] == 0 && counts[i + 1] != 0) counts[i] = 1;
                    }
                }
            }
            if (nonzeros < 28) return;
        }
        Array.Clear(goodForRle, 0, length);
        {
            uint symbol = counts[0];
            int step = 0;
            for (int i = 0; i <= length; ++i)
            {
                if (i == length || counts[i] != symbol)
                {
                    if ((symbol == 0 && step >= 5) || (symbol != 0 && step >= 7))
                    {
                        for (int k = 0; k < step; ++k) goodForRle[i - k - 1] = 1;
                    }
                    step = 1;
                    if (i != length) symbol = counts[i];
                }
                else
                {
                    ++step;
                }
            }
        }
        long stride = 0;
        long limit = 256L * (counts[0] + counts[1] + counts[2]) / 3 + 420;
        long sum = 0;
        for (int i = 0; i <= length; ++i)
        {
            // The reference computes this in unsigned arithmetic, so the test is |256 * count - limit| >= streak limit.
            long delta = i == length ? 0 : 256L * counts[i] - limit;
            if (i == length || goodForRle[i] != 0 || (i != 0 && goodForRle[i - 1] != 0) || delta >= streakLimit || delta < -streakLimit)
            {
                if (stride >= 4 || (stride >= 3 && sum == 0))
                {
                    long count = (sum + stride / 2) / stride;
                    if (count == 0) count = 1;
                    if (sum == 0) count = 0;
                    for (int k = 0; k < stride; ++k) counts[i - k - 1] = (uint)count;
                }
                stride = 0;
                sum = 0;
                if (i < length - 2) limit = 256L * (counts[i] + counts[i + 1] + counts[i + 2]) / 3 + 420;
                else if (i < length) limit = 256L * counts[i];
                else limit = 0;
            }
            ++stride;
            if (i != length)
            {
                sum += counts[i];
                if (stride >= 4) limit = (256 * sum + stride / 2) / stride;
                if (stride == 4) limit += 120;
            }
        }
    }

    // ------------------------------------------------------------------ storage

    private static int BlockLengthPrefixCode(uint len)
    {
        int code = len >= 177 ? (len >= 753 ? 20 : 14) : (len >= 41 ? 7 : 0);
        while (code < Constants.NumBlockLengthSymbols - 1 && len >= (uint)Constants.BlockLengthOffset[code + 1]) ++code;
        return code;
    }

    private struct BlockTypeCodeCalculator
    {
        public int LastType, SecondLastType;

        public static BlockTypeCodeCalculator Create() => new() { LastType = 1, SecondLastType = 0 };

        public int Next(int type)
        {
            int typeCode = type == LastType + 1 ? 1 : type == SecondLastType ? 0 : type + 2;
            SecondLastType = LastType;
            LastType = type;
            return typeCode;
        }
    }

    private static void StoreVarLenUint8(int n, BitWriter w)
    {
        if (n == 0)
        {
            w.WriteBits(1, 0);
        }
        else
        {
            int nbits = BitOperations.Log2((uint)n);
            w.WriteBits(1, 1);
            w.WriteBits(3, (ulong)nbits);
            w.WriteBits(nbits, (ulong)(n - (1 << nbits)));
        }
    }

    /// <summary>Entropy codes of one block category plus its position in the block sequence.</summary>
    private sealed class BlockEncoder
    {
        public int HistogramLength;
        public BlockSplit Split = null!;
        public BlockTypeCodeCalculator TypeCalc;
        public byte[] TypeDepths = new byte[Constants.MaxBlockTypeSymbols];
        public ushort[] TypeBits = new ushort[Constants.MaxBlockTypeSymbols];
        public byte[] LengthDepths = new byte[Constants.NumBlockLengthSymbols];
        public ushort[] LengthBits = new ushort[Constants.NumBlockLengthSymbols];
        public int BlockIx;
        public uint BlockLen;
        public int EntropyIx;
        public byte[] Depths = Array.Empty<byte>();
        public ushort[] Bits = Array.Empty<ushort>();

        public void Init(int histogramLength, BlockSplit split)
        {
            HistogramLength = histogramLength;
            Split = split;
            TypeCalc = BlockTypeCodeCalculator.Create();
            BlockIx = 0;
            BlockLen = split.NumBlocks == 0 ? 0 : split.Lengths[0];
            EntropyIx = 0;
        }

        private void StoreBlockSwitch(uint blockLen, int blockType, bool isFirstBlock, BitWriter w)
        {
            int typecode = TypeCalc.Next(blockType);
            if (!isFirstBlock) w.WriteBits(TypeDepths[typecode], TypeBits[typecode]);
            int lencode = BlockLengthPrefixCode(blockLen);
            int nExtra = Constants.BlockLengthExtraBits[lencode];
            uint extra = blockLen - (uint)Constants.BlockLengthOffset[lencode];
            w.WriteBits(LengthDepths[lencode], LengthBits[lencode]);
            w.WriteBits(nExtra, extra);
        }

        public void BuildAndStoreBlockSwitchEntropyCodes(BitWriter w)
        {
            int numTypes = Split.NumTypes;
            var typeHisto = new uint[numTypes + 2];
            var lengthHisto = new uint[Constants.NumBlockLengthSymbols];
            var calc = BlockTypeCodeCalculator.Create();
            for (int i = 0; i < Split.NumBlocks; ++i)
            {
                int typeCode = calc.Next(Split.Types[i]);
                if (i != 0) ++typeHisto[typeCode];
                ++lengthHisto[BlockLengthPrefixCode(Split.Lengths[i])];
            }
            StoreVarLenUint8(numTypes - 1, w);
            if (numTypes > 1)
            {
                HuffmanEncoder.BuildAndStoreHuffmanTree(typeHisto, numTypes + 2, numTypes + 2, TypeDepths, TypeBits, w);
                HuffmanEncoder.BuildAndStoreHuffmanTree(lengthHisto, Constants.NumBlockLengthSymbols, Constants.NumBlockLengthSymbols, LengthDepths, LengthBits, w);
                StoreBlockSwitch(Split.Lengths[0], Split.Types[0], true, w);
            }
        }

        public void BuildAndStoreEntropyCodes(Histogram[] histograms, int histogramsSize, int alphabetSize, BitWriter w)
        {
            int tableSize = histogramsSize * HistogramLength;
            Depths = new byte[tableSize];
            Bits = new ushort[tableSize];
            var depth = new byte[HistogramLength];
            var bits = new ushort[HistogramLength];
            for (int i = 0; i < histogramsSize; ++i)
            {
                Array.Clear(depth, 0, depth.Length);
                Array.Clear(bits, 0, bits.Length);
                HuffmanEncoder.BuildAndStoreHuffmanTree(histograms[i].Data, HistogramLength, alphabetSize, depth, bits, w);
                depth.CopyTo(Depths, i * HistogramLength);
                bits.CopyTo(Bits, i * HistogramLength);
            }
        }

        public void StoreSymbol(int symbol, BitWriter w)
        {
            if (BlockLen == 0)
            {
                int blockIx = ++BlockIx;
                uint blockLen = Split.Lengths[blockIx];
                int blockType = Split.Types[blockIx];
                BlockLen = blockLen;
                EntropyIx = blockType * HistogramLength;
                StoreBlockSwitch(blockLen, blockType, false, w);
            }
            --BlockLen;
            int ix = EntropyIx + symbol;
            w.WriteBits(Depths[ix], Bits[ix]);
        }

        public void StoreSymbolWithContext(int symbol, int context, uint[] contextMap, int contextBits, BitWriter w)
        {
            if (BlockLen == 0)
            {
                int blockIx = ++BlockIx;
                uint blockLen = Split.Lengths[blockIx];
                int blockType = Split.Types[blockIx];
                BlockLen = blockLen;
                EntropyIx = blockType << contextBits;
                StoreBlockSwitch(blockLen, blockType, false, w);
            }
            --BlockLen;
            int histoIx = (int)contextMap[EntropyIx + context];
            int ix = histoIx * HistogramLength + symbol;
            w.WriteBits(Depths[ix], Bits[ix]);
        }
    }

    private static void MoveToFrontTransform(uint[] vIn, int vSize, uint[] vOut)
    {
        if (vSize == 0) return;
        uint maxValue = vIn[0];
        for (int i = 1; i < vSize; ++i) if (vIn[i] > maxValue) maxValue = vIn[i];
        var mtf = new byte[256];
        for (int i = 0; i <= maxValue; ++i) mtf[i] = (byte)i;
        int mtfSize = (int)maxValue + 1;
        for (int i = 0; i < vSize; ++i)
        {
            byte value = (byte)vIn[i];
            int index = 0;
            while (index < mtfSize && mtf[index] != value) index++;
            vOut[i] = (uint)index;
            for (int k = index; k != 0; --k) mtf[k] = mtf[k - 1];
            mtf[0] = value;
        }
    }

    private static void RunLengthCodeZeros(int inSize, uint[] v, out int outSize, ref int maxRunLengthPrefix)
    {
        uint maxReps = 0;
        for (int i = 0; i < inSize;)
        {
            uint reps = 0;
            for (; i < inSize && v[i] != 0; ++i) { }
            for (; i < inSize && v[i] == 0; ++i) ++reps;
            maxReps = Math.Max(reps, maxReps);
        }
        int maxPrefix = maxReps > 0 ? BitOperations.Log2(maxReps) : 0;
        maxPrefix = Math.Min(maxPrefix, maxRunLengthPrefix);
        maxRunLengthPrefix = maxPrefix;
        outSize = 0;
        for (int i = 0; i < inSize;)
        {
            if (v[i] != 0)
            {
                v[outSize] = v[i] + (uint)maxRunLengthPrefix;
                ++i;
                ++outSize;
            }
            else
            {
                uint reps = 1;
                for (int k = i + 1; k < inSize && v[k] == 0; ++k) ++reps;
                i += (int)reps;
                while (reps != 0)
                {
                    if (reps < (2u << maxPrefix))
                    {
                        int runLengthPrefix = BitOperations.Log2(reps);
                        uint extraBits = reps - (1u << runLengthPrefix);
                        v[outSize] = (uint)runLengthPrefix + (extraBits << 9);
                        ++outSize;
                        break;
                    }
                    else
                    {
                        uint extraBits = (1u << maxPrefix) - 1u;
                        v[outSize] = (uint)maxPrefix + (extraBits << 9);
                        reps -= (2u << maxPrefix) - 1u;
                        ++outSize;
                    }
                }
            }
        }
    }

    private static void EncodeContextMap(uint[] contextMap, int contextMapSize, int numClusters, BitWriter w)
    {
        StoreVarLenUint8(numClusters - 1, w);
        if (numClusters == 1) return;
        var rleSymbols = new uint[contextMapSize];
        int maxRunLengthPrefix = 6;
        MoveToFrontTransform(contextMap, contextMapSize, rleSymbols);
        RunLengthCodeZeros(contextMapSize, rleSymbols, out int numRleSymbols, ref maxRunLengthPrefix);
        var histogram = new uint[Constants.MaxContextMapSymbols];
        const uint kSymbolMask = (1u << 9) - 1u;
        for (int i = 0; i < numRleSymbols; ++i) ++histogram[rleSymbols[i] & kSymbolMask];
        bool useRle = maxRunLengthPrefix > 0;
        w.WriteBits(1, useRle ? 1UL : 0UL);
        if (useRle) w.WriteBits(4, (ulong)(maxRunLengthPrefix - 1));
        var depths = new byte[Constants.MaxContextMapSymbols];
        var bits = new ushort[Constants.MaxContextMapSymbols];
        HuffmanEncoder.BuildAndStoreHuffmanTree(histogram, numClusters + maxRunLengthPrefix, numClusters + maxRunLengthPrefix, depths, bits, w);
        for (int i = 0; i < numRleSymbols; ++i)
        {
            uint rleSymbol = rleSymbols[i] & kSymbolMask;
            uint extraBitsVal = rleSymbols[i] >> 9;
            w.WriteBits(depths[rleSymbol], bits[rleSymbol]);
            if (rleSymbol > 0 && rleSymbol <= maxRunLengthPrefix) w.WriteBits((int)rleSymbol, extraBitsVal);
        }
        w.WriteBits(1, 1);   // inverse move-to-front
    }

    /// <summary>Writes the metablock body after the header (block splits, context maps, tree groups, then the data).</summary>
    public static void Store(byte[] buf, int pos, byte prevByte, byte prevByte2, in DistanceParams dist, int contextMode,
        Command[] commands, int numCommands, MetaBlockSplit mb, BitWriter w)
    {
        var literalEnc = new BlockEncoder();
        var commandEnc = new BlockEncoder();
        var distanceEnc = new BlockEncoder();
        literalEnc.Init(Constants.NumLiteralSymbols, mb.LiteralSplit);
        commandEnc.Init(Constants.NumCommandSymbols, mb.CommandSplit);
        distanceEnc.Init(dist.AlphabetSizeLimit, mb.DistanceSplit);
        literalEnc.BuildAndStoreBlockSwitchEntropyCodes(w);
        commandEnc.BuildAndStoreBlockSwitchEntropyCodes(w);
        distanceEnc.BuildAndStoreBlockSwitchEntropyCodes(w);
        w.WriteBits(2, (ulong)dist.PostfixBits);
        w.WriteBits(4, (ulong)(dist.NumDirectCodes >> dist.PostfixBits));
        for (int i = 0; i < mb.LiteralSplit.NumTypes; ++i) w.WriteBits(2, (ulong)contextMode);
        EncodeContextMap(mb.LiteralContextMap, mb.LiteralContextMapSize, mb.LiteralHistogramsSize, w);
        EncodeContextMap(mb.DistanceContextMap, mb.DistanceContextMapSize, mb.DistanceHistogramsSize, w);
        literalEnc.BuildAndStoreEntropyCodes(mb.LiteralHistograms, mb.LiteralHistogramsSize, Constants.NumLiteralSymbols, w);
        commandEnc.BuildAndStoreEntropyCodes(mb.CommandHistograms, mb.CommandHistogramsSize, Constants.NumCommandSymbols, w);
        distanceEnc.BuildAndStoreEntropyCodes(mb.DistanceHistograms, mb.DistanceHistogramsSize, dist.AlphabetSizeMax, w);

        ReadOnlySpan<byte> lut = ContextLookup.Table.Slice(contextMode << 9, 512);
        for (int i = 0; i < numCommands; ++i)
        {
            ref Command cmd = ref commands[i];
            int cmdCode = cmd.CmdPrefix;
            commandEnc.StoreSymbol(cmdCode, w);
            ref readonly CommandLut.Entry lutEntry = ref CommandLut.Table[cmdCode];
            int copyLenCode = cmd.CopyLength == 0 ? 4 : cmd.CopyLenCode;
            ulong insExtra = (ulong)(cmd.InsertLength - lutEntry.InsertOffset);
            ulong copyExtra = (ulong)(copyLenCode - lutEntry.CopyOffset);
            w.WriteBits(lutEntry.InsertExtraBits + lutEntry.CopyExtraBits, (copyExtra << lutEntry.InsertExtraBits) | insExtra);
            for (int j = cmd.InsertLength; j != 0; --j)
            {
                int context = lut[prevByte] | lut[256 + prevByte2];
                byte literal = buf[pos];
                literalEnc.StoreSymbolWithContext(literal, context, mb.LiteralContextMap, Constants.LiteralContextBits, w);
                prevByte2 = prevByte;
                prevByte = literal;
                ++pos;
            }
            pos += cmd.CopyLen;
            if (cmd.CopyLen != 0)
            {
                prevByte2 = buf[pos - 2];
                prevByte = buf[pos - 1];
                if (cmd.CmdPrefix >= 128)
                {
                    int distCode = cmd.DistPrefix & 0x3FF;
                    int distNumExtra = cmd.DistPrefix >> 10;
                    int context = CommandDistanceContext(cmd.CmdPrefix);
                    distanceEnc.StoreSymbolWithContext(distCode, context, mb.DistanceContextMap, Constants.DistanceContextBits, w);
                    w.WriteBits(distNumExtra, cmd.DistExtra);
                }
            }
        }
    }
}
