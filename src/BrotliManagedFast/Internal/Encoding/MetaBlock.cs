using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
#if NET7_0_OR_GREATER
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
#endif

namespace BrotliManagedFast.Internal;

/// <summary>Block types and lengths of one symbol category (the reference's BlockSplit).</summary>
internal sealed class BlockSplit
{
    public int NumTypes;
    public int NumBlocks;
    public byte[] Types = Array.Empty<byte>();
    public uint[] Lengths = Array.Empty<uint>();

    public void Reset()
    {
        NumTypes = 0;
        NumBlocks = 0;
    }

    public void EnsureCapacity(int n)
    {
        if (Types.Length < n) Array.Resize(ref Types, Math.Max(n, Types.Length * 2));
        if (Lengths.Length < n) Array.Resize(ref Lengths, Math.Max(n, Lengths.Length * 2));
    }
}

/// <summary>Symbol histogram with its total and cached bit cost (the reference's Histogram*).</summary>
internal sealed class Histogram
{
    public readonly uint[] Data;
    public int TotalCount;
    public double BitCost;

    public Histogram(int size)
    {
        Data = new uint[size];
        BitCost = double.PositiveInfinity;
    }

    public int Size => Data.Length;

    public void Clear()
    {
        Array.Clear(Data, 0, Data.Length);
        TotalCount = 0;
        BitCost = double.PositiveInfinity;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Add(int symbol)
    {
        Data[symbol]++;
        TotalCount++;
    }

    public void AddVector(ReadOnlySpan<ushort> p)
    {
        TotalCount += p.Length;
        for (int i = 0; i < p.Length; i++) Data[p[i]]++;
    }

    public void AddHistogram(Histogram v)
    {
        TotalCount += v.TotalCount;
        uint[] d = Data, s = v.Data;
        for (int i = 0; i < d.Length; i++) d[i] += s[i];
    }

    public void CopyFrom(Histogram v)
    {
        v.Data.CopyTo(Data, 0);
        TotalCount = v.TotalCount;
        BitCost = v.BitCost;
    }

    public static Histogram[] Create(int count, int size)
    {
        var a = new Histogram[count];
        for (int i = 0; i < count; i++) a[i] = new Histogram(size);
        return a;
    }
}

/// <summary>Entropy and Huffman-cost estimates (bit_cost.c).</summary>
internal static class BitCost
{
    /// <summary>log2 of 0..255 (log2(0) taken as 0), the reference's kBrotliLog2Table.</summary>
    private static readonly double[] Log2Table = BuildLog2Table();

    private static double[] BuildLog2Table()
    {
        var t = new double[256];
        for (int i = 1; i < 256; i++) t[i] = Log2Slow(i);
        return t;
    }

