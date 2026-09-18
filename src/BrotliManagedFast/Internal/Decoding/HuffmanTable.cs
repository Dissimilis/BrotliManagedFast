using System;
using BrotliManagedFast.Internal;

namespace BrotliManagedFast.Internal;

/// <summary>
/// Two-level Huffman decoding tables, entries packed as (bits &lt;&lt; 16) | value.
/// Root table has 2^rootBits entries (8 or 10); entries with bits &gt; rootBits point (via value) to a second-level table.
/// </summary>
internal static class HuffmanTable
{
    public const int RootBits = Constants.HuffmanTableBits;

    /// <summary>Probability mass of the codes longer than the 8-bit root, in units of 2^-15, from a length histogram.</summary>
    public static int LongMass(ReadOnlySpan<int> count)
    {
        int longMass = 0;
        for (int len = RootBits + 1; len <= Constants.HuffmanMaxCodeLength; len++)
        {
            longMass += count[len] << (Constants.HuffmanMaxCodeLength - len);
        }
        return longMass;
    }

    /// <summary>
    /// Root width for a group of trees: 10 bits when some tree has at least an eighth of its probability mass
    /// in codes longer than 8 bits (the second-level lookup costs a dependent load and a mispredicted branch
    /// on those symbols) and the group is small enough for 4 KB roots to stay in L1; else 8.
    /// </summary>
    public static int ChooseGroupRootBits(int maxLongMass, int numTrees)
        => maxLongMass >= (1 << Constants.HuffmanMaxCodeLength) / 8 && numTrees <= 16 ? Constants.HuffmanTableMaxBits : RootBits;

    private static ReadOnlySpan<byte> ReverseBits8 => new byte[]
    {
        0x00, 0x80, 0x40, 0xC0, 0x20, 0xA0, 0x60, 0xE0, 0x10, 0x90, 0x50, 0xD0, 0x30, 0xB0, 0x70, 0xF0,
        0x08, 0x88, 0x48, 0xC8, 0x28, 0xA8, 0x68, 0xE8, 0x18, 0x98, 0x58, 0xD8, 0x38, 0xB8, 0x78, 0xF8,
        0x04, 0x84, 0x44, 0xC4, 0x24, 0xA4, 0x64, 0xE4, 0x14, 0x94, 0x54, 0xD4, 0x34, 0xB4, 0x74, 0xF4,
        0x0C, 0x8C, 0x4C, 0xCC, 0x2C, 0xAC, 0x6C, 0xEC, 0x1C, 0x9C, 0x5C, 0xDC, 0x3C, 0xBC, 0x7C, 0xFC,
        0x02, 0x82, 0x42, 0xC2, 0x22, 0xA2, 0x62, 0xE2, 0x12, 0x92, 0x52, 0xD2, 0x32, 0xB2, 0x72, 0xF2,
        0x0A, 0x8A, 0x4A, 0xCA, 0x2A, 0xAA, 0x6A, 0xEA, 0x1A, 0x9A, 0x5A, 0xDA, 0x3A, 0xBA, 0x7A, 0xFA,
        0x06, 0x86, 0x46, 0xC6, 0x26, 0xA6, 0x66, 0xE6, 0x16, 0x96, 0x56, 0xD6, 0x36, 0xB6, 0x76, 0xF6,
        0x0E, 0x8E, 0x4E, 0xCE, 0x2E, 0xAE, 0x6E, 0xEE, 0x1E, 0x9E, 0x5E, 0xDE, 0x3E, 0xBE, 0x7E, 0xFE,
        0x01, 0x81, 0x41, 0xC1, 0x21, 0xA1, 0x61, 0xE1, 0x11, 0x91, 0x51, 0xD1, 0x31, 0xB1, 0x71, 0xF1,
        0x09, 0x89, 0x49, 0xC9, 0x29, 0xA9, 0x69, 0xE9, 0x19, 0x99, 0x59, 0xD9, 0x39, 0xB9, 0x79, 0xF9,
        0x05, 0x85, 0x45, 0xC5, 0x25, 0xA5, 0x65, 0xE5, 0x15, 0x95, 0x55, 0xD5, 0x35, 0xB5, 0x75, 0xF5,
        0x0D, 0x8D, 0x4D, 0xCD, 0x2D, 0xAD, 0x6D, 0xED, 0x1D, 0x9D, 0x5D, 0xDD, 0x3D, 0xBD, 0x7D, 0xFD,
        0x03, 0x83, 0x43, 0xC3, 0x23, 0xA3, 0x63, 0xE3, 0x13, 0x93, 0x53, 0xD3, 0x33, 0xB3, 0x73, 0xF3,
        0x0B, 0x8B, 0x4B, 0xCB, 0x2B, 0xAB, 0x6B, 0xEB, 0x1B, 0x9B, 0x5B, 0xDB, 0x3B, 0xBB, 0x7B, 0xFB,
        0x07, 0x87, 0x47, 0xC7, 0x27, 0xA7, 0x67, 0xE7, 0x17, 0x97, 0x57, 0xD7, 0x37, 0xB7, 0x77, 0xF7,
        0x0F, 0x8F, 0x4F, 0xCF, 0x2F, 0xAF, 0x6F, 0xEF, 0x1F, 0x9F, 0x5F, 0xDF, 0x3F, 0xBF, 0x7F, 0xFF,
    };

