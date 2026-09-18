using System;
using BrotliManagedFast.Internal;

namespace BrotliManagedFast.Internal;

/// <summary>Length-limited Huffman code construction and serialisation of prefix codes into the bit stream.</summary>
internal static class HuffmanEncoder
{
    internal struct TreeNode
    {
        public uint TotalCount;
        public short IndexLeft;
        public short IndexRightOrValue;
    }

    /// <summary>Per-encoder scratch buffers so building a code allocates nothing.</summary>
    internal sealed class Scratch
    {
        public const int MaxAlphabet = 1200;
        public readonly TreeNode[] Tree = new TreeNode[2 * MaxAlphabet + 1];
        public readonly byte[] RleTree = new byte[MaxAlphabet];
        public readonly byte[] RleExtra = new byte[MaxAlphabet];
        public readonly byte[] CodeLengthBitdepth = new byte[Constants.CodeLengthCodes];
        public readonly ushort[] CodeLengthSymbols = new ushort[Constants.CodeLengthCodes];
    }

    [ThreadStatic]
    private static Scratch? t_scratch;

    private static Scratch GetScratch() => t_scratch ??= new Scratch();

    private static bool SetDepth(int p0, TreeNode[] pool, byte[] depth, int maxDepth)
    {
        Span<int> stack = stackalloc int[16];
        int level = 0;
        int p = p0;
        stack[0] = -1;
        while (true)
        {
            if (pool[p].IndexLeft >= 0)
            {
                level++;
                if (level > maxDepth) return false;
                stack[level] = pool[p].IndexRightOrValue;
                p = pool[p].IndexLeft;
                continue;
            }
            depth[pool[p].IndexRightOrValue] = (byte)level;
            while (level >= 0 && stack[level] == -1) level--;
            if (level < 0) return true;
            p = stack[level];
            stack[level] = -1;
        }
    }

    private static int Compare(in TreeNode a, in TreeNode b)
    {
        if (a.TotalCount != b.TotalCount) return a.TotalCount < b.TotalCount ? -1 : 1;
        return a.IndexRightOrValue > b.IndexRightOrValue ? -1 : a.IndexRightOrValue < b.IndexRightOrValue ? 1 : 0;
    }

    private static void SortNodes(TreeNode[] items, int n)
    {
        // Insertion sort with shell gaps (n is at most ~1200).
        ReadOnlySpan<int> gaps = stackalloc int[] { 132, 57, 23, 10, 4, 1 };
        foreach (int gap in gaps)
        {
            if (gap >= n) continue;
            for (int i = gap; i < n; i++)
            {
                TreeNode tmp = items[i];
                int j = i;
                while (j >= gap && Compare(items[j - gap], tmp) > 0)
                {
                    items[j] = items[j - gap];
                    j -= gap;
                }
                items[j] = tmp;
            }
        }
    }

    /// <summary>
    /// Builds code lengths (depths) limited to <paramref name="treeLimit"/> bits from <paramref name="histogram"/>.
    /// Symbols with zero count get depth 0. A single used symbol gets depth 1 (caller handles the single-symbol case).
    /// </summary>
    public static void CreateHuffmanTree(ReadOnlySpan<uint> histogram, int length, int treeLimit, byte[] depth)
    {
        Array.Clear(depth, 0, length);
        TreeNode[] tree = length <= Scratch.MaxAlphabet ? GetScratch().Tree : new TreeNode[2 * length + 1];
        var sentinel = new TreeNode { TotalCount = uint.MaxValue, IndexLeft = -1, IndexRightOrValue = -1 };
        for (uint countLimit = 1; ; countLimit *= 2)
        {
            int n = 0;
            for (int i = length; i != 0;)
            {
                --i;
                if (histogram[i] != 0)
                {
                    uint count = Math.Max(histogram[i], countLimit);
                    tree[n++] = new TreeNode { TotalCount = count, IndexLeft = -1, IndexRightOrValue = (short)i };
                }
            }
            if (n == 0) return;
            if (n == 1)
            {
                depth[tree[0].IndexRightOrValue] = 1;
                return;
            }
            SortNodes(tree, n);
            tree[n] = sentinel;
            tree[n + 1] = sentinel;
            int ii = 0;
            int jj = n + 1;
            for (int k = n - 1; k != 0; --k)
            {
                int left, right;
                if (tree[ii].TotalCount <= tree[jj].TotalCount) { left = ii; ++ii; } else { left = jj; ++jj; }
                if (tree[ii].TotalCount <= tree[jj].TotalCount) { right = ii; ++ii; } else { right = jj; ++jj; }
                int jEnd = 2 * n - k;
                tree[jEnd].TotalCount = tree[left].TotalCount + tree[right].TotalCount;
                tree[jEnd].IndexLeft = (short)left;
                tree[jEnd].IndexRightOrValue = (short)right;
                tree[jEnd + 1] = sentinel;
            }
            if (SetDepth(2 * n - 1, tree, depth, treeLimit)) return;
        }
    }