    private static double Log2Slow(double v)
    {
#if NETSTANDARD2_0
        return Math.Log(v) * 1.4426950408889634;
#else
        return Math.Log2(v);
#endif
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double FastLog2(long v) => (ulong)v < 256 ? Log2Table[v] : Log2Slow(v);

    public static double BitsEntropy(ReadOnlySpan<uint> population)
    {
        long sum = 0;
        double retval = 0;
        double[] table = Log2Table;
        for (int i = 0; i < population.Length; i++)
        {
            uint p = population[i];
            if (p == 0) continue;
            sum += p;
            retval -= p * (p < 256 ? table[p] : Log2Slow(p));
        }
        if (sum != 0) retval += sum * FastLog2(sum);
        if (retval < sum) retval = sum;
        return retval;
    }

    /// <summary>Estimated bits to store the Huffman code and the symbols of a histogram.</summary>
    public static double PopulationCost(Histogram histogram)
    {
        const double kOneSymbolHistogramCost = 12;
        const double kTwoSymbolHistogramCost = 20;
        const double kThreeSymbolHistogramCost = 28;
        const double kFourSymbolHistogramCost = 37;
        uint[] data = histogram.Data;
        int dataSize = data.Length;
        int count = 0;
        Span<int> s = stackalloc int[5];
        double bits = 0.0;
        if (histogram.TotalCount == 0) return kOneSymbolHistogramCost;
        for (int i = 0; i < dataSize; ++i)
        {
            if (data[i] > 0)
            {
                s[count] = i;
                ++count;
                if (count > 4) break;
            }
        }
        if (count == 1) return kOneSymbolHistogramCost;
        if (count == 2) return kTwoSymbolHistogramCost + histogram.TotalCount;
        if (count == 3)
        {
            uint h0 = data[s[0]], h1 = data[s[1]], h2 = data[s[2]];
            uint hmax = Math.Max(h0, Math.Max(h1, h2));
            return kThreeSymbolHistogramCost + 2 * (h0 + h1 + h2) - hmax;
        }
        if (count == 4)
        {
            Span<uint> h = stackalloc uint[4];
            for (int i = 0; i < 4; ++i) h[i] = data[s[i]];
            for (int i = 0; i < 4; ++i)
            {
                for (int j = i + 1; j < 4; ++j)
                {
                    if (h[j] > h[i]) (h[j], h[i]) = (h[i], h[j]);
                }
            }
            uint h23 = h[2] + h[3];
            uint hmax = Math.Max(h23, h[0]);
            return kFourSymbolHistogramCost + 3 * h23 + 2 * (h[0] + h[1]) - hmax;
        }
        {
            int maxDepth = 1;
            Span<uint> depthHisto = stackalloc uint[Constants.CodeLengthCodes];
            depthHisto.Clear();
            double log2total = FastLog2(histogram.TotalCount);
            for (int i = 0; i < dataSize;)
            {
                if (data[i] > 0)
                {
                    double log2p = log2total - FastLog2(data[i]);
                    int depth = (int)(log2p + 0.5);
                    bits += data[i] * log2p;
                    if (depth > 15) depth = 15;
                    if (depth > maxDepth) maxDepth = depth;
                    ++depthHisto[depth];
                    ++i;
                }
                else
                {
                    int reps = 1;
                    for (int k = i + 1; k < dataSize && data[k] == 0; ++k) ++reps;
                    i += reps;
                    if (i == dataSize) break;
                    if (reps < 3)
                    {
                        depthHisto[0] += (uint)reps;
                    }
                    else
                    {
                        reps -= 2;
                        while (reps > 0)
                        {
                            ++depthHisto[Constants.RepeatZeroCodeLength];
                            bits += 3;
                            reps >>= 3;
                        }
                    }
                }
            }
            bits += 18 + 2 * maxDepth;
            bits += BitsEntropy(depthHisto);
        }
        return bits;
    }
}

/// <summary>Greedy pairwise clustering of histograms (cluster.c).</summary>
internal static class HistogramCluster
{
    public struct HistogramPair
    {
        public int Idx1, Idx2;
        public double CostCombo, CostDiff;
    }

    private static bool IsLess(in HistogramPair p1, in HistogramPair p2)
    {
        if (p1.CostDiff != p2.CostDiff) return p1.CostDiff > p2.CostDiff;
        return (p1.Idx2 - p1.Idx1) > (p2.Idx2 - p2.Idx1);
    }

    private static double ClusterCostDiff(int sizeA, int sizeB)
    {
        int sizeC = sizeA + sizeB;
        return sizeA * BitCost.FastLog2(sizeA) + sizeB * BitCost.FastLog2(sizeB) - sizeC * BitCost.FastLog2(sizeC);
    }

    private static void CompareAndPushToQueue(Histogram[] output, Histogram tmp, uint[] clusterSize, int idx1, int idx2,
        int maxNumPairs, HistogramPair[] pairs, ref int numPairs)
    {
        bool isGoodPair = false;
        HistogramPair p = default;
        if (idx1 == idx2) return;
        if (idx2 < idx1) (idx1, idx2) = (idx2, idx1);
        p.Idx1 = idx1;
        p.Idx2 = idx2;
        p.CostDiff = 0.5 * ClusterCostDiff((int)clusterSize[idx1], (int)clusterSize[idx2]);
        p.CostDiff -= output[idx1].BitCost;
        p.CostDiff -= output[idx2].BitCost;
        if (output[idx1].TotalCount == 0)
        {
            p.CostCombo = output[idx2].BitCost;
            isGoodPair = true;
        }
        else if (output[idx2].TotalCount == 0)
        {
            p.CostCombo = output[idx1].BitCost;
            isGoodPair = true;
        }
        else
        {
            double threshold = numPairs == 0 ? 1e99 : Math.Max(0.0, pairs[0].CostDiff);
            tmp.CopyFrom(output[idx1]);
            tmp.AddHistogram(output[idx2]);
            double costCombo = BitCost.PopulationCost(tmp);
            if (costCombo < threshold - p.CostDiff)
            {
                p.CostCombo = costCombo;
                isGoodPair = true;
            }
        }
        if (isGoodPair)
        {
            p.CostDiff += p.CostCombo;
            if (numPairs > 0 && IsLess(pairs[0], p))
            {
                if (numPairs < maxNumPairs)
                {
                    pairs[numPairs] = pairs[0];
                    ++numPairs;
                }
                pairs[0] = p;
            }
            else if (numPairs < maxNumPairs)
            {
                pairs[numPairs] = p;
                ++numPairs;
            }
        }
    }