    private const int ReverseBitsLowest = 1 << 7;
    private const int ReverseBits16Lowest = 1 << 15;

    /// <summary>Bit reversal of a 16-bit key (root tables wider than 8 bits need keys up to 15 bits).</summary>
    private static int Reverse16(int key) => (ReverseBits8[key & 0xFF] << 8) | ReverseBits8[key >> 8];

    public static uint Make(int bits, int value) => ((uint)bits << 16) | (uint)value;

    private static void Replicate(Span<uint> table, int start, int step, int end, uint code)
    {
        do
        {
            end -= step;
            table[start + end] = code;
        } while (end > 0);
    }

    private static int NextTableBitSize(ReadOnlySpan<int> count, int len, int rootBits)
    {
        int left = 1 << (len - rootBits);
        while (len < Constants.HuffmanMaxCodeLength)
        {
            left -= count[len];
            if (left <= 0) break;
            ++len;
            left <<= 1;
        }
        return len - rootBits;
    }

    /// <summary>
    /// Builds the 32-entry table for the code-length code (max code length 5).
    /// <paramref name="codeLengths"/> has 18 entries; <paramref name="count"/> is the histogram of lengths 0..5.
    /// </summary>
    public static void BuildCodeLengthsTable(Span<uint> table, ReadOnlySpan<byte> codeLengths, ReadOnlySpan<int> count)
    {
        Span<int> sorted = stackalloc int[Constants.CodeLengthCodes];
        Span<int> offset = stackalloc int[Constants.HuffmanMaxCodeLengthCodeLength + 1];
        int symbol = -1;
        int bits = 1;
        for (int k = 0; k < Constants.HuffmanMaxCodeLengthCodeLength; k++)
        {
            symbol += count[bits];
            offset[bits] = symbol;
            bits++;
        }
        offset[0] = Constants.CodeLengthCodes - 1;

        symbol = Constants.CodeLengthCodes;
        do
        {
            symbol--;
            sorted[offset[codeLengths[symbol]]--] = symbol;
        } while (symbol != 0);

        const int tableSize = 1 << Constants.HuffmanMaxCodeLengthCodeLength;
        if (offset[0] == 0)
        {
            uint code = Make(0, sorted[0]);
            for (int key = 0; key < tableSize; key++) table[key] = code;
            return;
        }

        int keyv = 0;
        int keyStep = ReverseBitsLowest;
        symbol = 0;
        bits = 1;
        int step = 2;
        do
        {
            for (int bitsCount = count[bits]; bitsCount != 0; --bitsCount)
            {
                uint code = Make(bits, sorted[symbol++]);
                Replicate(table, ReverseBits8[keyv], step, tableSize, code);
                keyv += keyStep;
            }
            step <<= 1;
            keyStep >>= 1;
        } while (++bits <= Constants.HuffmanMaxCodeLengthCodeLength);
    }