    private static ushort ReverseBits(int numBits, ushort bits)
    {
        ReadOnlySpan<byte> lut = stackalloc byte[] { 0x00, 0x08, 0x04, 0x0C, 0x02, 0x0A, 0x06, 0x0E, 0x01, 0x09, 0x05, 0x0D, 0x03, 0x0B, 0x07, 0x0F };
        int retval = lut[bits & 0x0F];
        for (int i = 4; i < numBits; i += 4)
        {
            retval <<= 4;
            bits = (ushort)(bits >> 4);
            retval |= lut[bits & 0x0F];
        }
        retval >>= (0 - numBits) & 0x03;
        return (ushort)retval;
    }

    /// <summary>Assigns canonical codes (bit-reversed for LSB-first writing) from depths.</summary>
    public static void ConvertBitDepthsToSymbols(ReadOnlySpan<byte> depth, int len, ushort[] bits)
    {
        Span<int> blCount = stackalloc int[16];
        Span<int> nextCode = stackalloc int[16];
        for (int i = 0; i < len; ++i) ++blCount[depth[i]];
        blCount[0] = 0;
        nextCode[0] = 0;
        int code = 0;
        for (int i = 1; i < 16; ++i)
        {
            code = (code + blCount[i - 1]) << 1;
            nextCode[i] = code;
        }
        for (int i = 0; i < len; ++i)
        {
            if (depth[i] != 0) bits[i] = ReverseBits(depth[i], (ushort)nextCode[depth[i]]++);
        }
    }

    // ---------------------------------------------------------------- code-length RLE (BrotliWriteHuffmanTree)

    private static void Reverse(byte[] v, int start, int end)
    {
        --end;
        while (start < end)
        {
            (v[start], v[end]) = (v[end], v[start]);
            ++start;
            --end;
        }
    }

    private static void WriteHuffmanTreeRepetitions(byte previousValue, byte value, int repetitions, ref int treeSize, byte[] tree, byte[] extraBitsData)
    {
        if (previousValue != value)
        {
            tree[treeSize] = value;
            extraBitsData[treeSize] = 0;
            ++treeSize;
            --repetitions;
        }
        if (repetitions == 7)
        {
            tree[treeSize] = value;
            extraBitsData[treeSize] = 0;
            ++treeSize;
            --repetitions;
        }
        if (repetitions < 3)
        {
            for (int i = 0; i < repetitions; ++i)
            {
                tree[treeSize] = value;
                extraBitsData[treeSize] = 0;
                ++treeSize;
            }
        }
        else
        {
            int start = treeSize;
            repetitions -= 3;
            while (true)
            {
                tree[treeSize] = Constants.RepeatPreviousCodeLength;
                extraBitsData[treeSize] = (byte)(repetitions & 0x3);
                ++treeSize;
                repetitions >>= 2;
                if (repetitions == 0) break;
                --repetitions;
            }
            Reverse(tree, start, treeSize);
            Reverse(extraBitsData, start, treeSize);
        }
    }

    private static void WriteHuffmanTreeRepetitionsZeros(int repetitions, ref int treeSize, byte[] tree, byte[] extraBitsData)
    {
        if (repetitions == 11)
        {
            tree[treeSize] = 0;
            extraBitsData[treeSize] = 0;
            ++treeSize;
            --repetitions;
        }
        if (repetitions < 3)
        {
            for (int i = 0; i < repetitions; ++i)
            {
                tree[treeSize] = 0;
                extraBitsData[treeSize] = 0;
                ++treeSize;
            }
        }
        else
        {
            int start = treeSize;
            repetitions -= 3;
            while (true)
            {
                tree[treeSize] = Constants.RepeatZeroCodeLength;
                extraBitsData[treeSize] = (byte)(repetitions & 0x7);
                ++treeSize;
                repetitions >>= 3;
                if (repetitions == 0) break;
                --repetitions;
            }
            Reverse(tree, start, treeSize);
            Reverse(extraBitsData, start, treeSize);
        }
    }

