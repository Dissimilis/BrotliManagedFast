using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace BrotliManagedFast.Internal;

/// <summary>Receives the commands of a quality-11 parse. Distance codes are the intermediate codes: 0-15 short codes, else distance + 15.</summary>
internal interface IZopfliSink
{
    void AddZopfliCommand(int literalIdx, int insertLength, int copyLength, int copyLengthCode, int distCode, int distance, bool isDictionary);
}

/// <summary>One backward match: distance, length and, for static-dictionary words, the length the code describes (0 = same as the length).</summary>
internal struct BackwardMatch
{
    public int Distance;
    public int Length;
    public int LengthCode;

    public int Code => LengthCode == 0 ? Length : LengthCode;
}

/// <summary>
/// Hash table whose buckets are binary trees of positions sharing a 4-byte hash, sorted lexicographically
/// (the reference's H10). Positions are the encoder's virtual positions (dictionary bytes are negative).
/// </summary>
internal sealed class BinaryTreeHasher
{
    public const int BucketBits = 17;
    public const int MaxTreeSearchDepth = 64;
    public const int MaxTreeCompLength = 128;
    private const uint HashMul32 = 0x1E35A7BD;
    private const int Invalid = int.MinValue;

    /// <summary>Largest tree the forest covers (2^24 positions = 128 MB, the reference's size for a 16 MB window).</summary>
    private const int MaxTreeBits = 24;

    private readonly int[] _buckets = new int[1 << BucketBits];
    private int[] _forest;
    private readonly int _windowMask;

    /// <param name="treeBits">Log2 of the positions the forest covers; positions alias above it.</param>
    public BinaryTreeHasher(int treeBits)
    {
        // The forest costs 8 bytes per covered position, so it is capped (Large Window) and shrunk to the
        // announced input. Two positions further apart than the covered span share forest slots, and a tree
        // that links foreign nodes breaks the invariant the traversal relies on (it starts comparing at the
        // prefix length inherited from the path and never rechecks those bytes), which would report matches
        // that do not hold. Candidates further back than the covered span are therefore never followed, see
        // the distance test in StoreAndFindMatches.
        treeBits = Math.Min(Math.Max(treeBits, Constants.MinWindowBits), MaxTreeBits);
        _windowMask = (1 << treeBits) - 1;
        // Rented, never zeroed: a node is written before it can be reached, because every traversal starts at a
        // bucket and Reset() puts every bucket out of use. The rented array may be longer than asked for, which
        // is why the mask above, not the array length, bounds every index.
        _forest = ArrayPool<int>.Shared.Rent(2 << treeBits);
        Reset();
    }

    public void Reset()
    {
        for (int i = 0; i < _buckets.Length; i++) _buckets[i] = Invalid;
    }