    /// <summary>Merges clusters while it reduces the cost; returns the remaining cluster count. <paramref name="clusters"/> is compacted in place.</summary>
    public static int Combine(Histogram[] output, Histogram tmp, uint[] clusterSize, uint[] symbols, int symbolsOffset, uint[] clusters, int clustersOffset,
        HistogramPair[] pairs, int numClusters, int symbolsSize, int maxClusters, int maxNumPairs)
    {
        double costDiffThreshold = 0.0;
        int minClusterSize = 1;
        int numPairs = 0;
        for (int idx1 = 0; idx1 < numClusters; ++idx1)
        {
            for (int idx2 = idx1 + 1; idx2 < numClusters; ++idx2)
            {
                CompareAndPushToQueue(output, tmp, clusterSize, (int)clusters[clustersOffset + idx1], (int)clusters[clustersOffset + idx2], maxNumPairs, pairs, ref numPairs);
            }
        }
        while (numClusters > minClusterSize)
        {
            if (pairs[0].CostDiff >= costDiffThreshold)
            {
                costDiffThreshold = 1e99;
                minClusterSize = maxClusters;
                continue;
            }
            int bestIdx1 = pairs[0].Idx1;
            int bestIdx2 = pairs[0].Idx2;
            output[bestIdx1].AddHistogram(output[bestIdx2]);
            output[bestIdx1].BitCost = pairs[0].CostCombo;
            clusterSize[bestIdx1] += clusterSize[bestIdx2];
            for (int i = 0; i < symbolsSize; ++i)
            {
                if (symbols[symbolsOffset + i] == bestIdx2) symbols[symbolsOffset + i] = (uint)bestIdx1;
            }
            for (int i = 0; i < numClusters; ++i)
            {
                if (clusters[clustersOffset + i] == bestIdx2)
                {
                    Array.Copy(clusters, clustersOffset + i + 1, clusters, clustersOffset + i, numClusters - i - 1);
                    break;
                }
            }
            --numClusters;
            {
                int copyToIdx = 0;
                for (int i = 0; i < numPairs; ++i)
                {
                    HistogramPair p = pairs[i];
                    if (p.Idx1 == bestIdx1 || p.Idx2 == bestIdx1 || p.Idx1 == bestIdx2 || p.Idx2 == bestIdx2) continue;
                    if (IsLess(pairs[0], p))
                    {
                        HistogramPair front = pairs[0];
                        pairs[0] = p;
                        pairs[copyToIdx] = front;
                    }
                    else
                    {
                        pairs[copyToIdx] = p;
                    }
                    ++copyToIdx;
                }
                numPairs = copyToIdx;
            }
            for (int i = 0; i < numClusters; ++i)
            {
                CompareAndPushToQueue(output, tmp, clusterSize, bestIdx1, (int)clusters[clustersOffset + i], maxNumPairs, pairs, ref numPairs);
            }
        }
        return numClusters;
    }

    public static double BitCostDistance(Histogram histogram, Histogram candidate, Histogram tmp)
    {
        if (histogram.TotalCount == 0) return 0.0;
        tmp.CopyFrom(histogram);
        tmp.AddHistogram(candidate);
        return BitCost.PopulationCost(tmp) - candidate.BitCost;
    }

    private static void Remap(Histogram[] input, int inSize, uint[] clusters, int numClusters, Histogram[] output, Histogram tmp, uint[] symbols)
    {
        for (int i = 0; i < inSize; ++i)
        {
            uint bestOut = i == 0 ? symbols[0] : symbols[i - 1];
            double bestBits = BitCostDistance(input[i], output[bestOut], tmp);
            for (int j = 0; j < numClusters; ++j)
            {
                double curBits = BitCostDistance(input[i], output[clusters[j]], tmp);
                if (curBits < bestBits)
                {
                    bestBits = curBits;
                    bestOut = clusters[j];
                }
            }
            symbols[i] = bestOut;
        }
        for (int i = 0; i < numClusters; ++i) output[clusters[i]].Clear();
        for (int i = 0; i < inSize; ++i) output[symbols[i]].AddHistogram(input[i]);
    }