    private static void DecideOverRleUse(ReadOnlySpan<byte> depth, int length, out bool useRleForNonZero, out bool useRleForZero)
    {
        int totalRepsZero = 0, totalRepsNonZero = 0, countRepsZero = 1, countRepsNonZero = 1;
        for (int i = 0; i < length;)
        {
            byte value = depth[i];
            int reps = 1;
            for (int k = i + 1; k < length && depth[k] == value; ++k) ++reps;
            if (reps >= 3 && value == 0) { totalRepsZero += reps; ++countRepsZero; }
            if (reps >= 4 && value != 0) { totalRepsNonZero += reps; ++countRepsNonZero; }
            i += reps;
        }
        useRleForNonZero = totalRepsNonZero > countRepsNonZero * 2;
        useRleForZero = totalRepsZero > countRepsZero * 2;
    }

    /// <summary>Converts depths into the RLE'd code-length symbol sequence (values 0..17 plus extra bits).</summary>
    public static void WriteHuffmanTree(ReadOnlySpan<byte> depth, int length, out int treeSize, byte[] tree, byte[] extraBitsData)
    {
        byte previousValue = Constants.InitialRepeatedCodeLength;
        treeSize = 0;
        int newLength = length;
        for (int i = 0; i < length; ++i)
        {
            if (depth[length - i - 1] == 0) --newLength; else break;
        }
        bool useRleForNonZero = false, useRleForZero = false;
        if (length > 50) DecideOverRleUse(depth, newLength, out useRleForNonZero, out useRleForZero);
        for (int i = 0; i < newLength;)
        {
            byte value = depth[i];
            int reps = 1;
            if ((value != 0 && useRleForNonZero) || (value == 0 && useRleForZero))
            {
                for (int k = i + 1; k < newLength && depth[k] == value; ++k) ++reps;
            }
            if (value == 0)
            {
                WriteHuffmanTreeRepetitionsZeros(reps, ref treeSize, tree, extraBitsData);
            }
            else
            {
                WriteHuffmanTreeRepetitions(previousValue, value, reps, ref treeSize, tree, extraBitsData);
                previousValue = value;
            }
            i += reps;
        }
    }

    // ---------------------------------------------------------------- storing codes

    private static void StoreHuffmanTreeOfHuffmanTreeToBitMask(int numCodes, ReadOnlySpan<byte> codeLengthBitdepth, BitWriter w)
    {
        ReadOnlySpan<byte> storageOrder = Constants.CodeLengthCodeOrder;
        ReadOnlySpan<byte> symbols = Constants.CodeLengthCodeSymbols;
        ReadOnlySpan<byte> lengths = Constants.CodeLengthCodeBitLengths;
        int skipSome = 0;
        int codesToStore = Constants.CodeLengthCodes;
        if (numCodes > 1)
        {
            for (; codesToStore > 0; --codesToStore)
            {
                if (codeLengthBitdepth[storageOrder[codesToStore - 1]] != 0) break;
            }
        }
        if (codeLengthBitdepth[storageOrder[0]] == 0 && codeLengthBitdepth[storageOrder[1]] == 0)
        {
            skipSome = 2;
            if (codeLengthBitdepth[storageOrder[2]] == 0) skipSome = 3;
        }
        w.WriteBits(2, (ulong)skipSome);
        for (int i = skipSome; i < codesToStore; ++i)
        {
            int l = codeLengthBitdepth[storageOrder[i]];
            w.WriteBits(lengths[l], symbols[l]);
        }
    }

    private static void StoreHuffmanTreeToBitMask(int huffmanTreeSize, byte[] huffmanTree, byte[] huffmanTreeExtraBits,
        ReadOnlySpan<byte> codeLengthBitdepth, ReadOnlySpan<ushort> codeLengthBitdepthSymbols, BitWriter w)
    {
        for (int i = 0; i < huffmanTreeSize; ++i)
        {
            int ix = huffmanTree[i];
            w.WriteBits(codeLengthBitdepth[ix], codeLengthBitdepthSymbols[ix]);
            switch (ix)
            {
                case Constants.RepeatPreviousCodeLength:
                    w.WriteBits(2, huffmanTreeExtraBits[i]);
                    break;
                case Constants.RepeatZeroCodeLength:
                    w.WriteBits(3, huffmanTreeExtraBits[i]);
                    break;
            }
        }
    }