    /// <summary>Returns the forest to the pool. The hasher is unusable afterwards.</summary>
    public void ReleaseBuffers()
    {
        if (_forest.Length == 0) return;
        ArrayPool<int>.Shared.Return(_forest);
        _forest = Array.Empty<int>();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Hash(byte[] data, int idx)
    {
        uint v = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref MemoryMarshal.GetReference(data.AsSpan()), idx));
        if (!BitConverter.IsLittleEndian) v = BinaryPrimitives.ReverseEndianness(v);
        uint h = v * HashMul32;
        return h >> (32 - BucketBits);
    }

    /// <summary>Number of equal bytes at the two buffer indexes, at most <paramref name="limit"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int MatchLength(byte[] data, int a, int b, int limit)
    {
        int matched = 0;
        ref byte pa = ref MemoryMarshal.GetReference(data.AsSpan());
        while (limit >= 8)
        {
            ulong x = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref pa, a + matched));
            ulong y = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref pa, b + matched));
            if (x != y)
            {
                ulong diff = x ^ y;
                if (!BitConverter.IsLittleEndian) diff = BinaryPrimitives.ReverseEndianness(diff);
#if NETSTANDARD2_0
                int tz = 0;
                while ((diff & 1) == 0) { diff >>= 1; tz++; }
                return matched + (tz >> 3);
#else
                return matched + (BitOperations.TrailingZeroCount(diff) >> 3);
#endif
            }
            matched += 8;
            limit -= 8;
        }
        while (limit > 0 && Unsafe.Add(ref pa, a + matched) == Unsafe.Add(ref pa, b + matched))
        {
            matched++;
            limit--;
        }
        return matched;
    }

    /// <summary>
    /// Stores the hash of the next 4 bytes and, in one traversal, searches the bucket's tree for matches and
    /// re-roots it at the current position (only when at least 128 bytes follow, so the sort order is final).
    /// Returns the number of matches appended (of strictly increasing length).
    /// </summary>
    public int StoreAndFindMatches(byte[] data, long bufBase, long cur, int maxLength, long maxBackward, ref int bestLen, BackwardMatch[]? matches, int matchPos)
    {
        int curIdx = (int)(cur - bufBase);
        int maxCompLen = Math.Min(maxLength, MaxTreeCompLength);
        bool shouldReroot = maxLength >= MaxTreeCompLength;
        uint key = Hash(data, curIdx);
        int[] buckets = _buckets;
        int[] forest = _forest;
        ref byte dataRef = ref MemoryMarshal.GetReference(data.AsSpan());
        int prev = buckets[key];
        int nodeLeft = 2 * (int)(cur & _windowMask);
        int nodeRight = nodeLeft + 1;
        int bestLenLeft = 0;
        int bestLenRight = 0;
        int added = 0;
        if (shouldReroot) buckets[key] = (int)cur;
        for (int depth = MaxTreeSearchDepth; ; --depth)
        {
            long backward = prev == Invalid ? long.MaxValue : cur - prev;
            // |_windowMask| bounds the distance as well as the slot: within one covered span every position
            // has its own forest slot, so the tree cannot contain a foreign node and the inherited prefix holds.
            if (backward <= 0 || backward > maxBackward || backward > _windowMask || depth == 0)
            {
                if (shouldReroot)
                {
                    forest[nodeLeft] = Invalid;
                    forest[nodeRight] = Invalid;
                }
                break;
            }
            int prevIdx = (int)(prev - bufBase);
            int curLen = Math.Min(bestLenLeft, bestLenRight);
            int len = curLen + MatchLength(data, curIdx + curLen, prevIdx + curLen, maxLength - curLen);
            // A prefix-dictionary candidate (negative position) cannot be copied past the dictionary's end.
            int reportLen = prev < 0 ? Math.Min(len, -prev) : len;
            if (matches != null && reportLen > bestLen)
            {
                bestLen = reportLen;
                matches[matchPos + added] = new BackwardMatch { Distance = (int)backward, Length = reportLen };
                added++;
            }
            if (len >= maxCompLen)
            {
                if (shouldReroot)
                {
                    forest[nodeLeft] = forest[2 * (int)(prev & _windowMask)];
                    forest[nodeRight] = forest[2 * (int)(prev & _windowMask) + 1];
                }
                break;
            }
            if (Unsafe.Add(ref dataRef, curIdx + len) > Unsafe.Add(ref dataRef, prevIdx + len))
            {
                bestLenLeft = len;
                if (shouldReroot) forest[nodeLeft] = prev;
                nodeLeft = 2 * (int)(prev & _windowMask) + 1;
                prev = forest[nodeLeft];
            }
            else
            {
                bestLenRight = len;
                if (shouldReroot) forest[nodeRight] = prev;
                nodeRight = 2 * (int)(prev & _windowMask);
                prev = forest[nodeRight];
            }
        }
        return added;
    }

    /// <summary>
    /// Finds all backward matches at <paramref name="cur"/> up to <paramref name="maxLength"/> and stores the
    /// position. Matches come out sorted by strictly increasing length.
    /// </summary>
    public int FindAllMatches(byte[] data, long bufBase, long cur, int maxLength, long maxBackward, BackwardMatch[] matches, int matchPos)
    {
        int curIdx = (int)(cur - bufBase);
        int bestLen = 1;
        int count = 0;
        const int shortMatchMaxBackward = 64;   // quality 11
        long stop = cur - shortMatchMaxBackward;
        long historyStart = cur - maxBackward;
        if (stop < historyStart) stop = historyStart;
        // Near positions are scanned directly (the tree only indexes 4-byte prefixes): two bytes of the
        // candidate are compared with one load.
        ref byte dataRef = ref MemoryMarshal.GetReference(data.AsSpan());
        ushort curPair = Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref dataRef, curIdx));
        for (long i = cur - 1; i > stop && bestLen <= 2; --i)
        {
            int prevIdx = (int)(i - bufBase);
            if (Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref dataRef, prevIdx)) != curPair) continue;
            int len = MatchLength(data, prevIdx, curIdx, maxLength);
            if (i < 0) len = Math.Min(len, (int)-i);
            if (len > bestLen)
            {
                bestLen = len;
                matches[matchPos + count] = new BackwardMatch { Distance = (int)(cur - i), Length = len };
                count++;
            }
        }
        if (bestLen < maxLength)
        {
            count += StoreAndFindMatches(data, bufBase, cur, maxLength, maxBackward, ref bestLen, matches, matchPos + count);
        }
        return count;
    }

    /// <summary>Stores a position without returning matches. Requires 128 bytes of data after it.</summary>
    public void Store(byte[] data, long bufBase, long pos, long maxBackward)
    {
        int unused = 0;
        StoreAndFindMatches(data, bufBase, pos, MaxTreeCompLength, maxBackward, ref unused, null, 0);
    }

    /// <summary>Stores the positions [start, end): every one near the end, every 8th further back (the reference's StoreRange).</summary>
    public void StoreRange(byte[] data, long bufBase, long start, long end, long maxBackward)
    {
        long i = start;
        long j = start;
        if (start + 63 <= end) i = end - 63;
        if (start + 512 <= i)
        {
            for (; j < i; j += 8) Store(data, bufBase, j, maxBackward);
        }
        for (; i < end; ++i) Store(data, bufBase, i, maxBackward);
    }
}

/// <summary>
/// The reference's quality-11 parser: all matches per position from the binary-tree hasher, then two
/// shortest-path passes over a cost model (first from local literal statistics, then from the commands of
/// the first pass). Produces the same command structure as the other parsers through <see cref="IZopfliSink"/>.
/// </summary>
internal sealed class ZopfliParser
{
    private const float Infinity = 1.7e38f;
    private const int LongCopyQuickStep = 16384;
    private const int MaxNumMatches = 128;         // 64 short + MaxTreeSearchDepth

    private static readonly int[] DistanceCacheIndex = { 0, 1, 2, 3, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 1, 1 };
    private static readonly int[] DistanceCacheOffset = { 0, 0, 0, 0, -1, 1, -2, 2, -3, 3, -1, 1, -2, 2, -3, 3 };

    /// <summary>Path node: copy length (low 25 bits) and length-code modifier, distance, short code (high 5 bits) and insert length, and a union of cost / next / shortcut.</summary>
    private struct Node
    {
        public uint Length;
        public uint Distance;
        public uint DcodeInsertLength;
        public uint U;

        public float Cost
        {
            get { uint u = U; return Unsafe.As<uint, float>(ref u); }
            set { float f = value; U = Unsafe.As<float, uint>(ref f); }
        }
        public uint CopyLength => Length & 0x1FFFFFF;
        public uint LengthCode => CopyLength + 9u - (Length >> 25);
        public uint InsertLength => DcodeInsertLength & 0x7FFFFFF;
        public uint CommandLength => CopyLength + InsertLength;
        public uint DistanceCode
        {
            get
            {
                uint shortCode = DcodeInsertLength >> 27;
                return shortCode == 0 ? Distance + Constants.NumDistanceShortCodes - 1 : shortCode - 1;
            }
        }
    }

    private struct PosData
    {
        public int Pos;
        public int Cache0, Cache1, Cache2, Cache3;
        public float CostDiff;
        public float Cost;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int Cache(int i) => Unsafe.Add(ref Cache0, i);
    }

    /// <summary>The eight command start positions with the smallest cost difference.</summary>
    private sealed class StartPosQueue
    {
        private readonly PosData[] _q = new PosData[8];
        public int Idx;