    private static int Reindex(Histogram[] output, uint[] symbols, int length)
    {
        const uint kInvalidIndex = uint.MaxValue;
        var newIndex = new uint[length];
        for (int i = 0; i < newIndex.Length; i++) newIndex[i] = kInvalidIndex;
        uint nextIndex = 0;
        for (int i = 0; i < length; ++i)
        {
            if (newIndex[symbols[i]] == kInvalidIndex)
            {
                newIndex[symbols[i]] = nextIndex;
                ++nextIndex;
            }
        }
        var tmp = new Histogram[nextIndex];
        nextIndex = 0;
        for (int i = 0; i < length; ++i)
        {
            if (newIndex[symbols[i]] == nextIndex)
            {
                tmp[nextIndex] = output[symbols[i]];
                ++nextIndex;
            }
            symbols[i] = newIndex[symbols[i]];
        }
        for (int i = 0; i < nextIndex; ++i) output[i] = tmp[i];
        return (int)nextIndex;
    }

    /// <summary>
    /// Clusters <paramref name="input"/>[0..inSize) into at most <paramref name="maxHistograms"/> histograms in
    /// <paramref name="output"/> (same length as input, contents replaced) and writes the map into
    /// <paramref name="histogramSymbols"/>. Returns the number of output histograms.
    /// </summary>
    public static int ClusterHistograms(Histogram[] input, int inSize, int maxHistograms, Histogram[] output, uint[] histogramSymbols)
    {
        var clusterSize = new uint[inSize];
        var clusters = new uint[inSize];
        int numClusters = 0;
        const int maxInputHistograms = 64;
        int pairsCapacity = maxInputHistograms * maxInputHistograms / 2;
        var pairs = new HistogramPair[pairsCapacity + 1];
        var tmp = new Histogram(input[0].Size);
        for (int i = 0; i < inSize; ++i) clusterSize[i] = 1;
        for (int i = 0; i < inSize; ++i)
        {
            output[i].CopyFrom(input[i]);
            output[i].BitCost = BitCost.PopulationCost(input[i]);
            histogramSymbols[i] = (uint)i;
        }
        for (int i = 0; i < inSize; i += maxInputHistograms)
        {
            int numToCombine = Math.Min(inSize - i, maxInputHistograms);
            for (int j = 0; j < numToCombine; ++j) clusters[numClusters + j] = (uint)(i + j);
            int numNewClusters = Combine(output, tmp, clusterSize, histogramSymbols, i, clusters, numClusters, pairs, numToCombine, numToCombine, maxHistograms, pairsCapacity);
            numClusters += numNewClusters;
        }
        {
            int maxNumPairs = Math.Min(64 * numClusters, (numClusters / 2) * numClusters);
            if (pairs.Length < maxNumPairs + 1) pairs = new HistogramPair[maxNumPairs + 1];
            numClusters = Combine(output, tmp, clusterSize, histogramSymbols, 0, clusters, 0, pairs, numClusters, inSize, maxHistograms, maxNumPairs);
        }
        Remap(input, inSize, clusters, numClusters, output, tmp, histogramSymbols);
        return Reindex(output, histogramSymbols, inSize);
    }
}

/// <summary>Block split point selection (block_splitter.c), one symbol category at a time.</summary>
internal static class BlockSplitter
{
    private const int HistogramsPerBatch = 64;
    private const int ClustersPerBatch = 16;
    private const int MinLengthForBlockSplitting = 128;
    private const int IterMulForRefining = 2;
    private const int MinItersForRefining = 100;

    public const int MaxLiteralHistograms = 100;
    public const int MaxCommandHistograms = 50;
    public const double LiteralBlockSwitchCost = 28.1;
    public const double CommandBlockSwitchCost = 13.5;
    public const double DistanceBlockSwitchCost = 14.6;
    public const int LiteralStrideLength = 70;
    public const int CommandStrideLength = 40;
    public const int DistanceStrideLength = 40;
    public const int SymbolsPerLiteralHistogram = 544;
    public const int SymbolsPerCommandHistogram = 530;
    public const int SymbolsPerDistanceHistogram = 544;