    /// <summary>
    /// Builds a two-level table from <paramref name="symbolsByLength"/> (symbols sorted by code length, then by
    /// symbol value, only non-zero lengths) and <paramref name="count"/> (histogram of lengths 1..15;
    /// entries for lengths &gt; root bits are consumed/modified). Returns the total table size used.
    /// </summary>
    public static int BuildTable(Span<uint> rootTable, int rootBits, ReadOnlySpan<ushort> symbolsByLength, Span<int> count)
    {
        int maxLength = Constants.HuffmanMaxCodeLength;
        while (maxLength > 0 && count[maxLength] == 0) maxLength--;

        int tableOffset = 0;
        int tableBits = rootBits;
        int tableSize = 1 << tableBits;
        int totalSize = tableSize;

        if (tableBits > maxLength)
        {
            tableBits = maxLength;
            tableSize = 1 << tableBits;
        }
        int key = 0;
        int keyStep = ReverseBits16Lowest;
        int bits = 1;
        int step = 2;
        int symIdx = 0;
        do
        {
            for (int bitsCount = count[bits]; bitsCount != 0; --bitsCount)
            {
                uint code = Make(bits, symbolsByLength[symIdx++]);
                Replicate(rootTable, tableOffset + Reverse16(key), step, tableSize, code);
                key += keyStep;
            }
            step <<= 1;
            keyStep >>= 1;
        } while (++bits <= tableBits);

        while (totalSize != tableSize)
        {
            rootTable.Slice(0, tableSize).CopyTo(rootTable.Slice(tableSize));
            tableSize <<= 1;
        }

        keyStep = ReverseBits16Lowest >> (rootBits - 1);
        int subKey = ReverseBits16Lowest << 1;
        int subKeyStep = ReverseBits16Lowest;
        step = 2;
        for (int len = rootBits + 1; len <= maxLength; ++len)
        {
            for (; count[len] != 0; --count[len])
            {
                if (subKey == (ReverseBits16Lowest << 1))
                {
                    tableOffset += tableSize;
                    tableBits = NextTableBitSize(count, len, rootBits);
                    tableSize = 1 << tableBits;
                    totalSize += tableSize;
                    subKey = Reverse16(key);
                    key += keyStep;
                    rootTable[subKey] = Make(tableBits + rootBits, tableOffset - subKey);
                    subKey = 0;
                }
                uint code = Make(len - rootBits, symbolsByLength[symIdx++]);
                Replicate(rootTable, tableOffset + Reverse16(subKey), step, tableSize, code);
                subKey += subKeyStep;
            }
            step <<= 1;
            subKeyStep >>= 1;
        }
        return totalSize;
    }

    /// <summary>Builds the table for a "simple" prefix code with 1..4 symbols. Returns table size (256).</summary>
    public static int BuildSimpleTable(Span<uint> table, int rootBits, Span<ushort> val, int numSymbols)
    {
        int tableSize = 1;
        int goalSize = 1 << rootBits;
        switch (numSymbols)
        {
            case 0:
                table[0] = Make(0, val[0]);
                break;
            case 1:
                if (val[1] > val[0])
                {
                    table[0] = Make(1, val[0]);
                    table[1] = Make(1, val[1]);
                }
                else
                {
                    table[0] = Make(1, val[1]);
                    table[1] = Make(1, val[0]);
                }
                tableSize = 2;
                break;
            case 2:
                table[0] = Make(1, val[0]);
                table[2] = Make(1, val[0]);
                if (val[2] > val[1])
                {
                    table[1] = Make(2, val[1]);
                    table[3] = Make(2, val[2]);
                }
                else
                {
                    table[1] = Make(2, val[2]);
                    table[3] = Make(2, val[1]);
                }
                tableSize = 4;
                break;
            case 3:
                for (int i = 0; i < 3; ++i)
                {
                    for (int k = i + 1; k < 4; ++k)
                    {
                        if (val[k] < val[i])
                        {
                            (val[k], val[i]) = (val[i], val[k]);
                        }
                    }
                }
                table[0] = Make(2, val[0]);
                table[2] = Make(2, val[1]);
                table[1] = Make(2, val[2]);
                table[3] = Make(2, val[3]);
                tableSize = 4;
                break;
            case 4:
                if (val[3] < val[2])
                {
                    (val[3], val[2]) = (val[2], val[3]);
                }
                table[0] = Make(1, val[0]);
                table[1] = Make(2, val[1]);
                table[2] = Make(1, val[0]);
                table[3] = Make(3, val[2]);
                table[4] = Make(1, val[0]);
                table[5] = Make(2, val[1]);
                table[6] = Make(1, val[0]);
                table[7] = Make(3, val[3]);
                tableSize = 8;
                break;
        }
        while (tableSize != goalSize)
        {
            table.Slice(0, tableSize).CopyTo(table.Slice(tableSize));
            tableSize <<= 1;
        }
        return goalSize;
    }
}

/// <summary>Root width as a type, so a generic hot loop sees it as a constant.</summary>
internal interface IRootWidth
{
    int Bits { get; }
}

internal struct Root8 : IRootWidth
{
    public int Bits => 8;
}

internal struct Root10 : IRootWidth
{
    public int Bits => 10;
}