    /// <summary>Stores a complex prefix code given symbol depths for an alphabet of <paramref name="num"/> symbols.</summary>
    public static void StoreHuffmanTree(ReadOnlySpan<byte> depths, int num, BitWriter w)
    {
        Scratch scratch = GetScratch();
        byte[] huffmanTree = num <= Scratch.MaxAlphabet ? scratch.RleTree : new byte[num];
        byte[] huffmanTreeExtraBits = num <= Scratch.MaxAlphabet ? scratch.RleExtra : new byte[num];
        WriteHuffmanTree(depths, num, out int huffmanTreeSize, huffmanTree, huffmanTreeExtraBits);

        Span<uint> histogram = stackalloc uint[Constants.CodeLengthCodes];
        for (int i = 0; i < huffmanTreeSize; ++i) ++histogram[huffmanTree[i]];
        int numCodes = 0;
        int code = 0;
        for (int i = 0; i < Constants.CodeLengthCodes; ++i)
        {
            if (histogram[i] != 0)
            {
                if (numCodes == 0) { code = i; numCodes = 1; }
                else if (numCodes == 1) { numCodes = 2; break; }
            }
        }
        byte[] codeLengthBitdepth = scratch.CodeLengthBitdepth;
        ushort[] codeLengthBitdepthSymbols = scratch.CodeLengthSymbols;
        Array.Clear(codeLengthBitdepth, 0, codeLengthBitdepth.Length);
        CreateHuffmanTree(histogram, Constants.CodeLengthCodes, 5, codeLengthBitdepth);
        ConvertBitDepthsToSymbols(codeLengthBitdepth, Constants.CodeLengthCodes, codeLengthBitdepthSymbols);
        StoreHuffmanTreeOfHuffmanTreeToBitMask(numCodes, codeLengthBitdepth, w);
        if (numCodes == 1) codeLengthBitdepth[code] = 0;
        StoreHuffmanTreeToBitMask(huffmanTreeSize, huffmanTree, huffmanTreeExtraBits, codeLengthBitdepth, codeLengthBitdepthSymbols, w);
    }

    private static void StoreSimpleHuffmanTree(ReadOnlySpan<byte> depths, Span<int> symbols, int numSymbols, int maxBits, BitWriter w)
    {
        w.WriteBits(2, 1);
        w.WriteBits(2, (ulong)(numSymbols - 1));
        for (int i = 0; i < numSymbols; i++)
        {
            for (int j = i + 1; j < numSymbols; j++)
            {
                if (depths[symbols[j]] < depths[symbols[i]]) (symbols[j], symbols[i]) = (symbols[i], symbols[j]);
            }
        }
        for (int i = 0; i < numSymbols; i++) w.WriteBits(maxBits, (ulong)symbols[i]);
        if (numSymbols == 4) w.WriteBits(1, depths[symbols[0]] == 1 ? 1UL : 0UL);
    }

    /// <summary>
    /// Builds a prefix code for <paramref name="histogram"/> (first <paramref name="histogramLength"/> symbols of an
    /// alphabet of <paramref name="alphabetSize"/>), stores it, and fills <paramref name="depth"/>/<paramref name="bits"/>.
    /// </summary>
    public static void BuildAndStoreHuffmanTree(ReadOnlySpan<uint> histogram, int histogramLength, int alphabetSize, byte[] depth, ushort[] bits, BitWriter w)
    {
        int count = 0;
        Span<int> s4 = stackalloc int[4];
        for (int i = 0; i < histogramLength; i++)
        {
            if (histogram[i] != 0)
            {
                if (count < 4) s4[count] = i;
                else if (count > 4) break;
                count++;
            }
        }
        int maxBits = 0;
        for (int maxBitsCounter = alphabetSize - 1; maxBitsCounter != 0; maxBitsCounter >>= 1) ++maxBits;

        if (count <= 1)
        {
            w.WriteBits(4, 1);
            w.WriteBits(maxBits, (ulong)s4[0]);
            depth[s4[0]] = 0;
            bits[s4[0]] = 0;
            return;
        }
        Array.Clear(depth, 0, histogramLength);
        CreateHuffmanTree(histogram, histogramLength, 15, depth);
        ConvertBitDepthsToSymbols(depth, histogramLength, bits);
        if (count <= 4)
        {
            StoreSimpleHuffmanTree(depth, s4, count, maxBits, w);
        }
        else
        {
            StoreHuffmanTree(depth, histogramLength, w);
        }
    }
}