    private static uint MyRand(ref uint seed)
    {
        seed *= 16807U;
        return seed;
    }

    private static double SymbolBitCost(uint count) => count == 0 ? -2.0 : BitCost.FastLog2(count);

    private static void InitialEntropyCodes(ushort[] data, int length, int stride, int numHistograms, Histogram[] histograms)
    {
        uint seed = 7;
        int blockLength = length / numHistograms;
        for (int i = 0; i < numHistograms; ++i) histograms[i].Clear();
        for (int i = 0; i < numHistograms; ++i)
        {
            int pos = (int)((long)length * i / numHistograms);
            if (i != 0) pos += (int)(MyRand(ref seed) % (uint)blockLength);
            if (pos + stride >= length) pos = length - stride - 1;
            histograms[i].AddVector(data.AsSpan(pos, stride));
        }
    }

    private static void RandomSample(ref uint seed, ushort[] data, int length, int stride, Histogram sample)
    {
        int pos = 0;
        if (stride >= length) stride = length;
        else pos = (int)(MyRand(ref seed) % (uint)(length - stride + 1));
        sample.AddVector(data.AsSpan(pos, stride));
    }

    private static void RefineEntropyCodes(ushort[] data, int length, int stride, int numHistograms, Histogram[] histograms, Histogram tmp)
    {
        long iters = (long)IterMulForRefining * length / stride + MinItersForRefining;
        uint seed = 7;
        iters = ((iters + numHistograms - 1) / numHistograms) * numHistograms;
        for (long iter = 0; iter < iters; ++iter)
        {
            tmp.Clear();
            RandomSample(ref seed, data, length, stride, tmp);
            histograms[(int)(iter % numHistograms)].AddHistogram(tmp);
        }
    }