        public void Reset() => Idx = 0;

        public int Size => Math.Min(Idx, 8);

        public ref PosData At(int slot) => ref _q[slot & 7];

        public ref PosData Get(int k) => ref At(k - Idx);

        public void Push(in PosData posdata)
        {
            int offset = ~Idx++ & 7;
            int len = Size;
            At(offset) = posdata;
            for (int i = 1; i < len; ++i)
            {
                if (At(offset & 7).CostDiff > At((offset + 1) & 7).CostDiff)
                {
                    (At(offset & 7), At((offset + 1) & 7)) = (At((offset + 1) & 7), At(offset & 7));
                }
                ++offset;
            }
        }
    }

    // Cost model
    private readonly float[] _costCmd = new float[Constants.NumCommandSymbols];
    private float[] _costDist = Array.Empty<float>();
    private int _distanceHistogramSize;
    private float[] _literalCosts = Array.Empty<float>();
    private float _minCostCmd;
    private int _numBytes;
    private readonly uint[] _histoLiteral = new uint[Constants.NumLiteralSymbols];
    private readonly uint[] _histoCmd = new uint[Constants.NumCommandSymbols];
    private readonly uint[] _histoDist = new uint[544];
    private readonly float[] _costLiteral = new float[Constants.NumLiteralSymbols];
    private readonly int[] _literalHistograms = new int[3 * 256];

    private readonly StartPosQueue _queue = new();
    private Node[] _nodes = Array.Empty<Node>();
    private int[] _numMatches = Array.Empty<int>();
    private BackwardMatch[] _matches = new BackwardMatch[1 << 16];

    // Commands of the current chunk, kept so the second pass can read them (insert, copy, code, distance).
    private struct Cmd
    {
        public int Insert, Copy, LenCode, DistCode, Distance, CmdCode, DistSymbol;
        public bool IsDictionary;
    }
    private Cmd[] _cmds = new Cmd[1 << 12];
    private int _numCmds;

    private readonly BinaryTreeHasher _hasher;
    private readonly int _distanceAlphabetLimit;
    private readonly long _maxDistanceLimit;
    /// <summary>Copy length above which only the longest match is considered (150 at quality 10, 325 at 11).</summary>
    private readonly int _maxZopfliLen;
    /// <summary>Command start positions expanded per position (1 at quality 10, 5 at 11).</summary>
    private readonly int _maxCandidates;
    /// <summary>Shortest-path passes; the second uses the cost model of the first pass's commands.</summary>
    private readonly int _passes;
    private readonly uint[] _dictMatches = new uint[StaticDictionaryMatcher.MaxMatchLength + 1];
    // Bytes before the stream that the decoder can address (prefix dictionary); static words lie beyond window + prefix.
    private int _prefixLength;
    private bool _useDictionary;

    public ZopfliParser(int treeBits, int distanceAlphabetLimit, long maxDistanceLimit, int maxZopfliLen, int maxCandidates, int passes)
    {
        _hasher = new BinaryTreeHasher(treeBits);
        _distanceAlphabetLimit = distanceAlphabetLimit;
        _maxDistanceLimit = maxDistanceLimit;
        _maxZopfliLen = maxZopfliLen;
        _maxCandidates = maxCandidates;
        _passes = passes;
    }

    public BinaryTreeHasher Hasher => _hasher;

    /// <summary>Returns the parser's pooled buffers; it is unusable afterwards.</summary>
    public void ReleaseBuffers() => _hasher.ReleaseBuffers();

    // ------------------------------------------------------------------ helpers

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float FastLog2(long v) => v == 0 ? 0f : (float)Log2(v);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double Log2(double v)
    {
#if NETSTANDARD2_0
        return Math.Log(v) * 1.4426950408889634;
#else
        return Math.Log2(v);
#endif
    }

    private static int GetInsertLengthCode(int insertlen)
    {
        if (insertlen < 6) return insertlen;
        if (insertlen < 130)
        {
            int nbits = BitOperations.Log2((uint)(insertlen - 2)) - 1;
            return (nbits << 1) + ((insertlen - 2) >> nbits) + 2;
        }
        if (insertlen < 2114) return BitOperations.Log2((uint)(insertlen - 66)) + 10;
        if (insertlen < 6210) return 21;
        if (insertlen < 22594) return 22;
        return 23;
    }

    /// <summary>Copy length code per length up to 2117; longer lengths use code 23.</summary>
    private static readonly byte[] CopyCodeTable = BuildCopyCodeTable();
    /// <summary>Insert length code per length up to 6209; longer lengths use codes 22 and 23.</summary>
    private static readonly byte[] InsertCodeTable = BuildInsertCodeTable();
    /// <summary>Command code for (inscode, copycode) without and with the last-distance flag.</summary>
    private static readonly ushort[] CombineTable = BuildCombineTable(false);
    private static readonly ushort[] CombineTableLast = BuildCombineTable(true);
    private static readonly byte[] CopyExtraTable = Constants.CopyLengthExtraBits.ToArray();
    private static readonly byte[] InsertExtraTable = Constants.InsertLengthExtraBits.ToArray();

    private static byte[] BuildCopyCodeTable()
    {
        var t = new byte[2118];
        for (int len = 2; len < t.Length; len++) t[len] = (byte)GetCopyLengthCode(len);
        return t;
    }

    private static byte[] BuildInsertCodeTable()
    {
        var t = new byte[6210];
        for (int len = 0; len < t.Length; len++) t[len] = (byte)GetInsertLengthCode(len);
        return t;
    }