    private static int FindBlocks(ushort[] data, int length, double blockSwitchBitcost, int numHistograms, Histogram[] histograms,
        double[] insertCost, double[] cost, byte[] switchSignal, byte[] blockId)
    {
        int alphabetSize = histograms[0].Size;
        int bitmapLen = (numHistograms + 7) >> 3;
        int numBlocks = 1;
        if (numHistograms <= 1)
        {
            for (int i = 0; i < length; ++i) blockId[i] = 0;
            return 1;
        }
        Array.Clear(insertCost, 0, alphabetSize * numHistograms);
        for (int i = 0; i < numHistograms; ++i) insertCost[i] = BitCost.FastLog2(histograms[i].TotalCount);
        for (int i = alphabetSize; i != 0;)
        {
            --i;
            for (int j = 0; j < numHistograms; ++j)
            {
                insertCost[i * numHistograms + j] = insertCost[j] - SymbolBitCost(histograms[j].Data[i]);
            }
        }
        Array.Clear(cost, 0, numHistograms);
        Array.Clear(switchSignal, 0, length * bitmapLen);
#if NET7_0_OR_GREATER
        if (System.Runtime.Intrinsics.X86.Avx2.IsSupported && numHistograms >= 8)
        {
            FindBlocksAvx2(data, length, blockSwitchBitcost, numHistograms, insertCost, cost, switchSignal, blockId, bitmapLen);
        }
        else
#endif
        {
            ref double costRef = ref MemoryMarshal.GetReference(cost.AsSpan());
            ref double icRef = ref MemoryMarshal.GetReference(insertCost.AsSpan());
            for (int byteIx = 0; byteIx < length; ++byteIx)
            {
                int ix = byteIx * bitmapLen;
                int symbol = data[byteIx];
                ref double ic = ref Unsafe.Add(ref icRef, symbol * numHistograms);
                double minCost = 1e99;
                int best = 0;
                double blockSwitchCost = blockSwitchBitcost;
                for (int k = 0; k < numHistograms; ++k)
                {
                    double c = Unsafe.Add(ref costRef, k) + Unsafe.Add(ref ic, k);
                    Unsafe.Add(ref costRef, k) = c;
                    if (c < minCost)
                    {
                        minCost = c;
                        best = k;
                    }
                }
                blockId[byteIx] = (byte)best;
                if (byteIx < 2000) blockSwitchCost *= 0.77 + 0.07 / 2000 * byteIx;
                for (int k = 0; k < numHistograms; ++k)
                {
                    double c = Unsafe.Add(ref costRef, k) - minCost;
                    if (c >= blockSwitchCost)
                    {
                        c = blockSwitchCost;
                        switchSignal[ix + (k >> 3)] |= (byte)(1u << (k & 7));
                    }
                    Unsafe.Add(ref costRef, k) = c;
                }
            }
        }
        {
            int byteIx = length - 1;
            int ix = byteIx * bitmapLen;
            byte curId = blockId[byteIx];
            while (byteIx > 0)
            {
                byte mask = (byte)(1u << (curId & 7));
                --byteIx;
                ix -= bitmapLen;
                if ((switchSignal[ix + (curId >> 3)] & mask) != 0)
                {
                    if (curId != blockId[byteIx])
                    {
                        curId = blockId[byteIx];
                        ++numBlocks;
                    }
                }
                blockId[byteIx] = curId;
            }
        }
        return numBlocks;
    }

#if NET7_0_OR_GREATER
    /// <summary>The two inner loops of <see cref="FindBlocks"/> four histograms at a time; requires numHistograms &gt;= 8.</summary>
    private static unsafe void FindBlocksAvx2(ushort[] data, int length, double blockSwitchBitcost, int numHistograms,
        double[] insertCost, double[] cost, byte[] switchSignal, byte[] blockId, int bitmapLen)
    {
        int vecEnd = numHistograms & ~3;
        fixed (double* costP = cost)
        fixed (double* icBase = insertCost)
        fixed (byte* sigBase = switchSignal)
        {
            for (int byteIx = 0; byteIx < length; ++byteIx)
            {
                byte* sig = sigBase + byteIx * bitmapLen;
                double* ic = icBase + data[byteIx] * numHistograms;
                var vmin = Vector256.Create(1e99);
                for (int k = 0; k < vecEnd; k += 4)
                {
                    var c = Avx.Add(Avx.LoadVector256(costP + k), Avx.LoadVector256(ic + k));
                    Avx.Store(costP + k, c);
                    vmin = Avx.Min(vmin, c);
                }
                double minCost = Math.Min(Math.Min(vmin.GetElement(0), vmin.GetElement(1)), Math.Min(vmin.GetElement(2), vmin.GetElement(3)));
                int best = -1;
                for (int k = vecEnd; k < numHistograms; ++k)
                {
                    double c = costP[k] + ic[k];
                    costP[k] = c;
                    if (c < minCost) minCost = c;
                }
                // First histogram at the minimum (the scalar rule).
                var vminAll = Vector256.Create(minCost);
                for (int k = 0; k < vecEnd; k += 4)
                {
                    int eq = Avx.MoveMask(Avx.CompareEqual(Avx.LoadVector256(costP + k), vminAll));
                    if (eq != 0)
                    {
                        best = k + BitOperations.TrailingZeroCount(eq);
                        break;
                    }
                }
                if (best < 0)
                {
                    for (int k = vecEnd; k < numHistograms; ++k)
                    {
                        if (costP[k] == minCost) { best = k; break; }
                    }
                }
                blockId[byteIx] = (byte)best;
                double blockSwitchCost = blockSwitchBitcost;
                if (byteIx < 2000) blockSwitchCost *= 0.77 + 0.07 / 2000 * byteIx;
                var vsw = Vector256.Create(blockSwitchCost);
                for (int k = 0; k < vecEnd; k += 4)
                {
                    var c = Avx.Subtract(Avx.LoadVector256(costP + k), vminAll);
                    int over = Avx.MoveMask(Avx.CompareGreaterThanOrEqual(c, vsw));
                    if (over != 0)
                    {
                        c = Avx.Min(c, vsw);
                        sig[k >> 3] |= (byte)(over << (k & 7));
                    }
                    Avx.Store(costP + k, c);
                }
                for (int k = vecEnd; k < numHistograms; ++k)
                {
                    double c = costP[k] - minCost;
                    if (c >= blockSwitchCost)
                    {
                        c = blockSwitchCost;
                        sig[k >> 3] |= (byte)(1u << (k & 7));
                    }
                    costP[k] = c;
                }
            }
        }
    }
#endif

    private static int RemapBlockIds(byte[] blockIds, int length, ushort[] newId, int numHistograms)
    {
        const ushort kInvalidId = 256;
        ushort nextId = 0;
        for (int i = 0; i < numHistograms; ++i) newId[i] = kInvalidId;
        for (int i = 0; i < length; ++i)
        {
            if (newId[blockIds[i]] == kInvalidId) newId[blockIds[i]] = nextId++;
        }
        for (int i = 0; i < length; ++i) blockIds[i] = (byte)newId[blockIds[i]];
        return nextId;
    }

    private static void BuildBlockHistograms(ushort[] data, int length, byte[] blockIds, int numHistograms, Histogram[] histograms)
    {
        for (int i = 0; i < numHistograms; ++i) histograms[i].Clear();
        for (int i = 0; i < length; ++i) histograms[blockIds[i]].Add(data[i]);
    }

    private static void ClusterBlocks(ushort[] data, int length, int numBlocks, byte[] blockIds, BlockSplit split, int alphabetSize)
    {
        var histogramSymbols = new uint[numBlocks];
        var blockLengths = new uint[numBlocks];
        int expectedNumClusters = ClustersPerBatch * (numBlocks + HistogramsPerBatch - 1) / HistogramsPerBatch;
        var allHistograms = new List<Histogram>(expectedNumClusters);
        var clusterSizeList = new List<uint>(expectedNumClusters);
        int numClusters = 0;
        Histogram[] histograms = Histogram.Create(Math.Min(numBlocks, HistogramsPerBatch), alphabetSize);
        int maxNumPairs = HistogramsPerBatch * HistogramsPerBatch / 2;
        var pairs = new HistogramCluster.HistogramPair[maxNumPairs + 1];
        int pos = 0;
        var sizes = new uint[HistogramsPerBatch];
        var newClusters = new uint[HistogramsPerBatch];
        var symbols = new uint[HistogramsPerBatch];
        var remap = new uint[HistogramsPerBatch];
        var tmp = new Histogram(alphabetSize);
        {
            int blockIdx = 0;
            for (int i = 0; i < length; ++i)
            {
                ++blockLengths[blockIdx];
                if (i + 1 == length || blockIds[i] != blockIds[i + 1]) ++blockIdx;
            }
        }
        for (int i = 0; i < numBlocks; i += HistogramsPerBatch)
        {
            int numToCombine = Math.Min(numBlocks - i, HistogramsPerBatch);
            for (int j = 0; j < numToCombine; ++j)
            {
                uint blockLength = blockLengths[i + j];
                histograms[j].Clear();
                for (uint k = 0; k < blockLength; ++k) histograms[j].Add(data[pos++]);
                histograms[j].BitCost = BitCost.PopulationCost(histograms[j]);
                newClusters[j] = (uint)j;
                symbols[j] = (uint)j;
                sizes[j] = 1;
            }
            int numNewClusters = HistogramCluster.Combine(histograms, tmp, sizes, symbols, 0, newClusters, 0, pairs, numToCombine, numToCombine, HistogramsPerBatch, maxNumPairs);
            for (int j = 0; j < numNewClusters; ++j)
            {
                var h = new Histogram(alphabetSize);
                h.CopyFrom(histograms[newClusters[j]]);
                allHistograms.Add(h);
                clusterSizeList.Add(sizes[newClusters[j]]);
                remap[newClusters[j]] = (uint)j;
            }
            for (int j = 0; j < numToCombine; ++j) histogramSymbols[i + j] = (uint)numClusters + remap[symbols[j]];
            numClusters += numNewClusters;
        }
        Histogram[] all = allHistograms.ToArray();
        uint[] clusterSize = clusterSizeList.ToArray();
        maxNumPairs = Math.Min(64 * numClusters, (numClusters / 2) * numClusters);
        if (pairs.Length < maxNumPairs + 1) pairs = new HistogramCluster.HistogramPair[maxNumPairs + 1];
        var clusters = new uint[numClusters];
        for (int i = 0; i < numClusters; ++i) clusters[i] = (uint)i;
        int numFinalClusters = HistogramCluster.Combine(all, tmp, clusterSize, histogramSymbols, 0, clusters, 0, pairs, numClusters, numBlocks, Constants.MaxBlockTypes, maxNumPairs);
        var newIndex = new uint[numClusters];
        for (int i = 0; i < newIndex.Length; i++) newIndex[i] = uint.MaxValue;
        pos = 0;
        {
            uint nextIndex = 0;
            var tmp2 = new Histogram(alphabetSize);
            for (int i = 0; i < numBlocks; ++i)
            {
                tmp.Clear();
                for (uint j = 0; j < blockLengths[i]; ++j) tmp.Add(data[pos++]);
                uint bestOut = i == 0 ? histogramSymbols[0] : histogramSymbols[i - 1];
                double bestBits = HistogramCluster.BitCostDistance(tmp, all[bestOut], tmp2);
                for (int j = 0; j < numFinalClusters; ++j)
                {
                    double curBits = HistogramCluster.BitCostDistance(tmp, all[clusters[j]], tmp2);
                    if (curBits < bestBits)
                    {
                        bestBits = curBits;
                        bestOut = clusters[j];
                    }
                }
                histogramSymbols[i] = bestOut;
                if (newIndex[bestOut] == uint.MaxValue) newIndex[bestOut] = nextIndex++;
            }
        }
        split.EnsureCapacity(numBlocks);
        {
            uint curLength = 0;
            int blockIdx = 0;
            byte maxType = 0;
            for (int i = 0; i < numBlocks; ++i)
            {
                curLength += blockLengths[i];
                if (i + 1 == numBlocks || histogramSymbols[i] != histogramSymbols[i + 1])
                {
                    byte id = (byte)newIndex[histogramSymbols[i]];
                    split.Types[blockIdx] = id;
                    split.Lengths[blockIdx] = curLength;
                    maxType = Math.Max(maxType, id);
                    curLength = 0;
                    ++blockIdx;
                }
            }
            split.NumBlocks = blockIdx;
            split.NumTypes = maxType + 1;
        }
    }