    private static ushort[] BuildCombineTable(bool useLastDistance)
    {
        var t = new ushort[24 * 24];
        for (int ins = 0; ins < 24; ins++)
        {
            for (int copy = 0; copy < 24; copy++) t[ins * 24 + copy] = (ushort)CombineLengthCodes(ins, copy, useLastDistance);
        }
        return t;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int CopyCode(int len) => len < 2118 ? CopyCodeTable[len] : 23;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int InsertCode(int len) => len < 6210 ? InsertCodeTable[len] : (len < 22594 ? 22 : 23);

    private static int GetCopyLengthCode(int copylen)
    {
        if (copylen < 10) return copylen - 2;
        if (copylen < 134)
        {
            int nbits = BitOperations.Log2((uint)(copylen - 6)) - 1;
            return (nbits << 1) + ((copylen - 6) >> nbits) + 4;
        }
        if (copylen < 2118) return BitOperations.Log2((uint)(copylen - 70)) + 12;
        return 23;
    }

    private static int CombineLengthCodes(int inscode, int copycode, bool useLastDistance)
    {
        int bits64 = (copycode & 0x7) | ((inscode & 0x7) << 3);
        if (useLastDistance && inscode < 8 && copycode < 16)
        {
            return copycode < 8 ? bits64 : bits64 | 64;
        }
        int offset = 2 * ((copycode >> 3) + 3 * (inscode >> 3));
        offset = (offset << 5) + 0x40 + ((0x520D40 >> offset) & 0xC0);
        return offset | bits64;
    }

    /// <summary>Distance symbol (low 10 bits) and extra-bit count (high bits) for NPOSTFIX = NDIRECT = 0.</summary>
    private static int DistanceSymbol(int distanceCode)
    {
        if (distanceCode < Constants.NumDistanceShortCodes) return distanceCode;
        long dist = 4 + (distanceCode - Constants.NumDistanceShortCodes);
        int bucket = BitOperations.Log2((ulong)dist) - 1;
        long prefix = (dist >> bucket) & 1;
        int nbits = bucket;
        return (nbits << 10) | (int)(Constants.NumDistanceShortCodes + (2 * (nbits - 1) + prefix));
    }

    // ------------------------------------------------------------------ cost model

    private void SetCost(uint[] histogram, int size, bool literal, float[] cost)
    {
        long sum = 0;
        for (int i = 0; i < size; i++) sum += histogram[i];
        float log2sum = FastLog2(sum);
        long missingSum = sum;
        if (!literal)
        {
            for (int i = 0; i < size; i++) if (histogram[i] == 0) missingSum++;
        }
        float missingCost = FastLog2(missingSum) + 2;
        for (int i = 0; i < size; i++)
        {
            if (histogram[i] == 0)
            {
                cost[i] = missingCost;
                continue;
            }
            cost[i] = log2sum - FastLog2(histogram[i]);
            if (cost[i] < 1) cost[i] = 1;
        }
    }

    private void SetCostFromCommands(byte[] buf, long bufBase, long position, int lastInsertLen)
    {
        long pos = position - lastInsertLen;
        Array.Clear(_histoLiteral, 0, _histoLiteral.Length);
        Array.Clear(_histoCmd, 0, _histoCmd.Length);
        Array.Clear(_histoDist, 0, _histoDist.Length);
        for (int i = 0; i < _numCmds; i++)
        {
            ref Cmd c = ref _cmds[i];
            _histoCmd[c.CmdCode]++;
            if (c.CmdCode >= 128) _histoDist[c.DistSymbol & 0x3FF]++;
            int idx = (int)(pos - bufBase);
            for (int j = 0; j < c.Insert; j++) _histoLiteral[buf[idx + j]]++;
            pos += c.Insert + c.Copy;
        }
        SetCost(_histoLiteral, Constants.NumLiteralSymbols, true, _costLiteral);
        SetCost(_histoCmd, Constants.NumCommandSymbols, false, _costCmd);
        SetCost(_histoDist, _distanceHistogramSize, false, _costDist);
        float minCost = Infinity;
        for (int i = 0; i < Constants.NumCommandSymbols; ++i) minCost = Math.Min(minCost, _costCmd[i]);
        _minCostCmd = minCost;
        float[] lc = _literalCosts;
        float carry = 0f;
        int start = (int)(position - bufBase);
        lc[0] = 0f;
        for (int i = 0; i < _numBytes; ++i)
        {
            carry += _costLiteral[buf[start + i]];
            lc[i + 1] = lc[i] + carry;
            carry -= lc[i + 1] - lc[i];
        }
    }

    private void SetCostFromLiteralCosts(byte[] buf, int start)
    {
        float[] lc = _literalCosts;
        EstimateBitCostsForLiterals(buf, start, _numBytes, lc);
        // lc[1..] holds per-byte costs; turn into cumulative sums with error carry.
        float carry = 0f;
        lc[0] = 0f;
        for (int i = 0; i < _numBytes; ++i)
        {
            carry += lc[i + 1];
            lc[i + 1] = lc[i] + carry;
            carry -= lc[i + 1] - lc[i];
        }
        for (int i = 0; i < Constants.NumCommandSymbols; ++i) _costCmd[i] = FastLog2(11 + i);
        for (int i = 0; i < _distanceHistogramSize; ++i) _costDist[i] = FastLog2(20 + i);
        _minCostCmd = FastLog2(11);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private float LiteralCosts(int from, int to) => _literalCosts[to] - _literalCosts[from];

    // ------------------------------------------------------------------ literal cost estimation (literal_cost.c)

    private static int Utf8Position(int last, int c, int clamp)
    {
        if (c < 128) return 0;
        if (c >= 192) return Math.Min(1, clamp);
        return last < 0xE0 ? 0 : Math.Min(2, clamp);
    }

    private static int DecideMultiByteStatsLevel(byte[] data, int pos, int len)
    {
        int c0 = 0, c1 = 0, c2 = 0;
        int lastC = 0;
        for (int i = 0; i < len; ++i)
        {
            int c = data[pos + i];
            int p = Utf8Position(lastC, c, 2);
            if (p == 0) c0++; else if (p == 1) c1++; else c2++;
            lastC = c;
        }
        int maxUtf8 = 1;
        if (c2 < 500) maxUtf8 = 1;
        if (c1 + c2 < 25) maxUtf8 = 0;
        return maxUtf8;
    }

    /// <summary>Per-byte literal cost estimate into cost[1..len] (the reference writes to cost[0..len) of &amp;literal_costs[1]).</summary>
    private void EstimateBitCostsForLiterals(byte[] data, int pos, int len, float[] cost)
    {
        int[] histogram = _literalHistograms;
        if (IsMostlyUtf8(data, pos, len))
        {
            int maxUtf8 = DecideMultiByteStatsLevel(data, pos, len);
            const int windowHalf = 495;
            int inWindow = Math.Min(windowHalf, len);
            int iw0 = 0, iw1 = 0, iw2 = 0;
            Array.Clear(histogram, 0, histogram.Length);
            {
                int lastC = 0;
                int utf8Pos = 0;
                for (int i = 0; i < inWindow; ++i)
                {
                    int c = data[pos + i];
                    ++histogram[256 * utf8Pos + c];
                    if (utf8Pos == 0) iw0++; else if (utf8Pos == 1) iw1++; else iw2++;
                    utf8Pos = Utf8Position(lastC, c, maxUtf8);
                    lastC = c;
                }
            }
            for (int i = 0; i < len; ++i)
            {
                if (i >= windowHalf)
                {
                    int c = i < windowHalf + 1 ? 0 : data[pos + i - windowHalf - 1];
                    int lastC = i < windowHalf + 2 ? 0 : data[pos + i - windowHalf - 2];
                    int utf8Pos2 = Utf8Position(lastC, c, maxUtf8);
                    --histogram[256 * utf8Pos2 + data[pos + i - windowHalf]];
                    if (utf8Pos2 == 0) iw0--; else if (utf8Pos2 == 1) iw1--; else iw2--;
                }
                if (i + windowHalf < len)
                {
                    int c = data[pos + i + windowHalf - 1];
                    int lastC = data[pos + i + windowHalf - 2];
                    int utf8Pos2 = Utf8Position(lastC, c, maxUtf8);
                    ++histogram[256 * utf8Pos2 + data[pos + i + windowHalf]];
                    if (utf8Pos2 == 0) iw0++; else if (utf8Pos2 == 1) iw1++; else iw2++;
                }
                {
                    int c = i < 1 ? 0 : data[pos + i - 1];
                    int lastC = i < 2 ? 0 : data[pos + i - 2];
                    int utf8Pos = Utf8Position(lastC, c, maxUtf8);
                    int histo = histogram[256 * utf8Pos + data[pos + i]];
                    if (histo == 0) histo = 1;
                    int inWindowUtf8 = utf8Pos == 0 ? iw0 : utf8Pos == 1 ? iw1 : iw2;
                    double litCost = Log2(inWindowUtf8) - Log2(histo);
                    litCost += 0.02905;
                    if (litCost < 1.0)
                    {
                        litCost *= 0.5;
                        litCost += 0.5;
                    }
                    if (i < 2000) litCost += 0.35 + 0.35 / 2000 * i;
                    cost[i + 1] = (float)litCost;
                }
            }
        }
        else
        {
            const int windowHalf = 2000;
            int inWindow = Math.Min(windowHalf, len);
            Array.Clear(histogram, 0, 256);
            for (int i = 0; i < inWindow; ++i) ++histogram[data[pos + i]];
            for (int i = 0; i < len; ++i)
            {
                if (i >= windowHalf)
                {
                    --histogram[data[pos + i - windowHalf]];
                    --inWindow;
                }
                if (i + windowHalf < len)
                {
                    ++histogram[data[pos + i + windowHalf]];
                    ++inWindow;
                }
                int histo = histogram[data[pos + i]];
                if (histo == 0) histo = 1;
                double litCost = Log2(inWindow) - Log2(histo);
                litCost += 0.029;
                if (litCost < 1.0)
                {
                    litCost *= 0.5;
                    litCost += 0.5;
                }
                cost[i + 1] = (float)litCost;
            }
        }
    }

    /// <summary>True when at least 75 percent of the bytes are well-formed UTF-8 (the reference's kMinUTF8Ratio).</summary>
    public static bool IsMostlyUtf8(byte[] data, int pos, int length)
    {
        long sizeUtf8 = 0;
        int i = 0;
        while (i < length)
        {
            int bytesRead = ParseAsUtf8(data, pos + i, length - i, out int symbol);
            i += bytesRead;
            if (symbol < 0x110000) sizeUtf8 += bytesRead;
        }
        return sizeUtf8 > 0.75 * length;
    }

    private static int ParseAsUtf8(byte[] input, int p, int size, out int symbol)
    {
        int b0 = input[p];
        if ((b0 & 0x80) == 0)
        {
            symbol = b0;
            if (symbol > 0) return 1;
        }
        if (size > 1 && (b0 & 0xE0) == 0xC0 && (input[p + 1] & 0xC0) == 0x80)
        {
            symbol = ((b0 & 0x1F) << 6) | (input[p + 1] & 0x3F);
            if (symbol > 0x7F) return 2;
        }
        if (size > 2 && (b0 & 0xF0) == 0xE0 && (input[p + 1] & 0xC0) == 0x80 && (input[p + 2] & 0xC0) == 0x80)
        {
            symbol = ((b0 & 0x0F) << 12) | ((input[p + 1] & 0x3F) << 6) | (input[p + 2] & 0x3F);
            if (symbol > 0x7FF) return 3;
        }
        if (size > 3 && (b0 & 0xF8) == 0xF0 && (input[p + 1] & 0xC0) == 0x80 && (input[p + 2] & 0xC0) == 0x80 && (input[p + 3] & 0xC0) == 0x80)
        {
            symbol = ((b0 & 0x07) << 18) | ((input[p + 1] & 0x3F) << 12) | ((input[p + 2] & 0x3F) << 6) | (input[p + 3] & 0x3F);
            if (symbol > 0xFFFF && symbol <= 0x10FFFF) return 4;
        }
        symbol = 0x110000 | b0;
        return 1;
    }

    // ------------------------------------------------------------------ shortest path

    private void UpdateNode(int pos, int startPos, int len, int lenCode, int dist, int shortCode, float cost)
    {
        ref Node next = ref _nodes[pos + len];
        next.Length = (uint)(len | ((len + 9 - lenCode) << 25));
        next.Distance = (uint)dist;
        next.DcodeInsertLength = (uint)((shortCode << 27) | (pos - startPos));
        next.Cost = cost;
    }

    private int ComputeMinimumCopyLength(float startCost, int numBytes, int pos)
    {
        float minCost = startCost;
        int len = 2;
        int nextLenBucket = 4;
        int nextLenOffset = 10;
        ref Node nodes = ref MemoryMarshal.GetReference(_nodes.AsSpan());
        while (pos + len <= numBytes && Unsafe.Add(ref nodes, pos + len).Cost <= minCost)
        {
            ++len;
            if (len == nextLenOffset)
            {
                minCost += 1.0f;
                nextLenOffset += nextLenBucket;
                nextLenBucket *= 2;
            }
        }
        return len;
    }

    private uint ComputeDistanceShortcut(long blockStart, int pos, long maxBackwardLimit)
    {
        ref Node n = ref _nodes[pos];
        uint cLen = n.CopyLength;
        uint iLen = n.InsertLength;
        uint dist = n.Distance;
        if (pos == 0) return 0;
        if (dist + cLen <= blockStart + pos + _prefixLength && dist <= maxBackwardLimit + _prefixLength && n.DistanceCode > 0) return (uint)pos;
        return _nodes[pos - cLen - iLen].U;   // shortcut of the previous node
    }

    private void ComputeDistanceCache(int pos, int c0, int c1, int c2, int c3, ref PosData pd)
    {
        int idx = 0;
        ref Node nodes = ref MemoryMarshal.GetReference(_nodes.AsSpan());
        uint p = Unsafe.Add(ref nodes, pos).U;
        ref int cache = ref pd.Cache0;
        while (idx < 4 && p > 0)
        {
            ref Node n = ref Unsafe.Add(ref nodes, (int)p);
            Unsafe.Add(ref cache, idx++) = (int)n.Distance;
            p = Unsafe.Add(ref nodes, (int)(p - n.CopyLength - n.InsertLength)).U;
        }
        if (idx < 4) { Unsafe.Add(ref cache, idx++) = c0; }
        if (idx < 4) { Unsafe.Add(ref cache, idx++) = c1; }
        if (idx < 4) { Unsafe.Add(ref cache, idx++) = c2; }
        if (idx < 4) { Unsafe.Add(ref cache, idx) = c3; }
    }

    private void EvaluateNode(long blockStart, int pos, long maxBackwardLimit, int c0, int c1, int c2, int c3, ref StartPosQueue queue)
    {
        ref Node node = ref _nodes[pos];
        float nodeCost = node.Cost;
        node.U = ComputeDistanceShortcut(blockStart, pos, maxBackwardLimit);
        if (nodeCost <= LiteralCosts(0, pos))
        {
            PosData pd = default;
            pd.Pos = pos;
            pd.Cost = nodeCost;
            pd.CostDiff = nodeCost - LiteralCosts(0, pos);
            ComputeDistanceCache(pos, c0, c1, c2, c3, ref pd);
            queue.Push(pd);
        }
    }

    /// <summary>Returns the longest copy length found at this position.</summary>
    private int UpdateNodes(byte[] buf, long bufBase, int numBytes, long blockStart, int pos, long maxBackwardLimit, long historyStart,
        int c0, int c1, int c2, int c3, int numMatches, int matchPos, ref StartPosQueue queue)
    {
        long curPos = blockStart + pos;
        int curIdx = (int)(curPos - bufBase);
        long maxDistance = Math.Min(curPos - historyStart, maxBackwardLimit);
        long dictionaryStart = Math.Min(curPos, maxBackwardLimit) + _prefixLength;
        int maxLen = numBytes - pos;
        int result = 0;
        ref byte bufRef = ref MemoryMarshal.GetReference(buf.AsSpan());
        ref byte cur = ref Unsafe.Add(ref bufRef, curIdx);
        float[] costDist = _costDist;
        EvaluateNode(blockStart, pos, maxBackwardLimit, c0, c1, c2, c3, ref queue);
        int minLen;
        {
            ref PosData posdata = ref queue.Get(0);
            float minCost = posdata.Cost + _minCostCmd + LiteralCosts(posdata.Pos, pos);
            minLen = ComputeMinimumCopyLength(minCost, numBytes, pos);
        }
        int queueSize = queue.Size;
        for (int k = 0; k < _maxCandidates && k < queueSize; ++k)
        {
            ref PosData posdata = ref queue.Get(k);
            int start = posdata.Pos;
            int inscode = InsertCode(pos - start);
            float startCostDiff = posdata.CostDiff;
            float baseCost = startCostDiff + InsertExtraTable[inscode] + LiteralCosts(0, pos);
            ref ushort combine = ref MemoryMarshal.GetReference(CombineTable.AsSpan());
            ref ushort combineLast = ref MemoryMarshal.GetReference(CombineTableLast.AsSpan());
            ref byte copyExtra = ref MemoryMarshal.GetReference(CopyExtraTable.AsSpan());
            ref byte copyCode = ref MemoryMarshal.GetReference(CopyCodeTable.AsSpan());
            ref float costCmd = ref MemoryMarshal.GetReference(_costCmd.AsSpan());
            ref Node nodeAtPos = ref Unsafe.Add(ref MemoryMarshal.GetReference(_nodes.AsSpan()), pos);
            int bestLen = minLen - 1;
            int j = 0;
            for (; j < Constants.NumDistanceShortCodes && bestLen < maxLen; ++j)
            {
                long backward = (long)posdata.Cache(DistanceCacheIndex[j]) + DistanceCacheOffset[j];
                if (backward <= 0 || backward > maxDistance) continue;
                long prevPos = curPos - backward;
                int prevIdx = (int)(prevPos - bufBase);
                if (Unsafe.Add(ref cur, bestLen) != Unsafe.Add(ref bufRef, prevIdx + bestLen)) continue;
                int len = BinaryTreeHasher.MatchLength(buf, prevIdx, curIdx, maxLen);
                if (prevPos < 0) len = Math.Min(len, (int)-prevPos);   // prefix-dictionary bytes end at position 0
                float distCost = baseCost + costDist[j];
                ref ushort comb = ref Unsafe.Add(ref (j == 0 ? ref combineLast : ref combine), inscode * 24);
                for (int l = bestLen + 1; l <= len; ++l)
                {
                    int copycode = l < 2118 ? Unsafe.Add(ref copyCode, l) : 23;
                    int cmdcode = Unsafe.Add(ref comb, copycode);
                    float cost = (cmdcode < 128 ? baseCost : distCost) + Unsafe.Add(ref copyExtra, copycode) + Unsafe.Add(ref costCmd, cmdcode);
                    ref Node target = ref Unsafe.Add(ref nodeAtPos, l);
                    if (cost < target.Cost)
                    {
                        target.Length = (uint)(l | (9 << 25));
                        target.Distance = (uint)backward;
                        target.DcodeInsertLength = (uint)(((j + 1) << 27) | (pos - start));
                        target.Cost = cost;
                        if (l > result) result = l;
                    }
                }
                if (len > bestLen) bestLen = len;
            }
            if (k >= 2) continue;
            {
                int len = minLen;
                for (j = 0; j < numMatches; ++j)
                {
                    BackwardMatch match = _matches[matchPos + j];
                    int dist = match.Distance;
                    bool isDictionaryMatch = dist > dictionaryStart;
                    int distCode = dist + Constants.NumDistanceShortCodes - 1;
                    int distSymbol = DistanceSymbol(distCode);
                    int distNumExtra = distSymbol >> 10;
                    float distCost = baseCost + distNumExtra + _costDist[distSymbol & 0x3FF];
                    int maxMatchLen = match.Length;
                    if (len < maxMatchLen && (isDictionaryMatch || maxMatchLen > _maxZopfliLen)) len = maxMatchLen;
                    ref ushort comb2 = ref Unsafe.Add(ref combine, inscode * 24);
                    for (; len <= maxMatchLen; ++len)
                    {
                        int lenCode = isDictionaryMatch ? match.Code : len;
                        int copycode = lenCode < 2118 ? Unsafe.Add(ref copyCode, lenCode) : 23;
                        int cmdcode = Unsafe.Add(ref comb2, copycode);
                        float cost = distCost + Unsafe.Add(ref copyExtra, copycode) + Unsafe.Add(ref costCmd, cmdcode);
                        ref Node target = ref Unsafe.Add(ref nodeAtPos, len);
                        if (cost < target.Cost)
                        {
                            target.Length = (uint)(len | ((len + 9 - lenCode) << 25));
                            target.Distance = (uint)dist;
                            target.DcodeInsertLength = (uint)(pos - start);
                            target.Cost = cost;
                            if (len > result) result = len;
                        }
                    }
                }
            }
        }
        return result;
    }

    private int ComputeShortestPathFromNodes(int numBytes)
    {
        int index = numBytes;
        int numCommands = 0;
        while (_nodes[index].InsertLength == 0 && _nodes[index].Length == 1) --index;
        _nodes[index].U = uint.MaxValue;
        while (index != 0)
        {
            int len = (int)_nodes[index].CommandLength;
            index -= len;
            _nodes[index].U = (uint)len;
            numCommands++;
        }
        return numCommands;
    }

    private int ZopfliIterate(byte[] buf, long bufBase, int numBytes, long position, long maxBackwardLimit, long historyStart, int c0, int c1, int c2, int c3)
    {
        StartPosQueue queue = _queue;
        queue.Reset();
        int curMatchPos = 0;
        _nodes[0].Length = 0;
        _nodes[0].Cost = 0;
        for (int i = 0; i + 3 < numBytes; i++)
        {
            int skip = UpdateNodes(buf, bufBase, numBytes, position, i, maxBackwardLimit, historyStart, c0, c1, c2, c3, _numMatches[i], curMatchPos, ref queue);
            if (skip < LongCopyQuickStep) skip = 0;
            curMatchPos += _numMatches[i];
            if (_numMatches[i] == 1 && _matches[curMatchPos - 1].Length > _maxZopfliLen)
            {
                skip = Math.Max(_matches[curMatchPos - 1].Length, skip);
            }
            if (skip > 1)
            {
                skip--;
                while (skip != 0)
                {
                    i++;
                    if (i + 3 >= numBytes) break;
                    EvaluateNode(position, i, maxBackwardLimit, c0, c1, c2, c3, ref queue);
                    curMatchPos += _numMatches[i];
                    skip--;
                }
            }
        }
        return ComputeShortestPathFromNodes(numBytes);
    }

    /// <summary>Appends static-dictionary matches (distance = dictionaryDistance + word index + 1) and returns how many.</summary>
    private int FindDictionaryMatches(byte[] buf, int idx, int minLength, int maxLength, long dictionaryDistance, int matchPos)
    {
        int maxlen = Math.Min(StaticDictionaryMatcher.MaxMatchLength, maxLength);
        // No dictionary word is longer than 37 bytes after transforms, so a longer match already found wins.
        if (minLength > maxlen) return 0;
        uint[] dm = _dictMatches;
        // Only the lengths that are read need clearing; writes outside the range land on stale entries.
        for (int i = minLength; i <= maxlen; i++) dm[i] = StaticDictionaryMatcher.InvalidMatch;
        if (!StaticDictionaryMatcher.FindAllMatches(buf.AsSpan(idx), minLength, maxLength, dm)) return 0;
        int count = 0;
        for (int l = minLength; l <= maxlen; ++l)
        {
            uint dictId = dm[l];
            if (dictId < StaticDictionaryMatcher.InvalidMatch)
            {
                long distance = dictionaryDistance + (dictId >> 5) + 1;
                if (distance <= _maxDistanceLimit)
                {
                    int lenCode = (int)(dictId & 31);
                    _matches[matchPos + count] = new BackwardMatch { Distance = (int)distance, Length = l, LengthCode = lenCode == l ? 0 : lenCode };
                    count++;
                }
            }
        }
        return count;
    }

    /// <summary>Walks the shortest path into <see cref="_cmds"/> and updates the distance cache; returns the trailing insert length.</summary>
    private int CreateCommands(int numBytes, long blockStart, int lastInsertLen, int[] distCache, long maxBackwardLimit)
    {
        int pos = 0;
        uint offset = _nodes[0].U;
        _numCmds = 0;
        for (int i = 0; offset != uint.MaxValue; i++)
        {
            ref Node next = ref _nodes[pos + (int)offset];
            int copyLength = (int)next.CopyLength;
            int insertLength = (int)next.InsertLength;
            pos += insertLength;
            offset = next.U;
            if (i == 0)
            {
                insertLength += lastInsertLen;
                lastInsertLen = 0;
            }
            int distance = (int)next.Distance;
            int distCode = (int)next.DistanceCode;
            int lenCode = (int)next.LengthCode;
            long dictionaryStart = Math.Min(blockStart + pos, maxBackwardLimit) + _prefixLength;
            bool isDictionary = distance > dictionaryStart;
            if (_numCmds == _cmds.Length) Array.Resize(ref _cmds, _cmds.Length * 2);
            int distSymbol = DistanceSymbol(distCode);
            int cmdCode = CombineLengthCodes(InsertCode(insertLength), CopyCode(lenCode), (distSymbol & 0x3FF) == 0);
            _cmds[_numCmds++] = new Cmd { Insert = insertLength, Copy = copyLength, LenCode = lenCode, DistCode = distCode, Distance = distance, CmdCode = cmdCode, DistSymbol = distSymbol, IsDictionary = isDictionary };
            if (!isDictionary && distCode > 0)
            {
                distCache[3] = distCache[2];
                distCache[2] = distCache[1];
                distCache[1] = distCache[0];
                distCache[0] = distance;
            }
            pos += copyLength;
        }
        return lastInsertLen + numBytes - pos;
    }

    /// <summary>
    /// Parses [position, position + numBytes) with two shortest-path passes and reports the commands to the
    /// sink. <paramref name="lastInsertLen"/> literals before the chunk are still pending and become part of the
    /// first command; the pending count after the chunk is returned. The distance cache is updated in place.
    /// </summary>
    public int Parse(byte[] buf, long bufBase, long position, int numBytes, long maxBackwardLimit, long historyStart,
        int prefixLength, bool useDictionary, int[] distCache, int lastInsertLen, IZopfliSink sink)
    {
        _numBytes = numBytes;
        _prefixLength = prefixLength;
        _useDictionary = useDictionary;
        if (_nodes.Length < numBytes + 1) _nodes = new Node[Math.Max(numBytes + 1, _nodes.Length * 2)];
        if (_numMatches.Length < numBytes) _numMatches = new int[Math.Max(numBytes, _numMatches.Length * 2)];
        if (_literalCosts.Length < numBytes + 2) _literalCosts = new float[Math.Max(numBytes + 2, _literalCosts.Length * 2)];
        if (_costDist.Length < _distanceAlphabetLimit) _costDist = new float[_distanceAlphabetLimit];
        _distanceHistogramSize = _distanceAlphabetLimit;

        long storeEnd = numBytes >= BinaryTreeHasher.MaxTreeCompLength ? position + numBytes - BinaryTreeHasher.MaxTreeCompLength + 1 : position;
        int curMatchPos = 0;
        for (int i = 0; i + 3 < numBytes; ++i)
        {
            long pos = position + i;
            long maxDistance = Math.Min(pos - historyStart, maxBackwardLimit);
            int maxLength = numBytes - i;
            if (curMatchPos + MaxNumMatches > _matches.Length) Array.Resize(ref _matches, Math.Max(_matches.Length * 2, curMatchPos + MaxNumMatches));
            int found = _hasher.FindAllMatches(buf, bufBase, pos, maxLength, maxDistance, _matches, curMatchPos);
            if (_useDictionary)
            {
                int bestLen = found > 0 ? _matches[curMatchPos + found - 1].Length : 1;
                found += FindDictionaryMatches(buf, (int)(pos - bufBase), Math.Max(4, bestLen + 1), maxLength, Math.Min(pos, maxBackwardLimit) + _prefixLength, curMatchPos + found);
            }
            int curMatchEnd = curMatchPos + found;
            _numMatches[i] = found;
            if (found > 0)
            {
                int matchLen = _matches[curMatchEnd - 1].Length;
                if (matchLen > _maxZopfliLen)
                {
                    int skip = matchLen - 1;
                    _matches[curMatchPos++] = _matches[curMatchEnd - 1];
                    _numMatches[i] = 1;
                    _hasher.StoreRange(buf, bufBase, pos + 1, Math.Min(pos + matchLen, storeEnd), maxBackwardLimit);
                    Array.Clear(_numMatches, i + 1, Math.Min(skip, numBytes - i - 1));
                    i += skip;
                }
                else
                {
                    curMatchPos = curMatchEnd;
                }
            }
        }

        Span<int> origCache = stackalloc int[4];
        distCache.AsSpan(0, 4).CopyTo(origCache);
        int origLastInsertLen = lastInsertLen;
        int startIdx = (int)(position - bufBase);
        int resultInsertLen = lastInsertLen;
        for (int pass = 0; pass < _passes; pass++)
        {
            {
                float inf = Infinity;
                var stub = new Node { Length = 1, Distance = 0, DcodeInsertLength = 0, U = Unsafe.As<float, uint>(ref inf) };
                for (int n = 0; n <= numBytes; n++) _nodes[n] = stub;
            }
            if (pass == 0) SetCostFromLiteralCosts(buf, startIdx);
            else SetCostFromCommands(buf, bufBase, position, origLastInsertLen);
            origCache.CopyTo(distCache);
            ZopfliIterate(buf, bufBase, numBytes, position, maxBackwardLimit, historyStart, origCache[0], origCache[1], origCache[2], origCache[3]);
            resultInsertLen = CreateCommands(numBytes, position, origLastInsertLen, distCache, maxBackwardLimit);
        }
        // Report the final pass's commands.
        long litPos = position - origLastInsertLen;
        for (int i = 0; i < _numCmds; i++)
        {
            ref Cmd c = ref _cmds[i];
            sink.AddZopfliCommand((int)(litPos - bufBase), c.Insert, c.Copy, c.LenCode, c.DistCode, c.Distance, c.IsDictionary);
            litPos += c.Insert + c.Copy;
        }
        return resultInsertLen;
    }
}