    /// <summary>Splits <paramref name="data"/>[0..length) into blocks of similar statistics.</summary>
    public static void SplitByteVector(ushort[] data, int length, int alphabetSize, int symbolsPerHistogram, int maxHistograms,
        int samplingStrideLength, double blockSwitchCost, BlockSplit split)
    {
        int numHistograms = length / symbolsPerHistogram + 1;
        if (numHistograms > maxHistograms) numHistograms = maxHistograms;
        if (length == 0)
        {
            split.NumTypes = 1;
            return;
        }
        if (length < MinLengthForBlockSplitting)
        {
            split.EnsureCapacity(split.NumBlocks + 1);
            split.NumTypes = 1;
            split.Types[split.NumBlocks] = 0;
            split.Lengths[split.NumBlocks] = (uint)length;
            split.NumBlocks++;
            return;
        }
        Histogram[] histograms = Histogram.Create(numHistograms + 1, alphabetSize);
        Histogram tmp = histograms[numHistograms];
        InitialEntropyCodes(data, length, samplingStrideLength, numHistograms, histograms);
        RefineEntropyCodes(data, length, samplingStrideLength, numHistograms, histograms, tmp);
        {
            var blockIds = new byte[length];
            int numBlocks = 0;
            int bitmaplen = (numHistograms + 7) >> 3;
            var insertCost = new double[alphabetSize * numHistograms];
            var cost = new double[numHistograms];
            var switchSignal = new byte[length * bitmaplen];
            var newId = new ushort[numHistograms];
            const int iters = 10;   // quality 11
            // The loop is a fixed-point iteration: an assignment equal to the previous one rebuilds equal
            // histograms, and FindBlocks is deterministic, so every remaining pass would reproduce the same
            // assignment. Stopping there is bit-identical, and the comparison is exact rather than a proxy
            // such as an equal block count.
            var prevIds = new byte[length];
            int prevHistograms = -1;
            for (int i = 0; i < iters; ++i)
            {
                numBlocks = FindBlocks(data, length, blockSwitchCost, numHistograms, histograms, insertCost, cost, switchSignal, blockIds);
                numHistograms = RemapBlockIds(blockIds, length, newId, numHistograms);
                if (numHistograms == prevHistograms && blockIds.AsSpan(0, length).SequenceEqual(prevIds.AsSpan(0, length)))
                {
                    break;
                }
                BuildBlockHistograms(data, length, blockIds, numHistograms, histograms);
                Array.Copy(blockIds, prevIds, length);
                prevHistograms = numHistograms;
            }
            ClusterBlocks(data, length, numBlocks, blockIds, split, alphabetSize);
        }
    }
}
